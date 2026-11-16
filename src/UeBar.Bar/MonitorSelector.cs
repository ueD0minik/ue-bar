// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>Monitor choice according to SPEC 5.3.</summary>
public static class MonitorSelector
{
    public static MonitorInfo? Primary(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

    public static MonitorInfo? FindById(IReadOnlyList<MonitorInfo> monitors, string? id) =>
        id is null ? null : monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static MonitorInfo? FromPoint(IReadOnlyList<MonitorInfo> monitors, PixelPoint point) =>
        monitors.FirstOrDefault(m => m.Bounds.Contains(point));

    /// <summary>
    /// Which monitors have a trigger zone. If the chosen monitor is missing
    /// (laptop on the road), the primary monitor applies.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> TriggerCandidates(IReadOnlyList<MonitorInfo> monitors, BarSettings settings)
    {
        if (monitors.Count == 0)
        {
            return [];
        }

        return settings.MonitorMode switch
        {
            MonitorMode.UnderMouse => monitors,
            MonitorMode.Specific => [FindById(monitors, settings.MonitorId) ?? Primary(monitors)!],
            _ => [Primary(monitors)!],
        };
    }

    /// <summary>
    /// Monitor on which the bar appears without the mouse at the edge (notification, quick search): "under the
    /// mouse" → the mouse pointer's monitor, "specific" → that one, otherwise primary.
    /// </summary>
    public static MonitorInfo? ForAnnouncement(IReadOnlyList<MonitorInfo> monitors, BarSettings settings, PixelPoint? cursor) =>
        settings.MonitorMode switch
        {
            MonitorMode.UnderMouse when cursor is { } p => FromPoint(monitors, p) ?? Primary(monitors),
            MonitorMode.Specific => FindById(monitors, settings.MonitorId) ?? Primary(monitors),
            _ => Primary(monitors),
        };

    /// <summary>Monitor for a pinned bar: saved monitor, otherwise the chosen one, otherwise primary.</summary>
    public static MonitorInfo? ForPinned(IReadOnlyList<MonitorInfo> monitors, BarSettings settings) =>
        FindById(monitors, settings.PinnedMonitorId)
        ?? (settings.MonitorMode == MonitorMode.Specific ? FindById(monitors, settings.MonitorId) : null)
        ?? Primary(monitors);
}
