// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;

namespace UeBar.Core.Abstractions;

// Optional capabilities from M5a. Extension only, nothing in the contract broken (SPEC 4.8).

/// <summary>Where the device currently is – for tab visibility (SPEC 9, level 3).</summary>
public enum DeviceLocation
{
    /// <summary>Unknown: show all tabs.</summary>
    Unknown,

    /// <summary>Home (e.g. Home Assistant reachable locally).</summary>
    Home,

    /// <summary>Away (e.g. only connected externally, or disconnected).</summary>
    Away,
}

/// <summary>
/// Module knows the device's location (home/away). The event comes from any thread;
/// <see cref="CurrentLocation"/> must be fast (memory reads only).
/// </summary>
public interface ILocationProvider
{
    DeviceLocation CurrentLocation { get; }

    event EventHandler<DeviceLocation>? LocationChanged;
}

/// <summary>
/// Helps when creating a tile in edit mode: candidates for a slot (e.g. things of
/// the same device/area first) and pre-filling empty slots of a draft. The core does not
/// interpret slots – only the module knows them.
/// </summary>
public interface ITileSlotAssistant
{
    /// <summary>Matching candidates for <paramref name="slot"/> of the draft, filtered by <paramref name="query"/> (empty = all).</summary>
    Task<IReadOnlyList<EntityInfo>> SuggestForSlotAsync(TileConfig draft, string slot, string query, int maxResults, CancellationToken cancellationToken);

    /// <summary>
    /// Pre-fill empty slots sensibly (same device/area) and move an already set thing into the
    /// matching slot. Returns a new configuration; the draft stays unchanged.
    /// </summary>
    TileConfig CompleteSlots(TileConfig draft);
}
