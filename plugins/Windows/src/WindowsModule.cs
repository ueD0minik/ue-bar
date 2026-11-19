// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json.Nodes;
using UeBar.Modules.Windows.Logic;
using UeBar.Modules.Windows.Platform;
using UeBar.Modules.Windows.Tiles;
using UeBar.Sdk;
using static UeBar.Modules.Windows.WinText;

namespace UeBar.Modules.Windows;

/// <summary>Everything the plugin knows right now – replaced as a whole (readers never see half an update).</summary>
internal sealed record WinState
{
    public AudioState Audio { get; init; } = AudioState.None;

    public IReadOnlyList<AppAudio> Apps { get; init; } = [];

    /// <summary>Media sessions without the ignored apps.</summary>
    public IReadOnlyList<MediaSessionInfo> Media { get; init; } = [];

    public MediaSessionInfo? SelectedMedia { get; init; }

    public RadioState Radio { get; init; } = RadioState.None;

    public IReadOnlyList<BluetoothDeviceInfo> Bluetooth { get; init; } = [];

    /// <summary>Batteries other plugins report (vendor plugins for wireless dongles …).</summary>
    public IReadOnlyList<DeviceBatteryInfo> ExternalBatteries { get; init; } = [];

    public PowerState Power { get; init; } = PowerState.NoBattery;

    public SystemSample? Sample { get; init; }

    /// <summary>Brightness of the built-in display, null = none.</summary>
    public int? Brightness { get; init; }

    public bool BrightnessRead { get; init; }

    public WindowsSettings Settings { get; init; } = WindowsSettings.Default;

    /// <summary>Apps that play sound right now (app volume tile).</summary>
    public IReadOnlyList<AppAudio> PlayingApps => Settings.ShowAppVolume ? [.. Apps.Where(a => a.IsPlaying)] : [];
}

/// <summary>
/// Plugin "windows" (P2-2): this PC as tiles – volume and output device, app volume, media of all apps
/// (also as Now Playing), Wi-Fi/Bluetooth, device batteries, battery/CPU/memory/disk, brightness of the
/// built-in display, lock/sleep/screen off. Event driven where Windows offers events; CPU, memory, disk,
/// Wi-Fi signal and brightness are only read while they are visible (<see cref="IVisibilityAware"/>).
/// </summary>
public sealed partial class WindowsModule :
    IModule,
    IEntitySource,
    IStatusProvider,
    INowPlayingSource,
    IImageSource,
    ILayoutSuggestionProvider,
    ILiveModuleSettings,
    IVisibilityAware,
    IDeviceBatterySource,
    IModuleDiagnostics,
    IModuleSettingsActions
{
    public const string ModuleId = "windows";

    /// <summary>Playback on this computer comes before other devices by default (SPEC 10, P2-2).</summary>
    public const int LocalPlaybackPriority = 100;

    private const string ImagePrefix = "win.media:";

    private readonly Func<IModuleLogger, Task<WindowsPlatform>> _platformFactory;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Serializes changes to the tiles: a new tile is refreshed on the core's thread while the worker
    /// may already refresh it as a tracked tile – two threads adding to the same element list break it
    /// for good (P2-3). Held only while tiles are changed; the app picks the changes up asynchronously.
    /// </summary>
    internal readonly Lock ViewLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPlaying = new(StringComparer.Ordinal);
    private readonly List<Tracked> _tiles = [];
    private readonly Dictionary<Area, int> _signals = [];
    private readonly Dictionary<string, StatusValue> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly WinTileType[] _tileTypes;
    private WinState _state = new();
    private WindowsPlatform? _platform;
    private IModuleContext? _context;
    private IDeviceBatteryCatalog? _catalog;
    private ModuleVisibility _visibility = ModuleVisibility.Hidden;
    private Area _dirty;
    private string? _pinnedMedia;
    private NowPlayingInfo? _nowPlaying;
    private string? _lastDeviceBatteries;
    private string? _lastAudioDevice;
    private DateTimeOffset? _sleepArmedAt;
    private int _polls;
    private bool _timelineStale;
    private bool _working;
    private DateTimeOffset _radioPolledAt = DateTimeOffset.MinValue;
    private DateTimeOffset _brightnessPolledAt = DateTimeOffset.MinValue;

    /// <summary>Constructor the plugin loader uses: real Windows APIs.</summary>
    public WindowsModule()
        : this(CreatePlatformAsync, TimeProvider.System)
    {
    }

    /// <summary>For tests: own platform (fakes) and clock.</summary>
    internal WindowsModule(Func<IModuleLogger, Task<WindowsPlatform>> platformFactory, TimeProvider time)
    {
        _platformFactory = platformFactory;
        _time = time;
        _tileTypes =
        [
            new VolumeTileType(this),
            new AppMixerTileType(this),
            new AppVolumeTileType(this),
            new MediaTileType(this),
            new WifiTileType(this),
            new BluetoothTileType(this),
            new DevicesTileType(this),
            new DeviceTileType(this),
            new SystemValueTileType(this),
            new BrightnessTileType(this),
            new ActionTileType(this),
        ];
    }

    public string Id => ModuleId;

    public string DisplayName => T["Win_ModuleName"];

    public string Icon => "Laptop";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<EntityInfo>? EntityChanged;

    public event EventHandler? NowPlayingChanged;

    public event EventHandler? DeviceBatteriesChanged;

    /// <summary>Pause between an event and reading the new state (several events come together). 0 in tests.</summary>
    internal TimeSpan Coalesce { get; set; } = TimeSpan.FromMilliseconds(40);

    /// <summary>How long a first tap on "Sleep" waits for the confirming second one.</summary>
    internal static TimeSpan SleepConfirmWindow { get; } = TimeSpan.FromSeconds(5);

    internal WinState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    internal WindowsPlatform Platform => _platform ?? throw new InvalidOperationException("Windows plugin not started");

    internal IModuleLogger? Log => _context?.Log;

    /// <summary>Number of polling rounds (CPU, memory …) so far – for tests and diagnostics.</summary>
    internal int PollCount => Volatile.Read(ref _polls);

    internal TimeProvider Time => _time;

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (context is IUiLanguageInfo language)
        {
            Culture = language.UiCulture;
            language.UiCultureChanged += OnUiCultureChanged;
        }

        _catalog = context as IDeviceBatteryCatalog;
        if (_catalog is not null)
        {
            _catalog.DeviceBatteriesChanged += (_, _) => Signal(Area.Devices);
        }

        var platform = await _platformFactory(context.Log).ConfigureAwait(false);
        _platform = platform;
        platform.Audio.Changed += (_, _) => Signal(Area.Audio);
        platform.Audio.AppsChanged += (_, _) => Signal(Area.Apps);
        platform.Media.Changed += (_, _) => Signal(Area.Media);
        platform.Media.TimelineChanged += (_, _) => OnTimelineChanged();
        platform.Radio.Changed += (_, _) => Signal(Area.Radio);
        platform.Bluetooth.Changed += (_, _) => Signal(Area.Devices);
        platform.System.PowerChanged += (_, _) => Signal(Area.Power);

        PublishOnBus(context.Bus);
        // CPU/memory/disk, brightness (WMI) and Bluetooth devices are read only once they are visible.
        await RefreshAsync(Area.All & ~(Area.System | Area.Brightness), cancellationToken).ConfigureAwait(false);

        context.RunBackground("updates", UpdateLoopAsync);
        context.RunBackground("system values", PollLoopAsync);
        SetStatus(ModuleStatus.Connected);
        context.Log.Info($"Windows plugin started: {State.Audio.Outputs.Count} outputs, {State.Media.Count} media sessions, {State.Bluetooth.Count} Bluetooth devices, battery {(State.Power.HasBattery ? "yes" : "no")}");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var platform = Interlocked.Exchange(ref _platform, null);
        platform?.Dispose();
        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    /// <summary>Real Windows APIs; each part on its own – a part that fails is replaced by a stand-in.</summary>
    private static async Task<WindowsPlatform> CreatePlatformAsync(IModuleLogger log)
    {
        if (DemoPlatform.IsOn)
        {
            log.Warn($"{DemoPlatform.Variable} is set: demo data instead of this PC (for screenshots)");
            return DemoPlatform.Create();
        }

        async Task<T> Try<T>(string what, Func<Task<T>> create, Func<T> fallback)
        {
            try
            {
                return await create().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Warn($"{what} not available – the tiles show it as such", ex);
                return fallback();
            }
        }

        IAudioPlatform audio = await Try("Core Audio", () => Task.FromResult<IAudioPlatform>(new CoreAudioPlatform()), () => new NullAudioPlatform()).ConfigureAwait(false);
        IMediaPlatform media = await Try("Media sessions", async () => (IMediaPlatform)await MediaPlatform.CreateAsync().ConfigureAwait(false), () => new NullMediaPlatform()).ConfigureAwait(false);
        IRadioPlatform radio = await Try("Radios", async () => (IRadioPlatform)await RadioPlatform.CreateAsync().ConfigureAwait(false), () => new NullRadioPlatform()).ConfigureAwait(false);
        IBluetoothPlatform bluetooth = await Try("Bluetooth devices", () => Task.FromResult<IBluetoothPlatform>(new BluetoothPlatform()), () => new NullBluetoothPlatform()).ConfigureAwait(false);
        ISystemPlatform system = await Try("Power/system values", () => Task.FromResult<ISystemPlatform>(new SystemPlatform()), () => new NullSystemPlatform()).ConfigureAwait(false);
        return new WindowsPlatform(audio, media, radio, bluetooth, system, new ShellPlatform());
    }

    private void OnUiCultureChanged(object? sender, CultureInfo culture)
    {
        Culture = culture;
        if (Status.State == ModuleState.Connected)
        {
            SetStatus(ModuleStatus.Connected);
        }

        Signal(Area.Texts); // tiles are only ever updated on the worker
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Only the position moved: read it only if a media tile is visible (players report this often).</summary>
    private void OnTimelineChanged()
    {
        lock (_lock)
        {
            if (!_visibility.IsVisible(WinIds.Media))
            {
                _timelineStale = true;
                return;
            }
        }

        Signal(Area.Media);
    }

    // ------------------------------------------------------------------ settings

    /// <summary>Key of the button "Open location access" (only while the Wi-Fi name is hidden).</summary>
    internal const string LocationButtonKey = "openLocationAccess";

    public IReadOnlyList<SettingField> SettingFields
    {
        get
        {
            List<SettingField> fields =
            [
                new(WindowsSettings.PollKey, T["Win_SettingPoll"], SettingFieldKind.Number, "2", T["Win_SettingPoll_Hint"], Min: WindowsSettings.MinPollSeconds, Max: 60),
                new(WindowsSettings.AppVolumeKey, T["Win_SettingAppVolume"], SettingFieldKind.Toggle, "true", T["Win_SettingAppVolume_Hint"]),
                new(WindowsSettings.VirtualOutputsKey, T["Win_SettingVirtual"], SettingFieldKind.Toggle, "false", T["Win_SettingVirtual_Hint"]),
                new(WindowsSettings.IgnoredKey, T["Win_SettingIgnored"], SettingFieldKind.Text, WindowsSettings.DefaultIgnored, T["Win_SettingIgnored_Hint"]) { Placeholder = "Teams, Discord" },
            ];

            // Hint with a button only when the network name cannot be read (P2-3).
            if (State.Radio.NetworkNameHidden)
            {
                fields.Insert(0, new SettingField(LocationButtonKey, T["Win_LocationHint"], SettingFieldKind.Button, null, T["Win_LocationHint_Detail"])
                {
                    ButtonText = T["Win_LocationButton"],
                });
            }

            return fields;
        }
    }

    public Task<string?> RunSettingsActionAsync(string key, CancellationToken cancellationToken)
    {
        if (key == LocationButtonKey)
        {
            Platform.Shell.OpenSettings("ms-settings:privacy-location");
            Signal(Area.Radio); // the name may be readable after coming back
            return Task.FromResult<string?>(T["Win_LocationOpened"]);
        }

        return Task.FromResult<string?>(null);
    }

    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        var settings = WindowsSettings.From(values);
        lock (_lock)
        {
            _state = _state with { Settings = settings };
        }

        if (_platform is not null)
        {
            Signal(Area.Audio | Area.Apps | Area.Media | Area.Texts);
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ visibility (polling only while visible)

    public void OnVisibilityChanged(ModuleVisibility visibility)
    {
        bool wake;
        var timeline = false;
        lock (_lock)
        {
            var before = NeedsPolling(_visibility);
            _visibility = visibility;
            wake = !before && NeedsPolling(visibility);
            if (_timelineStale && visibility.IsVisible(WinIds.Media))
            {
                _timelineStale = false;
                timeline = true; // position moved while nobody looked
            }
            if (!visibility.IsPanelOpen)
            {
                _sleepArmedAt = null;
            }
        }

        if (!visibility.IsPanelOpen)
        {
            Signal(Area.Texts); // drops a pending "tap again to confirm"
        }

        if (timeline)
        {
            Signal(Area.Media);
        }

        if (wake)
        {
            Release(_pollWake);
        }
    }

    internal bool IsPolling
    {
        get
        {
            lock (_lock)
            {
                return NeedsPolling(_visibility);
            }
        }
    }

    private static bool NeedsPolling(ModuleVisibility visibility) => visibility.VisibleEntities.Any(WinIds.IsPolled);

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsPolling)
            {
                await _pollWake.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(State.Settings.PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One polling round for what is visible right now.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _polls);
        HashSet<string> visible;
        lock (_lock)
        {
            visible = [.. _visibility.VisibleEntities];
        }

        var areas = Area.None;
        if (visible.Overlaps([WinIds.Cpu, WinIds.Ram, WinIds.Disk]))
        {
            areas |= Area.System;
        }

        // Slower things only every SlowPollInterval (they change rarely; own changes are read right away).
        var now = _time.GetUtcNow();
        if (visible.Contains(WinIds.Wifi) && Due(ref _radioPolledAt, now))
        {
            areas |= Area.Radio; // signal strength has no event
        }

        if (visible.Contains(WinIds.Brightness) && Due(ref _brightnessPolledAt, now))
        {
            areas |= Area.Brightness; // WMI query – the most expensive read
        }

        if (visible.Any(WinIds.NeedsDevices))
        {
            await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);

            // Also the batteries other plugins report: a read that failed (e.g. time limit) is not lost.
            areas |= Area.Devices;
        }

        await RefreshAsync(areas, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Wi-Fi signal and brightness: at most this often while visible.</summary>
    internal static TimeSpan SlowPollInterval { get; } = TimeSpan.FromSeconds(10);

    private bool Due(ref DateTimeOffset last, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (now - last < SlowPollInterval)
            {
                return false;
            }

            last = now;
            return true;
        }
    }

    /// <summary>Bluetooth devices and their batteries (read on request only; the platform limits how often).</summary>
    internal async Task RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Platform.Bluetooth.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Warn("Reading Bluetooth devices failed", ex);
        }
    }

    // ------------------------------------------------------------------ updates (event driven)

    /// <summary>Something changed in Windows: read it on the worker, not in the callback.</summary>
    internal void Signal(Area area)
    {
        lock (_lock)
        {
            _dirty |= area;
            foreach (var flag in Enum.GetValues<Area>().Where(f => f is not Area.None and not Area.All && area.HasFlag(f)))
            {
                _signals[flag] = _signals.GetValueOrDefault(flag) + 1;
            }
        }

        Release(_wake);
    }

    private static void Release(SemaphoreSlim semaphore)
    {
        try
        {
            if (semaphore.CurrentCount == 0)
            {
                semaphore.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    private async Task UpdateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Coalesce > TimeSpan.Zero)
            {
                await Task.Delay(Coalesce, cancellationToken).ConfigureAwait(false);
            }

            Area areas;
            lock (_lock)
            {
                areas = _dirty;
                _dirty = Area.None;
                _working = true;
            }

            try
            {
                await RefreshAsync(areas, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _working = false;
                }
            }
        }
    }

    /// <summary>Nothing waiting and no refresh running (tests wait for this instead of sleeping).</summary>
    internal bool IsIdle
    {
        get
        {
            lock (_lock)
            {
                return _dirty == Area.None && !_working && _wake.CurrentCount == 0 && _refreshGate.CurrentCount == 1;
            }
        }
    }

    /// <summary>Reads the given areas, then updates tiles, status values, Now Playing and the bus.</summary>
    internal async Task RefreshAsync(Area areas, CancellationToken cancellationToken)
    {
        if (areas == Area.None || _platform is not { } platform)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = State;
            next = await ReadAsync(areas, Area.Audio, next, s => s with { Audio = platform.Audio.GetState() }).ConfigureAwait(false);
            next = await ReadAsync(areas, Area.Apps, next, s => s with { Apps = platform.Audio.GetApps() }).ConfigureAwait(false);
            next = await ReadAsync(areas, Area.Radio, next, s => s with { Radio = platform.Radio.GetState() }).ConfigureAwait(false);
            next = await ReadAsync(areas, Area.Devices, next, s => s with
            {
                Bluetooth = platform.Bluetooth.GetDevices(),
                ExternalBatteries = _catalog?.GetDeviceBatteries() ?? [],
            }).ConfigureAwait(false);
            next = await ReadAsync(areas, Area.Power, next, s => s with { Power = platform.System.GetPower() }).ConfigureAwait(false);
            next = await ReadAsync(areas, Area.Brightness, next, s => s with { Brightness = platform.System.GetBrightness(), BrightnessRead = true }).ConfigureAwait(false);
            if (areas.HasFlag(Area.System))
            {
                next = await ReadAsync(areas, Area.System, next, async s => s with { Sample = await platform.System.SampleAsync(cancellationToken).ConfigureAwait(false) }).ConfigureAwait(false);
            }

            if (areas.HasFlag(Area.Media))
            {
                next = await ReadAsync(areas, Area.Media, next, async s =>
                {
                    var sessions = await platform.Media.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
                    return WithMedia(s, sessions, platform.Media.CurrentSessionId);
                }).ConfigureAwait(false);
            }

            lock (_lock)
            {
                _state = next with { Settings = _state.Settings };
            }

            AfterRefresh(areas);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<WinState> ReadAsync(Area areas, Area area, WinState state, Func<WinState, WinState> read) =>
        await ReadAsync(areas, area, state, s => Task.FromResult(read(s))).ConfigureAwait(false);

    private async Task<WinState> ReadAsync(Area areas, Area area, WinState state, Func<WinState, Task<WinState>> read)
    {
        if (!areas.HasFlag(area))
        {
            return state;
        }

        try
        {
            return await read(state).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One area failing must not stop the others (or the plugin).
            Log?.Warn($"Reading {area} failed", ex);
            return state;
        }
    }

    private WinState WithMedia(WinState state, IReadOnlyList<MediaSessionInfo> sessions, string? windowsCurrent)
    {
        var settings = State.Settings;
        var visible = sessions.Where(s => !settings.IsIgnored(s)).ToList();
        var now = _time.GetUtcNow();
        Dictionary<string, DateTimeOffset> lastPlaying;
        string? pinned;
        lock (_lock)
        {
            foreach (var session in visible.Where(s => s.IsPlaying))
            {
                _lastPlaying[session.Id] = now;
            }

            foreach (var gone in _lastPlaying.Keys.Where(k => visible.All(s => s.Id != k)).ToList())
            {
                _lastPlaying.Remove(gone);
            }

            if (_pinnedMedia is not null && visible.All(s => s.Id != _pinnedMedia))
            {
                _pinnedMedia = null;
            }

            lastPlaying = new Dictionary<string, DateTimeOffset>(_lastPlaying, StringComparer.Ordinal);
            pinned = _pinnedMedia;
        }

        return state with { Media = visible, SelectedMedia = MediaChooser.Choose(visible, pinned, windowsCurrent, lastPlaying) };
    }

    private void AfterRefresh(Area areas)
    {
        UpdateViews(areas);
        if (areas.HasFlag(Area.Media) || areas.HasFlag(Area.Texts))
        {
            UpdateNowPlaying();
        }

        if (areas.HasFlag(Area.Devices))
        {
            var batteries = DeviceBatteries;
            var key = string.Join(";", batteries.Select(b => $"{b.DeviceId}={b.Percent}/{b.IsConnected}"));
            if (key != Interlocked.Exchange(ref _lastDeviceBatteries, key))
            {
                DeviceBatteriesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        UpdateBus(areas);
        RaiseEntityChanges(areas);
    }

    /// <summary>The search shows states – tell it about the things that changed.</summary>
    private void RaiseEntityChanges(Area areas)
    {
        if (EntityChanged is null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (areas.HasFlag(Area.Audio))
        {
            ids.Add(WinIds.Volume);
        }

        if (areas.HasFlag(Area.Media))
        {
            ids.Add(WinIds.Media);
        }

        if (areas.HasFlag(Area.Radio))
        {
            ids.UnionWith([WinIds.Wifi, WinIds.Bluetooth]);
        }

        if (areas.HasFlag(Area.Power))
        {
            ids.Add(WinIds.Battery);
        }

        foreach (var (info, _) in AllEntities().Where(e => ids.Contains(e.Info.Id)))
        {
            EntityChanged?.Invoke(this, info);
        }
    }

    /// <summary>Pins a media session (choice on the media tile) – until it disappears.</summary>
    internal void PinMedia(string sessionId)
    {
        lock (_lock)
        {
            _pinnedMedia = sessionId;
        }

        Signal(Area.Media);
    }

    // ------------------------------------------------------------------ tiles and status values

    internal void Track(TileModel tile, WinTileType type, TileConfig config, string entity)
    {
        lock (_lock)
        {
            _tiles.RemoveAll(t => !t.Tile.TryGetTarget(out _));
            _tiles.Add(new Tracked(new WeakReference<TileModel>(tile), type, config, entity));
        }
    }

    /// <summary>Refreshes the tiles and status values that depend on <paramref name="areas"/>.</summary>
    internal void UpdateViews(Area areas)
    {
        List<(TileModel Tile, WinTileType Type, TileConfig Config, string Entity)> tiles = [];
        lock (_lock)
        {
            foreach (var tracked in _tiles)
            {
                if (tracked.Tile.TryGetTarget(out var tile) && (areas.HasFlag(Area.Texts) || (tracked.Type.Areas & areas) != 0))
                {
                    tiles.Add((tile, tracked.Type, tracked.Config, tracked.Entity));
                }
            }
        }

        WinState state;
        lock (ViewLock)
        {
            state = State;
            foreach (var (tile, type, config, entity) in tiles)
            {
                try
                {
                    type.Refresh(tile, config, entity, state);
                }
                catch (Exception ex)
                {
                    Log?.Warn($"Updating tile {config.Id} failed", ex);
                }
            }
        }

        UpdateStatusValues(state);
    }

    internal bool IsSleepArmed
    {
        get
        {
            lock (_lock)
            {
                return _sleepArmedAt is { } at && _time.GetUtcNow() - at < SleepConfirmWindow;
            }
        }
    }

    /// <summary>First tap arms, second tap within <see cref="SleepConfirmWindow"/> confirms.</summary>
    internal bool ConfirmSleep()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_sleepArmedAt is { } at && now - at < SleepConfirmWindow)
            {
                _sleepArmedAt = null;
                return true;
            }

            _sleepArmedAt = now;
            return false;
        }
    }

    private sealed record Tracked(WeakReference<TileModel> Tile, WinTileType Type, TileConfig Config, string Entity);

    // ------------------------------------------------------------------ Now Playing (SPEC 10)

    public NowPlayingInfo? CurrentNowPlaying
    {
        get
        {
            lock (_lock)
            {
                return _nowPlaying;
            }
        }
    }

    public int NowPlayingPriority => LocalPlaybackPriority;

    public async Task ExecuteNowPlayingAsync(NowPlayingCommand command, CancellationToken cancellationToken)
    {
        if (State.SelectedMedia is { } session)
        {
            await SendMediaAsync(session.Id, command switch
            {
                NowPlayingCommand.Next => MediaCommand.Next,
                NowPlayingCommand.Previous => MediaCommand.Previous,
                _ => MediaCommand.PlayPause,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task SendMediaAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken)
    {
        await Platform.Media.SendAsync(sessionId, command, cancellationToken).ConfigureAwait(false);
        Signal(Area.Media);
    }

    private void UpdateNowPlaying()
    {
        var session = State.SelectedMedia;
        NowPlayingInfo? info = null;
        if (session is { HasContent: true, Playback: MediaPlayback.Playing or MediaPlayback.Paused })
        {
            DateTimeOffset lastActive;
            lock (_lock)
            {
                lastActive = _lastPlaying.TryGetValue(session.Id, out var t) ? t : _time.GetUtcNow();
            }

            info = new NowPlayingInfo(session.Id, session.Title!, session.Artist, ImageId(session), session.IsPlaying, session.AppName) { LastActive = lastActive };
        }

        bool changed;
        lock (_lock)
        {
            var before = _nowPlaying;
            changed = !SameNowPlaying(before, info);
            _nowPlaying = info;
        }

        if (changed)
        {
            NowPlayingChanged?.Invoke(this, EventArgs.Empty);
            RaiseBusEvent("win.media_changed", info is null
                ? new JsonObject { ["playing"] = false }
                : new JsonObject { ["app"] = info.SourceName, ["title"] = info.Title, ["artist"] = info.Artist, ["playing"] = info.IsPlaying });
        }
    }

    private static bool SameNowPlaying(NowPlayingInfo? a, NowPlayingInfo? b) =>
        a is null ? b is null
        : b is not null && a.SourceId == b.SourceId && a.Title == b.Title && a.Artist == b.Artist && a.IsPlaying == b.IsPlaying && a.ImageId == b.ImageId;

    internal static string? ImageId(MediaSessionInfo session) =>
        session.ThumbnailKey is { } key ? $"{ImagePrefix}{session.Id}|{key}" : null;

    public async Task<ModuleImage?> LoadImageAsync(string imageId, CancellationToken cancellationToken)
    {
        if (!imageId.StartsWith(ImagePrefix, StringComparison.Ordinal) || _platform is not { } platform)
        {
            return null;
        }

        var rest = imageId[ImagePrefix.Length..];
        var bar = rest.LastIndexOf('|');
        var sessionId = bar > 0 ? rest[..bar] : rest;
        var data = await platform.Media.LoadThumbnailAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return data is { Length: > 0 } ? new ModuleImage(data) : null;
    }

    // ------------------------------------------------------------------ device batteries (for other plugins)

    public IReadOnlyList<DeviceBatteryInfo> DeviceBatteries =>
    [
        .. State.Bluetooth
            .Where(d => d.Battery is not null)
            .Select(d => new DeviceBatteryInfo(d.Id, d.Name, d.Battery, null, d.IsConnected, d.Icon)),
    ];

    // ------------------------------------------------------------------ diagnostics

    private string SignalCounts()
    {
        lock (_lock)
        {
            return _signals.Count == 0 ? "none" : string.Join(", ", _signals.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));
        }
    }

    public IReadOnlyList<string> GetDiagnostics()
    {
        var s = State;
        return
        [
            $"Audio: {s.Audio.Outputs.Count} outputs, default \"{s.Audio.Default?.Name ?? "-"}\", {s.Apps.Count} apps ({s.PlayingApps.Count} playing)",
            $"Media: {s.Media.Count} sessions{(s.SelectedMedia is { } m ? $", showing {m.AppName}" : "")}",
            $"Wi-Fi {s.Radio.Wifi}, Bluetooth {s.Radio.Bluetooth}, {s.Bluetooth.Count} Bluetooth devices, {s.ExternalBatteries.Count} batteries from other plugins",
            $"Battery: {(s.Power.HasBattery ? $"{s.Power.Percent} %" : "none")}, brightness: {(s.Brightness is { } b ? $"{b} %" : s.BrightnessRead ? "no built-in display" : "not read yet (read while visible)")}",
            $"Polling: {(IsPolling ? $"on, every {s.Settings.PollInterval.TotalSeconds:0} s" : "paused (nothing visible)")}, {PollCount} rounds",
            $"Events: {SignalCounts()}",
        ];
    }
}
