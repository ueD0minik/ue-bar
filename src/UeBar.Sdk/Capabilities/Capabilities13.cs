// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Contract 1.7 (P2-6): watching the clipboard, writing any content to it, a choice popup with search and keys,
// pasting into the window that was in front, taking over a hotkey Windows keeps for itself (Win+V) and entries in
// the tray menu. Everything new is optional – plugins built for an older contract run unchanged.

/// <summary>What is (or goes) on the clipboard. Every part is optional; apps pick the richest one they understand.</summary>
public sealed record ClipboardContent
{
    /// <summary>Plain text (Unicode).</summary>
    public string? Text { get; init; }

    /// <summary>The "HTML Format" of Windows (with its header), as an app put it there.</summary>
    public string? Html { get; init; }

    /// <summary>Rich text (RTF).</summary>
    public string? Rtf { get; init; }

    /// <summary>An image as PNG.</summary>
    public byte[]? ImagePng { get; init; }

    /// <summary>Full paths of files and folders (as copied in Explorer) – only the paths, never the files.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    public bool IsEmpty => string.IsNullOrEmpty(Text) && string.IsNullOrEmpty(Html) && string.IsNullOrEmpty(Rtf)
        && ImagePng is not { Length: > 0 } && Files is not { Count: > 0 };
}

/// <summary>The app that put something on the clipboard, as far as Windows tells.</summary>
/// <param name="ProcessName">Process name without ".exe", e.g. "WINWORD".</param>
/// <param name="DisplayName">Name for people, e.g. "Microsoft Word" (from the file description), null if unknown.</param>
/// <param name="ExePath">Full path of the program, null if not readable.</param>
public sealed record ClipboardSourceApp(string ProcessName, string? DisplayName = null, string? ExePath = null)
{
    /// <summary>The program's icon as a small PNG (32 px), null if there is none.</summary>
    public byte[]? IconPng { get; init; }
}

/// <summary>One change of the clipboard (<see cref="IClipboardWatcher"/>).</summary>
public sealed record ClipboardChange(ClipboardContent Content, DateTimeOffset Timestamp)
{
    public ClipboardSourceApp? Source { get; init; }

    /// <summary>Size of the image in pixels (0 without an image).</summary>
    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>A small copy of the image (PNG, at most <see cref="ClipboardWatchOptions.ThumbnailSize"/> px), for lists.</summary>
    public byte[]? ThumbnailPng { get; init; }

    /// <summary>There was an image, but it was left out (images off, or larger than allowed).</summary>
    public bool ImageSkipped { get; init; }

    /// <summary>
    /// The module that wrote this through the core (<see cref="IClipboardAccess"/>, <see cref="IClipboardWriter"/>,
    /// <see cref="IPasteService"/>); null = another app. Lets a history recognize its own pasting.
    /// </summary>
    public string? WrittenByModule { get; init; }
}

/// <summary>What the core reads for a watcher – the rest is never read.</summary>
public sealed record ClipboardWatchOptions
{
    /// <summary>Read images at all.</summary>
    public bool Images { get; init; } = true;

    /// <summary>Larger images (as PNG) are left out (<see cref="ClipboardChange.ImageSkipped"/>).</summary>
    public long MaxImageBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>Longer text is cut.</summary>
    public int MaxTextLength { get; init; } = 1_000_000;

    /// <summary>HTML and RTF larger than this (characters) are left out – the plain text stays.</summary>
    public int MaxRichTextLength { get; init; } = 512 * 1024;

    /// <summary>Longest side of <see cref="ClipboardChange.ThumbnailPng"/> in pixels.</summary>
    public int ThumbnailSize { get; init; } = 160;
}

/// <summary>
/// Changes of the Windows clipboard (contract 1.7), offered on the <see cref="IModuleContext"/>
/// (<c>context as IClipboardWatcher</c>). Event driven (no polling). Content that password managers mark as private
/// (<c>ExcludeClipboardContentFromMonitorProcessing</c>, <c>CanIncludeInClipboardHistory</c> = 0,
/// <c>Clipboard Viewer Ignore</c>) is never read and never reported. Reports come on a background thread,
/// through the module guard; stopping the module ends the watch.
/// </summary>
public interface IClipboardWatcher
{
    /// <summary>Starts watching; dispose the result to stop. Several watches of one module are allowed.</summary>
    IDisposable WatchClipboard(ClipboardWatchOptions options, Action<ClipboardChange> changed);
}

/// <summary>Writes any content to the clipboard (contract 1.7) – text, HTML, RTF, image and file lists together.</summary>
public interface IClipboardWriter
{
    Task<bool> SetContentAsync(ClipboardContent content, CancellationToken cancellationToken);
}

/// <summary>A window to paste into (<see cref="ChoicePopupResult.Target"/>, <see cref="IPasteService"/>).</summary>
/// <param name="WindowHandle">Windows handle of the window – only meaningful to the core.</param>
public sealed record PasteTarget(long WindowHandle)
{
    public string? Title { get; init; }
}

/// <summary>
/// Pastes into another app (contract 1.7), offered on the <see cref="IModuleContext"/> (<c>context as IPasteService</c>):
/// puts the content on the clipboard, gives the focus back to the target window and sends Ctrl+V.
/// </summary>
public interface IPasteService
{
    /// <summary>The window in front right now (null if it is the bar itself or none).</summary>
    PasteTarget? CurrentTarget();

    /// <summary>False if the clipboard could not be written or the window is gone (the content may still be on the clipboard).</summary>
    Task<bool> PasteAsync(ClipboardContent content, PasteTarget? target, CancellationToken cancellationToken);
}

/// <summary>One row of a <see cref="ChoicePopupRequest"/>.</summary>
/// <param name="Id">Comes back in <see cref="ChoicePopupResult.ItemId"/>.</param>
/// <param name="Title">First line (text preview, file names …).</param>
public sealed record ChoiceItem(string Id, string Title)
{
    /// <summary>Second, smaller line (time, app, size …).</summary>
    public string? Detail { get; init; }

    /// <summary>Icon name (Segoe Fluent Icons, "U+E8C8").</summary>
    public string? Icon { get; init; }

    /// <summary>A small picture instead of the icon (PNG, e.g. an image preview).</summary>
    public byte[]? ImagePng { get; init; }

    /// <summary>Rows with the same group are shown together under its name, in the order of the list; null = no heading.</summary>
    public string? Group { get; init; }

    /// <summary>More text the search looks at (e.g. the whole text, not only the preview).</summary>
    public string? SearchText { get; init; }

    /// <summary>Marked with a small star (e.g. kept entries).</summary>
    public bool IsMarked { get; init; }
}

/// <summary>Something the user can do with the selected row.</summary>
/// <param name="Id">Comes back in <see cref="ChoicePopupResult.ActionId"/>.</param>
/// <param name="Label">Name at the bottom of the popup and in the tooltip.</param>
/// <param name="Shortcut">Key while the popup is open: "Shift+Enter", "Ctrl+C", "Delete" … (Enter and a click always choose the first action, Esc closes).</param>
public sealed record ChoiceAction(string Id, string Label, string? Shortcut = null)
{
    public string? Icon { get; init; }

    /// <summary>
    /// The popup stays open and the core calls <see cref="ChoicePopupRequest.OnAction"/> – for changing the list
    /// (keep, delete). Without it the popup closes and the choice comes back.
    /// </summary>
    public bool KeepOpen { get; init; }
}

/// <summary>Where the popup appears.</summary>
public enum ChoicePlacement
{
    /// <summary>At the text cursor of the window in front, if Windows tells where it is – otherwise at the mouse.</summary>
    Caret,
    Mouse,

    /// <summary>In the middle of the monitor under the mouse.</summary>
    Center,
}

/// <summary>A new list for an open popup (answer of <see cref="ChoicePopupRequest.OnAction"/>).</summary>
public sealed record ChoicePopupUpdate(IReadOnlyList<ChoiceItem> Items)
{
    public string? Hint { get; init; }

    /// <summary>Row to select afterwards; null = the one at the same position.</summary>
    public string? SelectId { get; init; }

    /// <summary>Close the popup after all.</summary>
    public bool Close { get; init; }
}

/// <summary>What the choice popup shows (<see cref="IChoicePopup"/>).</summary>
public sealed record ChoicePopupRequest(IReadOnlyList<ChoiceItem> Items)
{
    /// <summary>Heading, e.g. "Clipboard".</summary>
    public string? Title { get; init; }

    /// <summary>Short notice under the heading, e.g. "History paused".</summary>
    public string? Hint { get; init; }

    /// <summary>Text in the empty search field.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Shown when the list (or the search) is empty.</summary>
    public string? EmptyText { get; init; }

    /// <summary>The actions; the first one is Enter and a click. Shown with their keys at the bottom of the popup.</summary>
    public IReadOnlyList<ChoiceAction> Actions { get; init; } = [];

    public ChoicePlacement Placement { get; init; } = ChoicePlacement.Caret;

    /// <summary>Called (through the module guard) for actions with <see cref="ChoiceAction.KeepOpen"/>; null answer = list unchanged.</summary>
    public Func<ChoicePopupResult, CancellationToken, Task<ChoicePopupUpdate?>>? OnAction { get; init; }
}

/// <summary>The row and action the user chose.</summary>
/// <param name="ItemId"><see cref="ChoiceItem.Id"/> of the row.</param>
/// <param name="ActionId"><see cref="ChoiceAction.Id"/> (Enter and a click: the first action).</param>
/// <param name="Target">The window that was in front before the popup opened – for <see cref="IPasteService.PasteAsync"/>.</param>
public sealed record ChoicePopupResult(string ItemId, string ActionId, PasteTarget? Target = null);

/// <summary>
/// A popup list with search (contract 1.7), drawn by the core in the style of the quick search: typing searches,
/// arrow keys select, actions with keys, at the text cursor on the right monitor and scale. Offered on the
/// <see cref="IModuleContext"/> (<c>context as IChoicePopup</c>). One popup at a time: a second call while one is
/// open closes it (both return null) – the same hotkey opens and closes it.
/// </summary>
public interface IChoicePopup
{
    /// <summary>The choice, or null (Esc, clicked elsewhere, closed).</summary>
    Task<ChoicePopupResult?> ShowChoicesAsync(ChoicePopupRequest request, CancellationToken cancellationToken);
}

/// <summary>State of a taken-over hotkey (<see cref="IHotkeyTakeover"/>).</summary>
public enum HotkeyTakeoverState
{
    /// <summary>Active: Windows no longer gets the key, the module does.</summary>
    Active,

    /// <summary>The keyboard hook could not be set (yet) – the core tries again.</summary>
    Retrying,

    /// <summary>Not possible on this PC; Windows keeps the hotkey.</summary>
    Failed,
}

/// <summary>A hotkey taken over from Windows; dispose to give it back.</summary>
public interface IHotkeyTakeover : IDisposable
{
    HotkeyTakeoverState State { get; }

    /// <summary>Why it is not active (in the UI language), null when active.</summary>
    string? Problem { get; }

    /// <summary>The state changed (through the module guard).</summary>
    event EventHandler? StateChanged;
}

/// <summary>
/// Takes over a hotkey that Windows keeps for itself and that cannot be registered normally, e.g. Win+V (contract 1.7),
/// offered on the <see cref="IModuleContext"/> (<c>context as IHotkeyTakeoverService</c>). The core uses a lean
/// low-level keyboard hook on its own thread that only recognizes the hotkey and returns at once; it exists only
/// while a takeover is active. Stopping the module gives every hotkey back.
/// </summary>
public interface IHotkeyTakeoverService
{
    /// <summary>"Win+V"; <paramref name="pressed"/> is called through the module guard. Null for a hotkey that cannot be read.</summary>
    IHotkeyTakeover? TakeOverHotkey(string hotkey, Action pressed);
}

/// <summary>An entry of the module in the tray menu (<see cref="ITrayMenuProvider"/>).</summary>
public sealed record TrayMenuEntry(string Id, string Label)
{
    /// <summary>With a check mark (true/false) – or a normal entry (null).</summary>
    public bool? Checked { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Entries in the tray menu of ue Bar (contract 1.7), read each time the menu opens; a click calls
/// <see cref="RunTrayMenuEntryAsync"/> through the module guard. Keep it to one or two entries.
/// </summary>
public interface ITrayMenuProvider
{
    IReadOnlyList<TrayMenuEntry> TrayMenuEntries { get; }

    Task RunTrayMenuEntryAsync(string entryId, CancellationToken cancellationToken);
}
