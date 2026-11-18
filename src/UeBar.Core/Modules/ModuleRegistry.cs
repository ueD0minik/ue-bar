// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Sdk;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Settings;

namespace UeBar.Core.Modules;

/// <summary>A registered module with the state the core keeps for it.</summary>
public sealed class ModuleEntry
{
    internal ModuleEntry(IModule module, string id, string displayName, string icon)
    {
        Module = module;
        Id = id;
        DisplayName = displayName;
        Icon = icon;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Icon { get; }

    /// <summary>
    /// The module itself – only for capability checks (is/as). Calls into it always go through the ModuleGuard.
    /// </summary>
    public IModule Module { get; }

    public bool IsEnabled { get; internal set; } = true;

    /// <summary>
    /// false: only announced (<see cref="ModuleRegistry.RegisterUnavailable"/>) – the plugin behind it is
    /// off, not allowed or could not be loaded. It cannot be started; a change takes effect after a restart.
    /// </summary>
    public bool IsLoaded { get; internal init; } = true;

    /// <summary>Developer/test module (<see cref="IDeveloperModule"/>, M7).</summary>
    public bool IsDeveloper => Module is IDeveloperModule;

    public bool IsRunning { get; internal set; }

    /// <summary>Effective status (core view: Disabled/Error override the module's status).</summary>
    public ModuleStatus Status { get; internal set; } = ModuleStatus.Stopped;

    public string? LastError { get; internal set; }

    /// <summary>Automatic restart given up – waits for a manual "Restart".</summary>
    public bool AutoRestartGivenUp { get; internal set; }

    public IReadOnlyList<ITileType> TileTypes { get; internal set; } = [];

    /// <summary>Running and not faulted.</summary>
    public bool IsAvailable => IsEnabled && IsRunning && LastError is null;

    public T? GetCapability<T>()
        where T : class =>
        Module as T;

    internal ModuleContext? Context { get; set; }

    internal int Generation { get; set; }

    internal bool RestartPending { get; set; }

    internal ModuleStatus? LastModuleStatus { get; set; }

    /// <summary>Visibility last passed to the module (<see cref="IVisibilityAware"/>), as a comparison key.</summary>
    internal string? LastVisibilityKey { get; set; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed record TileTypeRegistration(ModuleEntry Module, ITileType Type);

/// <summary>
/// Knows all modules: register, start/stop, on/off, find tile types, create and
/// operate tiles. Every call into a module goes through the <see cref="ModuleGuard"/>;
/// errors set the module to "Error" and trigger an automatic restart with backoff.
/// </summary>
public sealed class ModuleRegistry : IAsyncDisposable
{
    private readonly ModuleGuard _guard;
    private readonly ModuleBus _bus;
    private readonly IUeLogger _log;
    private readonly IModuleStateStore _state;
    private readonly string _dataRoot;
    private readonly List<ModuleEntry> _modules = [];
    private readonly Dictionary<string, TileTypeRegistration> _tileTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private int _shutdownStarted;
    private readonly Lock _lock = new();
    private LayoutDocument? _lastLayout;
    private VisibilitySnapshot _lastVisibility = new(false, [], [], false);

    public ModuleRegistry(
        ModuleGuard guard,
        ModuleBus bus,
        IUeLogger log,
        IModuleStateStore? state = null,
        string? dataRoot = null)
    {
        _guard = guard;
        _bus = bus;
        _log = log;
        _state = state ?? new InMemoryModuleStateStore();
        _dataRoot = dataRoot ?? AppPaths.Default.Root;
        _guard.ModuleFaulted += OnModuleFaulted;
        _bus.IsModuleAvailable = id => Find(id)?.IsAvailable == true;
        Batteries = new DeviceBatteryHub(this);
    }

    /// <summary>Device batteries across the modules (P2-2), offered on every module context.</summary>
    internal DeviceBatteryHub Batteries { get; }

    /// <summary>Effective status of a module changed (may come from any thread).</summary>
    public event EventHandler<ModuleEntry>? ModuleStatusChanged;

    /// <summary>
    /// Developer modules (<see cref="IDeveloperModule"/>) without a saved setting start on
    /// (Debug) or off (Release, M7). Set before <see cref="Register"/>.
    /// </summary>
    public bool DeveloperModulesEnabledByDefault { get; set; } = true;

    /// <summary>Device name for the modules (<see cref="IHostInfo"/>); null = Windows computer name.</summary>
    public Func<string?>? DeviceName { get; set; }

    /// <summary>UI language for the modules (<see cref="IUiLanguageInfo"/>, R1).</summary>
    public UiCultureSource UiCulture { get; set; } = UiCultureSource.Shared;

    public IReadOnlyList<ModuleEntry> Modules
    {
        get
        {
            lock (_lock)
            {
                return [.. _modules];
            }
        }
    }

    public ModuleEntry? Find(string moduleId)
    {
        lock (_lock)
        {
            return _modules.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public TileTypeRegistration? FindTileType(string typeId)
    {
        lock (_lock)
        {
            return _tileTypes.TryGetValue(typeId, out var reg) ? reg : null;
        }
    }

    /// <summary>All running modules with a specific capability.</summary>
    public IReadOnlyList<(ModuleEntry Module, T Capability)> GetCapabilities<T>()
        where T : class =>
        [.. Modules.Where(m => m.IsAvailable && m.Module is T).Select(m => (m, (T)m.Module))];

    /// <summary>
    /// Registers a module. Returns null if the module already crashes while reading its basic
    /// data (it is then skipped, the app keeps running).
    /// </summary>
    public ModuleEntry? Register(IModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        string id;
        try
        {
            id = module.Id;
        }
        catch (Exception ex)
        {
            _log.Error(null, $"Module {module.GetType().Name} provides no ID – skipped", ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            _log.Error(null, $"Module {module.GetType().Name} has an empty ID – skipped");
            return null;
        }

        if (Find(id) is not null)
        {
            throw new InvalidOperationException($"Module ID '{id}' is already registered.");
        }

        var name = _guard.Invoke(id, "DisplayName", () => module.DisplayName, reportFaults: false).Value ?? id;
        var icon = _guard.Invoke(id, "Icon", () => module.Icon, reportFaults: false).Value ?? "Puzzle";
        var entry = new ModuleEntry(module, id, name, icon)
        {
            IsEnabled = (_state as IModuleEnabledLookup)?.GetModuleEnabled(id)
                ?? (module is IDeveloperModule ? DeveloperModulesEnabledByDefault : _state.IsModuleEnabled(id)),
        };
        entry.Status = entry.IsEnabled ? ModuleStatus.Stopped : ModuleStatus.Disabled;

        var types = _guard.Invoke(id, "TileTypes", () => module.TileTypes, reportFaults: false);
        if (!types.Succeeded)
        {
            entry.LastError = CoreText.T.Format("Module_TileTypesUnreadable", types.Exception?.Message);
            entry.Status = ModuleStatus.Error(entry.LastError);
        }

        var accepted = new List<ITileType>();
        lock (_lock)
        {
            foreach (var type in types.Value ?? [])
            {
                var typeId = _guard.Invoke(id, "TileType.Id", () => type.Id, reportFaults: false).Value;
                if (string.IsNullOrWhiteSpace(typeId))
                {
                    _log.Warn(id, "Tile type without ID is ignored");
                    continue;
                }

                if (_tileTypes.TryGetValue(typeId, out var other))
                {
                    _log.Warn(id, $"Tile type '{typeId}' already exists in module '{other.Module.Id}' – ignored");
                    continue;
                }

                _tileTypes[typeId] = new TileTypeRegistration(entry, type);
                accepted.Add(type);
            }

            entry.TileTypes = accepted;
            _modules.Add(entry);
        }

        _guard.Invoke(id, "Subscribe to StatusChanged", () => module.StatusChanged += (_, status) => OnModuleStatus(entry, status), reportFaults: false);
        _log.Info(id, $"Module registered: {name} ({accepted.Count} tile types, {(entry.IsEnabled ? "on" : "off")})");
        return entry;
    }

    /// <summary>
    /// Announces a module that is not loaded (plugin off, not allowed, incompatible or broken), so that
    /// its tiles show the fitting placeholder instead of "module missing" and it appears in lists
    /// (SPEC 4.5). It has no tile types and cannot be started.
    /// </summary>
    public ModuleEntry? RegisterUnavailable(string id, string displayName, ModuleStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(status);
        if (Find(id) is not null)
        {
            return null;
        }

        var isError = status.State == ModuleState.Error;
        var entry = new ModuleEntry(new UnloadedModule(id, displayName, status), id, displayName, "Puzzle")
        {
            IsLoaded = false,
            IsEnabled = isError,
            Status = status,
            LastError = isError ? status.Text ?? CoreText.T["ModuleState_Error"] : null,
            AutoRestartGivenUp = isError,
        };
        lock (_lock)
        {
            _modules.Add(entry);
        }

        _log.Info(id, $"Module not loaded: {status.State}{(status.Text is null ? "" : " – " + status.Text)}");
        return entry;
    }

    public Task StartAllAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Modules.Where(m => m.IsEnabled).Select(m => StartModuleAsync(m.Id, cancellationToken)));

    public Task StopAllAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Modules.Select(m => StopModuleAsync(m.Id, cancellationToken)));

    public async Task<bool> StartModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var entry = Find(moduleId);
        if (entry is null)
        {
            return false;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task StopModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var entry = Find(moduleId);
        if (entry is null)
        {
            return;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Generation++;
            entry.RestartPending = false;
            await StopCoreAsync(entry).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Manual "Restart" (tray/settings): resets error and restart counter.</summary>
    public async Task<bool> RestartModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var entry = Find(moduleId);
        if (entry is null)
        {
            return false;
        }

        if (!entry.IsLoaded)
        {
            return false;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _log.Info(entry.Id, "Manual restart");
            _guard.ResetRestarts(entry.Id);
            entry.Generation++;
            entry.RestartPending = false;
            entry.AutoRestartGivenUp = false;
            await StopCoreAsync(entry).ConfigureAwait(false);
            entry.LastError = null;
            return await StartCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Switch a module on/off (saved). Off = not started, tiles show "Module disabled".</summary>
    public async Task SetEnabledAsync(string moduleId, bool enabled, CancellationToken cancellationToken = default)
    {
        var entry = Find(moduleId);
        if (entry is null)
        {
            return;
        }

        _state.SetModuleEnabled(entry.Id, enabled);
        if (!entry.IsLoaded)
        {
            return; // saved; takes effect when the plugin is loaded on the next start
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Generation++;
            entry.RestartPending = false;
            if (!enabled)
            {
                entry.IsEnabled = false;
                await StopCoreAsync(entry).ConfigureAwait(false);
                SetStatus(entry, ModuleStatus.Disabled);
            }
            else
            {
                entry.IsEnabled = true;
                entry.LastError = null;
                entry.AutoRestartGivenUp = false;
                _guard.ResetRestarts(entry.Id);
                await StartCoreAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Creates the model for a tile from the layout. If the module is missing, off or
    /// faulted, a placeholder comes back – the tile configuration stays unchanged.
    /// </summary>
    public async Task<TileModel> CreateTileAsync(TileConfig config, CancellationToken cancellationToken = default)
    {
        var entry = Find(config.Module);
        if (entry is null)
        {
            return TileModel.CreatePlaceholder(config, TilePlaceholder.ModuleMissing);
        }

        if (!entry.IsEnabled)
        {
            return TileModel.CreatePlaceholder(config, TilePlaceholder.ModuleDisabled);
        }

        if (entry.LastError is not null)
        {
            return TileModel.CreatePlaceholder(config, TilePlaceholder.ModuleError);
        }

        var registration = FindTileType(config.Type);
        if (registration is null || !ReferenceEquals(registration.Module, entry))
        {
            return TileModel.CreatePlaceholder(config, TilePlaceholder.UnknownType);
        }

        var result = await _guard.RunAsync(
            entry.Id,
            $"Create tile ({config.Type}, {config.Id})",
            _ => Task.FromResult(registration.Type.CreateTile(config)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.Value is null)
        {
            return TileModel.CreatePlaceholder(config, TilePlaceholder.ModuleError);
        }

        var tile = result.Value;
        tile.TileId = config.Id;
        tile.ModuleId = entry.Id;
        tile.TypeId = config.Type;
        if (!string.IsNullOrWhiteSpace(config.Label))
        {
            tile.Title = config.Label;
        }

        if (!string.IsNullOrWhiteSpace(config.Icon))
        {
            tile.Icon = config.Icon;
        }

        return tile;
    }

    /// <summary>Forwards an interaction to the module – inside the fault protection, with a time limit.</summary>
    public Task<GuardResult> InteractAsync(TileModel tile, TileInteraction interaction, CancellationToken cancellationToken = default)
    {
        if (tile.IsPlaceholder)
        {
            return Task.FromResult(GuardResult.Reject("Placeholder tile"));
        }

        var entry = Find(tile.ModuleId);
        if (entry is null || !entry.IsAvailable)
        {
            return Task.FromResult(GuardResult.Reject("Module not available"));
        }

        if (tile.Handler is not { } handler)
        {
            return Task.FromResult(GuardResult.Reject("Tile has no interaction"));
        }

        return _guard.RunAsync(
            entry.Id,
            $"Interaction {tile.TypeId}/{interaction.ElementId} ({interaction.Kind})",
            ct => handler.HandleAsync(tile, interaction, ct),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Layout changed: every module with <see cref="ILayoutAware"/> gets its entries.
    /// Remembered and passed again on every (re)start.
    /// </summary>
    public async Task NotifyLayoutAsync(LayoutDocument layout, CancellationToken cancellationToken = default)
    {
        _lastLayout = layout;
        foreach (var entry in Modules.Where(m => m.IsEnabled && m.Module is ILayoutAware))
        {
            await PassLayoutAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What is on screen (P2-2): panel open, the tiles of the current tab, the visible status values.
    /// Every module with <see cref="IVisibilityAware"/> gets its part – only when it changed for that
    /// module. Remembered and passed again on every (re)start.
    /// </summary>
    public async Task NotifyVisibilityAsync(bool panelOpen, IReadOnlyList<TileConfig> visibleTiles, IReadOnlyList<StatusItemConfig> visibleStatus, CancellationToken cancellationToken = default, bool? barVisible = null)
    {
        _lastVisibility = new VisibilitySnapshot(panelOpen, [.. visibleTiles], [.. visibleStatus], barVisible ?? (panelOpen || visibleStatus.Count > 0));
        foreach (var entry in Modules.Where(m => m.IsEnabled && m.Module is IVisibilityAware))
        {
            await PassVisibilityAsync(entry, force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous call into a module (only for quick things like diagnostic queries), inside the fault protection.</summary>
    public GuardResult<T> Invoke<T>(string moduleId, string operation, Func<IModule, T> call)
    {
        var entry = Find(moduleId);
        return entry is null
            ? new GuardResult<T>(GuardOutcome.Rejected, default)
            : _guard.Invoke(entry.Id, operation, () => call(entry.Module));
    }

    /// <summary>Asynchronous call into a module (e.g. test settings), inside the fault protection with a time limit.</summary>
    public Task<GuardResult<T>> InvokeAsync<T>(
        string moduleId,
        string operation,
        Func<IModule, CancellationToken, Task<T>> call,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(moduleId);
        return entry is null
            ? Task.FromResult(new GuardResult<T>(GuardOutcome.Rejected, default))
            : _guard.RunAsync(entry.Id, operation, ct => call(entry.Module, ct), timeout, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Like <see cref="InvokeAsync{T}(string, string, Func{IModule, CancellationToken, Task{T}}, TimeSpan?, CancellationToken)"/>,
    /// but expected errors (e.g. "offline right now") do not set the module to "Error" if
    /// <paramref name="reportFaults"/> is false – they are only logged.
    /// </summary>
    public Task<GuardResult<T>> InvokeAsync<T>(
        string moduleId,
        string operation,
        Func<IModule, CancellationToken, Task<T>> call,
        TimeSpan? timeout,
        bool reportFaults,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(moduleId);
        return entry is null
            ? Task.FromResult(new GuardResult<T>(GuardOutcome.Rejected, default))
            : _guard.RunAsync(entry.Id, operation, ct => call(entry.Module, ct), timeout, reportFaults, cancellationToken);
    }

    /// <summary>Synchronous call without reporting faults to the module (e.g. subscribe/unsubscribe events).</summary>
    public GuardResult<T> Invoke<T>(string moduleId, string operation, Func<IModule, T> call, bool reportFaults)
    {
        var entry = Find(moduleId);
        return entry is null
            ? new GuardResult<T>(GuardOutcome.Rejected, default)
            : _guard.Invoke(entry.Id, operation, () => call(entry.Module), reportFaults);
    }

    /// <summary>Observable status value for the status line (or null if the module/value is missing).</summary>
    public async Task<StatusValue?> GetStatusValueAsync(StatusItemConfig item, CancellationToken cancellationToken = default)
    {
        var entry = Find(item.Module);
        if (entry is not { IsAvailable: true } || entry.Module is not IStatusProvider provider)
        {
            return null;
        }

        var result = await _guard.RunAsync(
            entry.Id,
            $"Status value {item.Entity}",
            _ => Task.FromResult(provider.GetStatusValue(item.Entity)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Value : null;
    }

    /// <summary>Suggested layouts of all running modules (SPEC 9).</summary>
    public async Task<IReadOnlyList<LayoutDocument>> GetSuggestedLayoutsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<LayoutDocument>();
        foreach (var (module, provider) in GetCapabilities<ILayoutSuggestionProvider>())
        {
            var result = await _guard.RunAsync(
                module.Id,
                "Vorschlags-Layout",
                _ => Task.FromResult(provider.GetSuggestedLayout()),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result is { Succeeded: true, Value: { } layout })
            {
                list.Add(layout);
            }
        }

        return list;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(_guard.Options.LifecycleTimeout).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    /// <summary>
    /// Exit (P2-1-Fix 1): stops all modules at the same time, each at most <paramref name="perModule"/>
    /// (waiting for a running start/stop included). A module that does not make it is left behind
    /// with its token cancelled – the exit goes on. Logs how long each module took.
    /// </summary>
    public async Task<IReadOnlyList<ShutdownStepResult>> ShutdownAsync(TimeSpan perModule)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return [];
        }

        _guard.ModuleFaulted -= OnModuleFaulted;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var results = await Task.WhenAll(Modules.Where(m => m.IsLoaded).Select(m => ShutdownModuleAsync(m, perModule))).ConfigureAwait(false);
        return results;
    }

    private async Task<ShutdownStepResult> ShutdownModuleAsync(ModuleEntry entry, TimeSpan limit)
    {
        var start = _guard.Time.GetTimestamp();
        var outcome = ShutdownStepOutcome.Done;
        using var waitCts = new CancellationTokenSource(limit, _guard.Time);
        var gated = false;
        try
        {
            gated = await entry.Gate.WaitAsync(Timeout.InfiniteTimeSpan, waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            if (!gated)
            {
                // A start or restart still holds the module – cut it off instead of waiting for it.
                entry.Context?.Cancel();
                outcome = ShutdownStepOutcome.TimedOut;
                _log.Warn(entry.Id, $"Exit: still busy after {ShutdownSequence.Ms(limit)} – left behind");
            }
            else
            {
                entry.Generation++;
                entry.RestartPending = false;
                var remaining = limit - _guard.Time.GetElapsedTime(start);
                if (!await StopCoreAsync(entry, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1)).ConfigureAwait(false))
                {
                    outcome = ShutdownStepOutcome.TimedOut;
                    _log.Warn(entry.Id, $"Exit: did not stop within {ShutdownSequence.Ms(limit)} – left behind");
                }
            }
        }
        catch (Exception ex)
        {
            outcome = ShutdownStepOutcome.Failed;
            _log.Warn(entry.Id, "Exit: stopping failed", ex);
        }
        finally
        {
            if (gated)
            {
                entry.Gate.Release();
            }
        }

        var duration = _guard.Time.GetElapsedTime(start);
        _log.Debug(entry.Id, $"Exit: stopped in {ShutdownSequence.Ms(duration)}");
        return new ShutdownStepResult(entry.Id, duration, outcome);
    }

    // ------------------------------------------------------------------ internal

    private async Task<bool> StartCoreAsync(ModuleEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.IsLoaded)
        {
            return false;
        }

        if (!entry.IsEnabled)
        {
            SetStatus(entry, ModuleStatus.Disabled);
            return false;
        }

        if (entry.IsRunning)
        {
            return true;
        }

        var generation = entry.Generation;
        entry.LastModuleStatus = null;
        SetStatus(entry, ModuleStatus.Starting);

        if (entry.Module is IModuleSettings settings)
        {
            var values = _state.GetModuleSettings(entry.Id);
            var applied = await _guard.RunAsync(
                entry.Id,
                "Apply settings",
                ct => settings.ApplySettingsAsync(values, ct),
                _guard.Options.LifecycleTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!applied.Succeeded)
            {
                HandleStartProblem(entry, applied, generation);
                return false;
            }
        }

        if (entry.Module is ILayoutAware)
        {
            await PassLayoutAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        if (entry.Module is IVisibilityAware)
        {
            await PassVisibilityAsync(entry, force: true, cancellationToken).ConfigureAwait(false);
        }

        entry.Context?.Dispose();
        var context = new ModuleContext(entry.Id, _bus.For(entry.Id), _log.ForModule(entry.Id), _guard, _dataRoot, _state as IModuleSecretStore, () => DeviceName?.Invoke() ?? "", UiCulture, Batteries);
        entry.Context = context;

        var started = await _guard.RunAsync(
            entry.Id,
            "Start",
            ct => entry.Module.StartAsync(context, ct),
            _guard.Options.LifecycleTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!started.Succeeded)
        {
            HandleStartProblem(entry, started, generation);
            return false;
        }

        // The error may have come from a background task during the start.
        if (entry.LastError is not null)
        {
            return false;
        }

        entry.IsRunning = true;
        var status = entry.LastModuleStatus
            ?? _guard.Invoke(entry.Id, "Status", () => entry.Module.Status).Value
            ?? ModuleStatus.Connected;
        SetStatus(entry, status);
        _log.Info(entry.Id, $"Started: {status.State}");
        return true;
    }

    private Task PassLayoutAsync(ModuleEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Module is not ILayoutAware aware)
        {
            return Task.CompletedTask;
        }

        var layout = _lastLayout;
        var info = layout is null
            ? ModuleLayoutInfo.Empty
            : new ModuleLayoutInfo(
                [.. layout.AllTiles.Where(t => string.Equals(t.Module, entry.Id, StringComparison.OrdinalIgnoreCase))],
                [.. layout.Status.Where(s => string.Equals(s.Module, entry.Id, StringComparison.OrdinalIgnoreCase))]);
        return _guard.RunAsync(entry.Id, "Pass layout", _ =>
        {
            aware.OnLayoutChanged(info);
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);
    }

    private Task PassVisibilityAsync(ModuleEntry entry, bool force, CancellationToken cancellationToken)
    {
        if (entry.Module is not IVisibilityAware aware)
        {
            return Task.CompletedTask;
        }

        var snapshot = _lastVisibility;
        var tiles = snapshot.Tiles.Where(t => string.Equals(t.Module, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var status = snapshot.Status.Where(s => string.Equals(s.Module, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var info = tiles.Count == 0 && status.Count == 0 && !snapshot.PanelOpen && !snapshot.BarVisible
            ? ModuleVisibility.Hidden
            : new ModuleVisibility(snapshot.PanelOpen, tiles, status) { IsBarVisible = snapshot.BarVisible };
        var key = $"{info.IsPanelOpen}|{info.IsBarVisible}|{string.Join(";", tiles.Select(t => $"{t.Id}:{t.Type}:{t.Size}:{string.Join(",", t.Entities.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value))}"))}|{string.Join(";", status.Select(s => s.Entity))}";
        lock (_lock)
        {
            if (!force && entry.LastVisibilityKey == key)
            {
                return Task.CompletedTask;
            }

            entry.LastVisibilityKey = key;
        }

        return _guard.RunAsync(entry.Id, "Pass visibility", _ =>
        {
            aware.OnVisibilityChanged(info);
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);
    }

    private sealed record VisibilitySnapshot(bool PanelOpen, IReadOnlyList<TileConfig> Tiles, IReadOnlyList<StatusItemConfig> Status, bool BarVisible);

    private void HandleStartProblem(ModuleEntry entry, GuardResult result, int generation)
    {
        if (result.Outcome == GuardOutcome.TimedOut)
        {
            // A time limit during start counts as an error.
            _guard.ReportFault(entry.Id, "Start", new TimeoutException("Start takes too long"));
        }
        else if (result.Outcome == GuardOutcome.Canceled && entry.Generation == generation)
        {
            SetStatus(entry, ModuleStatus.Stopped);
        }

        // Faulted: the fault handler has already taken care of status and restart.
    }

    /// <summary>Returns false if the module's StopAsync ran into the time limit.</summary>
    private async Task<bool> StopCoreAsync(ModuleEntry entry, TimeSpan? limit = null)
    {
        if (!entry.IsLoaded)
        {
            return true;
        }

        var stopped = true;
        var context = entry.Context;
        if (context is not null)
        {
            context.Cancel();
            var result = await _guard.RunAsync(
                entry.Id,
                "Stop",
                ct => entry.Module.StopAsync(ct),
                limit ?? _guard.Options.LifecycleTimeout,
                reportFaults: false).ConfigureAwait(false);
            stopped = result.Outcome != GuardOutcome.TimedOut;
            context.Dispose();
            entry.Context = null;
        }

        _bus.RemoveModule(entry.Id);
        var wasRunning = entry.IsRunning;
        entry.IsRunning = false;

        if (!entry.IsEnabled)
        {
            SetStatus(entry, ModuleStatus.Disabled);
        }
        else if (entry.LastError is null)
        {
            SetStatus(entry, ModuleStatus.Stopped);
        }

        if (wasRunning)
        {
            _log.Info(entry.Id, "Stopped");
        }

        return stopped;
    }

    private void OnModuleStatus(ModuleEntry entry, ModuleStatus? status)
    {
        try
        {
            if (status is null)
            {
                return;
            }

            entry.LastModuleStatus = status;
            if (entry.IsRunning && entry.IsEnabled && entry.LastError is null)
            {
                SetStatus(entry, status);
            }
        }
        catch (Exception ex)
        {
            _log.Error(entry.Id, "Error processing the module status", ex);
        }
    }

    private void OnModuleFaulted(object? sender, ModuleFaultEventArgs e)
    {
        var entry = Find(e.ModuleId);
        if (entry is null || !entry.IsEnabled || !entry.IsLoaded)
        {
            return;
        }

        int generation;
        lock (entry)
        {
            if (entry.RestartPending || entry.AutoRestartGivenUp)
            {
                // Already faulted – only logged (done by the guard).
                return;
            }

            entry.LastError = e.Exception.Message;
            entry.RestartPending = true;
            generation = entry.Generation;
        }

        // Stop the module's background tasks.
        entry.Context?.Cancel();

        var delay = _guard.TryAcquireRestart(entry.Id);
        if (delay is null)
        {
            lock (entry)
            {
                entry.RestartPending = false;
                entry.AutoRestartGivenUp = true;
            }

            var text = CoreText.T.Format("Module_ErrorRestartStopped", e.Exception.Message);
            _log.Error(entry.Id, $"Restart limit reached ({_guard.Options.MaxRestarts} in {_guard.Options.RestartWindow.TotalMinutes:0} min). Waiting for a manual restart.");
            SetStatus(entry, ModuleStatus.Error(text));
            return;
        }

        SetStatus(entry, ModuleStatus.Error(CoreText.T.Format("Module_ErrorRestartIn", e.Exception.Message, delay.Value.TotalSeconds)));
        _ = AutoRestartAsync(entry, delay.Value, generation);
    }

    private async Task AutoRestartAsync(ModuleEntry entry, TimeSpan delay, int generation)
    {
        try
        {
            await Task.Delay(delay, _guard.Time, _shutdown.Token).ConfigureAwait(false);
            await entry.Gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (entry.Generation != generation || !entry.IsEnabled)
            {
                return; // manual intervention in the meantime
            }

            _log.Info(entry.Id, $"Automatic restart (attempt {_guard.GetRestartCount(entry.Id)}/{_guard.Options.MaxRestarts})");
            await StopCoreAsync(entry).ConfigureAwait(false);
            lock (entry)
            {
                entry.RestartPending = false;
                entry.LastError = null;
            }

            await StartCoreAsync(entry, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(entry.Id, "Automatic restart failed", ex);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void SetStatus(ModuleEntry entry, ModuleStatus status)
    {
        if (Equals(entry.Status, status))
        {
            return;
        }

        entry.Status = status;
        var handlers = ModuleStatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ModuleEntry>>())
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception ex)
            {
                _log.Error(null, "Error in ModuleStatusChanged handler", ex);
            }
        }
    }
}

/// <summary>Stand-in for a module whose plugin is not loaded. Never started.</summary>
internal sealed class UnloadedModule(string id, string displayName, ModuleStatus status) : IModule
{
    public string Id => id;

    public string DisplayName => displayName;

    public string Icon => "Puzzle";

    public ModuleStatus Status => status;

    public event EventHandler<ModuleStatus>? StatusChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<ITileType> TileTypes => [];

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("The plugin of this module is not loaded."));

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
