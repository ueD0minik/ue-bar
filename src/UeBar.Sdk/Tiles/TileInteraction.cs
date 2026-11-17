// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

public enum InteractionKind
{
    /// <summary>Tile or TapAction tapped.</summary>
    Tap,

    /// <summary>Value set (slider, stepper) – <see cref="TileInteraction.Number"/>.</summary>
    SetValue,

    /// <summary>Stepper +.</summary>
    Increment,

    /// <summary>Stepper −.</summary>
    Decrement,

    /// <summary>Button in a ButtonRow – <see cref="TileInteraction.Text"/> = button ID.</summary>
    Press,

    /// <summary>Choice in Choice – <see cref="TileInteraction.Text"/> = option ID.</summary>
    Select,
}

/// <summary>
/// Interaction with an element as plain data (JSON-transferable, e.g. later from a phone).
/// </summary>
public sealed record TileInteraction(
    string ElementId,
    InteractionKind Kind,
    double? Number = null,
    string? Text = null);

/// <summary>
/// Receives interactions of a tile. Set by the module and called by the core
/// exclusively through the module guard (fault protection, time limit).
/// </summary>
public interface ITileInteractionHandler
{
    Task HandleAsync(TileModel tile, TileInteraction interaction, CancellationToken cancellationToken);
}

/// <summary>Convenient handler from a delegate.</summary>
public sealed class DelegateInteractionHandler(Func<TileModel, TileInteraction, CancellationToken, Task> handler)
    : ITileInteractionHandler
{
    public DelegateInteractionHandler(Action<TileModel, TileInteraction> handler)
        : this((t, i, _) =>
        {
            handler(t, i);
            return Task.CompletedTask;
        })
    {
    }

    public Task HandleAsync(TileModel tile, TileInteraction interaction, CancellationToken cancellationToken) =>
        handler(tile, interaction, cancellationToken);
}
