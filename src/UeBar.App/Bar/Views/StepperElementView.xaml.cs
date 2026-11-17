// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using UeBar.Sdk;

namespace UeBar.App.Bar.Views;

public partial class StepperElementView : UserControl
{
    public StepperElementView()
    {
        InitializeComponent();
    }

    private void OnMinus(object sender, RoutedEventArgs e) => Send(InteractionKind.Decrement, e);

    private void OnPlus(object sender, RoutedEventArgs e) => Send(InteractionKind.Increment, e);

    private void Send(InteractionKind kind, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is StepperElement element)
        {
            TileEvents.Raise(this, new TileInteraction(element.Id, kind));
        }
    }
}
