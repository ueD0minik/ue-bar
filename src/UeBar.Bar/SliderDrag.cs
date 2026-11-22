// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// How every One UI slider in ue Bar behaves (tiles, settings, pin menu – P2-5-Fix 1): press anywhere and drag,
/// the value follows the mouse at once (<see cref="Live"/> on every change); on release the value is committed
/// once (<see cref="Committed"/>). The mouse wheel moves by 5 % of the range. No thumb, no fixed stops beyond
/// the step. WPF-free so it can be tested; the window side only hands in the mouse position as a ratio 0…1.
/// </summary>
public sealed class SliderDrag
{
    private double _value;

    public SliderDrag(double min, double max, double step, double value)
    {
        Configure(min, max, step);
        _value = Snap(value);
    }

    public double Min { get; private set; }

    public double Max { get; private set; }

    public double Step { get; private set; }

    /// <summary>Range or step changed (a tile's bound values arrive after it was created) – not while dragging.</summary>
    public void Configure(double min, double max, double step)
    {
        if (IsDragging)
        {
            return;
        }

        if (max < min)
        {
            (min, max) = (max, min);
        }

        Min = min;
        Max = max;
        Step = step > 0 ? step : 1;
    }

    public double Value => _value;

    public bool IsDragging { get; private set; }

    /// <summary>The value changed while dragging or by the wheel (for a live preview).</summary>
    public event Action<double>? Live;

    /// <summary>Dragging ended (release) – the value to keep.</summary>
    public event Action<double>? Committed;

    /// <summary>The value was set from outside (a new state of the module): only while not dragging.</summary>
    public void SetExternal(double value)
    {
        if (!IsDragging)
        {
            _value = Snap(value);
        }
    }

    public double Down(double ratio)
    {
        IsDragging = true;
        Set(FromRatio(ratio));
        return _value;
    }

    public double Move(double ratio)
    {
        if (IsDragging)
        {
            Set(FromRatio(ratio));
        }

        return _value;
    }

    public double Up()
    {
        if (!IsDragging)
        {
            return _value;
        }

        IsDragging = false;
        Committed?.Invoke(_value);
        return _value;
    }

    /// <summary>The mouse capture got lost (window closed, menu closed): commit what was reached.</summary>
    public void Lost() => Up();

    /// <summary>One notch of the mouse wheel: ±5 % of the range (at least one step).</summary>
    public double Wheel(int delta)
    {
        if (IsDragging || delta == 0)
        {
            return _value;
        }

        var range = Max - Min;
        var change = Math.Max(Step, Math.Round(range * 0.05 / Step) * Step) * Math.Sign(delta);
        Set(_value + change);
        return _value;
    }

    public double FromRatio(double ratio) => Snap(Min + (Math.Clamp(double.IsNaN(ratio) ? 0 : ratio, 0, 1) * (Max - Min)));

    public double Snap(double value)
    {
        var snapped = Min + (Math.Round((value - Min) / Step) * Step);
        return Math.Clamp(Math.Round(snapped, 6), Min, Max);
    }

    private void Set(double value)
    {
        var next = Snap(value);
        if (next.Equals(_value))
        {
            return;
        }

        _value = next;
        Live?.Invoke(_value);
    }
}
