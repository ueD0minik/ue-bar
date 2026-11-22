// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json.Nodes;
using UeBar.Sdk;
using static UeBar.Modules.Screenshot.ShotText;

namespace UeBar.Modules.Screenshot;

/// <summary>
/// Plugin "screenshot" (P2-5): capture an area (default), a window or a whole screen through the core's
/// picker (<see cref="IScreenAreaPicker"/>, no UI code of its own) – hotkey (default Win+Alt+S), tile, quick
/// command <c>shot</c>, quick search, bus action <c>screenshot.capture</c>. Afterwards pin, copy, save or
/// recognize text (Windows OCR, offline) – asked in a small bar or set as the default. Images stay on this
/// PC: the bus event <c>screenshot.taken</c> carries only metadata.
/// </summary>
public sealed class ScreenshotModule :
    IModule,
    ILiveModuleSettings,
    IHotkeyHandler,
    IQuickCommandProvider,
    IEntitySource,
    INotificationSource,
    INotificationActionHandler,
    IModuleDiagnostics
{
    public const string ModuleId = "screenshot";
    public const string ShotIcon = "U+E722";
    public const string CaptureAction = "screenshot.capture";
    public const string TakenEvent = "screenshot.taken";
    public const string CommandPrefix = "shot";

    /// <summary>Quick search and Assist close first, then the screen is frozen.</summary>
    internal static readonly TimeSpan CommandDelay = TimeSpan.FromMilliseconds(250);

    private readonly IShotOcr _ocr;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<WeakReference<TileModel>> _tiles = [];
    private readonly ITileType[] _tileTypes;
    private IReadOnlyList<(string Tag, string Name)>? _languages;
    private ShotOptions _options = ShotOptions.Default;
    private IModuleContext? _context;
    private int _capturing;
    private int _taken;
    private (string Id, string Text)? _lastOcr;
    private int _notificationCounter;

    /// <summary>Constructor the plugin loader uses: the real Windows text recognition.</summary>
    public ScreenshotModule()
        : this(new WindowsOcr(), TimeProvider.System)
    {
    }

    /// <summary>For tests: own recognition (fake) and clock.</summary>
    internal ScreenshotModule(IShotOcr ocr, TimeProvider time)
    {
        _ocr = ocr;
        _time = time;
        _tileTypes = [new CaptureTileType(this)];
    }

    public string Id => ModuleId;

    public string DisplayName => T["Shot_ModuleName"];

    public string Icon => ShotIcon;

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public IReadOnlyList<SettingField> SettingFields => ShotOptions.Fields(Languages);

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<UeNotification>? NotificationReceived;

    public event EventHandler<EntityInfo>? EntityChanged { add { } remove { } }

    internal ShotOptions Options
    {
        get
        {
            lock (_lock)
            {
                return _options;
            }
        }
    }

    /// <summary>Recognition languages of this PC (read once – Windows only adds them with a language pack).</summary>
    private IReadOnlyList<(string Tag, string Name)> Languages => _languages ??= _ocr.Languages;

    // ------------------------------------------------------------------ lifecycle

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (context is IUiLanguageInfo language)
        {
            Culture = language.UiCulture;
            language.UiCultureChanged += OnUiCultureChanged;
        }

        context.Bus.PublishAction(CaptureAction, (call, _) =>
        {
            var mode = ShotOptions.ParseMode(call.Arguments?["mode"]?.GetValue<string>());
            var action = ShotOptions.ParseAction(call.Arguments?["action"]?.GetValue<string>());
            var started = StartCapture(mode, action, $"bus ({call.CallerModuleId})");
            return Task.FromResult<JsonNode?>(new JsonObject { ["started"] = started });
        }, T["Shot_BusAction"]);
        context.Bus.PublishEvent(TakenEvent, T["Shot_BusEvent"]);
        SetStatus(context is IScreenAreaPicker ? ModuleStatus.Connected : new ModuleStatus(ModuleState.Error, T["Shot_NotAvailable"]));
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
        RefreshTiles();
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        var options = ShotOptions.From(values);
        lock (_lock)
        {
            _options = options;
        }

        RefreshTiles();
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetDiagnostics() =>
        [$"Taken: {Volatile.Read(ref _taken)}, capturing: {Volatile.Read(ref _capturing) != 0}, OCR languages: {string.Join(", ", Languages.Select(l => l.Tag))}"];

    // ------------------------------------------------------------------ tiles

    internal void Track(TileModel tile)
    {
        lock (_lock)
        {
            _tiles.RemoveAll(w => !w.TryGetTarget(out _));
            _tiles.Add(new WeakReference<TileModel>(tile));
        }

        Refresh(tile);
    }

    private void RefreshTiles()
    {
        List<TileModel> tiles;
        lock (_lock)
        {
            tiles = [.. _tiles.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<TileModel>()];
        }

        foreach (var tile in tiles)
        {
            Refresh(tile);
        }
    }

    private void Refresh(TileModel tile)
    {
        tile.Title = T["Shot_TileName"];
        tile.StateText = HotkeyText(Options.Hotkey);
    }

    /// <summary>"Win + Alt + S" – or that there is none.</summary>
    internal static string HotkeyText(string hotkey) =>
        string.IsNullOrWhiteSpace(hotkey) ? T["Shot_NoHotkey"] : string.Join(" + ", hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------------ ways in

    public Task OnHotkeyAsync(string fieldKey, CancellationToken cancellationToken)
    {
        if (fieldKey == ShotOptions.HotkeyKey)
        {
            StartCapture(null, null, "hotkey");
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<QuickCommandInfo> QuickCommands => [new(CommandPrefix, T["Shot_Command"], ShotIcon) { ArgumentOptional = true }];

    public Task<QuickCommandResult> ExecuteQuickCommandAsync(string prefix, string argument, CancellationToken cancellationToken)
    {
        // "shot", "shot fenster", "shot bildschirm text" – mode and action in any order.
        ScreenAreaMode? mode = null;
        ShotAction? action = null;
        foreach (var word in argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mode ??= ShotOptions.ParseMode(word);
            action ??= ShotOptions.ParseAction(word) is { } a and not ShotAction.Ask ? a : null;
        }

        var started = StartCapture(mode, action, "quick command", CommandDelay);
        return Task.FromResult(new QuickCommandResult(started, started ? T["Shot_Starting"] : T["Shot_Busy"]));
    }

    public Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        var all = new List<(EntityInfo Entity, IEnumerable<string> Names)>
        {
            (new EntityInfo("capture", T["Shot_TileName"], ShotIcon, null, HotkeyText(Options.Hotkey), T["Shot_ModuleName"]), [.. BothLanguages("Shot_TileName"), "screenshot", "shot", "snip", "capture", "bildschirmfoto", "aufnahme"]),
            (new EntityInfo("capture.window", T["Shot_SearchWindow"], "U+E737", null, null, T["Shot_ModuleName"]), BothLanguages("Shot_SearchWindow")),
            (new EntityInfo("capture.screen", T["Shot_SearchScreen"], "U+E7F4", null, null, T["Shot_ModuleName"]), BothLanguages("Shot_SearchScreen")),
            (new EntityInfo("capture.text", T["Shot_SearchText"], "U+E8D2", null, null, T["Shot_ModuleName"]), [.. BothLanguages("Shot_SearchText"), "ocr"]),
        };
        IReadOnlyList<EntityInfo> hits = q.Length == 0
            ? [.. all.Select(a => a.Entity).Take(maxResults)]
            : [.. all.Where(a => a.Names.Any(n => n.Contains(q, StringComparison.CurrentCultureIgnoreCase) || q.Contains(n, StringComparison.CurrentCultureIgnoreCase) && n.Length >= 4)).Select(a => a.Entity).Take(maxResults)];
        return Task.FromResult(hits);
    }

    public Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        ScreenAreaMode? mode = entityId switch
        {
            "capture.window" => ScreenAreaMode.Window,
            "capture.screen" => ScreenAreaMode.Screen,
            _ => null,
        };
        ShotAction? action = entityId == "capture.text" ? ShotAction.Ocr : null;
        StartCapture(mode, action, "quick search", CommandDelay);
        return Task.CompletedTask;
    }

    public TileConfig? SuggestTile(EntityInfo entity) =>
        entity.Id.StartsWith("capture", StringComparison.Ordinal)
            ? new TileConfig { Module = ModuleId, Type = CaptureTileType.TypeId, Size = entity.Id == "capture" ? TileSize.Small : TileSize.Wide }
            : null;

    // ------------------------------------------------------------------ capture

    /// <summary>Starts a capture in the background (one at a time). False = one is already running or the plugin is stopped.</summary>
    internal bool StartCapture(ScreenAreaMode? mode, ShotAction? action, string source, TimeSpan? delay = null)
    {
        if (_context is not { } context || context.Stopping.IsCancellationRequested)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
        {
            context.Log.Info("Capture already running – ignored");
            return false;
        }

        context.Log.Info($"Capture ({source})");
        context.RunBackground("Capture", async ct =>
        {
            try
            {
                if (delay is { } wait && wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                }

                await CaptureAsync(mode, action, ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _capturing, 0);
            }
        });
        return true;
    }

    /// <summary>Picks the area and does the chosen (or default) thing with it. Returns what was done (null = cancelled).</summary>
    internal async Task<ShotAction?> CaptureAsync(ScreenAreaMode? mode, ShotAction? action, CancellationToken cancellationToken)
    {
        if (_context is not IScreenAreaPicker picker)
        {
            Notify(T["Shot_ModuleName"], T["Shot_NotAvailable"], "Error");
            return null;
        }

        var options = Options;
        var chosen = action ?? options.After;
        var request = new ScreenAreaRequest(mode ?? options.Mode)
        {
            Actions = chosen == ShotAction.Ask ? Actions() : [],
        };
        var result = await picker.PickScreenAreaAsync(request, cancellationToken).ConfigureAwait(false);
        if (result?.Image is not { Length: > 0 } png)
        {
            _context.Log.Info("Capture cancelled");
            return null;
        }

        var done = chosen == ShotAction.Ask ? ShotOptions.ParseAction(result.ActionId) ?? ShotAction.Pin : chosen;
        Interlocked.Increment(ref _taken);
        _context.Bus.RaiseEvent(TakenEvent, new JsonObject
        {
            ["mode"] = ShotOptions.Id(result.Mode),
            ["action"] = ShotOptions.Id(done),
            ["width"] = result.Bounds.Width,
            ["height"] = result.Bounds.Height,
            ["monitors"] = result.Monitors.Count(m => !m.Bounds.Intersect(result.Bounds).IsEmpty),
        });
        _context.Log.Info($"Captured {result.Bounds.Width}×{result.Bounds.Height} ({ShotOptions.Id(result.Mode)}) → {ShotOptions.Id(done)}");
        await DoAsync(done, png, result, options, cancellationToken).ConfigureAwait(false);
        return done;
    }

    /// <summary>The buttons of the bar after picking.</summary>
    internal static IReadOnlyList<ScreenAreaAction> Actions() =>
    [
        new("pin", T["Shot_ActionPin"], "U+E718"),
        new("copy", T["Shot_ActionCopy"], "U+E8C8") { Shortcut = "Ctrl+C" },
        new("save", T["Shot_ActionSave"], "U+E74E"),
        new("saveas", T["Shot_ActionSaveAs"], "U+E70D") { Compact = true, Shortcut = "Ctrl+S" }, // the small arrow at "Save"
        new("ocr", T["Shot_ActionOcr"], "U+E8D2"),
    ];

    private async Task DoAsync(ShotAction action, byte[] png, ScreenAreaResult result, ShotOptions options, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case ShotAction.Copy:
                var copied = _context is IClipboardAccess clipboard && await clipboard.SetImageAsync(png, cancellationToken).ConfigureAwait(false);
                Notify(copied ? T["Shot_Copied"] : T["Shot_CopyFailed"], "", copied ? "U+E8C8" : "Error", TimeSpan.FromSeconds(3));
                break;

            case ShotAction.Save:
                var (path, error) = ShotFiles.Save(options.Folder, png, _time.GetLocalNow());
                if (path is not null)
                {
                    Notify(T["Shot_Saved"], Path.GetFileName(path), "U+E74E", TimeSpan.FromSeconds(4));
                }
                else
                {
                    _context?.Log.Warn($"Screenshot not saved: {error}");
                    Notify(T["Shot_SaveFailed"], error ?? "", "Error");
                }

                break;

            case ShotAction.SaveAs:
                await SaveAsAsync(png, options, cancellationToken).ConfigureAwait(false);
                break;

            case ShotAction.Ocr:
                await RecognizeAsync(png, options, cancellationToken).ConfigureAwait(false);
                break;

            default:
                var pinned = _context is IPinService pins
                    && pins.CreateImagePin(new ImagePinRequest(png) { ScreenBounds = result.Bounds, Title = result.WindowTitle }) is not null;
                if (!pinned)
                {
                    Notify(T["Shot_PinFailed"], "", "Error");
                }

                break;
        }
    }

    /// <summary>
    /// "Save as …" (P2-5-Fix 1): the Windows dialog of the core (starts in the folder picked last time, remembered
    /// per PC), then written where the user chose. Cancelled = nothing is written anywhere.
    /// </summary>
    internal async Task<string?> SaveAsAsync(byte[] png, ShotOptions options, CancellationToken cancellationToken)
    {
        if (_context is not IFileSaveDialog dialog)
        {
            Notify(T["Shot_SaveFailed"], T["Shot_NotAvailable"], "Error");
            return null;
        }

        var path = await dialog.AskSavePathAsync(new SaveFileRequest(ShotFiles.FileName(_time.GetLocalNow()))
        {
            Title = T["Shot_SaveAsTitle"],
            TypeName = T["Shot_SaveType"],
            InitialFolder = options.Folder,
        }, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            _context?.Log.Info("Save as: cancelled");
            return null;
        }

        if (ShotFiles.SaveTo(path, png) is { } error)
        {
            _context?.Log.Warn($"Screenshot not saved: {error}");
            Notify(T["Shot_SaveFailed"], error, "Error");
            return null;
        }

        Notify(T["Shot_Saved"], Path.GetFileName(path), "U+E74E", TimeSpan.FromSeconds(4));
        return path;
    }

    // ------------------------------------------------------------------ text recognition

    internal async Task<OcrOutcome> RecognizeAsync(byte[] png, ShotOptions options, CancellationToken cancellationToken)
    {
        var outcome = await _ocr.RecognizeAsync(png, options.OcrLanguage, cancellationToken).ConfigureAwait(false);
        switch (outcome.Problem)
        {
            case OcrProblem.NoLanguage:
                _context?.Log.Warn($"OCR: no recognition language ({options.OcrLanguage ?? "Windows languages"})");
                Notify(T["Shot_OcrNoLanguageTitle"], options.OcrLanguage is { } tag ? T.Format("Shot_OcrLanguageMissing", tag) : T["Shot_OcrNoLanguage"], "Error", TimeSpan.FromSeconds(12));
                return outcome;
            case OcrProblem.Failed:
                _context?.Log.Warn($"OCR failed: {outcome.Detail}");
                Notify(T["Shot_OcrFailed"], outcome.Detail ?? "", "Error");
                return outcome;
        }

        if (!outcome.HasText)
        {
            Notify(T["Shot_OcrEmpty"], "", "U+E8D2", TimeSpan.FromSeconds(4));
            return outcome;
        }

        // P2-5-Fix 2: paragraphs, lists and headings rebuilt from the line positions – the same for copying and pins.
        var text = outcome.Formatted(options.OcrFormat).Trim();
        _context?.Log.Info($"OCR: {text.Length} characters ({outcome.Language})");
        var copy = options.OcrResult is OcrTarget.Copy or OcrTarget.Both;
        var pin = options.OcrResult is OcrTarget.Pin or OcrTarget.Both;
        var copied = copy && _context is IClipboardAccess clipboard && await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
        var pinned = pin && _context is IPinService pins && pins.CreateTextPin(new TextPinRequest(text)) is not null;

        var id = $"ocr-{Interlocked.Increment(ref _notificationCounter)}";
        lock (_lock)
        {
            _lastOcr = (id, text);
        }

        List<NotificationAction> buttons = [];
        if (!pinned)
        {
            buttons.Add(new NotificationAction("pin", T["Shot_OcrPinButton"], "U+E718"));
        }

        if (!copied)
        {
            buttons.Add(new NotificationAction("copy", T["Shot_OcrCopyButton"], "U+E8C8"));
        }

        var title = T.Format((copied, pinned) switch
        {
            (true, true) => "Shot_OcrCopiedPinned",
            (true, false) => "Shot_OcrCopied",
            (false, true) => "Shot_OcrPinned",
            _ => "Shot_OcrDone",
        }, text.Length);
        Notify(title, Preview(text), "U+E8D2", TimeSpan.FromSeconds(8), id, buttons);
        return outcome;
    }

    public async Task HandleNotificationActionAsync(string notificationId, string actionId, CancellationToken cancellationToken)
    {
        string? text;
        lock (_lock)
        {
            text = _lastOcr is { } last && last.Id == notificationId ? last.Text : null;
        }

        if (text is null)
        {
            return;
        }

        if (actionId == "pin" && _context is IPinService pins)
        {
            pins.CreateTextPin(new TextPinRequest(text));
        }
        else if (actionId == "copy" && _context is IClipboardAccess clipboard)
        {
            await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The first two lines, at most 120 characters.</summary>
    internal static string Preview(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2);
        var joined = string.Join(" · ", lines);
        return joined.Length > 120 ? joined[..119] + "…" : joined;
    }

    /// <summary>
    /// Every message of this plugin answers something the user just did – marked as such (P2-5-Fix 1), so the
    /// app shows it at the place of the action, also during "Do not disturb" (user setting).
    /// </summary>
    private void Notify(string title, string message, string icon, TimeSpan? duration = null, string? id = null, IReadOnlyList<NotificationAction>? actions = null) =>
        NotificationReceived?.Invoke(this, new UeNotification(title, message, icon, duration ?? TimeSpan.FromSeconds(6))
        {
            Id = id,
            Actions = actions is { Count: > 0 } ? actions : null,
            IsActionFeedback = true,
        });
}
