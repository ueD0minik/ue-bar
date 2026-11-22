// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Controls;
using System.Windows.Threading;
using UeBar.App.Infrastructure;
using UeBar.Sdk;

namespace UeBar.App.Bar.Views;

/// <summary>
/// Slider: press and drag anywhere sets the value (One UI style, <see cref="OneUiSliderInput"/> – the same
/// behaviour as every slider of the app). It is sent on release, so the module is not flooded with intermediate
/// values. The binding to the module value stays (SetCurrentValue). Mouse wheel = ±5 % of the range; sent
/// shortly after the last notch (combined), not on every one.
/// </summary>
public partial class SliderElementView : UserControl
{
    private static readonly TimeSpan WheelSendDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _wheelTimer;

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

        var input = OneUiSliderInput.Attach(Slider, live: null, commit: _ =>
        {
            _wheelTimer.Stop();
            Send();
        }, commitWheel: false);
        input.Wheeled += _ =>
        {
            _wheelTimer.Stop();
            _wheelTimer.Start();
        };
    }

    private void Send()
    {
        if (DataContext is SliderElement element)
        {
            TileEvents.Raise(this, new TileInteraction(element.Id, InteractionKind.SetValue, Slider.Value));
        }
    }
}
