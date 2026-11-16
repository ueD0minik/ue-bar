// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Bar;

namespace UeBar.App.Interop;

/// <summary>
/// Fullscreen detection (SPEC 5.4): presentation mode always blocks; otherwise the
/// foreground window must be on the target monitor and either be reported as fullscreen
/// (SHQueryUserNotificationState: Busy / D3D fullscreen) or cover the whole monitor.
/// That way a game on monitor 2 does not block the bar on monitor 1.
/// </summary>
internal sealed class FullscreenDetector(Func<IntPtr> ownWindow)
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    public bool IsFullscreenOn(MonitorInfo monitor)
    {
        var state = 0;
        var hasState = Win32.SHQueryUserNotificationState(out state) == 0;
        if (hasState && state == Win32.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownWindow() || foreground == Win32.GetShellWindow()
            || ShellClasses.Contains(Win32.ClassNameOf(foreground)))
        {
            return false;
        }

        if (Win32.WindowBounds(foreground) is not { } bounds)
        {
            return false;
        }

        var window = MonitorService.ToRect(bounds);
        if (!monitor.Bounds.Contains(window.Center))
        {
            return false; // foreground window is on another monitor
        }

        if (hasState && state is Win32.QUNS_BUSY or Win32.QUNS_RUNNING_D3D_FULL_SCREEN)
        {
            return true;
        }

        var style = (long)Win32.GetWindowLongPtr(foreground, Win32.GWL_STYLE);
        var hasCaption = (style & Win32.WS_CAPTION) == Win32.WS_CAPTION;
        return FullscreenRules.IsFullscreenWindow(window, monitor.Bounds, Win32.IsZoomed(foreground), hasCaption);
    }
}
