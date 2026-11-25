// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>
/// What the host offers every module on its context (P2-5). The core keeps the pins itself; picking a
/// screen area and the clipboard need the UI – the app sets them. Without them the module gets null/false.
/// </summary>
public sealed class HostServices
{
    public PinManager? Pins { get; set; }

    public IScreenAreaPicker? ScreenAreaPicker { get; set; }

    public IClipboardAccess? Clipboard { get; set; }

    /// <summary>"Save as" dialog (P2-5-Fix 1); the module ID decides which last folder is used.</summary>
    public Func<string, SaveFileRequest, CancellationToken, Task<string?>>? SaveDialog { get; set; }

    /// <summary>Clipboard changes (P2-6, contract 1.7).</summary>
    public IHostClipboardWatcher? ClipboardWatcher { get; set; }

    /// <summary>Any content to the clipboard, remembering which module wrote it (P2-6).</summary>
    public IHostClipboardWriter? ClipboardWriter { get; set; }

    /// <summary>Paste into the window that was in front (P2-6).</summary>
    public IHostPaste? Paste { get; set; }

    /// <summary>The choice popup (P2-6).</summary>
    public IHostChoicePopup? ChoicePopup { get; set; }

    /// <summary>Hotkeys taken over from Windows with a keyboard hook (P2-6).</summary>
    public IHostHotkeyTakeover? HotkeyTakeover { get; set; }

    /// <summary>IDs the host uses itself (bus owner of the pins) – no module may take them.</summary>
    public static IReadOnlySet<string> ReservedIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PinManager.HostId };
}

/// <summary>The app's clipboard watcher (P2-6). <paramref name="changed"/> may be called on any thread.</summary>
public interface IHostClipboardWatcher
{
    IDisposable Watch(string moduleId, ClipboardWatchOptions options, Action<ClipboardChange> changed);
}

/// <summary>Writes content for a module (the change then reports it as <see cref="ClipboardChange.WrittenByModule"/>).</summary>
public interface IHostClipboardWriter
{
    Task<bool> SetContentAsync(string moduleId, ClipboardContent content, CancellationToken cancellationToken);
}

/// <summary>Focus back to a window and Ctrl+V (P2-6).</summary>
public interface IHostPaste
{
    PasteTarget? CurrentTarget();

    Task<bool> PasteAsync(string moduleId, ClipboardContent content, PasteTarget? target, CancellationToken cancellationToken);
}

/// <summary>Shows the choice popup (P2-6); <see cref="ChoicePopupRequest.OnAction"/> is already guarded by the context.</summary>
public interface IHostChoicePopup
{
    Task<ChoicePopupResult?> ShowAsync(string moduleId, ChoicePopupRequest request, CancellationToken cancellationToken);
}

/// <summary>Keyboard hook for hotkeys Windows keeps for itself (P2-6). <paramref name="pressed"/> may come on any thread.</summary>
public interface IHostHotkeyTakeover
{
    IHotkeyTakeover? TakeOver(string moduleId, string hotkey, Action pressed);
}
