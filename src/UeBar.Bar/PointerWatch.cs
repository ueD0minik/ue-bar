// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What changed for the watched areas: the area under the mouse now, and whether Ctrl is held over it.</summary>
public readonly record struct PointerChange(string? Over, bool Ctrl, string? Left);

/// <summary>
/// Areas the core's mouse polling also watches (P2-5-Fix 1): pins with "Let clicks through" get no mouse
/// events, so the polling tells when the mouse is over one (brighten it) and whether Ctrl is held (make it
/// usable for a moment). Only active while there are areas – without them it costs nothing. Reports only
/// changes, so the UI thread is woken rarely. Thread-safe: areas come from the UI thread, checks from the
/// polling thread.
/// </summary>
public sealed class PointerWatch
{
    private readonly Lock _lock = new();
    private (string Id, PixelRect Rect)[] _areas = [];
    private string? _over;
    private bool _ctrl;

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _areas.Length > 0;
            }
        }
    }

    /// <summary>New list of watched areas. Returns whether "active" changed. The mouse state starts over if the area under it is gone.</summary>
    public bool SetAreas(IEnumerable<(string Id, PixelRect Rect)> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        lock (_lock)
        {
            var before = _areas.Length > 0;
            _areas = [.. areas.Where(a => !a.Rect.IsEmpty)];
            if (_over is not null && !_areas.Any(a => a.Id == _over))
            {
                _over = null;
                _ctrl = false;
            }

            return before != (_areas.Length > 0);
        }
    }

    /// <summary>One poll: null = nothing changed (also when inactive).</summary>
    public PointerChange? Check(PixelPoint point, bool ctrl)
    {
        lock (_lock)
        {
            if (_areas.Length == 0)
            {
                return null;
            }

            string? over = null;
            for (var i = _areas.Length - 1; i >= 0; i--)
            {
                if (_areas[i].Rect.Contains(point))
                {
                    over = _areas[i].Id;
                    break;
                }
            }

            var held = over is not null && ctrl;
            if (over == _over && held == _ctrl)
            {
                return null;
            }

            var left = _over is not null && _over != over ? _over : null;
            _over = over;
            _ctrl = held;
            return new PointerChange(over, held, left);
        }
    }
}
