// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Sdk;
using UeBar.Core.Layout;

namespace UeBar.Core.Modules;

/// <summary>A section of the first start: what a module offers for selection.</summary>
public sealed record LayoutSetupSection(
    string ModuleId,
    string ModuleName,
    string ModuleIcon,
    string Question,
    string? Hint,
    IReadOnlyList<LayoutSuggestionChoice> Choices);

/// <summary>
/// First start (M5b), generic for all modules: collects the offers of the running modules
/// (<see cref="ILayoutSuggestionOptions"/>; modules with only <see cref="ILayoutSuggestionProvider"/>
/// appear as a single choice "Use suggestion") and builds a layout from the selection.
/// All calls into the module go through the guard.
/// </summary>
public static class LayoutSetup
{
    /// <summary>ID of the only choice for modules without own options.</summary>
    public const string WholeSuggestion = "*";

    public static IReadOnlyList<LayoutSetupSection> GetSections(ModuleRegistry registry)
    {
        var sections = new List<LayoutSetupSection>();
        foreach (var module in registry.Modules.Where(m => m.IsEnabled))
        {
            if (module.Module is ILayoutSuggestionOptions)
            {
                var options = module.IsAvailable
                    ? registry.Invoke(module.Id, "First-start options", m => ((ILayoutSuggestionOptions)m).GetSuggestionOptions()).Value
                    : null;
                sections.Add(options is null
                    ? new LayoutSetupSection(module.Id, module.DisplayName, module.Icon, CoreText.T.Format("Setup_NoChoiceTitle", module.DisplayName),
                        CoreText.T.Format("Setup_NoChoiceText", module.Status.DisplayText), [])
                    : new LayoutSetupSection(module.Id, module.DisplayName, module.Icon, options.Question, options.Hint, options.Choices));
            }
            else if (module.Module is ILayoutSuggestionProvider && module.IsAvailable)
            {
                sections.Add(new LayoutSetupSection(module.Id, module.DisplayName, module.Icon, CoreText.T.Format("Setup_SuggestionFrom", module.DisplayName), null,
                    [new LayoutSuggestionChoice(WholeSuggestion, CoreText.T["Setup_UseSuggestion"])]));
            }
        }

        return sections;
    }

    /// <summary>Layout from the selection (module ID → chosen option IDs). Empty selection = empty layout.</summary>
    public static LayoutDocument Build(ModuleRegistry registry, IReadOnlyDictionary<string, IReadOnlyCollection<string>> selection)
    {
        var parts = new List<LayoutDocument>();
        foreach (var module in registry.Modules)
        {
            if (!selection.TryGetValue(module.Id, out var chosen) || chosen.Count == 0 || !module.IsAvailable)
            {
                continue;
            }

            var layout = module.Module switch
            {
                ILayoutSuggestionOptions => registry.Invoke(module.Id, "Layout from selection", m => ((ILayoutSuggestionOptions)m).CreateSuggestedLayout(chosen)).Value,
                ILayoutSuggestionProvider when chosen.Contains(WholeSuggestion) => registry.Invoke(module.Id, "Suggested layout", m => ((ILayoutSuggestionProvider)m).GetSuggestedLayout()).Value,
                _ => null,
            };
            if (layout is not null)
            {
                parts.Add(LayoutLayers.Clone(layout));
            }
        }

        var result = LayoutComposer.Merge(parts);
        MakeIdsUnique(result);
        return result;
    }

    /// <summary>
    /// Modules whose whole suggestion can be added to an existing layout ("Add suggestion", P2-2):
    /// running, with <see cref="ILayoutSuggestionProvider"/> and a suggestion with at least one tab.
    /// </summary>
    public static IReadOnlyList<ModuleEntry> AddableSuggestions(ModuleRegistry registry) =>
    [
        .. registry.Modules.Where(m => m.IsAvailable && m.Module is ILayoutSuggestionProvider
            && registry.Invoke(m.Id, "Suggested layout", x => ((ILayoutSuggestionProvider)x).GetSuggestedLayout()).Value is { Tabs.Count: > 0 }),
    ];

    /// <summary>
    /// Appends the tabs of a module's suggestion to <paramref name="current"/> (P2-2). Existing tabs,
    /// tiles and the status line stay as they are; tab and tile IDs of the new tabs are made unique.
    /// Returns null if the module has no suggestion.
    /// </summary>
    public static LayoutDocument? AppendSuggestion(ModuleRegistry registry, string moduleId, LayoutDocument current)
    {
        var suggestion = registry.Invoke(moduleId, "Suggested layout", m => (m as ILayoutSuggestionProvider)?.GetSuggestedLayout()).Value;
        return suggestion is null ? null : Append(current, suggestion);
    }

    /// <summary>Appends the tabs of <paramref name="suggestion"/> to a copy of <paramref name="current"/> (public for tests).</summary>
    public static LayoutDocument Append(LayoutDocument current, LayoutDocument suggestion)
    {
        var result = LayoutLayers.Clone(current);
        var tabIds = new HashSet<string>(result.Tabs.Select(t => t.Id), StringComparer.Ordinal);
        var tileIds = new HashSet<string>(result.AllTiles.Select(t => t.Id), StringComparer.Ordinal);
        foreach (var tab in LayoutLayers.Clone(suggestion).Tabs)
        {
            tab.Id = Unique(string.IsNullOrWhiteSpace(tab.Id) ? "tab" : tab.Id, tabIds);
            foreach (var tile in tab.Tiles)
            {
                tile.Id = Unique(string.IsNullOrWhiteSpace(tile.Id) ? "k" : tile.Id, tileIds);
            }

            result.Tabs.Add(tab);
        }

        return result;

        static string Unique(string id, HashSet<string> taken)
        {
            var candidate = id;
            for (var n = 2; !taken.Add(candidate); n++)
            {
                candidate = $"{id}-{n}";
            }

            return candidate;
        }
    }

    /// <summary>Make tab and tile IDs unique (modules know nothing about each other).</summary>
    private static void MakeIdsUnique(LayoutDocument layout)
    {
        var tiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in layout.AllTiles)
        {
            var id = string.IsNullOrWhiteSpace(tile.Id) ? "k" : tile.Id;
            var candidate = id;
            for (var n = 2; !tiles.Add(candidate); n++)
            {
                candidate = $"{id}-{n}";
            }

            tile.Id = candidate;
        }
    }
}
