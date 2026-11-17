// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Core.Layout;

public static class LayoutComposer
{
    /// <summary>
    /// Merges suggested layouts of several modules into one (tabs one after another,
    /// equal tab IDs are merged).
    /// </summary>
    public static LayoutDocument Merge(IEnumerable<LayoutDocument> layouts)
    {
        var result = new LayoutDocument();
        foreach (var layout in layouts)
        {
            foreach (var tab in layout.Tabs)
            {
                var existing = result.Tabs.FirstOrDefault(t => t.Id == tab.Id);
                if (existing is null)
                {
                    result.Tabs.Add(tab);
                }
                else
                {
                    existing.Tiles.AddRange(tab.Tiles);
                }
            }

            result.Status.AddRange(layout.Status);
        }

        return result;
    }
}
