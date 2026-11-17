// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json;
using System.Text.Json.Serialization;

namespace UeBar.Sdk;

/// <summary>
/// Whole layout (SPEC 9): tabs with tiles + status line. Readable JSON, editable by hand.
/// Unknown fields are kept through <see cref="Extra"/>.
/// </summary>
public sealed class LayoutDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<LayoutTab> Tabs { get; set; } = [];

    public List<StatusItemConfig> Status { get; set; } = [];

    /// <summary>Time of the last change (for conflicts when reconciling, M5b). Missing in old files.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Device on which it was last changed (device name).</summary>
    public string? UpdatedBy { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public IEnumerable<TileConfig> AllTiles => Tabs.SelectMany(t => t.Tiles);
}

public sealed class LayoutTab
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Icon name (Segoe Fluent Icons).</summary>
    public string? Icon { get; set; }

    /// <summary>When the tab is visible (SPEC 9, level 3). The default "always" is not written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TabVisibility Visibility { get; set; } = TabVisibility.Always;

    public List<TileConfig> Tiles { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Visibility of a tab depending on location (in JSON "always" / "homeOnly" / "awayOnly").</summary>
public enum TabVisibility
{
    Always,
    HomeOnly,
    AwayOnly,
}

/// <summary>
/// A tile in the layout. <see cref="Module"/> decides which module creates it; if the
/// module is missing, the entry is kept and shows a placeholder (SPEC 4.5).
/// </summary>
public sealed class TileConfig
{
    public string Id { get; set; } = "";

    public string Module { get; set; } = "";

    /// <summary>Tile type with module prefix, e.g. "dummy.switch".</summary>
    public string Type { get; set; } = "";

    public TileSize Size { get; set; } = TileSize.Small;

    /// <summary>Slot name → ID of the "thing" in the module (e.g. "main" → "dummy.desk").</summary>
    public Dictionary<string, string> Entities { get; set; } = [];

    /// <summary>Name overridden by the user.</summary>
    public string? Label { get; set; }

    /// <summary>Icon overridden by the user.</summary>
    public string? Icon { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public string? GetEntity(string slot) => Entities.TryGetValue(slot, out var id) ? id : null;
}

/// <summary>A value in the status line: which module, which key.</summary>
public sealed class StatusItemConfig
{
    public string Module { get; set; } = "";

    public string Entity { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
