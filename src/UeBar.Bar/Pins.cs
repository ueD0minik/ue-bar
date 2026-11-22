// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.Bar;

/// <summary>Settings of the pins (P2-5), section "pins" in settings.json.</summary>
public sealed class PinSettings
{
    public const string Section = "pins";

    public const string DefaultHotkey = "Win+Alt+P";

    /// <summary>Dim after this long without the mouse over the pin; 0 = never.</summary>
    public int DimAfterSeconds { get; set; } = 60;

    /// <summary>Brightness while dimmed, 0.1–1 (0.35 = 35 %).</summary>
    public double DimLevel { get; set; } = 0.35;

    /// <summary>Move all pins by 1–2 px every few minutes (OLED).</summary>
    public bool PixelShift { get; set; } = true;

    /// <summary>Hide pins on a monitor while a fullscreen app runs there.</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Show/hide all pins; empty = no hotkey.</summary>
    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Font size of new text pins.</summary>
    public double FontSize { get; set; } = 15;

    public TimeSpan DimAfter => DimAfterSeconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Clamp(DimAfterSeconds, 5, 3600));

    public double EffectiveDimLevel => double.IsFinite(DimLevel) ? Math.Clamp(DimLevel, 0.1, 1) : 0.35;
}

/// <summary>
/// Where pins go on the screen (P2-5), WPF-free: physical pixels, a placement per monitor (offset from its
/// corner in physical pixels, size in device-independent pixels), a missing monitor falls back to the
/// primary one, edges snap to the work areas.
/// </summary>
public static class PinGeometry
{
    public const double TextWidth = 300;
    public const double TextHeight = 170;
    public const double MinSize = 40;
    public const int CascadeStep = 28;

    /// <summary>Distance (device-independent pixels) at which an edge snaps.</summary>
    public const double SnapDistance = 14;

    /// <summary>The monitor a placement belongs to: by its ID, otherwise the primary one (or the first).</summary>
    public static MonitorInfo? MonitorOf(PinPlacement? placement, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => placement?.Monitor is { } id && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Primary(monitors);

    public static MonitorInfo? Primary(IReadOnlyList<MonitorInfo> monitors) => monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

    /// <summary>
    /// The window rectangle for a saved placement. On a missing monitor the pin keeps its offset and size
    /// on the primary monitor; it is always moved fully into the work area (shrunk if it does not fit).
    /// </summary>
    public static PixelRect? Resolve(PinPlacement placement, IReadOnlyList<MonitorInfo> monitors)
    {
        if (MonitorOf(placement, monitors) is not { } monitor)
        {
            return null;
        }

        var width = (int)Math.Round(Math.Max(MinSize, placement.Width) * monitor.Scale);
        var height = (int)Math.Round(Math.Max(MinSize, placement.Height) * monitor.Scale);
        var rect = new PixelRect(monitor.Bounds.X + placement.X, monitor.Bounds.Y + placement.Y, width, height);
        return FitInto(rect, monitor.WorkArea);
    }

    /// <summary>The placement of a window rectangle: on the monitor with the largest part of it.</summary>
    public static PinPlacement ToPlacement(PixelRect window, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = MonitorAt(window, monitors);
        if (monitor is null)
        {
            return new PinPlacement { X = window.X, Y = window.Y, Width = window.Width, Height = window.Height };
        }

        return new PinPlacement
        {
            Monitor = monitor.Id,
            X = window.X - monitor.Bounds.X,
            Y = window.Y - monitor.Bounds.Y,
            Width = Math.Round(window.Width / monitor.Scale, 1),
            Height = Math.Round(window.Height / monitor.Scale, 1),
        };
    }

    /// <summary>Monitor with the largest overlap (the primary one if none overlaps).</summary>
    public static MonitorInfo? MonitorAt(PixelRect rect, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Select(m => (Monitor: m, Area: Overlap(m.Bounds, rect)))
            .Where(x => x.Area > 0)
            .OrderByDescending(x => x.Area)
            .Select(x => x.Monitor)
            .FirstOrDefault() ?? Primary(monitors);

    public static MonitorInfo? MonitorAt(PixelPoint point, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => m.Bounds.Contains(point)) ?? Primary(monitors);

    /// <summary>
    /// Where a pin appears the first time. Image from the screen (screenshot): exactly over the source
    /// (the same pixels). Other images: in their real pixel size (at most 60 % of the work area) in the
    /// middle of the monitor under the mouse. Text: top right on that monitor. New pins cascade a little.
    /// </summary>
    public static PixelRect Initial(PinRecord pin, IReadOnlyList<MonitorInfo> monitors, PixelPoint? cursor, int index)
    {
        var monitor = (cursor is { } c ? MonitorAt(c, monitors) : null) ?? Primary(monitors);
        if (monitor is null)
        {
            return new PixelRect(100, 100, 300, 170);
        }

        var step = (int)Math.Round(CascadeStep * monitor.Scale) * (index % 8);
        var work = monitor.WorkArea;
        if (pin.Kind == PinKind.Image && pin.Source is { } source && source.Width > 0 && source.Height > 0)
        {
            var exact = new PixelRect(source.X, source.Y, source.Width, source.Height);
            var target = MonitorAt(exact, monitors) ?? monitor;
            var minimum = (int)Math.Round(MinSize * target.Scale);
            exact = exact with { Width = Math.Max(minimum, exact.Width), Height = Math.Max(minimum, exact.Height) };
            return FitInto(exact, target.WorkArea);
        }

        if (pin.Kind == PinKind.Image && pin.PixelWidth > 0 && pin.PixelHeight > 0)
        {
            var (w, h) = FitSize(pin.PixelWidth, pin.PixelHeight, work.Width * 0.6, work.Height * 0.6);
            var minimum = MinSize * monitor.Scale;
            w = Math.Max(minimum, w);
            h = Math.Max(minimum, h);
            var rect = new PixelRect(work.X + (int)((work.Width - w) / 2) + step, work.Y + (int)((work.Height - h) / 2) + step, (int)Math.Round(w), (int)Math.Round(h));
            return FitInto(rect, work);
        }

        var width = (int)Math.Round((pin.Kind == PinKind.Image ? 320 : TextWidth) * monitor.Scale);
        var height = (int)Math.Round((pin.Kind == PinKind.Image ? 240 : TextHeight) * monitor.Scale);
        var margin = (int)Math.Round(32 * monitor.Scale);
        var text = new PixelRect(work.Right - width - margin - step, work.Y + margin + step, width, height);
        return FitInto(text, work);
    }

    /// <summary>Is a pin hidden because a fullscreen app runs on its monitor (setting "Hide pins over fullscreen apps")?</summary>
    public static bool HiddenByFullscreen(PixelRect pin, IReadOnlyList<MonitorInfo> monitors, IReadOnlyCollection<string> fullscreenMonitorIds, bool enabled) =>
        enabled && fullscreenMonitorIds.Count > 0 && MonitorAt(pin, monitors) is { } monitor && fullscreenMonitorIds.Contains(monitor.Id);

    /// <summary>Largest size with the same aspect ratio that fits into max (never larger than the original).</summary>
    public static (double Width, double Height) FitSize(double width, double height, double maxWidth, double maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return (maxWidth, maxHeight);
        }

        var factor = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return (width * factor, height * factor);
    }

    /// <summary>Moves (and if needed shrinks) a rectangle so it lies fully in the area.</summary>
    public static PixelRect FitInto(PixelRect rect, PixelRect area)
    {
        if (area.IsEmpty)
        {
            return rect;
        }

        var width = Math.Min(rect.Width, area.Width);
        var height = Math.Min(rect.Height, area.Height);
        var x = Math.Clamp(rect.X, area.X, area.Right - width);
        var y = Math.Clamp(rect.Y, area.Y, area.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Snaps a moved window to the edges of the work areas (outer edges flush, also the edges of other
    /// monitors): an edge within <paramref name="threshold"/> physical pixels jumps onto it, the size stays.
    /// </summary>
    public static PixelRect SnapMove(PixelRect rect, IEnumerable<PixelRect> areas, int threshold)
    {
        var list = areas.ToList();
        var dx = BestShift([rect.X, rect.Right], list.SelectMany(a => new[] { a.X, a.Right }), threshold);
        var dy = BestShift([rect.Y, rect.Bottom], list.SelectMany(a => new[] { a.Y, a.Bottom }), threshold);
        return rect with { X = rect.X + dx, Y = rect.Y + dy };
    }

    /// <summary>Snaps the edges that move while resizing (the others stay where they are).</summary>
    public static PixelRect SnapResize(PixelRect rect, IEnumerable<PixelRect> areas, int threshold, bool left, bool top, bool right, bool bottom)
    {
        var list = areas.ToList();
        int Snap(int value, IEnumerable<int> edges) => value + BestShift([value], edges, threshold);

        var xs = list.SelectMany(a => new[] { a.X, a.Right }).ToList();
        var ys = list.SelectMany(a => new[] { a.Y, a.Bottom }).ToList();
        var l = left ? Snap(rect.X, xs) : rect.X;
        var t = top ? Snap(rect.Y, ys) : rect.Y;
        var r = right ? Snap(rect.Right, xs) : rect.Right;
        var b = bottom ? Snap(rect.Bottom, ys) : rect.Bottom;
        return r - l > 0 && b - t > 0 ? PixelRect.FromEdges(l, t, r, b) : rect;
    }

    /// <summary>Smallest shift that puts one of <paramref name="own"/> onto one of <paramref name="edges"/> (0 if none is near).</summary>
    private static int BestShift(int[] own, IEnumerable<int> edges, int threshold)
    {
        var best = 0;
        var bestDistance = threshold + 1;
        foreach (var edge in edges.Distinct())
        {
            foreach (var value in own)
            {
                var distance = Math.Abs(edge - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = edge - value;
                }
            }
        }

        return bestDistance <= threshold ? best : 0;
    }

    private static long Overlap(PixelRect a, PixelRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? (long)w * h : 0;
    }
}

/// <summary>
/// OLED protection of the pins (P2-5), WPF-free: a pin is dimmed after <see cref="PinSettings.DimAfter"/>
/// without the mouse over it and lights up at once when the mouse comes; all pins move by a pixel or two
/// every few minutes.
/// </summary>
public sealed class PinGlow
{
    public static readonly TimeSpan ShiftInterval = TimeSpan.FromMinutes(3);

    private static readonly PixelPoint[] ShiftPath = [new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1), new(2, 0)];

    private readonly Dictionary<string, State> _pins = new(StringComparer.Ordinal);
    private DateTimeOffset _shiftSince;
    private int _shiftIndex;

    public PinGlow(DateTimeOffset now)
    {
        _shiftSince = now;
    }

    public TimeSpan DimAfter { get; set; } = TimeSpan.FromSeconds(60);

    public bool PixelShift { get; set; } = true;

    /// <summary>Current pixel shift of all pins (physical pixels).</summary>
    public PixelPoint Offset => PixelShift ? ShiftPath[_shiftIndex] : new PixelPoint(0, 0);

    public bool IsDimmed(string id) => _pins.TryGetValue(id, out var s) && s.Dimmed;

    /// <summary>A pin is shown (again): starts bright.</summary>
    public void Track(string id, DateTimeOffset now) => _pins[id] = new State { LastActive = now };

    public void Forget(string id) => _pins.Remove(id);

    /// <summary>Mouse over the pin or not; returns true if the pin was dimmed and lights up now.</summary>
    public bool Hover(string id, bool inside, DateTimeOffset now)
    {
        if (!_pins.TryGetValue(id, out var state))
        {
            state = new State();
            _pins[id] = state;
        }

        state.Hovered = inside;
        state.LastActive = now;
        if (inside && state.Dimmed)
        {
            state.Dimmed = false;
            return true;
        }

        return false;
    }

    /// <summary>Lights a pin up as if the mouse had been there (e.g. it was just moved by hand).</summary>
    public bool Touch(string id, DateTimeOffset now) => Hover(id, _pins.TryGetValue(id, out var s) && s.Hovered, now) | Wake(id);

    private bool Wake(string id)
    {
        if (_pins.TryGetValue(id, out var s) && s.Dimmed)
        {
            s.Dimmed = false;
            return true;
        }

        return false;
    }

    /// <summary>Advances time: pins whose dimming changed, and whether the pixel shift moved.</summary>
    public (IReadOnlyList<string> Changed, bool Shifted) Update(DateTimeOffset now)
    {
        var changed = new List<string>();
        foreach (var (id, state) in _pins)
        {
            var dim = DimAfter > TimeSpan.Zero && !state.Hovered && now - state.LastActive >= DimAfter;
            if (dim != state.Dimmed)
            {
                state.Dimmed = dim;
                changed.Add(id);
            }
        }

        var shifted = false;
        if (now - _shiftSince >= ShiftInterval)
        {
            _shiftSince = now;
            if (PixelShift)
            {
                _shiftIndex = (_shiftIndex + 1) % ShiftPath.Length;
                shifted = true;
            }
        }

        return (changed, shifted);
    }

    /// <summary>When something can change next (to set the timer), null = nothing pending.</summary>
    public TimeSpan? NextDue(DateTimeOffset now)
    {
        TimeSpan? next = null;
        if (DimAfter > TimeSpan.Zero)
        {
            foreach (var state in _pins.Values.Where(s => !s.Dimmed && !s.Hovered))
            {
                var due = state.LastActive + DimAfter - now;
                next = next is null || due < next ? due : next;
            }
        }

        if (PixelShift && _pins.Count > 0)
        {
            var shift = _shiftSince + ShiftInterval - now;
            next = next is null || shift < next ? shift : next;
        }

        return next is { } n && n < TimeSpan.Zero ? TimeSpan.Zero : next;
    }

    private sealed class State
    {
        public DateTimeOffset LastActive { get; set; }

        public bool Hovered { get; set; }

        public bool Dimmed { get; set; }
    }
}

/// <summary>
/// Picking a screen area (P2-5), WPF-free: one overlay window per monitor; the mouse comes in physical
/// pixels of the virtual desktop. Each overlay maps physical pixels to its own device-independent pixels
/// from its real size – right with every scaling, also when a rectangle spans monitors.
/// </summary>
public static class ScreenSelection
{
    /// <summary>Less movement than this (physical pixels) is a click, not a drag.</summary>
    public const int ClickTolerance = 4;

    /// <summary>The whole desktop (union of all monitors).</summary>
    public static PixelRect VirtualBounds(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Count == 0 ? default : monitors.Skip(1).Aggregate(monitors[0].Bounds, (all, m) => all.Union(m.Bounds));

    /// <summary>Rectangle between press and current point, clipped to the desktop; both corners count.</summary>
    public static PixelRect FromDrag(PixelPoint start, PixelPoint current, PixelRect desktop)
    {
        var rect = PixelRect.FromEdges(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Max(start.X, current.X) + 1, Math.Max(start.Y, current.Y) + 1);
        return Clip(rect, desktop);
    }

    public static bool IsClick(PixelPoint start, PixelPoint end) =>
        Math.Abs(end.X - start.X) <= ClickTolerance && Math.Abs(end.Y - start.Y) <= ClickTolerance;

    public static PixelRect Clip(PixelRect rect, PixelRect area)
    {
        var left = Math.Max(rect.X, area.X);
        var top = Math.Max(rect.Y, area.Y);
        var right = Math.Min(rect.Right, area.Right);
        var bottom = Math.Min(rect.Bottom, area.Bottom);
        return right > left && bottom > top ? PixelRect.FromEdges(left, top, right, bottom) : default;
    }

    /// <summary>Physical → local device-independent pixels of an overlay that covers <paramref name="window"/> with <paramref name="dipWidth"/> × <paramref name="dipHeight"/>.</summary>
    public static (double X, double Y, double Width, double Height) ToLocal(PixelRect rect, PixelRect window, double dipWidth, double dipHeight)
    {
        var sx = window.Width > 0 ? dipWidth / window.Width : 1;
        var sy = window.Height > 0 ? dipHeight / window.Height : 1;
        return ((rect.X - window.X) * sx, (rect.Y - window.Y) * sy, rect.Width * sx, rect.Height * sy);
    }

    /// <summary>Local device-independent pixels of an overlay → physical pixels of the desktop.</summary>
    public static PixelPoint ToPhysical(double x, double y, PixelRect window, double dipWidth, double dipHeight)
    {
        var sx = dipWidth > 0 ? window.Width / dipWidth : 1;
        var sy = dipHeight > 0 ? window.Height / dipHeight : 1;
        return new PixelPoint(window.X + (int)Math.Floor(x * sx), window.Y + (int)Math.Floor(y * sy));
    }

    /// <summary>The monitors as the SDK describes them.</summary>
    public static IReadOnlyList<ScreenMonitor> ToSdk(IReadOnlyList<MonitorInfo> monitors) =>
        [.. monitors.Select(m => new ScreenMonitor(m.Id, ToSdk(m.Bounds), ToSdk(m.WorkArea), m.Scale, m.IsPrimary))];

    public static ScreenRect ToSdk(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    public static PixelRect FromSdk(ScreenRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Where the action bar goes: below the area, above it if there is no room, otherwise inside at the bottom.</summary>
    public static PixelPoint ActionBarPosition(PixelRect area, int barWidth, int barHeight, PixelRect monitor, int gap)
    {
        var x = Math.Clamp(area.X + ((area.Width - barWidth) / 2), monitor.X + gap, Math.Max(monitor.X + gap, monitor.Right - barWidth - gap));
        int y;
        if (area.Bottom + gap + barHeight <= monitor.Bottom)
        {
            y = area.Bottom + gap;
        }
        else if (area.Y - gap - barHeight >= monitor.Y)
        {
            y = area.Y - gap - barHeight;
        }
        else
        {
            y = Math.Max(monitor.Y + gap, area.Bottom - gap - barHeight);
        }

        return new PixelPoint(x, y);
    }
}
