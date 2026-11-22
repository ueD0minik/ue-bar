// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UeBar.Bar;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Connects a WPF <see cref="Slider"/> in the One UI style to <see cref="SliderDrag"/> – the one slider behaviour
/// of the app (tiles, settings, pin menu). Press and drag anywhere, the value follows at once; release commits.
/// Works inside a context menu too: the slider takes the mouse capture itself and hands it back to the menu.
/// </summary>
internal sealed class OneUiSliderInput
{
    private readonly Slider _slider;
    private readonly SliderDrag _drag;
    private IInputElement? _previousCapture;

    private OneUiSliderInput(Slider slider, SliderDrag drag)
    {
        _slider = slider;
        _drag = drag;
        slider.PreviewMouseLeftButtonDown += OnDown;
        slider.PreviewMouseMove += OnMove;
        slider.PreviewMouseLeftButtonUp += OnUp;
        slider.LostMouseCapture += (_, _) =>
        {
            if (_drag.IsDragging)
            {
                _drag.Lost();
            }
        };
        slider.PreviewMouseWheel += OnWheel;
        drag.Live += value => slider.SetCurrentValue(RangeBase.ValueProperty, value);
    }

    public SliderDrag Drag => _drag;

    /// <summary>
    /// Attach the behaviour. <paramref name="live"/> runs on every change while dragging (and for the wheel),
    /// <paramref name="commit"/> once on release (and after a wheel notch, see <paramref name="commitWheel"/>).
    /// </summary>
    public static OneUiSliderInput Attach(Slider slider, Action<double>? live, Action<double>? commit, bool commitWheel = true)
    {
        var drag = new SliderDrag(slider.Minimum, slider.Maximum, slider.SmallChange > 0 ? slider.SmallChange : 1, slider.Value);
        var input = new OneUiSliderInput(slider, drag) { _commitWheel = commitWheel };
        if (live is not null)
        {
            drag.Live += live;
        }

        if (commit is not null)
        {
            drag.Committed += commit;
            input._wheelCommit = commit;
        }

        return input;
    }

    private bool _commitWheel;
    private Action<double>? _wheelCommit;

    /// <summary>Raised after a wheel notch (the caller may combine several before sending).</summary>
    public event Action<double>? Wheeled;

    /// <summary>Range, step and value as the slider has them now (bindings may have changed them).</summary>
    private void Sync()
    {
        _drag.Configure(_slider.Minimum, _slider.Maximum, _slider.SmallChange > 0 ? _slider.SmallChange : 1);
        _drag.SetExternal(_slider.Value);
    }

    private double Ratio(MouseEventArgs e) => _slider.ActualWidth > 0 ? e.GetPosition(_slider).X / _slider.ActualWidth : 0;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!_slider.IsEnabled)
        {
            return;
        }

        Sync();
        _previousCapture = Mouse.Captured;
        _slider.CaptureMouse();
        _drag.Down(Ratio(e));
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drag.IsDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Release();
            return;
        }

        _drag.Move(Ratio(e));
        e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drag.IsDragging)
        {
            return;
        }

        e.Handled = true;
        Release();
    }

    private void Release()
    {
        _drag.Up();
        _slider.ReleaseMouseCapture();

        // Inside a context menu the menu held the capture before – give it back, otherwise it stops closing on a click outside.
        if (_previousCapture is UIElement previous && previous.IsVisible && !ReferenceEquals(previous, _slider))
        {
            Mouse.Capture(previous, CaptureMode.SubTree);
        }

        _previousCapture = null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; // do not scroll what is around it
        if (!_slider.IsEnabled || _drag.IsDragging)
        {
            return;
        }

        Sync();
        var before = _drag.Value;
        var value = _drag.Wheel(e.Delta);
        if (value.Equals(before))
        {
            return;
        }

        Wheeled?.Invoke(value);
        if (_commitWheel)
        {
            _wheelCommit?.Invoke(value);
        }
    }
}
