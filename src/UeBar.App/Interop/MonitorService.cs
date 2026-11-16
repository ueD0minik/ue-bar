// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Bar;

namespace UeBar.App.Interop;

/// <summary>Reads the monitors (physical pixels, DPI, stable ID, display name).</summary>
internal static class MonitorService
{
    private const int MDT_EFFECTIVE_DPI = 0;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var names = DisplayConfig.Query();
        var list = new List<MonitorInfo>();
        var index = 0;
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            index++;
            var info = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            var scale = Win32.GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
                ? dpiX / 96.0
                : 1.0;

            var gdiName = info.szDevice ?? $"DISPLAY{index}";
            names.TryGetValue(gdiName, out var display);
            var id = string.IsNullOrWhiteSpace(display?.DevicePath) ? gdiName : display.DevicePath;
            var friendly = string.IsNullOrWhiteSpace(display?.FriendlyName) ? gdiName.TrimStart('\\', '.') : display.FriendlyName;

            list.Add(new MonitorInfo(
                id,
                friendly,
                ToRect(info.rcMonitor),
                ToRect(info.rcWork),
                (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                scale));
            return true;
        }, IntPtr.Zero);

        // Make equal display names (two identical monitors) distinguishable.
        foreach (var group in list.GroupBy(m => m.Name).Where(g => g.Count() > 1))
        {
            var n = 1;
            foreach (var monitor in group.ToList())
            {
                list[list.IndexOf(monitor)] = monitor with { Name = $"{monitor.Name} ({n++})" };
            }
        }

        return list;
    }

    public static PixelRect ToRect(Win32.RECT r) => PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);

    public static PixelPoint? CursorPosition() =>
        Win32.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;
}
