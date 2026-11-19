// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Core.Layout;

/// <summary>
/// Editing the entries of a tile in edit mode (P2-4-Fix 1, <see cref="ITileItemEditor"/>): the module gets a
/// copy of the tile's configuration and returns the changed one, the layout editor takes it like any edit –
/// so undo and "Cancel" also cover it, and the layout syncs it. Always through the fault protection.
/// </summary>
public static class TileItemEditing
{
    /// <summary>The entries of a tile (null = its module does not edit entries of this tile).</summary>
    public static TileItemList? ItemsOf(ModuleRegistry registry, TileConfig tile) =>
        registry.Find(tile.Module)?.Module is ITileItemEditor
            ? registry.Invoke(tile.Module, "Tile entries", m => ((ITileItemEditor)m).GetTileItems(LayoutLayers.Clone(tile))).Value
            : null;

    /// <summary>One change of a tile's entries. False = the module did not change anything (or failed).</summary>
    public static bool Change(ModuleRegistry registry, LayoutEditor editor, string tileId, string operation, Func<ITileItemEditor, TileConfig, TileConfig?> change)
    {
        if (editor.FindTile(tileId) is not { } found || registry.Find(found.Tile.Module)?.Module is not ITileItemEditor)
        {
            return false;
        }

        var copy = LayoutLayers.Clone(found.Tile);
        var result = registry.Invoke(found.Tile.Module, operation, m => change((ITileItemEditor)m, copy));
        if (result is not { Succeeded: true, Value: { } replacement })
        {
            return false;
        }

        replacement.Module = found.Tile.Module; // a module cannot move the tile to another module
        return editor.ReplaceTile(tileId, replacement);
    }
}
