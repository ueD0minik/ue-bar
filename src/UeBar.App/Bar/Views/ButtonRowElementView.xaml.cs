// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using UeBar.Sdk;

namespace UeBar.App.Bar.Views;

public partial class ButtonRowElementView : UserControl
{
    public ButtonRowElementView()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is ButtonRowElement row && sender is FrameworkElement { DataContext: TileButton button })
        {
            TileEvents.Raise(this, new TileInteraction(row.Id, InteractionKind.Press, Text: button.Id));
        }
    }
}
