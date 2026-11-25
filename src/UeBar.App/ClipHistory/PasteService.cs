// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Threading;
using UeBar.App.Infrastructure;
using UeBar.App.Interop;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.ClipHistory;

/// <summary>
/// Pastes for plugins (P2-6, <see cref="IPasteService"/>): content to the clipboard (as the module's own write),
/// focus back to the target window, wait until the user has let go of Shift/Ctrl/Alt/Win (otherwise the target would
/// get Ctrl+Shift+V …), then Ctrl+V with SendInput. Short one-off waits only, no timer.
/// </summary>
internal sealed class PasteService(Dispatcher dispatcher, ClipboardAccess clipboard, IUeLogger log) : IHostPaste
{
    private static readonly TimeSpan FocusWait = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ModifierWait = TimeSpan.FromMilliseconds(1500);

    public PasteTarget? CurrentTarget() => TargetOf(Win32.GetForegroundWindow());

    /// <summary>A window that can take a paste – not ours, not the desktop.</summary>
    public static PasteTarget? TargetOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == Win32.GetShellWindow())
        {
            return null;
        }

        ClipInterop.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId ? null : new PasteTarget(hwnd.ToInt64()) { Title = ClipInterop.WindowTitle(hwnd) };
    }

    public async Task<bool> PasteAsync(string moduleId, ClipboardContent content, PasteTarget? target, CancellationToken cancellationToken)
    {
        if (!await clipboard.SetContentAsync(moduleId, content, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var hwnd = target is { } t ? new IntPtr(t.WindowHandle) : Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd) || TargetOf(hwnd) is null)
        {
            // Gone, the desktop, or ue Bar itself (opened from the quick search without a window behind it).
            log.Info(moduleId, "Paste: no window to paste into – content left on the clipboard");
            return false;
        }

        var focused = await dispatcher.InvokeAsync(() => Focus(hwnd));
        var until = DateTime.UtcNow + FocusWait;
        while (Win32.GetForegroundWindow() != hwnd && DateTime.UtcNow < until)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        if (Win32.GetForegroundWindow() != hwnd)
        {
            log.Info(moduleId, $"Paste: window did not come to the front (focus call {focused}) – content left on the clipboard");
            return false;
        }

        until = DateTime.UtcNow + ModifierWait;
        while (AnyModifierDown() && DateTime.UtcNow < until)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        // Still held (the user keeps Shift down): release them for the target, so it gets a plain Ctrl+V.
        var release = new List<ClipInterop.INPUT>();
        foreach (var vk in new[] { ClipInterop.VK_SHIFT, ClipInterop.VK_MENU, ClipInterop.VK_LWIN, ClipInterop.VK_RWIN })
        {
            if (ClipInterop.IsDown(vk))
            {
                release.Add(ClipInterop.Key(vk, up: true));
            }
        }

        var sent = ClipInterop.Send([
            .. release,
            ClipInterop.Key(ClipInterop.VK_CONTROL, up: false),
            ClipInterop.Key(ClipInterop.VK_V, up: false),
            ClipInterop.Key(ClipInterop.VK_V, up: true),
            ClipInterop.Key(ClipInterop.VK_CONTROL, up: true),
        ]);
        log.Info(moduleId, sent ? "Paste: Ctrl+V sent" : "Paste: Ctrl+V could not be sent (blocked by Windows)");
        return sent;
    }

    private static bool AnyModifierDown() =>
        ClipInterop.IsDown(ClipInterop.VK_SHIFT) || ClipInterop.IsDown(ClipInterop.VK_CONTROL) || ClipInterop.IsDown(ClipInterop.VK_MENU)
        || ClipInterop.IsDown(ClipInterop.VK_LWIN) || ClipInterop.IsDown(ClipInterop.VK_RWIN);

    /// <summary>Brings a window to the front; we are allowed to, as long as our popup had the focus.</summary>
    public static bool Focus(IntPtr hwnd)
    {
        if (ClipInterop.IsIconic(hwnd))
        {
            ClipInterop.ShowWindow(hwnd, 9); // SW_RESTORE
        }

        if (Win32.SetForegroundWindow(hwnd))
        {
            return true;
        }

        // Fallback: share the input state with the current foreground thread for a moment.
        var foreground = Win32.GetForegroundWindow();
        var current = ClipInterop.GetCurrentThreadId();
        var other = ClipInterop.GetWindowThreadProcessId(foreground, out _);
        var attached = other != 0 && other != current && ClipInterop.AttachThreadInput(current, other, true);
        try
        {
            ClipInterop.BringWindowToTop(hwnd);
            return Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                ClipInterop.AttachThreadInput(current, other, false);
            }
        }
    }
}
