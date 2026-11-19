// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.App.Localization;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.Bar.Edit;

/// <summary>An entry of a tile in edit mode (rename, remove, drag to reorder).</summary>
internal sealed class TileItemRow(TileItem item, Action<TileItemRow, string?> rename) : ObservableObject
{
    public string Id => item.Id;

    public string? Icon => item.Icon;

    public string? ImageUrl => item.ImageUrl;

    public string? Detail => item.Detail;

    /// <summary>The name; changing it renames the entry (an empty name = back to the name from the target).</summary>
    public string Label
    {
        get => item.Label;
        set
        {
            if (value != item.Label)
            {
                rename(this, value);
            }
        }
    }
}

/// <summary>
/// P2-4-Fix 1: the entries of the selected tile when its module edits them itself
/// (<see cref="ITileItemEditor"/>) – add (search, file, folder, web page, drop), reorder, rename, remove.
/// Every change is a new tile configuration from the module that goes into the layout editor (undo,
/// cancel and sync as for every edit). Nothing module-specific.
/// </summary>
internal sealed class TileItemsViewModel : ObservableObject
{
    private readonly ModuleRegistry _registry;
    private readonly LayoutEditor _editor;
    private readonly IUeLogger _log;
    private readonly DispatcherTimer _searchTimer;
    private int _searchGeneration;

    public TileItemsViewModel(ModuleRegistry registry, LayoutEditor editor, IUeLogger log)
    {
        _registry = registry;
        _editor = editor;
        _log = log;
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchAsync();
        };
        RemoveCommand = new RelayCommand<TileItemRow>(row =>
        {
            if (row is not null)
            {
                Apply("Remove entry", (e, t) => e.RemoveTileItem(t, row.Id));
            }
        });
        PickCommand = new RelayCommand<EntityInfo>(hit =>
        {
            if (hit is not null && Apply("Add entry", (e, t) => e.AddTileItem(t, new TileItemSource(Entity: hit))))
            {
                SearchText = "";
            }
        });
        AddLinkCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(LinkText) && Add(new DroppedItem(DropKind.Link, LinkText.Trim())))
            {
                LinkText = "";
            }
            else if (!string.IsNullOrWhiteSpace(LinkText))
            {
                Hint = Loc.Get("Edit_ItemNotTaken");
            }
        });
    }

    /// <summary>The tile whose entries are shown (null = the selected tile has none).</summary>
    public string? TileId { get; private set; }

    public string? ModuleId { get; private set; }

    public bool IsActive => TileId is not null;

    public ObservableCollection<TileItemRow> Items { get; } = [];

    public ObservableCollection<EntityInfo> Results { get; } = [];

    public RelayCommand<TileItemRow> RemoveCommand { get; }

    public RelayCommand<EntityInfo> PickCommand { get; }

    public RelayCommand AddLinkCommand { get; }

    public string CountText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    public bool CanAdd
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool CanSearch
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool CanChooseFile
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool CanChooseFolder
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool CanEnterLink
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Short message (e.g. "not added").</summary>
    public string Hint
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    public string SearchText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    } = "";

    public string LinkText
    {
        get;
        set => SetProperty(ref field, value);
    } = "";

    /// <summary>Shows the entries of <paramref name="tile"/> (or nothing, if its module does not edit them).</summary>
    public void Load(TileConfig? tile)
    {
        var list = tile is null ? null : ItemsOf(_registry, tile);
        var changedTile = tile?.Id != TileId;
        TileId = list is null ? null : tile!.Id;
        ModuleId = list is null ? null : tile!.Module;
        Items.Clear();
        if (list is not null)
        {
            foreach (var item in list.Items)
            {
                Items.Add(new TileItemRow(item, (row, label) => Apply("Rename entry", (e, t) => e.RenameTileItem(t, row.Id, label))));
            }

            CountText = $"{list.Items.Count} / {list.MaxItems}";
            CanAdd = !list.IsFull;
            CanSearch = list.Sources.HasFlag(TileItemSources.Search) && !list.IsFull;
            CanChooseFile = list.Sources.HasFlag(TileItemSources.File) && !list.IsFull;
            CanChooseFolder = list.Sources.HasFlag(TileItemSources.Folder) && !list.IsFull;
            CanEnterLink = list.Sources.HasFlag(TileItemSources.Link) && !list.IsFull;
        }

        if (changedTile)
        {
            Hint = "";
            SearchText = "";
            LinkText = "";
            Results.Clear();
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(TileId));
    }

    /// <summary>The entries of a tile through the fault protection (null = it has none to edit).</summary>
    public static TileItemList? ItemsOf(ModuleRegistry registry, TileConfig tile) => TileItemEditing.ItemsOf(registry, tile);

    /// <summary>A file, folder or web page chosen in a dialog / typed in.</summary>
    public bool Add(DroppedItem item) => Apply("Add entry", (e, t) => e.AddTileItem(t, new TileItemSource(Dropped: item)));

    public bool Move(string itemId, int newIndex) => Apply("Move entry", (e, t) => e.MoveTileItem(t, itemId, newIndex));

    private bool Apply(string operation, Func<ITileItemEditor, TileConfig, TileConfig?> change)
    {
        if (TileId is not { } id || ModuleId is not { } module)
        {
            return false;
        }

        var done = Change(_registry, _editor, id, operation, change);
        Hint = done ? "" : Loc.Get("Edit_ItemNotTaken");
        if (!done)
        {
            _log.Info(module, $"Edit: {operation} – nothing changed");
        }

        return done;
    }

    /// <summary>One change of a tile's entries: the module returns the new configuration, the editor takes it.</summary>
    public static bool Change(ModuleRegistry registry, LayoutEditor editor, string tileId, string operation, Func<ITileItemEditor, TileConfig, TileConfig?> change) =>
        TileItemEditing.Change(registry, editor, tileId, operation, change);

    private async Task SearchAsync()
    {
        var generation = ++_searchGeneration;
        var query = SearchText.Trim();
        if (ModuleId is not { } module || query.Length == 0 || _registry.Find(module)?.Module is not IEntitySource)
        {
            Results.Clear();
            return;
        }

        var result = await _registry.InvokeAsync<IReadOnlyList<EntityInfo>>(
            module, "Search entries", (m, ct) => ((IEntitySource)m).SearchAsync(query, 8, ct), TimeSpan.FromSeconds(6));
        if (generation != _searchGeneration)
        {
            return;
        }

        Results.Clear();
        foreach (var hit in result.Value ?? [])
        {
            Results.Add(hit);
        }

        Hint = Results.Count == 0 ? Loc.Get("Search_NothingFound") : "";
    }
}
