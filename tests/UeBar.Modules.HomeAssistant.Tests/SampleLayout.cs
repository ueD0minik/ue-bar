// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;
using UeBar.Core.Tiles;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>
/// Test data: a start layout of the example home (former built-in layout). Since M5b
/// no longer in the module – there the suggestion is created from the HA areas.
/// </summary>
internal static class SampleLayout
{
    public static LayoutDocument Create() => new()
    {
        Tabs =
        [
            new LayoutTab
            {
                Id = "zimmer",
                Name = "Zimmer",
                Icon = "Home",
                Tiles =
                [
                    Tile("t1", "ha.light", TileSize.Wide, "Desk", ("main", "light.office_desk")),
                    Tile("t2", "ha.light", TileSize.Wide, "Deckenlampe", ("main", "light.office_ceiling")),
                    Tile("t3", "ha.climate_ir", TileSize.Large, "Klima",
                        ("power", "switch.office_ac_power"),
                        ("target", "number.office_ac_target"),
                        ("fan", "select.office_ac_fan")),
                    Tile("t4", "ha.media", TileSize.Large, "Monitor", ("main", "media_player.monitor")),
                ],
            },
            new LayoutTab
            {
                Id = "routinen",
                Name = "Routinen",
                Icon = "Lightning",
                Tiles =
                [
                    Tile("r1", "ha.script", TileSize.Small, null, ("main", "script.focus")),
                    Tile("r2", "ha.script", TileSize.Small, null, ("main", "script.movie_night")),
                    Tile("r3", "ha.script", TileSize.Small, null, ("main", "script.good_night")),
                    Tile("r4", "ha.script", TileSize.Small, null, ("main", "script.leave_home")),
                    Tile("r5", "ha.script", TileSize.Small, null, ("main", "script.lock_pc")),
                    Tile("r6", "ha.vacuum", TileSize.Wide, null, ("main", "vacuum.robot"), ("battery", "sensor.robot_battery")),
                ],
            },
            new LayoutTab
            {
                Id = "medien",
                Name = "Medien",
                Icon = "Tv",
                Tiles =
                [
                    Tile("m1", "ha.media", TileSize.Large, "Living room TV", ("main", "media_player.living_room_tv")),
                    Tile("m2", "ha.media", TileSize.Large, "PC", ("main", "media_player.office_pc")),
                ],
            },
        ],
        Status =
        [
            new StatusItemConfig { Module = HomeAssistantModule.ModuleId, Entity = "sensor.office_temperature" },
            new StatusItemConfig { Module = HomeAssistantModule.ModuleId, Entity = "weather.forecast_home" },
        ],
    };

    private static TileConfig Tile(string id, string type, TileSize size, string? label, params (string Slot, string Entity)[] entities) => new()
    {
        Id = id,
        Module = HomeAssistantModule.ModuleId,
        Type = type,
        Size = size,
        Label = label,
        Entities = entities.ToDictionary(e => e.Slot, e => e.Entity),
    };
}
