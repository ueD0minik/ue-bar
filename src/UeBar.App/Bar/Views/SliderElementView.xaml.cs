// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UeBar.Core.Tiles;

namespace UeBar.App.Bar.Views;

/// <summary>
/// Slider: press and drag anywhere sets the value (One UI style). It is sent on release,
/// so the module is not flooded with intermediate values. The binding to the module value stays
/// (SetCurrentValue). Mouse wheel = ±5 % of the range; sent shortly after the last
/// notch (combined), not on every one.
/// </summary>
public partial class SliderElementView : UserControl
{
    private static readonly TimeSpan WheelSendDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _wheelTimer;
    private bool _dragging;

    public SliderElementView()
    {
        InitializeComponent();
        _wheelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = WheelSendDelay };
        _wheelTimer.Tick += (_, _) =>
        {
            _wheelTimer.Stop();
            Send();
        };
        Unloaded += (_, _) =>
        {
            if (_wheelTimer.IsEnabled)
            {
                _wheelTimer.Stop();
                Send();
            }
        };
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; // do not scroll the grid
        if (!Slider.IsEnabled || _dragging)
        {
            return;
        }

        var range = Slider.Maximum - Slider.Minimum;
        var step = Slider.SmallChange > 0 ? Slider.SmallChange : 1;
        var delta = Math.Max(step, Math.Round(range * 0.05 / step) * step) * Math.Sign(e.Delta);
        var value = Math.Clamp(Math.Round((Slider.Value + delta) / step) * step, Slider.Minimum, Slider.Maximum);
        if (value == Slider.Value)
        {
            return;
        }

        Slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, value);
        _wheelTimer.Stop();
        _wheelTimer.Start();
    }

    private void Send()
    {
        if (DataContext is SliderElement element)
        {
            TileEvents.Raise(this, new TileInteraction(element.Id, InteractionKind.SetValue, Slider.Value));
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Slider.IsEnabled)
        {
            return;
        }

        _dragging = true;
        Slider.CaptureMouse();
        SetFromPoint(e);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            SetFromPoint(e);
            e.Handled = true;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Slider.ReleaseMouseCapture();
        e.Handled = true;
        _wheelTimer.Stop();
        Send();
    }

    private void OnLostCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void SetFromPoint(MouseEventArgs e)
    {
        var width = Slider.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(e.GetPosition(Slider).X / width, 0, 1);
        var value = Slider.Minimum + (ratio * (Slider.Maximum - Slider.Minimum));
        var step = Slider.SmallChange > 0 ? Slider.SmallChange : 1;
        value = Math.Round(value / step) * step;
        Slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Math.Clamp(value, Slider.Minimum, Slider.Maximum));
    }
}
