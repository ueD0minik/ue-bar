// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UeBar.Sdk;
using static UeBar.Modules.Timer.TimerText;

namespace UeBar.Modules.Timer;

/// <summary>Settings of the timer plugin.</summary>
internal sealed record TimerSettings(
    IReadOnlyList<TimeSpan> Presets,
    PomodoroSettings Pomodoro,
    string? Sound,
    double Volume,
    bool IgnoreDoNotDisturb,
    bool ShowInFullscreen,
    string? WorkAction,
    string? WorkArguments,
    string? BreakAction,
    string? BreakArguments)
{
    public const string PresetsKey = "presets";
    public const string WorkKey = "pomodoroWork";
    public const string ShortKey = "pomodoroShortBreak";
    public const string LongKey = "pomodoroLongBreak";
    public const string RoundsKey = "pomodoroRounds";
    public const string SoundKey = "sound";
    public const string SoundFileKey = "soundFile";
    public const string VolumeKey = "volume";
    public const string DndKey = "ignoreDoNotDisturb";
    public const string FullscreenKey = "showInFullscreen";
    public const string WorkActionKey = "workAction";
    public const string WorkArgumentsKey = "workArguments";
    public const string BreakActionKey = "breakAction";
    public const string BreakArgumentsKey = "breakArguments";

    public const string SoundDefault = "default";
    public const string SoundNone = "none";
    public const string SoundFile = "file";

    public static TimerSettings Default { get; } = From(ModuleSettingValues.Empty);

    public static TimerSettings From(ModuleSettingValues v)
    {
        static TimeSpan Minutes(double value, double fallback) => TimeSpan.FromSeconds(Math.Round(Math.Clamp(value > 0 ? value : fallback, 0.1, 600) * 60));

        var sound = v.GetString(SoundKey) ?? SoundDefault;
        return new TimerSettings(
            TimerCommand.ParsePresets(v.GetString(PresetsKey)),
            new PomodoroSettings(
                Minutes(v.GetNumber(WorkKey, 25), 25),
                Minutes(v.GetNumber(ShortKey, 5), 5),
                Minutes(v.GetNumber(LongKey, 15), 15),
                (int)Math.Clamp(v.GetNumber(RoundsKey, 4), 1, 12)),
            sound switch
            {
                SoundNone => null,
                SoundFile => v.GetString(SoundFileKey) is { Length: > 0 } file ? file : UeNotification.DefaultSound,
                _ => UeNotification.DefaultSound,
            },
            Math.Clamp(v.GetNumber(VolumeKey, 70), 0, 100) / 100,
            v.GetBool(DndKey, true),
            v.GetBool(FullscreenKey, true),
            Empty(v.GetString(WorkActionKey)),
            Empty(v.GetString(WorkArgumentsKey)),
            Empty(v.GetString(BreakActionKey)),
            Empty(v.GetString(BreakArgumentsKey)));

        static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}

/// <summary>
/// Plugin "timer" (P2-3): several named timers, stopwatch with laps, Pomodoro – as tiles, quick
/// commands ("t 10", "pomo"), status value and pill activity. Everything with absolute times, saved in
/// the plugin's data folder, so it survives a restart. Nothing ticks while the bar is collapsed: the
/// worker only wakes up when something is due; the per-second display update runs only while
/// something of the plugin is visible (<see cref="IVisibilityAware"/>).
/// </summary>
public sealed partial class TimerModule :
    IModule,
    IEntitySource,
    IStatusProvider,
    INotificationSource,
    INotificationActionHandler,
    IPillActivitySource,
    IQuickCommandProvider,
    IModuleSettings,
    IVisibilityAware,
    ILayoutSuggestionProvider,
    IModuleDiagnostics
{
    public const string ModuleId = "timer";
    private const string StateFile = "timers.json";
    private const string PlusFive = "plus5";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    /// <summary>
    /// Serializes changes to the tiles: a new tile is refreshed on the core's thread while the worker
    /// may already refresh it – two threads changing one element list break it (P2-3).
    /// </summary>
    internal readonly Lock ViewLock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly List<Tracked> _tiles = [];
    private readonly Dictionary<string, StatusValue> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Name, TimeSpan Duration)> _notified = new(StringComparer.Ordinal);
    private readonly TimerTileType[] _tileTypes;
    private TimerEngine _engine = new();
    private TimerSettings _settings = TimerSettings.Default;
    private ModuleVisibility _visibility = ModuleVisibility.Hidden;
    private IModuleContext? _context;
    private string? _stateFile;
    private int _ticks;
    private int _notificationCounter;

    public TimerModule()
        : this(TimeProvider.System)
    {
    }

    internal TimerModule(TimeProvider time)
    {
        _time = time;
        _tileTypes = [new StartTileType(this), new RunningTileType(this), new StopwatchTileType(this), new PomodoroTileType(this)];
    }

    public string Id => ModuleId;

    public string DisplayName => T["Timer_ModuleName"];

    public string Icon => "Clock";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<EntityInfo>? EntityChanged;

    public event EventHandler<UeNotification>? NotificationReceived;

    public event EventHandler? PillActivityChanged;

    internal TimeProvider Time => _time;

    internal IModuleLogger? Log => _context?.Log;

    /// <summary>Per-second display updates so far (tests: none while collapsed).</summary>
    internal int Ticks => Volatile.Read(ref _ticks);

    internal TimerSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    internal DateTimeOffset Now => _time.GetUtcNow();

    internal DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>Reads/changes the engine under the module lock.</summary>
    internal T With<T>(Func<TimerEngine, T> action)
    {
        lock (_lock)
        {
            return action(_engine);
        }
    }

    /// <summary>Changes the state: saved, worker woken (new due time), views updated.</summary>
    internal T Change<T>(Func<TimerEngine, T> action)
    {
        T result;
        lock (_lock)
        {
            result = action(_engine);
        }

        Save();
        Wake();
        return result;
    }

    internal void Change(Action<TimerEngine> action) => Change(e =>
    {
        action(e);
        return true;
    });

    // ------------------------------------------------------------------ lifecycle

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (context is IUiLanguageInfo language)
        {
            Culture = language.UiCulture;
            language.UiCultureChanged += (_, culture) =>
            {
                Culture = culture;
                Wake();
            };
        }

        _stateFile = Path.Combine(context.DataDirectory, StateFile);
        Load();
        PublishOnBus(context.Bus);
        context.RunBackground("clock", ClockLoopAsync);
        SetStatus(ModuleStatus.Connected);
        var (timers, pomodoro) = With(e => (e.Timers.Count, e.Pomodoro.Phase));
        context.Log.Info($"Timer plugin started: {timers} timers, Pomodoro {pomodoro}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Save();
        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Restores timers, stopwatch and Pomodoro (absolute times – the clock kept running while ue Bar was closed).</summary>
    private void Load()
    {
        if (_stateFile is null || !File.Exists(_stateFile))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<TimerEngine>(File.ReadAllText(_stateFile), Json);
            if (loaded is not null)
            {
                lock (_lock)
                {
                    loaded.PomodoroSettings = _settings.Pomodoro;
                    _engine = loaded;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            Log?.Warn($"{StateFile} could not be read – starting empty", ex);
        }
    }

    private void Save()
    {
        if (_stateFile is null)
        {
            return;
        }

        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_engine, Json);
            }

            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Warn($"{StateFile} could not be written", ex);
        }
    }

    // ------------------------------------------------------------------ the clock

    internal void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Wakes up when something is due (timer end, Pomodoro phase), when the state changed – and once a
    /// second only while something runs <b>and</b> something of the plugin is visible.
    /// </summary>
    private async Task ClockLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = Now;
            await ProcessAsync(now, cancellationToken).ConfigureAwait(false);
            UpdateViews();

            DateTimeOffset? until;
            lock (_lock)
            {
                until = _engine.NextDue();
                if (IsVisible(_visibility) && _engine.AnythingRunning)
                {
                    // Next full second of the display.
                    var tick = now.AddMilliseconds(1000 - now.Millisecond);
                    until = until is { } u && u < tick ? u : tick;
                }
            }

            await WaitAsync(until, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Until a point in time (null = until woken). The span is taken right before waiting, so time that passed meanwhile counts.</summary>
    private async Task WaitAsync(DateTimeOffset? until, CancellationToken cancellationToken)
    {
        TimeSpan? wait = until is { } u ? u - Now : null;
        if (wait is { } w && w <= TimeSpan.Zero)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var woken = _wake.WaitAsync(cts.Token);
        if (wait is { } span)
        {
            var delay = Task.Delay(span, _time, cts.Token);

            // Time that passed between taking the span and registering the wait must not be lost.
            if (Now < until!.Value)
            {
                await Task.WhenAny(woken, delay).ConfigureAwait(false);
            }
        }
        else
        {
            await Task.WhenAny(woken, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await woken.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the delay won – the semaphore was not taken
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsVisible(ModuleVisibility v) => v.IsBarVisible || v.IsAnythingVisible;

    /// <summary>Handles what is due: notifications, bus events, configured actions.</summary>
    internal async Task ProcessAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TimerEvent> events;
        lock (_lock)
        {
            events = _engine.Process(now, Today);
        }

        if (events.Count == 0)
        {
            return;
        }

        Save();
        foreach (var e in events)
        {
            switch (e)
            {
                case TimerFinished finished:
                    NotifyFinished(finished);
                    _context?.Bus.RaiseEvent("timer.finished", new JsonObject
                    {
                        ["name"] = finished.Timer.Name,
                        ["seconds"] = finished.Timer.Duration.TotalSeconds,
                        ["at"] = finished.At,
                    });
                    break;
                case PomodoroPhaseChanged change:
                    NotifyPhase(change);
                    await PhaseChangedAsync(change, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ notifications

    private UeNotification Notification(string title, string message, string icon, string key, bool withPlusFive)
    {
        var settings = Settings;
        var id = $"{key}:{Interlocked.Increment(ref _notificationCounter)}";
        return new UeNotification(title, message, icon, TimeSpan.FromSeconds(60))
        {
            Id = id,
            Actions = withPlusFive ? [new NotificationAction(PlusFive, T["Timer_PlusFive"])] : null,
            IgnoreDoNotDisturb = settings.IgnoreDoNotDisturb,
            ShowInFullscreen = settings.ShowInFullscreen,
            Sound = settings.Sound,
            SoundVolume = settings.Volume,
        };
    }

    private void NotifyFinished(TimerFinished finished)
    {
        var name = finished.Timer.Name ?? T["Timer_Default"];
        var message = finished.WhileAway
            ? T.Format("Timer_FinishedAt", LocalTime(finished.At))
            : T.Format("Timer_FinishedAfter", Duration(finished.Timer.Duration));
        var notification = Notification(name, message, "Clock", "timer", withPlusFive: true);
        lock (_lock)
        {
            _notified[notification.Id!] = (finished.Timer.Name, finished.Timer.Duration);
            if (_notified.Count > 50)
            {
                _notified.Remove(_notified.Keys.First());
            }
        }

        Log?.Info($"Timer finished ({Duration(finished.Timer.Duration)}{(finished.WhileAway ? ", while away" : "")})");
        NotificationReceived?.Invoke(this, notification);
    }

    private void NotifyPhase(PomodoroPhaseChanged change)
    {
        var settings = Settings;
        var (title, message) = change.Next switch
        {
            PomodoroPhase.Work => (T["Pomodoro_BackToWork"], T.Format("Pomodoro_WorkFor", Duration(settings.Pomodoro.Work), change.Round, settings.Pomodoro.RoundsBeforeLongBreak)),
            PomodoroPhase.LongBreak => (T["Pomodoro_LongBreak"], T.Format("Pomodoro_BreakFor", Duration(settings.Pomodoro.LongBreak))),
            _ => (T["Pomodoro_ShortBreak"], T.Format("Pomodoro_BreakFor", Duration(settings.Pomodoro.ShortBreak))),
        };
        if (change.WhileAway)
        {
            message = T.Format("Pomodoro_EndedAt", LocalTime(change.At)) + " " + message;
        }

        NotificationReceived?.Invoke(this, Notification(title, message, "U+E823", "pomodoro", withPlusFive: false));
    }

    private string LocalTime(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture);

    public Task HandleNotificationActionAsync(string notificationId, string actionId, CancellationToken cancellationToken)
    {
        if (actionId != PlusFive)
        {
            return Task.CompletedTask;
        }

        (string? Name, TimeSpan Duration) timer;
        lock (_lock)
        {
            if (!_notified.Remove(notificationId, out timer))
            {
                return Task.CompletedTask;
            }
        }

        Change(e => e.Start(TimeSpan.FromMinutes(5), timer.Name, Now));
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ visibility

    public void OnVisibilityChanged(ModuleVisibility visibility)
    {
        bool wake;
        lock (_lock)
        {
            wake = IsVisible(visibility) && !IsVisible(_visibility);
            _visibility = visibility;
        }

        if (wake)
        {
            Wake(); // show the current time right away, then every second
        }
    }

    internal bool IsTicking
    {
        get
        {
            lock (_lock)
            {
                return IsVisible(_visibility) && _engine.AnythingRunning;
            }
        }
    }

    // ------------------------------------------------------------------ views

    internal void Track(TileModel tile, TimerTileType type, TileConfig config)
    {
        lock (_lock)
        {
            _tiles.RemoveAll(t => !t.Tile.TryGetTarget(out _));
            _tiles.Add(new Tracked(new WeakReference<TileModel>(tile), type, config));
        }
    }

    internal void UpdateViews()
    {
        Interlocked.Increment(ref _ticks);
        List<(TileModel Tile, TimerTileType Type, TileConfig Config)> tiles = [];
        List<StatusValue> status;
        lock (_lock)
        {
            foreach (var tracked in _tiles)
            {
                if (tracked.Tile.TryGetTarget(out var tile))
                {
                    tiles.Add((tile, tracked.Type, tracked.Config));
                }
            }

            status = [.. _status.Values];
        }

        lock (ViewLock)
        {
            foreach (var (tile, type, config) in tiles)
            {
                try
                {
                    type.Refresh(tile, config);
                }
                catch (Exception ex)
                {
                    Log?.Warn($"Updating tile {config.Id} failed", ex);
                }
            }
        }

        foreach (var value in status)
        {
            ApplyStatus(value);
        }

        PillActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Tracked(WeakReference<TileModel> Tile, TimerTileType Type, TileConfig Config);

    /// <summary>The running countdown due first: a timer or the Pomodoro phase.</summary>
    internal (string Title, TimeSpan Remaining, DateTimeOffset Due, string Icon)? NextCountdown()
    {
        var now = Now;
        lock (_lock)
        {
            var timer = _engine.Timers.Where(t => t.IsRunning).OrderBy(t => t.EndsAt).FirstOrDefault();
            var pomodoro = _engine.Pomodoro;
            if (pomodoro.EndsAt is { } pe && (timer is null || pe < timer.EndsAt))
            {
                return (PhaseName(pomodoro.Phase), pomodoro.Remaining(now), pe, "U+E823");
            }

            return timer is null ? null : (timer.Name ?? T["Timer_Default"], timer.Remaining(now), timer.EndsAt!.Value, "Clock");
        }
    }

    internal static string PhaseName(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Work => T["Pomodoro_Work"],
        PomodoroPhase.ShortBreak => T["Pomodoro_ShortBreak"],
        PomodoroPhase.LongBreak => T["Pomodoro_LongBreak"],
        _ => T["Pomodoro_Idle"],
    };

    public PillActivity? CurrentPillActivity => NextCountdown() is { } next
        ? new PillActivity(ModuleId, next.Title, Clock(next.Remaining, roundUp: true), next.Icon) { Due = next.Due }
        : null;

    // ------------------------------------------------------------------ status line

    public IReadOnlyList<StatusValueInfo> AvailableStatusValues => [new(TimerIds.Next, T["Timer_NextStatus"], "Clock")];

    public StatusValue? GetStatusValue(string key)
    {
        if (!string.Equals(key, TimerIds.Next, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        StatusValue value;
        lock (_lock)
        {
            if (!_status.TryGetValue(key, out value!))
            {
                value = new StatusValue { Key = TimerIds.Next, Icon = "Clock" };
                _status[key] = value;
            }
        }

        ApplyStatus(value);
        return value;
    }

    private void ApplyStatus(StatusValue value)
    {
        var next = NextCountdown();
        value.Icon = next?.Icon ?? "Clock";
        value.Text = next is { } n ? Clock(n.Remaining, roundUp: true) : T["Timer_NoneRunning"];
        value.Tooltip = next?.Title;
    }

    // ------------------------------------------------------------------ diagnostics

    public IReadOnlyList<string> GetDiagnostics()
    {
        var now = Now;
        lock (_lock)
        {
            return
            [
                $"Timers: {_engine.Timers.Count} ({_engine.Timers.Count(t => t.IsRunning)} running), next due {(_engine.NextDue() is { } d ? Clock(d - now, roundUp: true) : "-")}",
                $"Stopwatch: {(_engine.Stopwatch.IsRunning ? "running" : "stopped")} {Clock(_engine.Stopwatch.Elapsed(now))}, {_engine.Stopwatch.Laps.Count} laps",
                $"Pomodoro: {_engine.Pomodoro.Phase}, round {_engine.Pomodoro.Round}, {_engine.CompletedToday(Today)} done today",
                $"Display ticks: {Ticks} ({(IsVisible(_visibility) ? "visible" : "collapsed – only due times wake the plugin")})",
            ];
        }
    }

    private void RaiseEntityChanged(EntityInfo info) => EntityChanged?.Invoke(this, info);
}
