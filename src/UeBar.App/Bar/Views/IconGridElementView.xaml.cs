// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using UeBar.Sdk;

namespace UeBar.App.Bar.Views;

public partial class IconGridElementView : UserControl
{
    public IconGridElementView()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is IconGridElement grid && sender is FrameworkElement { DataContext: IconGridItem item })
        {
            TileEvents.Raise(this, new TileInteraction(grid.Id, InteractionKind.Press, Text: item.Id));
        }
    }
}
