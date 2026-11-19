// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Optional capabilities from P2-4-Fix 1 (contract 1.4). Extension only (SPEC 4.8).

/// <summary>One entry of a tile the user edits in the bar (<see cref="ITileItemEditor"/>), e.g. a shortcut of a collection.</summary>
public sealed record TileItem(string Id, string Label, string? Icon = null, string? ImageUrl = null, string? Detail = null);

/// <summary>How new entries can come into a tile (shown as buttons in edit mode).</summary>
[Flags]
public enum TileItemSources
{
    None = 0,

    /// <summary>"Add entry": the module's search (<see cref="IEntitySource"/>), as when adding a tile.</summary>
    Search = 1,

    /// <summary>"Choose file …" (the app's dialog).</summary>
    File = 2,

    /// <summary>"Choose folder …".</summary>
    Folder = 4,

    /// <summary>"Enter web page …" (an address typed in).</summary>
    Link = 8,

    /// <summary>Dropping a file, folder, shortcut or link on the tile.</summary>
    Drop = 16,
}

/// <summary>The entries of a tile and what may be done with it.</summary>
public sealed record TileItemList(IReadOnlyList<TileItem> Items, int MaxItems, TileItemSources Sources)
{
    public bool IsFull => Items.Count >= MaxItems;
}

/// <summary>Where a new entry comes from: a search hit of the module, or something chosen/typed/dropped.</summary>
public sealed record TileItemSource(EntityInfo? Entity = null, DroppedItem? Dropped = null);

/// <summary>
/// The module lets the user edit the entries of some of its tiles directly in the bar's edit mode
/// (contract 1.4) – add (search, file, folder, web page, drop), reorder, rename, remove – without a
/// window of its own. Every method gets a copy of the tile's configuration and returns the changed one
/// (null = not possible); the app puts it into the layout like any edit, so "Cancel" in edit mode
/// discards it and the layout syncs it. Entries are best kept in the configuration itself
/// (<see cref="TileConfig.Entities"/>, <see cref="TileConfig.Options"/>). Must be fast (no network).
/// </summary>
public interface ITileItemEditor
{
    /// <summary>The entries of the tile, or null if it has none to edit (other tile types).</summary>
    TileItemList? GetTileItems(TileConfig tile);

    /// <summary>Adds an entry at the end; null = not taken (wrong kind, tile full).</summary>
    TileConfig? AddTileItem(TileConfig tile, TileItemSource source);

    TileConfig? MoveTileItem(TileConfig tile, string itemId, int newIndex);

    /// <summary>null or empty <paramref name="label"/> = back to the name from the target.</summary>
    TileConfig? RenameTileItem(TileConfig tile, string itemId, string? label);

    TileConfig? RemoveTileItem(TileConfig tile, string itemId);
}

/// <summary>A row of a <see cref="SettingFieldKind.List"/> on the settings page (<see cref="ISettingListPresenter"/>).</summary>
/// <param name="Title">Main text, e.g. the name.</param>
/// <param name="Detail">Second text, e.g. kind and a shortened target.</param>
/// <param name="Icon">Icon name (Segoe Fluent Icons or "U+XXXX").</param>
/// <param name="Group">Heading the row is shown under (rows of a group in the given order).</param>
/// <param name="EntryId">The <c>"id"</c> of the list entry – it opens its fields on a click; null = shown only (e.g. lives in the layout).</param>
public sealed record SettingListRow(string Title, string? Detail = null, string? Icon = null, string? Group = null, string? EntryId = null);

/// <summary>
/// The module decides how a <see cref="SettingFieldKind.List"/> is shown (contract 1.4): one compact row
/// per entry (a click opens its fields), grouped, plus rows for things that are only shown. The app adds a
/// search field and keeps "Add". Without it the app shows one row per entry with the first text field.
/// </summary>
public interface ISettingListPresenter
{
    /// <summary>Rows in display order; every entry should appear once (entries missing here are shown at the end).</summary>
    IReadOnlyList<SettingListRow> GetListRows(string fieldKey, IReadOnlyList<IReadOnlyDictionary<string, string>> entries);
}
