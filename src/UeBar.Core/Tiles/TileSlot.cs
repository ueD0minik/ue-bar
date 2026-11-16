// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Core.Tiles;

/// <summary>
/// A "slot" a tile type needs (e.g. main, power, target).
/// <paramref name="Filter"/> is a free hint for the module's entity search
/// (e.g. a category); the core does not interpret it.
/// </summary>
public sealed record TileSlot(string Name, string DisplayName, bool Required = true, string? Filter = null);
