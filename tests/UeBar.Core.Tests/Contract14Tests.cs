// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Layout;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>Contract 1.4 (P2-4-Fix 1): own values of a tile, editing a tile's entries in the bar.</summary>
public sealed class Contract14Tests
{
    [Fact]
    public void Own_values_of_a_tile_travel_with_the_layout_and_are_left_out_when_empty()
    {
        var tile = new TileConfig { Id = "t", Module = "m", Type = "m.x", Options = new() { ["item1.name"] = "Mathe" } };

        var json = UeBarJson.Serialize(tile);
        var back = UeBarJson.Deserialize<TileConfig>(json)!;

        Assert.Equal("Mathe", back.GetOption("item1.name"));
        Assert.Null(back.GetOption("missing"));
        Assert.Null(JsonNode.Parse(UeBarJson.Serialize(new TileConfig { Id = "u" }))!["options"]); // old layouts stay as they were
        Assert.Equal(new Version(1, 4), CoreApiVersion.Version);
    }

    [Fact]
    public async Task Entries_are_changed_through_the_layout_editor_and_the_module_cannot_move_the_tile()
    {
        await using var env = new TestEnv();
        var module = new ListModule();
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();
        var saved = new LayoutDocument { Tabs = [new LayoutTab { Id = "a", Tiles = [new TileConfig { Id = "t", Module = "lists", Type = "lists.list" }] }] };
        var editor = new LayoutEditor(saved, null);

        Assert.Empty(TileItemEditing.ItemsOf(env.Registry, editor.FindTile("t")!.Value.Tile)!.Items);
        Assert.True(TileItemEditing.Change(env.Registry, editor, "t", "Add", (e, t) => e.AddTileItem(t, new TileItemSource(Dropped: new DroppedItem(DropKind.Text, "eins")))));

        var changed = editor.FindTile("t")!.Value.Tile;
        Assert.Equal(("eins", "lists"), (changed.GetOption("1"), changed.Module));
        Assert.Null(saved.Tabs[0].Tiles[0].Options); // "Cancel" keeps the saved layout
        Assert.False(TileItemEditing.Change(env.Registry, editor, "t", "Remove", (e, t) => e.RemoveTileItem(t, "nope")));
        Assert.False(TileItemEditing.Change(env.Registry, editor, "missing", "Add", (e, t) => e.AddTileItem(t, new TileItemSource())));
        Assert.True(module.GotCopies); // the module never sees the editor's own object
    }

    /// <summary>A module that keeps entries of its tile in <see cref="TileConfig.Options"/> ("1", "2" …).</summary>
    private sealed class ListModule : IModule, ITileItemEditor
    {
        private readonly HashSet<TileConfig> _seen = [];

        public string Id => "lists";

        public string DisplayName => "Lists";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public bool GotCopies { get; private set; } = true;

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public TileItemList? GetTileItems(TileConfig tile) =>
            new([.. (tile.Options ?? []).Select(o => new TileItem(o.Key, o.Value))], 3, TileItemSources.Drop);

        public TileConfig? AddTileItem(TileConfig tile, TileItemSource source)
        {
            GotCopies &= _seen.Add(tile);
            if (source.Dropped is not { } dropped)
            {
                return null;
            }

            tile.Options ??= [];
            tile.Options[(tile.Options.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = dropped.Value;
            tile.Module = "somewhere-else"; // ignored by the core
            return tile;
        }

        public TileConfig? MoveTileItem(TileConfig tile, string itemId, int newIndex) => null;

        public TileConfig? RenameTileItem(TileConfig tile, string itemId, string? label) => null;

        public TileConfig? RemoveTileItem(TileConfig tile, string itemId) => tile.Options?.Remove(itemId) == true ? tile : null;

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
