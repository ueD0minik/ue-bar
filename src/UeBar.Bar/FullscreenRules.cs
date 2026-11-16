// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>Pure rules of fullscreen detection (SPEC 5.4); the Win32 queries are done by the app.</summary>
public static class FullscreenRules
{
    /// <summary>Window covers the whole monitor (not only the work area).</summary>
    public static bool CoversMonitor(PixelRect window, PixelRect monitor) => window.Contains(monitor);

    /// <summary>
    /// Fullscreen = covers the whole monitor, but is neither maximized nor has a
    /// title bar. Reason: with an auto-hiding taskbar even a normally
    /// maximized window covers the whole monitor. Browser fullscreen (F11) and borderless games
    /// remove the maximization and the title bar.
    /// </summary>
    public static bool IsFullscreenWindow(PixelRect window, PixelRect monitor, bool isMaximized, bool hasCaption) =>
        CoversMonitor(window, monitor) && !isMaximized && !hasCaption;
}
