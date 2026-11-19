// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UeBar.Modules.QuickAccess.Platform;
using UeBar.Sdk;
using static UeBar.Modules.QuickAccess.QuickText;

namespace UeBar.Modules.QuickAccess;

/// <summary>A target ready to start: what it is, the expanded path/address/app ID, its name and whether it exists here.</summary>
internal sealed record ResolvedTarget(EntryKind Kind, string Value, bool Admin, string Name, bool Exists, string? IconSource);

/// <summary>
/// Plugin "quickaccess" (P2-4): apps from the Start menu, files (Office templates → new document),
/// folders, web pages and own commands – as tiles (one or up to six in one tile), in the quick search,
/// by drag and drop and over the module bus (<c>quickaccess.open</c>). Everything a tile needs is in its
/// layout entry (paths as <c>%USERPROFILE%\…</c>), so a synced layout also works on another PC; own
/// commands stay on this device (settings) and tiles only point to them.
/// </summary>
public sealed partial class QuickAccessModule :
    IModule,
    IEntitySource,
    ITileSlotAssistant,
    ILiveModuleSettings,
    IDropHandler,
    IImageSource,
    IVisibilityAware,
    IModuleDiagnostics
{
    public const string ModuleId = "quickaccess";
    internal const string GroupEntity = "group";
    internal const string IconPrefix = "icon:";
    internal const int IconSize = 48;
    private const int MaxCachedIcons = 128;
    private static readonly TimeSpan AppsMaxAge = TimeSpan.FromMinutes(10);

    private readonly IQuickShell _shell;
    private readonly string _userProfile;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<ITileType> _tileTypes;
    private readonly List<Tracked> _tiles = [];
    private readonly Dictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _iconOrder = new();
    private readonly SemaphoreSlim _appsGate = new(1, 1);
    private IReadOnlyList<QuickEntry> _entries = [];
    private IReadOnlyList<AppInfo>? _apps;
    private DateTimeOffset _appsLoadedAt;
    private IModuleContext? _context;
    private bool _panelOpen;

    /// <summary>Constructor the plugin loader uses: the real Windows shell.</summary>
    public QuickAccessModule()
        : this(new WindowsShell(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), TimeProvider.System)
    {
    }

    /// <summary>For tests: own shell (fake), user folder and clock.</summary>
    internal QuickAccessModule(IQuickShell shell, string userProfile, TimeProvider time)
    {
        _shell = shell;
        _userProfile = userProfile;
        _time = time;
        _tileTypes = [new ItemTileType(this), new GroupTileType(this)];
    }

    /// <summary>Serializes changes to the tiles (a new tile and a refresh round can come at the same time).</summary>
    internal readonly Lock ViewLock = new();

    public string Id => ModuleId;

    public string DisplayName => T["Qa_ModuleName"];

    public string Icon => "U+E8A9";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public IReadOnlyList<SettingField> SettingFields => QuickSettings.Fields;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<EntityInfo>? EntityChanged { add { } remove { } }

    internal IModuleLogger? Log => _context?.Log;

    internal IReadOnlyList<QuickEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries;
            }
        }
    }

    internal string UserProfile => _userProfile;

    // ------------------------------------------------------------------ lifecycle

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (context is IUiLanguageInfo language)
        {
            Culture = language.UiCulture;
            language.UiCultureChanged += OnUiCultureChanged;
        }

        PublishOnBus(context.Bus);
        SetStatus(ModuleStatus.Connected);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_context is IUiLanguageInfo language)
        {
            language.UiCultureChanged -= OnUiCultureChanged;
        }

        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    private void OnUiCultureChanged(object? sender, CultureInfo culture)
    {
        Culture = culture;
        UpdateViews();
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        var entries = QuickSettings.Entries(values);
        lock (_lock)
        {
            _entries = entries;
        }

        UpdateViews(); // names of tiles that point to entries (live, no restart)
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetDiagnostics()
    {
        lock (_lock)
        {
            return [$"Entries: {_entries.Count}, apps known: {(_apps is null ? "not read yet" : _apps.Count.ToString(CultureInfo.InvariantCulture))}, icons cached: {_icons.Count}"];
        }
    }

    // ------------------------------------------------------------------ apps of the Start menu

    /// <summary>The Start menu apps, read on first need and again after 10 minutes (never on a timer).</summary>
    internal async Task<IReadOnlyList<AppInfo>> AppsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_apps is { } fresh && _time.GetUtcNow() - _appsLoadedAt < AppsMaxAge)
            {
                return fresh;
            }
        }

        await _appsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_apps is { } fresh && _time.GetUtcNow() - _appsLoadedAt < AppsMaxAge)
                {
                    return fresh;
                }
            }

            var apps = await _shell.GetAppsAsync(cancellationToken).ConfigureAwait(false);
            bool first;
            lock (_lock)
            {
                first = _apps is null;
                _apps = [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
                _appsLoadedAt = _time.GetUtcNow();
            }

            Log?.Log(LogLevel.Info, $"{apps.Count} apps of the Start menu read");
            if (first)
            {
                UpdateViews(); // app tiles get their names and "not found"
            }

            return apps;
        }
        finally
        {
            _appsGate.Release();
        }
    }

    /// <summary>The apps as known right now (null = not read yet) – never waits.</summary>
    internal IReadOnlyList<AppInfo>? KnownApps
    {
        get
        {
            lock (_lock)
            {
                return _apps;
            }
        }
    }

    /// <summary>Read the apps in the background once (a tile with an app needs its name).</summary>
    internal void EnsureAppsLoading()
    {
        if (KnownApps is not null || _context is not { } context)
        {
            return;
        }

        context.RunBackground("Read Start menu apps", async ct => await AppsAsync(ct).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ targets

    internal QuickEntry? FindEntry(string id)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>Everything needed to show and start a target on this PC (null = it cannot be understood).</summary>
    internal ResolvedTarget? Resolve(QuickTarget target)
    {
        switch (target.Kind)
        {
            case TargetKind.Entry:
                return FindEntry(target.Value) is { } entry ? Resolve(entry) : null;

            case TargetKind.App:
            {
                var apps = KnownApps;
                var app = apps?.FirstOrDefault(a => string.Equals(a.Id, target.Value, StringComparison.OrdinalIgnoreCase));
                return new ResolvedTarget(EntryKind.App, target.Value, target.Admin, app?.Name ?? QuickNames.FromValue(EntryKind.App, target.Value),
                    apps is null || app is not null, AppsFolder(target.Value));
            }

            case TargetKind.File:
            {
                var path = QuickPaths.Expand(target.Value, _userProfile);
                return new ResolvedTarget(EntryKind.File, path, false, QuickNames.FromValue(EntryKind.File, path), _shell.FileExists(path), path);
            }

            case TargetKind.Folder:
            {
                var path = QuickPaths.Expand(target.Value, _userProfile);
                return new ResolvedTarget(EntryKind.Folder, path, false, QuickNames.FromValue(EntryKind.Folder, path), _shell.FolderExists(path), path);
            }

            default:
                return new ResolvedTarget(EntryKind.Url, target.Value, false, QuickNames.FromValue(EntryKind.Url, target.Value), true, null);
        }
    }

    private ResolvedTarget Resolve(QuickEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.Command:
            {
                var (program, _) = QuickNames.SplitCommand(entry.Value);
                var expanded = QuickPaths.Expand(program, _userProfile);
                var isPath = Path.IsPathRooted(expanded);
                return new ResolvedTarget(EntryKind.Command, entry.Value, entry.Admin, entry.Name, !isPath || _shell.FileExists(expanded), isPath ? expanded : null);
            }

            case EntryKind.App:
            {
                // The entry may name the app instead of its ID.
                var app = KnownApps?.FirstOrDefault(a => string.Equals(a.Id, entry.Value, StringComparison.OrdinalIgnoreCase))
                    ?? KnownApps?.FirstOrDefault(a => string.Equals(a.Name, entry.Value, StringComparison.CurrentCultureIgnoreCase));
                var resolved = Resolve(new QuickTarget(TargetKind.App, app?.Id ?? entry.Value, entry.Admin))!;
                return resolved with { Name = entry.Name };
            }

            default:
                return Resolve(entry.TileTarget(_userProfile))! with { Name = entry.Name };
        }
    }

    private static string AppsFolder(string appId) => @"shell:AppsFolder\" + appId;

    /// <summary>
    /// What a tile made from <paramref name="entry"/> stores (<see cref="QuickEntry.TileTarget"/>); an app entry
    /// may name the app as in the Start menu – the tile gets its ID.
    /// </summary>
    internal QuickTarget TileTargetOf(QuickEntry entry)
    {
        var target = entry.TileTarget(_userProfile);
        if (target.Kind == TargetKind.App
            && KnownApps?.FirstOrDefault(a => string.Equals(a.Name, entry.Value, StringComparison.CurrentCultureIgnoreCase)) is { } app
            && !string.Equals(app.Id, entry.Value, StringComparison.OrdinalIgnoreCase))
        {
            return target with { Value = app.Id };
        }

        return target;
    }

    /// <summary>Start a target. Returns the message for the user (null = started).</summary>
    internal string? Launch(QuickTarget target)
    {
        if (Resolve(target) is not { } resolved)
        {
            return T["Qa_NotFound"];
        }

        if (!resolved.Exists)
        {
            Log?.Log(LogLevel.Info, $"Not started – target missing ({resolved.Kind})");
            return T["Qa_NotFound"];
        }

        var request = resolved.Kind switch
        {
            EntryKind.App => new LaunchRequest(AppsFolder(resolved.Value), Verb: resolved.Admin ? "runas" : null),
            EntryKind.File when QuickTarget.TemplateExtensions.Contains(Path.GetExtension(resolved.Value)) => new LaunchRequest(resolved.Value, Verb: "new"),
            EntryKind.Command => CommandRequest(resolved),
            _ => new LaunchRequest(resolved.Value),
        };

        var result = _shell.Launch(request);
        if (!result.Success && result.NoSuchVerb && request.Verb == "new")
        {
            // A template whose app has no "new" (e.g. no Office): open it instead of failing.
            result = _shell.Launch(request with { Verb = null });
        }

        if (result.Success)
        {
            Log?.Log(LogLevel.Info, $"Started: {resolved.Kind}{(request.Verb is { } verb ? $" ({verb})" : "")}");
            return null;
        }

        if (result.Cancelled)
        {
            Log?.Log(LogLevel.Info, $"Start cancelled by the user ({resolved.Kind})");
            return T["Qa_Cancelled"];
        }

        Log?.Log(LogLevel.Warning, $"Start failed ({resolved.Kind}): {result.Error}");
        return T.Format("Qa_StartFailed", result.Error ?? "?");
    }

    private LaunchRequest CommandRequest(ResolvedTarget command)
    {
        var verb = command.Admin ? "runas" : null;
        var (program, arguments) = QuickNames.SplitCommand(command.Value);
        var expanded = QuickPaths.Expand(program, _userProfile);
        if (string.Equals(Path.GetExtension(expanded), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            // PowerShell scripts run in PowerShell (Windows would open them in Notepad); the execution
            // policy of this PC applies, nothing is bypassed.
            var args = $"-NoProfile -File \"{expanded}\"" + (arguments is null ? "" : " " + arguments);
            return new LaunchRequest("powershell.exe", args, verb);
        }

        return new LaunchRequest(expanded, arguments, verb);
    }

    // ------------------------------------------------------------------ tiles and visibility

    private sealed record Tracked(WeakReference<TileModel> Tile, QuickTileType Type, TileConfig Config);

    internal void Track(TileModel tile, QuickTileType type, TileConfig config)
    {
        lock (_lock)
        {
            _tiles.RemoveAll(t => !t.Tile.TryGetTarget(out _));
            _tiles.Add(new Tracked(new WeakReference<TileModel>(tile), type, config));
        }
    }

    /// <summary>Refresh all tiles (names, "not found") – on start, settings, apps read, panel opened.</summary>
    internal void UpdateViews()
    {
        List<(TileModel Tile, QuickTileType Type, TileConfig Config)> tiles = [];
        lock (_lock)
        {
            foreach (var tracked in _tiles)
            {
                if (tracked.Tile.TryGetTarget(out var tile))
                {
                    tiles.Add((tile, tracked.Type, tracked.Config));
                }
            }
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
                    Log?.Log(LogLevel.Warning, $"Updating tile {config.Id} failed: {ex.Message}");
                }
            }
        }
    }

    public void OnVisibilityChanged(ModuleVisibility visibility)
    {
        bool opened;
        lock (_lock)
        {
            opened = visibility.IsPanelOpen && !_panelOpen;
            _panelOpen = visibility.IsPanelOpen;
        }

        if (opened && _context is { } context)
        {
            // A file may have been moved meanwhile: look again when the panel opens (not on a timer).
            context.RunBackground("Check targets", _ =>
            {
                UpdateViews();
                return Task.CompletedTask;
            });
        }
    }

    // ------------------------------------------------------------------ images (real icons)

    internal static string IconId(QuickTarget target) => IconPrefix + target;

    public Task<ModuleImage?> LoadImageAsync(string imageId, CancellationToken cancellationToken)
    {
        if (!imageId.StartsWith(IconPrefix, StringComparison.Ordinal) || QuickTarget.Parse(imageId[IconPrefix.Length..]) is not { } target)
        {
            return Task.FromResult<ModuleImage?>(null);
        }

        if (Resolve(target) is not { IconSource: { } source, Exists: true })
        {
            return Task.FromResult<ModuleImage?>(null);
        }

        byte[]? png;
        lock (_lock)
        {
            if (_icons.TryGetValue(source, out png))
            {
                return Task.FromResult(png is null ? null : new ModuleImage(png, "image/png"));
            }
        }

        png = _shell.GetIconPng(source, IconSize);
        lock (_lock)
        {
            if (!_icons.ContainsKey(source))
            {
                _icons[source] = png;
                _iconOrder.Enqueue(source);
                while (_iconOrder.Count > MaxCachedIcons)
                {
                    _icons.Remove(_iconOrder.Dequeue());
                }
            }
        }

        return Task.FromResult(png is null ? null : new ModuleImage(png, "image/png"));
    }

    // ------------------------------------------------------------------ module bus (SPEC 4.7)

    private void PublishOnBus(IModuleBus bus)
    {
        bus.PublishAction("quickaccess.open", (call, _) =>
        {
            var id = (call.Arguments as JsonObject)?["id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            if (id is null || FindEntry(id) is null)
            {
                throw new ArgumentException($"Unknown entry \"{id}\" – \"id\" of an entry in the quick access settings");
            }

            var error = Launch(new QuickTarget(TargetKind.Entry, id));
            return error is null
                ? Task.FromResult<JsonNode?>(new JsonObject { ["started"] = true })
                : throw new InvalidOperationException(error);
        }, "Open an entry of the quick access settings: { \"id\" } (the entry's ID)");
    }
}
