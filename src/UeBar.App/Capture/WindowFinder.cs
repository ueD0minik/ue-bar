// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.App.Interop;
using UeBar.Bar;

namespace UeBar.App.Capture;

/// <summary>A visible top-level window at the moment picking started (P2-5).</summary>
internal sealed record ScreenWindow(IntPtr Handle, PixelRect Bounds, string Title);

/// <summary>
/// The visible windows from top to bottom (P2-5) – read once when picking starts, so the frozen screen and
/// the windows match. Hidden, minimized and cloaked windows (other virtual desktops, suspended apps) and
/// the desktop itself are left out: a click there takes the monitor.
/// </summary>
internal static class WindowFinder
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW" };

    public static IReadOnlyList<ScreenWindow> Snapshot(IReadOnlyCollection<IntPtr> exclude)
    {
        var list = new List<ScreenWindow>();
        PinInterop.EnumWindows((hwnd, _) =>
        {
            if (exclude.Contains(hwnd) || !PinInterop.IsWindowVisible(hwnd) || PinInterop.IsIconic(hwnd) || PinInterop.IsCloaked(hwnd)
                || Skipped.Contains(Win32.ClassNameOf(hwnd)))
            {
                return true;
            }

            if (Win32.WindowBounds(hwnd) is { } r && MonitorService.ToRect(r) is { IsEmpty: false } bounds && bounds.Width > 8 && bounds.Height > 8)
            {
                list.Add(new ScreenWindow(hwnd, bounds, PinInterop.TitleOf(hwnd)));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The topmost window at a point.</summary>
    public static ScreenWindow? At(IReadOnlyList<ScreenWindow> windows, PixelPoint point) =>
        windows.FirstOrDefault(w => w.Bounds.Contains(point));
}
