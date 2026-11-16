// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Security.Cryptography;
using System.Text;
using UeBar.Core.Abstractions;
using UeBar.Core.Layout;
using UeBar.Core.Tiles;
using UeBar.Modules.HomeAssistant.Tiles;

namespace UeBar.Modules.HomeAssistant;

/// <summary>An HA tile type (SPEC 7.1). Creates the tile, registers it with the runtime and draws it right away. Names are resource keys (R1).</summary>
internal sealed class HaTileType(
    HaTileHost host,
    string id,
    string displayNameKey,
    string icon,
    IReadOnlyList<TileSize> sizes,
    Func<HaTileHost, TileConfig, HaTile> factory,
    params TileSlot[] slots) : ITileType
{
    public string Id { get; } = id;

    public string DisplayName => HaText.T[displayNameKey];

    public string Icon { get; } = icon;

    public IReadOnlyList<TileSize> AllowedSizes { get; } = sizes;

    public TileSize DefaultSize => AllowedSizes[0];

    public IReadOnlyList<TileSlot> Slots => [.. slots.Select(s => s with { DisplayName = HaText.T[s.DisplayName] })];

    public TileModel CreateTile(TileConfig config)
    {
        var tile = factory(host, config);
        host.Track(tile);
        tile.Render();
        return tile.Model;
    }

    public static IReadOnlyList<ITileType> All(HaTileHost host)
    {
        TileSize[] small = [TileSize.Small, TileSize.Wide];
        TileSize[] wide = [TileSize.Wide, TileSize.Small, TileSize.Large];
        TileSize[] large = [TileSize.Large, TileSize.Wide];
        var main = new TileSlot("main", "Slot_Entity");
        return
        [
            new HaTileType(host, "ha.light", "Type_Light", "Lightbulb", wide, (h, c) => new LightTile(h, c), main with { Filter = "light" }),
            new HaTileType(host, "ha.toggle", "Type_Toggle", "Power", small, (h, c) => new ToggleTile(h, c), main with { Filter = "switch,input_boolean,fan" }),
            new HaTileType(host, "ha.script", "Type_Script", "Lightning", small, (h, c) => new ScriptTile(h, c), main with { Filter = "script,scene,button,input_button" }),
            new HaTileType(host, "ha.climate_ir", "Type_ClimateIr", "Snow", large, (h, c) => new ClimateIrTile(h, c),
                new TileSlot("power", "Slot_Power", true, "switch,input_boolean"),
                new TileSlot("target", "Slot_Target", true, "number,input_number"),
                new TileSlot("fan", "Slot_Fan", false, "select,input_select")),
            new HaTileType(host, "ha.climate", "Type_Climate", "Thermometer", large, (h, c) => new ClimateTile(h, c), main with { Filter = "climate" }),
            new HaTileType(host, "ha.media", "Type_Media", "Tv", large, (h, c) => new MediaTile(h, c), main with { Filter = "media_player" }),
            new HaTileType(host, "ha.vacuum", "Type_Vacuum", "Robot", wide, (h, c) => new VacuumTile(h, c),
                main with { Filter = "vacuum" },
                new TileSlot("battery", "Slot_Battery", false, "sensor")),
            new HaTileType(host, "ha.sensor", "Type_Sensor", "Info", small, (h, c) => new SensorTile(h, c), main with { Filter = "sensor,binary_sensor" }),
            new HaTileType(host, "ha.select", "Type_Select", "Settings", wide, (h, c) => new SelectTile(h, c), main with { Filter = "select,input_select" }),
            new HaTileType(host, "ha.entity", "Type_Entity", "Home", small, (h, c) => new GenericTile(h, c), main),
        ];
    }

    /// <summary>Suggested tile type per domain (SPEC 7.1); unknown domains → generic tile.</summary>
    public static (string Type, TileSize Size) ForDomain(string domain) => domain switch
    {
        "light" => ("ha.light", TileSize.Wide),
        "switch" or "input_boolean" or "fan" => ("ha.toggle", TileSize.Small),
        "script" or "scene" or "button" or "input_button" => ("ha.script", TileSize.Small),
        "climate" => ("ha.climate", TileSize.Large),
        "media_player" => ("ha.media", TileSize.Large),
        "vacuum" => ("ha.vacuum", TileSize.Wide),
        "sensor" or "binary_sensor" => ("ha.sensor", TileSize.Small),
        "select" or "input_select" => ("ha.select", TileSize.Wide),
        _ => ("ha.entity", TileSize.Small),
    };
}

/// <summary>
/// Image IDs for cover art (SPEC 4.2: images as IDs). The ID contains only the entity + a checksum of the
/// entity_picture – never the proxy token from the URL. The module resolves them when loading.
/// </summary>
internal static class HaImages
{
    public const string Prefix = "ha-image:";

    public static string? IdFor(HaEntityState state)
    {
        var picture = state.Attribute<string>("entity_picture");
        if (string.IsNullOrWhiteSpace(picture))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(picture)), 0, 8).ToLowerInvariant();
        return $"{Prefix}{state.EntityId}?v={hash}";
    }

    public static string? EntityFor(string imageId)
    {
        if (!imageId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = imageId[Prefix.Length..];
        var query = rest.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? rest : rest[..query];
    }
}
