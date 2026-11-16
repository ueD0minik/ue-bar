// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using UeBar.Core.Tiles;

namespace UeBar.App.Bar.Views;

public delegate void TileInteractionEventHandler(object sender, TileInteractionEventArgs e);

public sealed class TileInteractionEventArgs(RoutedEvent routedEvent, TileInteraction interaction) : RoutedEventArgs(routedEvent)
{
    public TileInteraction Interaction { get; } = interaction;
}

/// <summary>
/// Element views report interactions as a routed event upwards; the tile forwards them through
/// the registry (and so the ModuleGuard) to the module.
/// </summary>
public static class TileEvents
{
    public static readonly RoutedEvent InteractionRequestedEvent = EventManager.RegisterRoutedEvent(
        "InteractionRequested", RoutingStrategy.Bubble, typeof(TileInteractionEventHandler), typeof(TileEvents));

    public static void Raise(UIElement source, TileInteraction interaction) =>
        source.RaiseEvent(new TileInteractionEventArgs(InteractionRequestedEvent, interaction));
}
