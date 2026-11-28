// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Threading;
using UeBar.App.ClipHistory;
using UeBar.App.Infrastructure;
using UeBar.App.Localization;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.App.ContentActions;

/// <summary>
/// The app's side of content actions (P3-1): the list of actions in the choice popup and the answer windows. Copy,
/// paste and pin of an answer go through the app's own clipboard, paste and pin services (as the host, not as a
/// module). Several answer windows may be open; each forgets its conversation when it closes.
/// </summary>
internal sealed class ContentActionUi : IHostContentActionUi
{
    /// <summary>Writer ID of the answer window for the clipboard (the history then shows it like the pins' "Copy").</summary>
    public const string HostId = "content-actions";

    private readonly Dispatcher _dispatcher;
    private readonly ChoicePopupHost _popup;
    private readonly ClipboardAccess _clipboard;
    private readonly PasteService _paste;
    private readonly PinManager _pins;
    private readonly IUeLogger _log;
    private readonly List<ResponseWindow> _open = [];

    public ContentActionUi(Dispatcher dispatcher, ChoicePopupHost popup, ClipboardAccess clipboard, PasteService paste, PinManager pins, IUeLogger log)
    {
        _dispatcher = dispatcher;
        _popup = popup;
        _clipboard = clipboard;
        _paste = paste;
        _pins = pins;
        _log = log;
    }

    /// <summary>Short notices ("Copied"); null = none.</summary>
    public Action<string>? Notice { get; set; }

    public int OpenWindows => _open.Count;

    public async Task<ContentActionEntry?> ChooseAsync(IReadOnlyList<ContentActionEntry> actions, ContentItem content, ContentActionStart start, CancellationToken cancellationToken)
    {
        var items = actions.Select((a, i) => new ChoiceItem(i.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Action.Label)
        {
            Detail = a.Action.Description is { Length: > 0 } d ? $"{d} · {a.ModuleName}" : a.ModuleName,
            Icon = a.Action.Icon ?? "U+E8BD",
            Group = actions.Select(x => x.ModuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ? a.ModuleName : null,
            SearchText = $"{a.Action.Label} {a.Action.Description} {a.ModuleName}",
        }).ToList();
        var request = new ChoicePopupRequest(items)
        {
            Title = Loc.Get("Actions_Title"),
            Hint = Describe(content),
            Placeholder = Loc.Get("Actions_Search"),
            EmptyText = Loc.Get("Actions_None"),
            Actions = [new ChoiceAction("run", Loc.Get("Actions_Run")) { Icon = "U+E768" }],
            Placement = start.Placement,
        };
        var choice = await _popup.ShowAsync(HostId, request, cancellationToken).ConfigureAwait(false);
        return choice is not null && int.TryParse(choice.ItemId, System.Globalization.CultureInfo.InvariantCulture, out var index) && index >= 0 && index < actions.Count
            ? actions[index]
            : null;
    }

    public void Show(ContentActionSession session) => _dispatcher.BeginInvoke(() =>
    {
        try
        {
            var window = new ResponseWindow(session, this);
            _open.Add(window);
            window.SourceInitialized += (_, _) => window.PlaceNearMouse();
            window.Show();
            window.PlaceNearMouse();
        }
        catch (Exception ex)
        {
            _log.Error(null, "Answer window could not be opened", ex);
            session.Dispose();
        }
    });

    /// <summary>Closes every answer window (e.g. at exit) – the conversations are gone.</summary>
    public void CloseAll()
    {
        foreach (var window in _open.ToList())
        {
            window.Close();
        }
    }

    internal void Closed(ResponseWindow window) => _open.Remove(window);

    public Task<bool> CopyAsync(string text) => _clipboard.SetContentAsync(HostId, new ClipboardContent { Text = text }, CancellationToken.None);

    public Task<bool> PasteAsync(string text, PasteTarget target) => _paste.PasteAsync(HostId, new ClipboardContent { Text = text }, target, CancellationToken.None);

    public bool Pin(string text, string title) => _pins.CreateText(text, title) is not null;

    public void Toast(string text) => Notice?.Invoke(text);

    private static string Describe(ContentItem content) =>
        content.Text is { Length: > 0 } text ? Loc.F("Actions_Text", text.Length)
        : content.ImagePng is { Length: > 0 } ? Loc.Get("Actions_Image")
        : content.Files is { Count: > 0 } files ? Loc.F("Actions_Files", files.Count)
        : "";
}
