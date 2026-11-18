// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Optional capabilities from P2-2 (contract 1.1). Extension only (SPEC 4.8).

/// <summary>
/// What of a module is on screen right now: the panel is open (or the pill shows the status
/// line) and these tiles of the current tab / these status values are visible. Lets a module
/// pause work that only feeds the display (e.g. polling CPU values) while nothing is visible.
/// </summary>
public sealed record ModuleVisibility(bool IsPanelOpen, IReadOnlyList<TileConfig> VisibleTiles, IReadOnlyList<StatusItemConfig> VisibleStatus)
{
    /// <summary>Nothing visible (bar collapsed).</summary>
    public static ModuleVisibility Hidden { get; } = new(false, [], []);

    /// <summary>The bar is out – pill or panel (contract 1.2). The pill shows <see cref="IPillActivitySource"/>.</summary>
    public bool IsBarVisible { get; init; }

    /// <summary>Anything of the module visible at all?</summary>
    public bool IsAnythingVisible => VisibleTiles.Count > 0 || VisibleStatus.Count > 0;

    /// <summary>All visible "things" (slots of the visible tiles + visible status values).</summary>
    public IReadOnlySet<string> VisibleEntities =>
        new HashSet<string>(
            VisibleTiles.SelectMany(t => t.Entities.Values).Concat(VisibleStatus.Select(s => s.Entity)).Where(e => !string.IsNullOrWhiteSpace(e)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Is the thing visible in a tile or in the status line?</summary>
    public bool IsVisible(string entityId) => VisibleEntities.Contains(entityId);
}

/// <summary>
/// The module wants to know what of it is visible (<see cref="ModuleVisibility"/>). Called through the
/// module guard before the start and whenever it changes (panel opens/closes, tab switches,
/// layout or status line changes) – only with a changed value. Must be fast.
/// </summary>
public interface IVisibilityAware
{
    void OnVisibilityChanged(ModuleVisibility visibility);
}

/// <summary>Battery level of a device (headphones, mouse, phone …), no matter which module knows it.</summary>
/// <param name="DeviceId">Stable ID within the providing module.</param>
/// <param name="Name">Display name (e.g. "Headset").</param>
/// <param name="Percent">0–100, null = connected but level unknown.</param>
/// <param name="IsCharging">null = unknown.</param>
/// <param name="IsConnected">Device is connected right now.</param>
/// <param name="Icon">Icon name (Segoe Fluent Icons or "U+E7xx").</param>
public sealed record DeviceBatteryInfo(
    string DeviceId,
    string Name,
    int? Percent,
    bool? IsCharging = null,
    bool IsConnected = true,
    string? Icon = null)
{
    /// <summary>Module that reported it; set by the core.</summary>
    public string? Source { get; init; }

    /// <summary>When the value was last read.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Module knows battery levels of devices (e.g. a vendor plugin for a wireless dongle). Other modules
/// see them through <see cref="IDeviceBatteryCatalog"/>. <see cref="DeviceBatteries"/> must be fast
/// (memory reads only); the event comes from any thread.
/// </summary>
public interface IDeviceBatterySource
{
    IReadOnlyList<DeviceBatteryInfo> DeviceBatteries { get; }

    event EventHandler? DeviceBatteriesChanged;
}

/// <summary>
/// Device batteries reported by all <em>other</em> running modules (<see cref="IDeviceBatterySource"/>),
/// offered on the <see cref="IModuleContext"/> (check with <c>context as IDeviceBatteryCatalog</c>).
/// </summary>
public interface IDeviceBatteryCatalog
{
    IReadOnlyList<DeviceBatteryInfo> GetDeviceBatteries();

    /// <summary>Raised (through the module guard) when another module reports changed batteries.</summary>
    event EventHandler? DeviceBatteriesChanged;
}
