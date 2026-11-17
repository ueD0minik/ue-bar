// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Suggested layout from the HA areas (SPEC 9, M5b) – without built-in entity IDs:
/// per chosen area a tab with lights (2x1), climate (2x2), media (2x2), covers,
/// vacuums, fans/switches/<c>input_boolean</c> (1x1), plus "Routines" with scripts and
/// scenes. At most 12 tiles per tab. Hidden, disabled and configuration/diagnostic
/// entities stay out. If the areas are not readable (e.g. permissions), it is offered by
/// device kind. Tab IDs stay stable (German, as stored in existing layouts); names follow the UI language (R1).
/// </summary>
internal static class HaLayoutSuggestion
{
    public const int MaxTilesPerTab = 12;
    public const string RoutinesChoice = "routinen";
    public const string AreaPrefix = "area:";
    public const string KindPrefix = "art:";

    /// <summary>Domains in the area tab, in this order.</summary>
    private static readonly string[] AreaDomains = ["light", "climate", "media_player", "cover", "vacuum", "fan", "switch", "input_boolean"];

    private static readonly string[] RoutineDomains = ["script", "scene"];

    /// <summary>Device kinds when no areas are known: (ID, name resource key, icon, domains).</summary>
    private static readonly (string Id, string Name, string Icon, string[] Domains)[] Kinds =
    [
        ("lichter", "Kind_Lights", "Lightbulb", ["light"]),
        ("klima", "Kind_Climate", "Thermometer", ["climate"]),
        ("medien", "Kind_Media", "Tv", ["media_player"]),
        ("abdeckungen", "Kind_Covers", "Window", ["cover"]),
        ("staubsauger", "Kind_Vacuums", "Robot", ["vacuum"]),
        ("schalter", "Kind_Switches", "Power", ["switch", "input_boolean", "fan"]),
    ];

    /// <summary>Tile type and size per domain (covers have no own type yet → general tile).</summary>
    public static (string Type, TileSize Size) TileFor(string domain) => domain switch
    {
        "light" => ("ha.light", TileSize.Wide),
        "climate" => ("ha.climate", TileSize.Large),
        "media_player" => ("ha.media", TileSize.Large),
        "vacuum" => ("ha.vacuum", TileSize.Wide),
        "switch" or "input_boolean" or "fan" => ("ha.toggle", TileSize.Small),
        "script" or "scene" => ("ha.script", TileSize.Small),
        _ => ("ha.entity", TileSize.Small),
    };

    /// <summary>Suitable for the suggestion: not hidden, no configuration/diagnostic entity.</summary>
    public static bool IsUsable(HaEntityEntry entry) => !entry.Hidden && string.IsNullOrEmpty(entry.EntityCategory);

    /// <summary>Offer for the first start, or null if nothing is known (yet).</summary>
    public static LayoutSuggestionOptions? Options(EntityIndex index)
    {
        if (index.Count == 0)
        {
            return null;
        }

        var usable = index.All.Where(IsUsable).ToList();
        var choices = new List<LayoutSuggestionChoice>();
        string question;
        string? hint = null;

        if (index.HasAreas)
        {
            foreach (var area in index.Areas.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var count = usable.Count(e => e.AreaId == area.Id && AreaDomains.Contains(e.Domain));
                if (count > 0)
                {
                    choices.Add(new LayoutSuggestionChoice(AreaPrefix + area.Id, area.Name, HaIcons.FromMdi(area.Icon) ?? "Home", Detail(count)));
                }
            }

            question = HaText.T["Suggest_AreasQuestion"];
        }
        else
        {
            question = HaText.T["Suggest_KindsQuestion"];
            hint = index.Source == EntityIndexSource.States
                ? HaText.T["Suggest_AreasNotReadable"]
                : HaText.T["Suggest_NoAreas"];
        }

        if (choices.Count == 0)
        {
            if (index.HasAreas)
            {
                question = HaText.T["Suggest_KindsQuestion"];
                hint = HaText.T["Suggest_AreasEmpty"];
            }

            foreach (var kind in Kinds)
            {
                var count = usable.Count(e => kind.Domains.Contains(e.Domain));
                if (count > 0)
                {
                    choices.Add(new LayoutSuggestionChoice(KindPrefix + kind.Id, HaText.T[kind.Name], kind.Icon, Detail(count)));
                }
            }
        }

        var routines = usable.Count(e => RoutineDomains.Contains(e.Domain));
        if (routines > 0)
        {
            choices.Add(new LayoutSuggestionChoice(RoutinesChoice, HaText.T["Suggest_Routines"], "Lightning", HaText.T.Format("Suggest_RoutinesDetail", Math.Min(routines, MaxTilesPerTab)), Selected: true));
        }

        return choices.Count == 0 ? null : new LayoutSuggestionOptions(question, choices, hint);
    }

    /// <summary>Layout from the selection (order as in the offer).</summary>
    public static LayoutDocument Create(EntityIndex index, IReadOnlyCollection<string> selected)
    {
        var layout = new LayoutDocument();
        var usable = index.All.Where(IsUsable).ToList();
        var options = Options(index);
        foreach (var choice in options?.Choices ?? [])
        {
            if (!selected.Contains(choice.Id))
            {
                continue;
            }

            if (choice.Id.StartsWith(AreaPrefix, StringComparison.Ordinal) && index.Areas.FirstOrDefault(a => a.Id == choice.Id[AreaPrefix.Length..]) is { } area)
            {
                var entities = usable.Where(e => e.AreaId == area.Id && AreaDomains.Contains(e.Domain));
                layout.Tabs.Add(Tab("bereich-" + area.Id, area.Name, choice.Icon ?? "Home", SortByDomain(entities, AreaDomains), area.Name));
            }
            else if (choice.Id.StartsWith(KindPrefix, StringComparison.Ordinal) && Kinds.FirstOrDefault(k => k.Id == choice.Id[KindPrefix.Length..]) is { Id: not null } kind)
            {
                var entities = usable.Where(e => kind.Domains.Contains(e.Domain));
                layout.Tabs.Add(Tab(kind.Id, HaText.T[kind.Name], kind.Icon, SortByDomain(entities, kind.Domains), null));
            }
            else if (choice.Id == RoutinesChoice)
            {
                var entities = usable.Where(e => RoutineDomains.Contains(e.Domain)).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
                layout.Tabs.Add(Tab("routinen", HaText.T["Suggest_Routines"], "Lightning", entities, null));
            }
        }

        // Status line: first weather entity, if there is one.
        if (layout.Tabs.Count > 0 && usable.Where(e => e.Domain == "weather").OrderBy(e => e.EntityId, StringComparer.Ordinal).FirstOrDefault() is { } weather)
        {
            layout.Status.Add(new StatusItemConfig { Module = HomeAssistantModule.ModuleId, Entity = weather.EntityId });
        }

        return layout;
    }

    private static IEnumerable<HaEntityEntry> SortByDomain(IEnumerable<HaEntityEntry> entities, string[] order) =>
        entities
            .OrderBy(e => Array.IndexOf(order, e.Domain))
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);

    private static LayoutTab Tab(string id, string name, string icon, IEnumerable<HaEntityEntry> entities, string? areaName)
    {
        var tab = new LayoutTab { Id = id, Name = name, Icon = icon };
        var n = 0;
        foreach (var entity in entities.Take(MaxTilesPerTab))
        {
            var (type, size) = TileFor(entity.Domain);
            tab.Tiles.Add(new TileConfig
            {
                Id = $"{id}-{++n}",
                Module = HomeAssistantModule.ModuleId,
                Type = type,
                Size = size,
                Label = ShortName(entity.Name, areaName),
                Entities = new Dictionary<string, string> { ["main"] = entity.EntityId },
            });
        }

        return tab;
    }

    /// <summary>"Living room ceiling lamp" in the tab "Living room" → "ceiling lamp" (otherwise no own name).</summary>
    private static string? ShortName(string name, string? areaName) =>
        areaName is not null && name.Length > areaName.Length + 1 && name.StartsWith(areaName + " ", StringComparison.CurrentCultureIgnoreCase)
            ? name[(areaName.Length + 1)..]
            : null;

    private static string Detail(int count) => count > MaxTilesPerTab ? HaText.T.Format("Suggest_TilesOf", MaxTilesPerTab, count) : count == 1 ? HaText.T["Suggest_OneTile"] : HaText.T.Format("Suggest_Tiles", count);
}
