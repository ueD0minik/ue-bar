// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

public sealed record TriggerZone(MonitorInfo Monitor, PixelRect Rect);

/// <summary>Calculations in physical pixels – independent of the DPI scaling of other monitors.</summary>
public static class BarGeometry
{
    /// <summary>
    /// Trigger zone: top pixel row(s), centered, <see cref="BarSettings.TriggerWidthPercent"/> wide.
    /// Limit: if another monitor sits above this one, the top row is not a real edge –
    /// the mouse then simply moves on upwards.
    /// </summary>
    public static PixelRect TriggerRect(MonitorInfo monitor, BarSettings settings)
    {
        var percent = Math.Clamp(settings.TriggerWidthPercent, 5, 100) / 100.0;
        var width = (int)Math.Round(monitor.Bounds.Width * percent);
        var x = monitor.Bounds.X + ((monitor.Bounds.Width - width) / 2);
        var height = Math.Max(1, settings.TriggerHeightPx);
        return new PixelRect(x, monitor.Bounds.Y, width, height);
    }

    public static IReadOnlyList<TriggerZone> TriggerZones(IReadOnlyList<MonitorInfo> monitors, BarSettings settings) =>
        [.. MonitorSelector.TriggerCandidates(monitors, settings).Select(m => new TriggerZone(m, TriggerRect(m, settings)))];

    /// <summary>Size in DIP → physical pixels on this monitor.</summary>
    public static (int Width, int Height) ToPixels(MonitorInfo monitor, double widthDip, double heightDip) =>
        ((int)Math.Ceiling(widthDip * monitor.Scale), (int)Math.Ceiling(heightDip * monitor.Scale));

    /// <summary>Window at the top center of the monitor edge.</summary>
    public static PixelRect TopCenter(MonitorInfo monitor, double widthDip, double heightDip)
    {
        var (w, h) = ToPixels(monitor, widthDip, heightDip);
        var x = monitor.Bounds.X + ((monitor.Bounds.Width - w) / 2);
        return new PixelRect(x, monitor.Bounds.Y, w, h);
    }

    /// <summary>Window at the saved position (relative to the monitor), clamped into the monitor.</summary>
    public static PixelRect AtOffset(MonitorInfo monitor, PixelPoint offset, double widthDip, double heightDip)
    {
        var (w, h) = ToPixels(monitor, widthDip, heightDip);
        return Clamp(new PixelRect(monitor.Bounds.X + offset.X, monitor.Bounds.Y + offset.Y, w, h), monitor.Bounds);
    }

    /// <summary>Moves <paramref name="rect"/> so that it lies within <paramref name="area"/> (as far as possible).</summary>
    public static PixelRect Clamp(PixelRect rect, PixelRect area)
    {
        var x = Math.Max(area.X, Math.Min(rect.X, area.Right - rect.Width));
        var y = Math.Max(area.Y, Math.Min(rect.Y, area.Bottom - rect.Height));
        return rect with { X = x, Y = y };
    }

    /// <summary>Offset of a window relative to the top-left monitor corner.</summary>
    public static PixelPoint OffsetIn(MonitorInfo monitor, PixelRect rect) =>
        new(rect.X - monitor.Bounds.X, rect.Y - monitor.Bounds.Y);

    /// <summary>
    /// Area in which the mouse counts as "on the bar": the bar itself, extended up to the
    /// monitor edge (gap between edge and pill), plus some tolerance.
    /// </summary>
    public static PixelRect HitArea(PixelRect bar, MonitorInfo monitor, int tolerance)
    {
        var top = Math.Min(bar.Y, monitor.Bounds.Y);
        return PixelRect.FromEdges(bar.X, top, bar.Right, bar.Bottom).Inflate(tolerance, tolerance);
    }
}
