// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;
using UeBar.Core.Tiles;

namespace UeBar.Core.Abstractions;

/// <summary>A tile type (e.g. "dummy.switch", later "win.volume"), always with module prefix.</summary>
public interface ITileType
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Icon name (Segoe Fluent Icons).</summary>
    string Icon { get; }

    IReadOnlyList<TileSize> AllowedSizes { get; }

    TileSize DefaultSize { get; }

    /// <summary>Which slots the type needs (e.g. main, power, target).</summary>
    IReadOnlyList<TileSlot> Slots { get; }

    /// <summary>Creates the observable model for the configuration. Should be fast (no network calls).</summary>
    TileModel CreateTile(TileConfig config);
}
