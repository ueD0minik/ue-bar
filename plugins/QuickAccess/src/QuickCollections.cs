// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;
using static UeBar.Modules.QuickAccess.QuickText;

namespace UeBar.Modules.QuickAccess;

/// <summary>An entry of a collection as stored in its tile: the target and an own name (null = from the target).</summary>
internal sealed record CollectionItem(string Target, string? Name);

/// <summary>
/// The entries of a collection tile live in the tile itself (synced with the layout): slots
/// <c>item1</c>…<c>item6</c> carry the targets, <see cref="TileConfig.Options"/> <c>itemN.name</c> the own names.
/// Writing always keeps them in order without gaps.
/// </summary>
internal static class CollectionItems
{
    public static string Slot(int index) => "item" + (index + 1).ToString(CultureInfo.InvariantCulture);

    public static string NameKey(string slot) => slot + ".name";

    public static List<CollectionItem> Read(TileConfig tile)
    {
        var items = new List<CollectionItem>();
        for (var i = 0; i < GroupTileType.MaxItems; i++)
        {
            var slot = Slot(i);
            if (tile.GetEntity(slot) is { Length: > 0 } target)
            {
                items.Add(new CollectionItem(target, tile.GetOption(NameKey(slot)) is { Length: > 0 } name ? name : null));
            }
        }

        return items;
    }

    /// <summary>A copy of <paramref name="tile"/> with exactly <paramref name="items"/> (other slots and options stay).</summary>
    public static TileConfig Write(TileConfig tile, IReadOnlyList<CollectionItem> items)
    {
        var copy = UeBarJson.Clone(tile);
        var options = copy.Options is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(copy.Options, StringComparer.Ordinal);
        for (var i = 0; i < GroupTileType.MaxItems; i++)
        {
            copy.Entities.Remove(Slot(i));
            options.Remove(NameKey(Slot(i)));
        }

        for (var i = 0; i < items.Count && i < GroupTileType.MaxItems; i++)
        {
            copy.Entities[Slot(i)] = items[i].Target;
            if (!string.IsNullOrWhiteSpace(items[i].Name))
            {
                options[NameKey(Slot(i))] = items[i].Name!.Trim();
            }
        }

        copy.Options = options.Count == 0 ? null : options;
        return copy;
    }
}

public sealed partial class QuickAccessModule
{
    private IReadOnlyList<TileConfig> _layoutTiles = [];

    // ------------------------------------------------------------------ editing a collection in the bar (ITileItemEditor)

    public TileItemList? GetTileItems(TileConfig tile)
    {
        if (tile.Type != GroupTileType.TypeId)
        {
            return null;
        }

        var items = CollectionItems.Read(tile).Select((item, i) =>
        {
            var target = QuickTarget.Parse(item.Target);
            var resolved = target is null ? null : Resolve(target);
            var name = item.Name ?? resolved?.Name ?? T["Qa_NotFound"];
            var detail = resolved is null || !resolved.Exists ? T["Qa_NotFound"] : $"{QuickNames.KindText(resolved.Kind)} · {Shorten(target!)}";
            return new TileItem(CollectionItems.Slot(i), name, resolved is null ? QuickNames.AppIcon : QuickNames.Icon(resolved.Kind),
                resolved is { Exists: true, IconSource: not null } ? IconId(target!) : null, detail);
        }).ToList();
        return new TileItemList(items, GroupTileType.MaxItems, TileItemSources.Search | TileItemSources.File | TileItemSources.Folder | TileItemSources.Link | TileItemSources.Drop);
    }

    public TileConfig? AddTileItem(TileConfig tile, TileItemSource source)
    {
        if (tile.Type != GroupTileType.TypeId)
        {
            return null;
        }

        var items = CollectionItems.Read(tile);
        if (items.Count >= GroupTileType.MaxItems)
        {
            Log?.Info("Collection is full – entry not added");
            return null;
        }

        var (target, name) = source switch
        {
            { Entity: { } entity } when entity.Id != GroupEntity =>
                (TargetOf(entity.Id), entity.Id.StartsWith("entry:", StringComparison.Ordinal) ? entity.Name : null),
            { Dropped: { } dropped } => FromDropped(dropped),
            _ => (null, null),
        };
        if (target is null || items.Any(i => string.Equals(i.Target, target.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return null; // not a target, or already in it
        }

        if (target.Kind == TargetKind.App)
        {
            EnsureAppsLoading();
        }

        items.Add(new CollectionItem(target.ToString(), string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
        Log?.Info($"Collection: {target.Kind} added ({items.Count}/{GroupTileType.MaxItems})");
        return CollectionItems.Write(tile, items);
    }

    public TileConfig? MoveTileItem(TileConfig tile, string itemId, int newIndex)
    {
        var items = CollectionItems.Read(tile);
        var index = IndexOf(itemId, items.Count);
        if (tile.Type != GroupTileType.TypeId || index < 0)
        {
            return null;
        }

        var item = items[index];
        items.RemoveAt(index);
        items.Insert(Math.Clamp(newIndex, 0, items.Count), item);
        return CollectionItems.Write(tile, items);
    }

    public TileConfig? RenameTileItem(TileConfig tile, string itemId, string? label)
    {
        var items = CollectionItems.Read(tile);
        var index = IndexOf(itemId, items.Count);
        if (tile.Type != GroupTileType.TypeId || index < 0)
        {
            return null;
        }

        items[index] = items[index] with { Name = string.IsNullOrWhiteSpace(label) ? null : label.Trim() };
        return CollectionItems.Write(tile, items);
    }

    public TileConfig? RemoveTileItem(TileConfig tile, string itemId)
    {
        var items = CollectionItems.Read(tile);
        var index = IndexOf(itemId, items.Count);
        if (tile.Type != GroupTileType.TypeId || index < 0)
        {
            return null;
        }

        // Nothing else to clean up: the entry lives only in this tile (an entry of the settings it came
        // from is the user's own and stays).
        items.RemoveAt(index);
        return CollectionItems.Write(tile, items);
    }

    private static int IndexOf(string itemId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (CollectionItems.Slot(i) == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Kind-independent short form of a target for the settings and the edit mode.</summary>
    internal static string Shorten(QuickTarget target, int max = 48)
    {
        var text = target.Kind switch
        {
            TargetKind.App => target.Value.Split('\\').Last(),
            TargetKind.Entry => T["Qa_Entries"],
            _ => target.Value,
        };
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max / 2 - 1), "…", text.AsSpan(text.Length - (max / 2)));
    }

    // ------------------------------------------------------------------ the layout (for the settings page)

    public void OnLayoutChanged(ModuleLayoutInfo layout)
    {
        lock (_lock)
        {
            _layoutTiles = [.. layout.Tiles];
        }
    }

    internal IReadOnlyList<TileConfig> LayoutTiles
    {
        get
        {
            lock (_lock)
            {
                return _layoutTiles;
            }
        }
    }

    // ------------------------------------------------------------------ settings page: compact, grouped (ISettingListPresenter)

    /// <summary>
    /// The entries grouped by where they are used: single tiles, each collection, not used. Things that
    /// live only in the layout (a folder dropped on a tile) are listed too, but only shown – they are
    /// edited in the bar.
    /// </summary>
    /// <summary>
    /// P2-5 (<see cref="ISettingListTiles"/>): an entry of the settings as a single tile – the same tile the
    /// search would make (the target itself, own commands stay a reference). Null while it has no target yet.
    /// </summary>
    public TileConfig? CreateTileForListEntry(string fieldKey, IReadOnlyDictionary<string, string> entry)
    {
        if (fieldKey != QuickSettings.EntriesKey)
        {
            return null;
        }

        var values = new ModuleSettingValues(new Dictionary<string, string> { [fieldKey] = ModuleSettingValues.FormatList([entry]) });
        if (QuickSettings.Entries(values).FirstOrDefault() is not { } parsed)
        {
            return null;
        }

        if (parsed.Kind == EntryKind.App)
        {
            EnsureAppsLoading();
        }

        return new TileConfig
        {
            Module = ModuleId,
            Type = ItemTileType.TypeId,
            Size = TileSize.Small,
            Entities = new Dictionary<string, string> { ["main"] = TileTargetOf(parsed).ToString() },
            Label = parsed.Name,
        };
    }

    public IReadOnlyList<SettingListRow> GetListRows(string fieldKey, IReadOnlyList<IReadOnlyDictionary<string, string>> entries)
    {
        if (fieldKey != QuickSettings.EntriesKey)
        {
            return [];
        }

        var parsed = QuickSettings.Entries(new ModuleSettingValues(new Dictionary<string, string> { [fieldKey] = ModuleSettingValues.FormatList(entries) }));
        EnsureAppsLoading(); // app names – the page shows them the next time it is opened
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<SettingListRow>();

        SettingListRow Row(string target, string? name, string group)
        {
            var parsedTarget = QuickTarget.Parse(target);
            var entry = parsedTarget?.Kind == TargetKind.Entry
                ? parsed.FirstOrDefault(e => e.Id == parsedTarget.Value)
                : parsed.FirstOrDefault(e => string.Equals(TileTargetOf(e).ToString(), target, StringComparison.OrdinalIgnoreCase));
            var resolved = parsedTarget is null ? null : Resolve(parsedTarget);
            var title = name ?? entry?.Name ?? resolved?.Name ?? T["Qa_NotFound"];
            var kind = resolved?.Kind ?? entry?.Kind ?? EntryKind.File;
            var where = entry?.Kind == EntryKind.Command ? Shorten(new QuickTarget(TargetKind.File, entry.Value)) : parsedTarget is null ? "" : Shorten(parsedTarget);
            var detail = resolved is { Exists: true } ? $"{QuickNames.KindText(kind)} · {where}" : $"{QuickNames.KindText(kind)} · {T["Qa_NotFound"]}";
            var editable = entry is not null && shown.Add(entry.Id);
            return new SettingListRow(title, editable ? detail : detail + " · " + T["Qa_EditInBar"], QuickNames.Icon(kind), group, editable ? entry!.Id : null);
        }

        foreach (var tile in LayoutTiles.Where(t => t.Type == ItemTileType.TypeId))
        {
            if (tile.GetEntity("main") is { } target)
            {
                rows.Add(Row(target, tile.Label, T["Qa_GroupSingle"]));
            }
        }

        var collections = LayoutTiles.Where(t => t.Type == GroupTileType.TypeId).ToList();
        for (var c = 0; c < collections.Count; c++)
        {
            var title = string.IsNullOrWhiteSpace(collections[c].Label) ? T["Qa_Group"] : collections[c].Label!;
            var heading = T.Format("Qa_GroupCollection", collections.Count(t => (t.Label ?? T["Qa_Group"]) == title) > 1 ? $"{title} ({c + 1})" : title);
            var items = CollectionItems.Read(collections[c]);
            if (items.Count == 0)
            {
                rows.Add(new SettingListRow(T["Qa_GroupEmpty"], null, QuickNames.GroupIcon, heading));
            }

            rows.AddRange(items.Select(i => Row(i.Target, i.Name, heading)));
        }

        rows.AddRange(parsed.Where(e => !shown.Contains(e.Id)).Select(e =>
            new SettingListRow(e.Name, $"{QuickNames.KindText(e.Kind)} · {Shorten(TileTargetOf(e) is { Kind: not TargetKind.Entry } t ? t : new QuickTarget(TargetKind.File, e.Value))}", QuickNames.Icon(e.Kind), T["Qa_GroupUnused"], e.Id) { CanAddTile = true }));
        return rows;
    }
}
