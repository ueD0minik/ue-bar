// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using UeBar.Sdk;
using static UeBar.Modules.Clipboard.ClipText;

namespace UeBar.Modules.Clipboard;

/// <summary>
/// Plugin "clipboard" (P2-6): a clipboard history. Records text (with HTML/RTF), images and file lists through the
/// core's watcher (event driven, password managers' private copies are never even read), stores them encrypted
/// (DPAPI) on this PC only, and offers them in the core's choice popup – Win+V (taken over from Windows, can be
/// switched off) or Win+Alt+V. Enter pastes into the previous window, Shift+Enter as plain text, Ctrl+C copies,
/// Ctrl+M keeps (★), Ctrl+P pins, Delete deletes. Also a tile, the quick command <c>cb</c>, a tray entry for pausing,
/// and on the bus only metadata (contents only if the user allows it). No UI code of its own, no own threads.
/// </summary>
public sealed class ClipboardModule :
    IModule,
    ILiveModuleSettings,
    IModuleSettingsActions,
    IHotkeyHandler,
    IQuickCommandProvider,
    IEntitySource,
    INotificationSource,
    ITrayMenuProvider,
    IImageSource,
    IModuleDiagnostics
{
    public const string ModuleId = "clipboard";
    public const string ClipIcon = "U+E77F";
    public const string ChangedEvent = "clipboard.changed";
    public const string CopyTextAction = "clipboard.copy_text";
    public const string RecentAction = "clipboard.recent";
    public const string CommandPrefix = "cb";
    public const string ThumbPrefix = "thumb:";
    public const string EntryPrefix = "entry:";

    /// <summary>The quick search closes first, so the window behind it gets the focus back.</summary>
    internal static readonly TimeSpan CommandDelay = TimeSpan.FromMilliseconds(250);

    internal static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly IClipProtector _protector;
    private readonly TimeProvider _time;
    private readonly string? _folder;
    private readonly Lock _lock = new();
    private readonly List<WeakReference<TileModel>> _tiles = [];
    private readonly ITileType[] _tileTypes;
    private ClipOptions _options = ClipOptions.Default;
    private IModuleContext? _context;
    private ClipStore? _store;
    private IDisposable? _watch;
    private ClipboardWatchOptions? _watching;
    private IHotkeyTakeover? _takeover;
    private volatile bool _paused;
    private int _stored;
    private int _skipped;

    /// <summary>Constructor the plugin loader uses: DPAPI and the system clock.</summary>
    public ClipboardModule()
        : this(new DpapiProtector(), TimeProvider.System, null)
    {
    }

    /// <summary>For tests: own protector, clock and folder (null = the plugin's data folder).</summary>
    internal ClipboardModule(IClipProtector protector, TimeProvider time, string? folder)
    {
        _protector = protector;
        _time = time;
        _folder = folder;
        _tileTypes = [new HistoryTileType(this)];
    }

    public string Id => ModuleId;

    public string DisplayName => T["Clip_ModuleName"];

    public string Icon => ClipIcon;

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<UeNotification>? NotificationReceived;

    public event EventHandler<EntityInfo>? EntityChanged { add { } remove { } }

    internal ClipOptions Options
    {
        get
        {
            lock (_lock)
            {
                return _options;
            }
        }
    }

    internal ClipStore? Store => _store;

    internal bool IsPaused => _paused;

    internal DateTimeOffset Now => _time.GetUtcNow();

    internal IHotkeyTakeover? Takeover => _takeover;

    public IReadOnlyList<SettingField> SettingFields =>
        ClipOptions.Fields(HookText(), _paused, _store?.Entries.Count ?? 0, _store?.TotalBytes ?? 0, _store?.Folder ?? _folder ?? _context?.DataDirectory ?? "");

    // ------------------------------------------------------------------ lifecycle

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _paused = false; // a restart always ends the pause
        if (context is IUiLanguageInfo language)
        {
            Culture = language.UiCulture;
            language.UiCultureChanged += OnUiCultureChanged;
        }

        var store = new ClipStore(Folder(context), _protector, _time, message => context.Log.Warn(message));
        var count = store.Load();
        store.Prune(Options.Limits);
        _store = store;
        context.Log.Info($"History loaded: {store.Entries.Count} entries ({count} before limits), {store.TotalBytes / 1024} KB");

        context.Bus.PublishEvent(ChangedEvent, T["Clip_BusChanged"]);
        context.Bus.PublishAction(CopyTextAction, CopyTextFromBusAsync, T["Clip_BusCopyText"]);
        context.Bus.PublishAction(RecentAction, RecentFromBus, T["Clip_BusRecent"]);

        ApplyWatch();
        ApplyTakeover();
        context.RunBackground("Prune", async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PruneInterval, _time, ct).ConfigureAwait(false);
                if (_store is { } s && s.Prune(Options.Limits) is { Count: > 0 } removed)
                {
                    context.Log.Info($"Limits: {removed.Count} old entries deleted");
                    RefreshTiles();
                }
            }
        });

        SetStatus(context is IClipboardWatcher ? ModuleStatus.Connected : ModuleStatus.Error(T["Clip_NotAvailable"]));
        RefreshTiles();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_context is IUiLanguageInfo language)
        {
            language.UiCultureChanged -= OnUiCultureChanged;
        }

        lock (_lock)
        {
            _watch?.Dispose();
            _watch = null;
            _watching = null;
            _takeover?.Dispose();
            _takeover = null;
        }

        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    private string Folder(IModuleContext context) => _folder ?? context.DataDirectory;

    private void OnUiCultureChanged(object? sender, CultureInfo culture)
    {
        Culture = culture;
        RefreshTiles();
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        var options = ClipOptions.From(values);
        lock (_lock)
        {
            _options = options;
        }

        if (_context is not null && _store is { } store)
        {
            ApplyWatch();
            ApplyTakeover();
            if (store.Prune(options.Limits) is { Count: > 0 } removed)
            {
                _context.Log.Info($"Limits: {removed.Count} entries deleted");
            }
        }

        RefreshTiles();
        return Task.CompletedTask;
    }

    /// <summary>(Re-)starts watching when what we read changed (images on/off, size).</summary>
    private void ApplyWatch()
    {
        if (_context is not IClipboardWatcher watcher)
        {
            return;
        }

        var wanted = Options.WatchOptions;
        lock (_lock)
        {
            if (_watch is not null && _watching == wanted)
            {
                return;
            }

            _watch?.Dispose();
            _watching = wanted;
            _watch = watcher.WatchClipboard(wanted, OnClipboardChanged);
        }
    }

    /// <summary>Win+V on → take it over; off → give it back to Windows.</summary>
    private void ApplyTakeover()
    {
        var want = Options.TakeOverWinV;
        IHotkeyTakeover? created = null;
        lock (_lock)
        {
            if (want && _takeover is null && _context is IHotkeyTakeoverService service)
            {
                created = service.TakeOverHotkey(ClipOptions.WinV, () => OpenPicker("Win+V"));
                _takeover = created;
            }
            else if (!want && _takeover is not null)
            {
                _takeover.Dispose();
                _takeover = null;
                _context?.Log.Info("Win+V given back to Windows");
            }
        }

        if (created is not null)
        {
            created.StateChanged += (_, _) => _context?.Log.Info($"Win+V: {created.State}{(created.Problem is null ? "" : " – " + created.Problem)}");
        }
    }

    /// <summary>The line about Win+V on the settings page.</summary>
    internal string HookText()
    {
        var fallback = HotkeyText(Options.Hotkey);
        if (!Options.TakeOverWinV)
        {
            return T.Format("Clip_HookOff", fallback);
        }

        return _takeover switch
        {
            { State: HotkeyTakeoverState.Active } => T["Clip_HookActive"],
            { State: HotkeyTakeoverState.Retrying, Problem: null } => T["Clip_HookStarting"],
            { } t => T.Format("Clip_HookProblem", t.Problem ?? "", fallback),
            null when _context is null => T["Clip_HookStarting"],
            null => T.Format("Clip_HookProblem", "", fallback),
        };
    }

    /// <summary>"Win + Alt + V".</summary>
    internal static string HotkeyText(string hotkey) =>
        string.Join(" + ", hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    public IReadOnlyList<string> GetDiagnostics() =>
        [$"Entries: {_store?.Entries.Count ?? 0} ({(_store?.TotalBytes ?? 0) / 1024} KB), stored {Volatile.Read(ref _stored)}, skipped {Volatile.Read(ref _skipped)}, paused {_paused}, Win+V {_takeover?.State.ToString() ?? "off"}"];

    // ------------------------------------------------------------------ recording

    /// <summary>A change of the clipboard (from the core's watcher, through the guard). Logs kind and size only.</summary>
    internal void OnClipboardChanged(ClipboardChange change)
    {
        if (_store is not { } store || _context is not { } context)
        {
            return;
        }

        if (_paused)
        {
            Interlocked.Increment(ref _skipped);
            context.Log.Debug("Paused – copy not stored");
            return;
        }

        var options = Options;
        if (options.IsExcluded(change.Source))
        {
            Interlocked.Increment(ref _skipped);
            context.Log.Info("Copied in an excluded app – not stored");
            return;
        }

        // Our own pasting puts the entry on the clipboard again: it only moves up, never a new entry.
        var own = string.Equals(change.WrittenByModule, ModuleId, StringComparison.OrdinalIgnoreCase);
        var result = store.Add(change, options.Limits, bumpOnly: own);
        if (result.Entry is not { } entry)
        {
            if (change.ImageSkipped)
            {
                context.Log.Info("Image left out (images off or too large)");
            }

            return;
        }

        if (result.IsNew)
        {
            Interlocked.Increment(ref _stored);
            context.Log.Info($"Stored {entry.Kind.ToString().ToLowerInvariant()} ({entry.ContentSize} bytes){(result.Removed.Count > 0 ? $", {result.Removed.Count} old removed" : "")}");
        }

        if (!own)
        {
            context.Bus.RaiseEvent(ChangedEvent, new JsonObject
            {
                ["kind"] = entry.Kind.ToString().ToLowerInvariant(),
                ["size"] = entry.ContentSize,
                ["app"] = entry.AppProcess,
                ["new"] = result.IsNew,
            });
        }

        RefreshTiles();
    }

    internal void SetPaused(bool paused, string source)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        _context?.Log.Info($"History {(paused ? "paused" : "resumed")} ({source})");
        RefreshTiles();
    }

    // ------------------------------------------------------------------ choice popup

    /// <summary>Opens the history (Win+V, Win+Alt+V, tile, quick search). Pressing the hotkey again closes it.</summary>
    internal void OpenPicker(string source, TimeSpan? delay = null)
    {
        if (_context is not { } context || context.Stopping.IsCancellationRequested)
        {
            return;
        }

        context.Log.Info($"History opened ({source})");
        context.RunBackground("Choice", async ct =>
        {
            if (delay is { } wait && wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }

            await ChooseAsync(ct).ConfigureAwait(false);
        });
    }

    /// <summary>Shows the popup and does what was chosen. Returns the choice (null = closed).</summary>
    internal async Task<ChoicePopupResult?> ChooseAsync(CancellationToken cancellationToken)
    {
        if (_context is not IChoicePopup popup)
        {
            return null;
        }

        var request = new ChoicePopupRequest(Items())
        {
            Title = T["Clip_PopupTitle"],
            Hint = _paused ? T["Clip_Paused"] : null,
            Placeholder = T["Clip_Search"],
            EmptyText = _store?.Entries.Count > 0 ? T["Clip_NoHit"] : T["Clip_Empty"],
            Actions = Actions(HasContentActions),
            Placement = ChoicePlacement.Caret,
            OnAction = OnPopupActionAsync,
        };
        var choice = await popup.ShowChoicesAsync(request, cancellationToken).ConfigureAwait(false);
        if (choice is not null)
        {
            await RunAsync(choice, cancellationToken).ConfigureAwait(false);
        }

        return choice;
    }

    /// <summary>P3-1: "Actions …" (Ctrl+K) – content actions of other plugins for the selected entry; only when there are any.</summary>
    internal const string ContentActionsId = "actions";

    internal bool HasContentActions => _context is IContentActions actions && actions.GetContentActions(ContentKinds.All).Count > 0;

    internal static IReadOnlyList<ChoiceAction> Actions(bool contentActions = false) => contentActions
        ? [.. BaseActions(), new(ContentActionsId, T["Clip_ActionActions"], "Ctrl+K") { Icon = "U+E8BD" }]
        : BaseActions();

    private static IReadOnlyList<ChoiceAction> BaseActions() =>
    [
        new("paste", T["Clip_ActionPaste"]) { Icon = "U+E77F" },
        new("plain", T["Clip_ActionPlain"], "Shift+Enter") { Icon = "U+E8E9" },
        new("copy", T["Clip_ActionCopy"], "Ctrl+C") { Icon = "U+E8C8" },
        new("keep", T["Clip_ActionKeep"], "Ctrl+M") { Icon = "U+E734", KeepOpen = true },
        new("pin", T["Clip_ActionPin"], "Ctrl+P") { Icon = "U+E718" },
        new("delete", T["Clip_ActionDelete"], "Delete") { Icon = "U+E74D", KeepOpen = true },
    ];

    /// <summary>Kept entries first (own heading), then the rest, newest first.</summary>
    internal IReadOnlyList<ChoiceItem> Items()
    {
        var entries = _store?.Entries ?? [];
        var kept = entries.Where(e => e.Kept).ToList();
        var rest = entries.Where(e => !e.Kept).ToList();
        var now = _time.GetUtcNow();
        return [.. kept.Select(e => Item(e, now, T["Clip_GroupKept"])), .. rest.Select(e => Item(e, now, kept.Count > 0 ? T["Clip_GroupRecent"] : null))];
    }

    private ChoiceItem Item(ClipEntry entry, DateTimeOffset now, string? group) => new(entry.Id, Title(entry))
    {
        Detail = Detail(entry, now),
        Icon = KindIcon(entry.Kind),
        ImagePng = entry.HasThumbnail ? _store?.Thumbnail(entry.Id) : null,
        Group = group,
        SearchText = $"{entry.SearchText} {entry.AppName} {entry.AppProcess}",
        IsMarked = entry.Kept,
    };

    internal static string Title(ClipEntry entry) => entry.Kind switch
    {
        ClipKind.Image => T.Format("Clip_Image", entry.ImageWidth, entry.ImageHeight),
        _ => entry.Preview.Length > 0 ? entry.Preview : "…",
    };

    internal string Detail(ClipEntry entry, DateTimeOffset now)
    {
        var parts = new List<string> { Ago(now - entry.UsedAt) };
        if (entry.AppName is { Length: > 0 } app)
        {
            parts.Add(T.Format("Clip_From", app));
        }

        if (entry.Kind == ClipKind.Files && entry.FileCount > 1)
        {
            parts.Add(T.Format("Clip_Files", entry.FileCount));
        }
        else
        {
            parts.Add(ClipOptions.Megabytes(entry.ContentSize));
        }

        return string.Join(" · ", parts);
    }

    internal static string KindIcon(ClipKind kind) => kind switch
    {
        ClipKind.Image => "U+E91B",
        ClipKind.Files => "U+E8B7",
        _ => "U+E8E4",
    };

    internal static string Ago(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? T["Clip_JustNow"]
        : age < TimeSpan.FromHours(1) ? T.Format("Clip_MinutesAgo", (int)age.TotalMinutes)
        : age < TimeSpan.FromDays(1) ? T.Format("Clip_HoursAgo", (int)age.TotalHours)
        : T.Format("Clip_DaysAgo", (int)age.TotalDays);

    /// <summary>Keep and delete – the popup stays open with the new list.</summary>
    private Task<ChoicePopupUpdate?> OnPopupActionAsync(ChoicePopupResult choice, CancellationToken cancellationToken)
    {
        if (_store is not { } store)
        {
            return Task.FromResult<ChoicePopupUpdate?>(null);
        }

        switch (choice.ActionId)
        {
            case "keep" when store.Find(choice.ItemId) is { } entry:
                store.SetKept(entry.Id, !entry.Kept);
                _context?.Log.Info($"Entry {(entry.Kept ? "no longer kept" : "kept")}");
                RefreshTiles();
                return Task.FromResult<ChoicePopupUpdate?>(new ChoicePopupUpdate(Items()) { SelectId = entry.Id });
            case "delete":
                store.Delete(choice.ItemId);
                _context?.Log.Info("Entry deleted");
                RefreshTiles();
                return Task.FromResult<ChoicePopupUpdate?>(new ChoicePopupUpdate(Items()));
            default:
                return Task.FromResult<ChoicePopupUpdate?>(null);
        }
    }

    /// <summary>Paste, plain text, copy or pin.</summary>
    internal async Task RunAsync(ChoicePopupResult choice, CancellationToken cancellationToken)
    {
        if (_store is not { } store || _context is not { } context)
        {
            return;
        }

        if (store.Find(choice.ItemId) is not { } entry || store.LoadContent(entry.Id) is not { } content)
        {
            Notify(T["Clip_Gone"], "", "Error");
            return;
        }

        store.Touch(entry.Id);
        RefreshTiles();
        switch (choice.ActionId)
        {
            case "paste":
            case "plain":
                var paste = choice.ActionId == "plain" ? PlainOf(content) : content;
                var pasted = context is IPasteService service && await service.PasteAsync(paste, choice.Target, cancellationToken).ConfigureAwait(false);
                if (!pasted)
                {
                    Notify(T["Clip_PasteFailed"], "", "U+E77F");
                }

                break;
            case "copy":
                if (!await CopyAsync(content, cancellationToken).ConfigureAwait(false))
                {
                    Notify(T["Clip_CopyFailed"], "", "Error");
                }

                break;
            case ContentActionsId:
                // The entry goes only to the action the user picks next (the list opens where the popup was).
                var item = new ContentItem { Text = content.Text, ImagePng = content.ImagePng, Files = content.Files, SourceTitle = entry.AppName };
                if (context is not IContentActions contentActions || item.IsEmpty)
                {
                    Notify(T["Clip_NoActions"], "", "U+E8BD");
                    break;
                }

                await contentActions.ChooseContentActionAsync(item, new ContentActionStart
                {
                    Origin = ContentOrigin.Clipboard,
                    PasteTarget = choice.Target,
                    Placement = ChoicePlacement.Caret,
                }, cancellationToken).ConfigureAwait(false);
                break;
            case "pin":
                if (!Pin(entry, content))
                {
                    Notify(T["Clip_PinFailed"], "", "Error");
                }

                break;
        }
    }

    /// <summary>Only the plain text (file lists as their paths); a picture stays a picture.</summary>
    internal static ClipboardContent PlainOf(ClipboardContent content) =>
        content.Files is { Count: > 0 } files ? new ClipboardContent { Text = string.Join(Environment.NewLine, files) }
        : !string.IsNullOrEmpty(content.Text) ? new ClipboardContent { Text = content.Text }
        : content;

    private Task<bool> CopyAsync(ClipboardContent content, CancellationToken cancellationToken) =>
        _context is IClipboardWriter writer ? writer.SetContentAsync(content, cancellationToken)
        : _context is IClipboardAccess access && content.Text is { Length: > 0 } text ? access.SetTextAsync(text, cancellationToken)
        : Task.FromResult(false);

    /// <summary>"Anpinnen": as a text or image pin through the core's pin service.</summary>
    private bool Pin(ClipEntry entry, ClipboardContent content)
    {
        if (_context is not IPinService pins)
        {
            return false;
        }

        if (entry.Kind == ClipKind.Image && content.ImagePng is { Length: > 0 } png)
        {
            return pins.CreateImagePin(new ImagePinRequest(png) { Title = Title(entry) }) is not null;
        }

        var text = PlainOf(content).Text;
        return !string.IsNullOrWhiteSpace(text) && pins.CreateTextPin(new TextPinRequest(text)) is not null;
    }

    // ------------------------------------------------------------------ ways in

    public Task OnHotkeyAsync(string fieldKey, CancellationToken cancellationToken)
    {
        if (fieldKey == ClipOptions.HotkeyKey)
        {
            OpenPicker("hotkey");
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<QuickCommandInfo> QuickCommands => [new(CommandPrefix, T["Clip_Command"], ClipIcon) { ArgumentOptional = true }];

    public async Task<QuickCommandResult> ExecuteQuickCommandAsync(string prefix, string argument, CancellationToken cancellationToken)
    {
        var word = argument.Trim();
        switch (word.ToLowerInvariant())
        {
            case "":
                OpenPicker("quick command", CommandDelay);
                return new QuickCommandResult(true, T["Clip_PopupTitle"]);
            case "pause" or "pausieren" or "stop":
                SetPaused(!_paused, "quick command");
                return new QuickCommandResult(true, _paused ? T["Clip_CommandPaused"] : T["Clip_CommandResumed"]);
            case "weiter" or "fortsetzen" or "resume" or "start":
                SetPaused(false, "quick command");
                return new QuickCommandResult(true, T["Clip_CommandResumed"]);
            case "leeren" or "clear":
                var removed = _store?.Clear(keepKept: true) ?? 0;
                _context?.Log.Info($"History cleared by quick command ({removed} removed)");
                RefreshTiles();
                return new QuickCommandResult(true, T.Format("Clip_CommandCleared", removed));
        }

        if (Search(word, 1) is not [var hit] || _store?.LoadContent(hit.Id) is not { } content)
        {
            return new QuickCommandResult(false, T.Format("Clip_CommandNoHit", word));
        }

        _store.Touch(hit.Id);
        var copied = await CopyAsync(content, cancellationToken).ConfigureAwait(false);
        RefreshTiles();
        return new QuickCommandResult(copied, copied ? T.Format("Clip_CommandCopied", ClipStore.Fold(Title(hit), 60)) : T["Clip_CopyFailed"]);
    }

    /// <summary>Entries whose text, file names or app contain every word, newest first.</summary>
    internal IReadOnlyList<ClipEntry> Search(string query, int max)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. (_store?.Entries ?? [])
            .Where(e => words.All(w => $"{e.Preview} {e.SearchText} {e.AppName}".Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .Take(max)];
    }

    public Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        var now = _time.GetUtcNow();

        // "cb …" → the matching entries; Enter on one copies it.
        if (q.StartsWith(CommandPrefix + " ", StringComparison.OrdinalIgnoreCase) && q[(CommandPrefix.Length + 1)..].Trim() is { Length: > 0 } words
            && words.ToLowerInvariant() is not ("pause" or "pausieren" or "leeren" or "clear" or "weiter" or "fortsetzen" or "resume"))
        {
            IReadOnlyList<EntityInfo> hits = [.. Search(words, maxResults).Select(e => new EntityInfo(EntryPrefix + e.Id, ClipStore.Fold(Title(e), 80), KindIcon(e.Kind), null, Detail(e, now), T["Clip_ModuleName"]))];
            return Task.FromResult(hits);
        }

        var names = new[] { "clipboard", "history", "win+v", "paste" }.Concat(BothLanguages("Clip_PopupTitle")).Concat(BothLanguages("Clip_ActionPaste"));
        IReadOnlyList<EntityInfo> open = q.Length >= 3 && names.Any(n => n.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            ? [new EntityInfo("open", T["Clip_PopupTitle"], ClipIcon, null, _paused ? T["Clip_PausedShort"] : CountText(), T["Clip_ModuleName"])]
            : [];
        return Task.FromResult(open);
    }

    public async Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        if (entityId == "open")
        {
            OpenPicker("quick search", CommandDelay);
            return;
        }

        if (entityId.StartsWith(EntryPrefix, StringComparison.Ordinal))
        {
            await CopyEntryAsync(entityId[EntryPrefix.Length..], cancellationToken).ConfigureAwait(false);
        }
    }

    public TileConfig? SuggestTile(EntityInfo entity) =>
        entity.Id == "open" ? new TileConfig { Module = ModuleId, Type = HistoryTileType.TypeId, Size = TileSize.Wide } : null;

    /// <summary>Copies an entry (tile, quick search) and moves it to the top.</summary>
    internal async Task<bool> CopyEntryAsync(string id, CancellationToken cancellationToken)
    {
        if (_store?.LoadContent(id) is not { } content)
        {
            Notify(T["Clip_Gone"], "", "Error");
            return false;
        }

        _store.Touch(id);
        var copied = await CopyAsync(content, cancellationToken).ConfigureAwait(false);
        Notify(copied ? T["Clip_Copied"] : T["Clip_CopyFailed"], "", copied ? "U+E8C8" : "Error", TimeSpan.FromSeconds(2));
        RefreshTiles();
        return copied;
    }

    public IReadOnlyList<TrayMenuEntry> TrayMenuEntries => [new("pause", T["Clip_TrayPause"]) { Checked = _paused }];

    public Task RunTrayMenuEntryAsync(string entryId, CancellationToken cancellationToken)
    {
        if (entryId == "pause")
        {
            SetPaused(!_paused, "tray");
        }

        return Task.CompletedTask;
    }

    public async Task<string?> RunSettingsActionAsync(string key, CancellationToken cancellationToken)
    {
        switch (key)
        {
            case ClipOptions.PauseKey:
                SetPaused(!_paused, "settings");
                return _paused ? T["Clip_CommandPaused"] : T["Clip_CommandResumed"];
            case ClipOptions.ClearKey or ClipOptions.ClearAllKey:
                var removed = _store?.Clear(keepKept: key == ClipOptions.ClearKey) ?? 0;
                _context?.Log.Info($"History cleared in the settings ({removed} removed, kept {(key == ClipOptions.ClearKey ? "stay" : "too")})");
                RefreshTiles();
                return T.Format("Clip_Cleared", removed);
            case ClipOptions.FolderKey:
                if (_store is { } store)
                {
                    Directory.CreateDirectory(store.Folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{store.Folder}\"") { UseShellExecute = false })?.Dispose();
                }

                await Task.CompletedTask.ConfigureAwait(false);
                return T["Clip_FolderOpened"];
            default:
                return null;
        }
    }

    public Task<ModuleImage?> LoadImageAsync(string imageId, CancellationToken cancellationToken)
    {
        var thumb = imageId.StartsWith(ThumbPrefix, StringComparison.Ordinal) ? _store?.Thumbnail(imageId[ThumbPrefix.Length..]) : null;
        return Task.FromResult(thumb is null ? null : new ModuleImage(thumb, "image/png"));
    }

    // ------------------------------------------------------------------ bus

    /// <summary>Another plugin puts text on the clipboard: it becomes a normal entry (not "our own pasting").</summary>
    private async Task<JsonNode?> CopyTextFromBusAsync(BusCall call, CancellationToken cancellationToken)
    {
        var text = call.Arguments?["text"]?.GetValue<string>();
        if (string.IsNullOrEmpty(text))
        {
            return new JsonObject { ["copied"] = false };
        }

        var content = new ClipboardContent { Text = text };
        if (!_paused && _store is { } store)
        {
            store.Add(new ClipboardChange(content, _time.GetUtcNow()) { Source = new ClipboardSourceApp(call.CallerModuleId, call.CallerModuleId) }, Options.Limits);
            RefreshTiles();
        }

        var copied = await CopyAsync(content, cancellationToken).ConfigureAwait(false);
        _context?.Log.Info($"Text from {call.CallerModuleId} copied ({text.Length} characters)");
        return new JsonObject { ["copied"] = copied };
    }

    /// <summary>The latest texts – only if the user allowed sharing contents (on top of the bus permission).</summary>
    private Task<JsonNode?> RecentFromBus(BusCall call, CancellationToken cancellationToken)
    {
        if (!Options.ShareContents || _store is not { } store)
        {
            _context?.Log.Info($"{call.CallerModuleId} asked for contents – not shared");
            return Task.FromResult<JsonNode?>(new JsonObject { ["shared"] = false, ["reason"] = T["Clip_BusNotShared"] });
        }

        var count = Math.Clamp(call.Arguments?["count"]?.GetValue<int>() ?? 5, 1, 20);
        var items = new JsonArray();
        foreach (var entry in store.Entries.Where(e => e.Kind == ClipKind.Text).Take(count))
        {
            if (store.LoadContent(entry.Id)?.Text is { } text)
            {
                items.Add(new JsonObject { ["text"] = text, ["time"] = entry.UsedAt.ToString("O", CultureInfo.InvariantCulture), ["app"] = entry.AppProcess });
            }
        }

        _context?.Log.Info($"{call.CallerModuleId} read {items.Count} texts (sharing is on)");
        return Task.FromResult<JsonNode?>(new JsonObject { ["shared"] = true, ["items"] = items });
    }

    // ------------------------------------------------------------------ tiles

    internal void Track(TileModel tile, TileConfig config)
    {
        lock (_lock)
        {
            _tiles.RemoveAll(w => !w.TryGetTarget(out _));
            _tiles.Add(new WeakReference<TileModel>(tile));
        }

        HistoryTileType.Refresh(this, tile, config.Size);
    }

    internal void RefreshTiles()
    {
        List<TileModel> tiles;
        lock (_lock)
        {
            tiles = [.. _tiles.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<TileModel>()];
        }

        foreach (var tile in tiles)
        {
            HistoryTileType.Refresh(this, tile, null);
        }
    }

    internal string CountText()
    {
        var count = _store?.Entries.Count ?? 0;
        return count == 1 ? T["Clip_CountOne"] : T.Format("Clip_Count", count);
    }

    private void Notify(string title, string message, string icon, TimeSpan? duration = null) =>
        NotificationReceived?.Invoke(this, new UeNotification(title, message, icon, duration ?? TimeSpan.FromSeconds(4)) { IsActionFeedback = true });
}
