// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json;

namespace UeBar.Modules.HomeAssistant;

/// <summary>Lean registry entry for the search (no state).</summary>
internal sealed record HaEntityEntry(
    string EntityId,
    string Name,
    string? Area,
    string? Icon,
    int? DisplayPrecision = null,
    string? DeviceId = null,
    string? AreaId = null,
    string? EntityCategory = null,
    bool Hidden = false)
{
    public string Domain => EntityId.Split('.', 2)[0];
}

/// <summary>An area from the area registry.</summary>
internal sealed record HaArea(string Id, string Name, string? Icon);

/// <summary>Only the fields of a device the index needs.</summary>
internal readonly record struct HaDeviceInfo(string? Name, string? AreaId);

/// <summary>
/// Only the fields of an entity from the registry (or short form/states) the index needs.
/// Created directly while reading the answer – the large answer itself is never held as an object tree (M5-Fix 1).
/// </summary>
internal sealed record HaRegistryEntity(
    string EntityId,
    string? Name,
    bool HasEntityName = false,
    string? DeviceId = null,
    string? AreaId = null,
    string? Icon = null,
    int? Precision = null,
    string? Category = null,
    bool Hidden = false);

/// <summary>Where the names/areas come from (diagnostics, non-admin fallbacks, M5b).</summary>
internal enum EntityIndexSource
{
    /// <summary>Nothing loaded yet.</summary>
    None,

    /// <summary>Full entity registry (<c>config/entity_registry/list</c>).</summary>
    Registry,

    /// <summary>Short form for display (<c>config/entity_registry/list_for_display</c>).</summary>
    Display,

    /// <summary>Only from the states (<c>get_states</c>): names yes, areas no.</summary>
    States,
}

/// <summary>
/// Names, areas and icons of all entities from config/entity_registry/list,
/// config/device_registry/list (for the area through the device) and config/area_registry/list.
/// If something is missing (e.g. permissions), there are fallbacks: list_for_display or only the states.
/// Keeps only the needed fields, no states. The <c>Parse*</c> methods read the answers
/// directly from the <see cref="JsonElement"/> (no <c>JsonNode</c> tree, little memory).
/// </summary>
internal sealed class EntityIndex
{
    private Dictionary<string, HaEntityEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<HaArea> _areas = [];

    public int Count => _entries.Count;

    public int AreaCount => _areas.Count;

    public IReadOnlyList<HaArea> Areas => _areas;

    public EntityIndexSource Source { get; private set; }

    /// <summary>Areas are known (area registry readable).</summary>
    public bool HasAreas => _areas.Count > 0;

    public HaEntityEntry? Get(string entityId) => _entries.TryGetValue(entityId, out var e) ? e : null;

    public IReadOnlyList<HaEntityEntry> All => [.. _entries.Values];

    // ------------------------------------------------------------------ Reading the answers

    /// <summary><c>config/area_registry/list</c> → areas.</summary>
    public static List<HaArea> ParseAreas(JsonElement areas)
    {
        var list = new List<HaArea>();
        foreach (var area in Items(areas))
        {
            if (Str(area, "area_id") is { } id)
            {
                list.Add(new HaArea(id, Str(area, "name") ?? id, Str(area, "icon")));
            }
        }

        return list;
    }

    /// <summary><c>config/device_registry/list</c> → only name and area per device.</summary>
    public static Dictionary<string, HaDeviceInfo> ParseDevices(JsonElement devices)
    {
        var info = new Dictionary<string, HaDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Items(devices))
        {
            if (Str(device, "id") is { } id)
            {
                info[id] = new HaDeviceInfo(Str(device, "name_by_user") ?? Str(device, "name"), Str(device, "area_id"));
            }
        }

        return info;
    }

    /// <summary><c>config/entity_registry/list</c> → entities (disabled ones are dropped).</summary>
    public static List<HaRegistryEntity> ParseRegistry(JsonElement entities)
    {
        var list = new List<HaRegistryEntity>();
        foreach (var entity in Items(entities))
        {
            if (Str(entity, "entity_id") is not { } entityId || Str(entity, "disabled_by") is not null)
            {
                continue; // skip disabled entities
            }

            var name = Str(entity, "name");
            int? precision = null;
            if (entity.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("sensor", out var sensor) && sensor.ValueKind == JsonValueKind.Object)
            {
                precision = Int(sensor, "display_precision") ?? Int(sensor, "suggested_display_precision");
            }

            list.Add(new HaRegistryEntity(
                entityId,
                string.IsNullOrWhiteSpace(name) ? Str(entity, "original_name") : name,
                HasEntityName: string.IsNullOrWhiteSpace(name) && Bool(entity, "has_entity_name"),
                DeviceId: Str(entity, "device_id"),
                AreaId: Str(entity, "area_id"),
                Icon: Str(entity, "icon") ?? Str(entity, "original_icon"),
                Precision: precision is >= 0 and <= 6 ? precision : null,
                Category: Str(entity, "entity_category"),
                Hidden: Str(entity, "hidden_by") is not null));
        }

        return list;
    }

    /// <summary>
    /// Fallback: short form <c>config/entity_registry/list_for_display</c>
    /// (<c>{ entity_categories: {0: "config", …}, entities: [{ ei, en, ai, di, ec, hb, hn, ic, dp }] }</c>;
    /// disabled ones are already left out there).
    /// </summary>
    public static List<HaRegistryEntity> ParseDisplay(JsonElement display)
    {
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        if (display.ValueKind == JsonValueKind.Object && display.TryGetProperty("entity_categories", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    categories[property.Name] = property.Value.GetString()!;
                }
            }
        }

        var list = new List<HaRegistryEntity>();
        if (display.ValueKind != JsonValueKind.Object || !display.TryGetProperty("entities", out var entities))
        {
            return list;
        }

        foreach (var entity in Items(entities))
        {
            if (Str(entity, "ei") is not { } entityId)
            {
                continue;
            }

            var category = Int(entity, "ec") is { } index && categories.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var c) ? c : null;
            var precision = Int(entity, "dp");
            list.Add(new HaRegistryEntity(
                entityId,
                Str(entity, "en"),
                HasEntityName: Bool(entity, "hn"),
                DeviceId: Str(entity, "di"),
                AreaId: Str(entity, "ai"),
                Icon: Str(entity, "ic"),
                Precision: precision is >= 0 and <= 6 ? precision : null,
                Category: category,
                Hidden: Bool(entity, "hb")));
        }

        return list;
    }

    /// <summary>Last fallback: <c>get_states</c> → only friendly_name and icon, no areas.</summary>
    public static List<HaRegistryEntity> ParseStates(JsonElement states)
    {
        var list = new List<HaRegistryEntity>();
        foreach (var state in Items(states))
        {
            if (Str(state, "entity_id") is not { } entityId)
            {
                continue;
            }

            var hasAttributes = state.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object;
            list.Add(new HaRegistryEntity(entityId, hasAttributes ? Str(attributes, "friendly_name") : null, Icon: hasAttributes ? Str(attributes, "icon") : null));
        }

        return list;
    }

    // ------------------------------------------------------------------ Building

    /// <summary>Rebuild the index (missing devices/areas are allowed).</summary>
    public void Load(
        IReadOnlyList<HaRegistryEntity> entities,
        IReadOnlyDictionary<string, HaDeviceInfo>? devices,
        IReadOnlyList<HaArea>? areas,
        EntityIndexSource source)
    {
        var areaList = areas ?? [];
        var areaNames = areaList.ToDictionary(a => a.Id, a => a.Name, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HaEntityEntry>(entities.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var device = entity.DeviceId is not null && devices is not null && devices.TryGetValue(entity.DeviceId, out var d) ? d : default;
            var areaId = entity.AreaId ?? device.AreaId;
            var name = entity.HasEntityName && device.Name is not null
                ? string.IsNullOrWhiteSpace(entity.Name) ? device.Name : $"{device.Name} {entity.Name}"
                : entity.Name;
            var knownArea = areaId is not null && areaNames.TryGetValue(areaId, out var areaName) ? areaName : null;

            result[entity.EntityId] = new HaEntityEntry(
                entity.EntityId,
                string.IsNullOrWhiteSpace(name) ? entity.EntityId : name!,
                knownArea,
                entity.Icon,
                entity.Precision,
                entity.DeviceId,
                knownArea is null ? null : areaId,
                string.IsNullOrEmpty(entity.Category) ? null : entity.Category,
                entity.Hidden);
        }

        _entries = result;
        _areas = areaList;
        Source = source;
    }

    /// <summary>Search by name, entity ID and area (all words must occur).</summary>
    public IReadOnlyList<HaEntityEntry> Search(string query, int maxResults)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return
        [
            .. _entries.Values
                .Where(e => words.All(w =>
                    e.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                    || e.EntityId.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || (e.Area?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)))
                .OrderBy(e => e.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(maxResults),
        ];
    }

    // ------------------------------------------------------------------ JSON helpers

    private static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : [];

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
