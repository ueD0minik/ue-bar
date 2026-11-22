// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;
using UeBar.Sdk;

namespace UeBar.Modules.QuickAccess.Tests;

/// <summary>P2-4-Fix 1: building a collection in the bar (edit mode) and the compact settings page.</summary>
public sealed class CollectionTests
{
    private static TileConfig Collection(string label = "Schule", params string[] targets)
    {
        var tile = new TileConfig { Id = "schule", Module = QuickAccessModule.ModuleId, Type = GroupTileType.TypeId, Size = TileSize.Large, Label = label };
        for (var i = 0; i < targets.Length; i++)
        {
            tile.Entities[$"item{i + 1}"] = targets[i];
        }

        return tile;
    }

    private static (LayoutDocument Base, LayoutEditor Editor) Editing(params TileConfig[] tiles)
    {
        var layout = new LayoutDocument { Tabs = [new LayoutTab { Id = "t", Name = "Tab", Tiles = [.. tiles] }] };
        return (layout, new LayoutEditor(layout, null));
    }

    private static TileConfig Current(LayoutEditor editor, string id = "schule") => editor.FindTile(id)!.Value.Tile;

    [Fact]
    public async Task A_collection_is_filled_in_the_bar_from_search_file_folder_web_page_and_drop()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Folders.Add(@"C:\Users\demo\Schule\Mathe");
        env.Shell.Files.Add(@"C:\Users\demo\Schule\Plan.xlsx");
        var (_, editor) = Editing(Collection());
        bool Add(TileItemSource source) => TileItemEditing.Change(env.Registry, editor, "schule", "Add", (e, t) => e.AddTileItem(t, source));

        var writer = (await env.Module.SearchAsync("Writer", 5, default))[0];
        Assert.True(Add(new TileItemSource(Entity: writer)));                                                   // "Add entry" (search)
        Assert.True(Add(new TileItemSource(Dropped: new DroppedItem(DropKind.File, @"C:\Users\demo\Schule\Plan.xlsx")))); // "Choose file …"
        Assert.True(Add(new TileItemSource(Dropped: new DroppedItem(DropKind.Folder, @"C:\Users\demo\Schule\Mathe"))));  // "Choose folder …"
        Assert.True(Add(new TileItemSource(Dropped: new DroppedItem(DropKind.Link, "example.org/schule"))));             // "Enter web page …"
        Assert.False(Add(new TileItemSource(Dropped: new DroppedItem(DropKind.Link, "kein link"))));
        Assert.False(Add(new TileItemSource(Dropped: new DroppedItem(DropKind.Folder, @"C:\Users\demo\Schule\Mathe")))); // already in it

        var tile = Current(editor);
        Assert.Equal(
        [
            "app:Example.Writer.EXE.15",
            @"file:%USERPROFILE%\Schule\Plan.xlsx",
            @"folder:%USERPROFILE%\Schule\Mathe",
            "url:https://example.org/schule",
        ], Enumerable.Range(1, 4).Select(i => tile.GetEntity($"item{i}")));
        var items = env.Module.GetTileItems(tile)!;
        Assert.Equal(["Writer", "Plan", "Mathe", "example.org"], items.Items.Select(i => i.Label));
        Assert.Equal("Ordner · %USERPROFILE%\\Schule\\Mathe", items.Items[2].Detail);
        Assert.Equal((4, 6, false), (items.Items.Count, items.MaxItems, items.IsFull));
        Assert.True(items.Sources.HasFlag(TileItemSources.Drop));
    }

    [Fact]
    public async Task Entries_are_reordered_renamed_and_removed_and_the_tile_follows()
    {
        await using var env = await new QuickEnv().StartAsync();
        foreach (var subject in new[] { "Mathe", "Deutsch", "Englisch" })
        {
            env.Shell.Folders.Add($@"C:\Users\demo\Schule\{subject}");
        }

        var (_, editor) = Editing(Collection("Schule", @"folder:%USERPROFILE%\Schule\Mathe", @"folder:%USERPROFILE%\Schule\Deutsch", @"folder:%USERPROFILE%\Schule\Englisch"));
        bool Do(Func<ITileItemEditor, TileConfig, TileConfig?> change) => TileItemEditing.Change(env.Registry, editor, "schule", "Edit", change);

        Assert.True(Do((e, t) => e.MoveTileItem(t, "item3", 0)));            // drag "Englisch" to the top
        Assert.True(Do((e, t) => e.RenameTileItem(t, "item2", " Mathematik ")));
        Assert.True(Do((e, t) => e.RemoveTileItem(t, "item3")));             // "Deutsch"

        var config = Current(editor);
        Assert.Equal((@"folder:%USERPROFILE%\Schule\Englisch", @"folder:%USERPROFILE%\Schule\Mathe"), (config.GetEntity("item1"), config.GetEntity("item2")));
        Assert.Null(config.GetEntity("item3")); // no gap left behind
        Assert.Equal("Mathematik", config.GetOption("item2.name"));
        var tile = await env.TileAsync(config);
        Assert.Equal(["Englisch", "Mathematik"], tile.FirstElement<IconGridElement>()!.Items.Select(i => i.Label));

        // A name back to empty = the name from the target again; the option is gone.
        Assert.True(Do((e, t) => e.RenameTileItem(t, "item2", "")));
        Assert.Null(Current(editor).Options);
        Assert.False(Do((e, t) => e.RemoveTileItem(t, "item9")));
    }

    [Fact]
    public async Task A_full_collection_takes_no_more_and_the_rest_of_a_drop_becomes_tiles()
    {
        await using var env = await new QuickEnv().StartAsync();
        var (_, editor) = Editing(Collection("Voll", [.. Enumerable.Range(1, 6).Select(i => $"url:https://example.org/{i}")]));

        Assert.True(env.Module.GetTileItems(Current(editor))!.IsFull);
        Assert.False(TileItemEditing.Change(env.Registry, editor, "schule", "Add", (e, t) => e.AddTileItem(t, new TileItemSource(Dropped: new DroppedItem(DropKind.Link, "https://example.org/7")))));
        Assert.Equal(6, Enumerable.Range(1, 7).Count(i => Current(editor).GetEntity($"item{i}") is not null));
    }

    [Fact]
    public async Task Cancel_and_undo_in_edit_mode_also_discard_changes_of_a_collection()
    {
        await using var env = await new QuickEnv().StartAsync();
        var (saved, editor) = Editing(Collection("Schule", "url:https://example.org/a"));

        Assert.True(TileItemEditing.Change(env.Registry, editor, "schule", "Add", (e, t) => e.AddTileItem(t, new TileItemSource(Dropped: new DroppedItem(DropKind.Link, "https://example.org/b")))));
        Assert.Equal("url:https://example.org/b", Current(editor).GetEntity("item2"));

        // "Cancel": the layout that was saved before stays as it was.
        Assert.Null(saved.Tabs[0].Tiles[0].GetEntity("item2"));
        // "Undo": back one step inside the edit session.
        editor.Undo();
        Assert.Null(Current(editor).GetEntity("item2"));
    }

    [Fact]
    public async Task Old_collections_with_gaps_keep_working()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Folders.Add(@"D:\B");
        var config = Collection("Alt");
        config.Entities["item2"] = @"folder:D:\A";
        config.Entities["item5"] = @"folder:D:\B";
        var tile = await env.TileAsync(config);

        Assert.Equal(["item1", "item2"], tile.FirstElement<IconGridElement>()!.Items.Select(i => i.Id));
        await env.Registry.InteractAsync(tile, new TileInteraction("items", InteractionKind.Press, Text: "item2"));
        Assert.Equal(@"D:\B", Assert.Single(env.Shell.Launches).File);
        Assert.Empty(new GroupTileType(env.Module).Slots); // nothing to pick when adding – edited in the bar
    }

    [Fact]
    public async Task Other_tiles_have_no_entries_to_edit()
    {
        await using var env = await new QuickEnv().StartAsync();
        var single = QuickEnv.Item("url:https://example.org/");
        Assert.Null(env.Module.GetTileItems(single));
        Assert.Null(env.Module.AddTileItem(single, new TileItemSource(Dropped: new DroppedItem(DropKind.Link, "https://example.org/x"))));
    }

    [Fact]
    public async Task Settings_page_groups_entries_by_single_tiles_collections_and_not_used()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Folders.Add(@"C:\Users\demo\Schule\Mathe");
        env.Shell.Folders.Add(@"D:\Archiv");
        await env.EntriesAsync(
            QuickEnv.Entry("m", "Mathe", "folder", folder: @"C:\Users\demo\Schule\Mathe"),
            QuickEnv.Entry("c", "Backup", "command", text: "robocopy a b"),
            QuickEnv.Entry("u", "Archiv", "folder", folder: @"D:\Archiv"));
        env.Module.OnLayoutChanged(new ModuleLayoutInfo(
        [
            QuickEnv.Item("entry:c"),
            QuickEnv.Item("url:https://example.org/"),
            Collection("Schule", @"folder:%USERPROFILE%\Schule\Mathe", "url:https://example.org/vokabeln"),
            Collection("Freizeit", @"folder:%USERPROFILE%\Schule\Mathe"),
        ], []));

        var rows = env.Module.GetListRows(QuickSettings.EntriesKey, env.State.GetModuleSettings(QuickAccessModule.ModuleId).GetList(QuickSettings.EntriesKey));

        Assert.Equal(
        [
            ("Einzelne Kacheln", "Backup", "c"),
            ("Einzelne Kacheln", "example.org", null),
            ("Sammlung „Schule“", "Mathe", "m"),
            ("Sammlung „Schule“", "example.org", null),   // lives only in the layout: shown, edited in the bar
            ("Sammlung „Freizeit“", "Mathe", null),        // the entry opens only once
            ("Noch auf keiner Kachel", "Archiv", "u"),
        ], rows.Select(r => (r.Group, r.Title, r.EntryId)));
        Assert.Equal([false, false, false, false, false, true], rows.Select(r => r.CanAddTile)); // P2-5: only entries on no tile get "Add as tile"
        Assert.Contains("in der Bar bearbeiten", rows[1].Detail, StringComparison.Ordinal);
        Assert.Equal("Ordner · D:\\Archiv", rows[^1].Detail);
    }

    [Fact]
    public async Task Settings_explain_the_page_and_an_entry_becomes_the_same_tile_as_from_the_search()
    {
        await using var env = await new QuickEnv().StartAsync();

        var intro = env.Module.SettingFields[0];
        Assert.Equal(SettingFieldKind.Info, intro.Kind);
        Assert.Contains("Bearbeiten → Kachel hinzufügen", intro.Description, StringComparison.Ordinal);

        var folder = env.Module.CreateTileForListEntry(QuickSettings.EntriesKey, QuickEnv.Entry("u", "Archiv", "folder", folder: @"C:\Users\demo\Archiv"))!;
        Assert.Equal((ItemTileType.TypeId, TileSize.Small, "Archiv"), (folder.Type, folder.Size, folder.Label));
        Assert.Equal(@"folder:%USERPROFILE%\Archiv", folder.GetEntity("main")); // like the search: the target itself, portable

        var command = env.Module.CreateTileForListEntry(QuickSettings.EntriesKey, QuickEnv.Entry("c", "Backup", "command", text: "robocopy a b"))!;
        Assert.Equal("entry:c", command.GetEntity("main")); // own commands stay a reference to this PC

        Assert.Null(env.Module.CreateTileForListEntry(QuickSettings.EntriesKey, QuickEnv.Entry("n", "Neu", "file"))); // no target yet
        Assert.Null(env.Module.CreateTileForListEntry("other", QuickEnv.Entry("u", "Archiv", "folder", folder: @"D:\Archiv")));
    }
}
