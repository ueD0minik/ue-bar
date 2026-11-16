// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using UeBar.Core.Tiles;

namespace UeBar.App.Bar.Views;

public partial class ChoiceElementView : UserControl
{
    public ChoiceElementView()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is ChoiceElement choice && sender is FrameworkElement { DataContext: ChoiceOption option })
        {
            TileEvents.Raise(this, new TileInteraction(choice.Id, InteractionKind.Select, Text: option.Id));
        }
    }
}
