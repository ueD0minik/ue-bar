// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Modules;

namespace UeBar.Core.Tests;

/// <summary>M5a: edit logic without UI, levels base + device, visibility by location.</summary>
public class LayoutEditorTests
{
    private static LayoutDocument Base() => new()
    {
        Tabs =
        [
            new LayoutTab
            {
                Id = "zimmer",
                Name = "Zimmer",
                Tiles =
                [
                    Tile("desk", TileSize.Wide),
                    Tile("decke", TileSize.Wide),
                    Tile("klima", TileSize.Large),
                ],
            },
            new LayoutTab { Id = "routinen", Name = "Routinen", Tiles = [Tile("r1"), Tile("r2")] },
        ],
        Status = [new StatusItemConfig { Module = "m", Entity = "wetter" }],
    };

    private static TileConfig Tile(string id, TileSize? size = null) => new()
    {
        Id = id,
        Module = "m",
        Type = id == "klima" ? "m.climate" : "m.any",
        Size = size ?? TileSize.Small,
        Entities = new() { ["main"] = "m." + id },
    };

    private static IReadOnlyList<string> Ids(LayoutTab tab) => [.. tab.Tiles.Select(t => t.Id)];

    private static IReadOnlyList<string> TabIds(LayoutEditor editor) => [.. editor.Tabs.Select(t => t.Tab.Id)];

    [Fact]
    public void Move_resize_rename_delete_tiles()
    {
        var editor = new LayoutEditor(Base(), null);

        Assert.True(editor.MoveTile("klima", "zimmer", 0));
        Assert.Equal(["klima", "desk", "decke"], Ids(editor.FindTab("zimmer")!));
        Assert.True(editor.ResizeTile("desk", TileSize.Large));
        Assert.True(editor.SetTileLabel("desk", "  Schreibtisch  "));
        Assert.True(editor.SetTileIcon("desk", "Laptop"));
        Assert.True(editor.RemoveTile("decke"));

        var (result, _) = editor.Result;
        var zimmer = result.Tabs[0];
        Assert.Equal(["klima", "desk"], Ids(zimmer));
        Assert.Equal(TileSize.Large, zimmer.Tiles[1].Size);
        Assert.Equal("Schreibtisch", zimmer.Tiles[1].Label);
        Assert.Equal("Laptop", zimmer.Tiles[1].Icon);
        Assert.Equal(5, editor.StepCount);
    }

    [Fact]
    public void Move_tile_to_other_tab_and_noop_changes_are_not_steps()
    {
        var editor = new LayoutEditor(Base(), null);

        Assert.False(editor.MoveTile("desk", "zimmer", 0)); // already there
        Assert.False(editor.SetTileLabel("desk", null)); // was already empty
        Assert.False(editor.RemoveTile("gibtsnicht"));
        Assert.Equal(0, editor.StepCount);

        Assert.True(editor.MoveTile("klima", "routinen", 1));
        Assert.Equal(["desk", "decke"], Ids(editor.FindTab("zimmer")!));
        Assert.Equal(["r1", "klima", "r2"], Ids(editor.FindTab("routinen")!));
    }

    [Fact]
    public void Resize_only_to_sizes_the_tile_type_allows()
    {
        var editor = new LayoutEditor(Base(), null, t => t.Type == "m.climate" ? [TileSize.Large, TileSize.Wide] : null);

        Assert.False(editor.ResizeTile("klima", TileSize.Small));
        Assert.True(editor.ResizeTile("klima", TileSize.Wide));
        Assert.True(editor.ResizeTile("r1", TileSize.Large)); // unknown restriction = all allowed
        Assert.Equal([TileSize.Large, TileSize.Wide], editor.AllowedSizes(editor.FindTile("klima")!.Value.Tile));
    }

    [Fact]
    public void Undo_steps_back_one_change_at_a_time()
    {
        var original = UeBarJson.Serialize(Base());
        var editor = new LayoutEditor(Base(), null);
        var changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.RemoveTile("desk");
        editor.RenameTab("zimmer", "Büro");
        editor.AddTab("Neu");

        editor.Undo();
        Assert.Equal(["zimmer", "routinen"], TabIds(editor));
        Assert.Equal("Büro", editor.FindTab("zimmer")!.Name);
        editor.Undo();
        editor.Undo();
        Assert.False(editor.CanUndo);
        editor.Undo(); // nothing left – no error

        Assert.Equal(original, UeBarJson.Serialize(editor.Result.Base));
        Assert.Equal(6, changes);
    }

    [Fact]
    public void Cancel_means_the_input_was_never_touched()
    {
        var input = Base();
        var before = UeBarJson.Serialize(input);
        var editor = new LayoutEditor(input, null);

        editor.RemoveTab("zimmer");
        editor.AddTile("routinen", Tile("neu"));

        // "Cancel" = discard the editor: the input is unchanged.
        Assert.Equal(before, UeBarJson.Serialize(input));
    }

    [Fact]
    public void Add_tile_gets_unique_id_and_tab_operations_work()
    {
        var editor = new LayoutEditor(Base(), null);

        var id = editor.AddTile("routinen", Tile("desk")); // ID exists already
        var tab = editor.AddTab("Medien", "Tv", index: 1);
        Assert.True(editor.MoveTab(tab, 2));
        Assert.True(editor.SetTabIcon(tab, "MusicNote"));
        Assert.True(editor.SetTabVisibility(tab, TabVisibility.HomeOnly));

        Assert.NotNull(id);
        Assert.NotEqual("desk", id);
        Assert.Equal(["zimmer", "routinen", tab], TabIds(editor));
        Assert.Equal(TabVisibility.HomeOnly, editor.FindTab(tab)!.Visibility);
        Assert.True(editor.RemoveTab(tab));
        Assert.Equal(["zimmer", "routinen"], TabIds(editor));
    }

    [Fact]
    public void Device_tabs_live_in_device_json_and_keep_their_position()
    {
        var editor = new LayoutEditor(Base(), null);
        var tab = editor.AddTab("Nur Laptop", index: 1);

        Assert.True(editor.SetTabOrigin(tab, TabOrigin.Device));
        var (baseLayout, device) = editor.Result;

        Assert.Equal(["zimmer", "routinen"], baseLayout.Tabs.Select(t => t.Id));
        Assert.Equal([tab], device.Tabs.Select(t => t.Id));
        Assert.Equal(["zimmer", tab, "routinen"], TabIds(editor)); // position stays
        Assert.Equal(["zimmer", tab, "routinen"], device.TabOrder);

        // back into the base → device.json empty again
        Assert.True(editor.SetTabOrigin(tab, TabOrigin.Base));
        Assert.True(editor.Result.Device.IsEmpty);
    }

    [Fact]
    public void Hide_base_tab_on_this_device_only()
    {
        var editor = new LayoutEditor(Base(), null);

        Assert.True(editor.SetTabHiddenOnDevice("routinen", true));
        var composed = editor.Composed;

        Assert.Equal(["zimmer"], composed.Document.Tabs.Select(t => t.Id)); // display without the hidden tab
        Assert.Contains(composed.AllTabs, t => t.Tab.Id == "routinen" && t.HiddenOnDevice); // editing sees it
        Assert.Equal(["zimmer", "routinen"], editor.Result.Base.Tabs.Select(t => t.Id)); // base stays complete

        Assert.True(editor.SetTabHiddenOnDevice("routinen", false));
        Assert.Empty(editor.Result.Device.HiddenBaseTabs);
    }

    [Fact]
    public void Compose_merges_base_minus_hidden_plus_device_tabs_in_saved_order()
    {
        var device = new DeviceLayout
        {
            Tabs = [new LayoutTab { Id = "laptop", Name = "Laptop" }, new LayoutTab { Id = "zimmer", Name = "Doppelt" }],
            HiddenBaseTabs = ["routinen"],
            TabOrder = ["laptop", "zimmer"],
        };

        var composed = LayoutLayers.Compose(Base(), device);

        Assert.Equal(["laptop", "zimmer"], composed.Document.Tabs.Select(t => t.Id));
        Assert.Equal("Zimmer", composed.Document.Tabs[1].Name); // same ID: base wins
        Assert.Equal(TabOrigin.Device, composed.OriginOf("laptop"));
        Assert.Single(composed.Document.Status); // status line from the base
        Assert.Equal(3, composed.AllTabs.Count);
    }

    [Theory]
    [InlineData(TabVisibility.Always, DeviceLocation.Home, true)]
    [InlineData(TabVisibility.Always, DeviceLocation.Away, true)]
    [InlineData(TabVisibility.HomeOnly, DeviceLocation.Home, true)]
    [InlineData(TabVisibility.HomeOnly, DeviceLocation.Away, false)]
    [InlineData(TabVisibility.HomeOnly, DeviceLocation.Unknown, true)]
    [InlineData(TabVisibility.AwayOnly, DeviceLocation.Home, false)]
    [InlineData(TabVisibility.AwayOnly, DeviceLocation.Away, true)]
    [InlineData(TabVisibility.AwayOnly, DeviceLocation.Unknown, true)]
    public void Visibility_by_location(TabVisibility visibility, DeviceLocation location, bool visible) =>
        Assert.Equal(visible, LayoutLayers.IsVisible(new LayoutTab { Visibility = visibility }, location));

    [Fact]
    public void Visibility_is_readable_json()
    {
        var json = UeBarJson.Serialize(new LayoutTab { Id = "x", Visibility = TabVisibility.HomeOnly });

        Assert.Contains("\"visibility\": \"homeOnly\"", json);
        Assert.Equal(TabVisibility.AwayOnly, UeBarJson.Deserialize<LayoutTab>("""{ "id": "x", "visibility": "awayOnly" }""")!.Visibility);
        Assert.Equal(TabVisibility.Always, UeBarJson.Deserialize<LayoutTab>("""{ "id": "x" }""")!.Visibility); // old files
    }
}

public class LayoutManagerTests
{
    private sealed class Env : IDisposable
    {
        public Env()
        {
            Store = new LocalFileLayoutStore(Path.Combine(Dir.Path, "layout.json"), Path.Combine(Dir.Path, "backups"));
            Device = new DeviceLayoutStore(Path.Combine(Dir.Path, "device.json"));
            Manager = new LayoutManager(Store, Device, deviceName: "TESTPC");
        }

        public TempDirectory Dir { get; } = new();

        public LocalFileLayoutStore Store { get; }

        public DeviceLayoutStore Device { get; }

        public LayoutManager Manager { get; }

        public void Dispose()
        {
            Store.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Without_layout_setup_is_needed_and_setup_then_editing_sticks()
    {
        using var env = new Env();

        var first = await env.Manager.LoadAsync();
        Assert.True(env.Manager.NeedsSetup); // first start instead of a built-in suggestion
        Assert.Empty(first.Document.Tabs);
        Assert.False(File.Exists(env.Store.FilePath));

        await env.Manager.ApplySetupAsync(new LayoutDocument { Tabs = [new LayoutTab { Id = "vorschlag", Name = "Vorschlag" }] });
        Assert.False(env.Manager.NeedsSetup);
        Assert.True(File.Exists(env.Store.FilePath));

        var editor = new LayoutEditor(env.Manager.Base, env.Manager.Device);
        editor.RenameTab("vorschlag", "Mein Tab");
        var tab = editor.AddTab("Nur hier");
        editor.SetTabOrigin(tab, TabOrigin.Device);
        await env.Manager.SaveAsync(editor.Result.Base, editor.Result.Device);

        // restart
        var manager2 = new LayoutManager(env.Store, env.Device);
        var again = await manager2.LoadAsync();
        Assert.False(manager2.NeedsSetup);
        Assert.Equal(["Mein Tab", "Nur hier"], again.Document.Tabs.Select(t => t.Name));
        Assert.Equal("TESTPC", (await env.Device.LoadAsync(CancellationToken.None)).DeviceName);
    }

    [Fact]
    public async Task Saving_stamps_updated_at_and_device()
    {
        using var env = new Env();
        var before = DateTimeOffset.Now.AddSeconds(-1);

        await env.Manager.SaveAsync(new LayoutDocument { Tabs = [new LayoutTab { Id = "a", Name = "A" }] }, new DeviceLayout());
        var saved = UeBarJson.Deserialize<LayoutDocument>(await File.ReadAllTextAsync(env.Store.FilePath))!;

        Assert.True(saved.UpdatedAt > before);
        Assert.Equal("TESTPC", saved.UpdatedBy);
        Assert.Contains("\"updatedAt\"", await File.ReadAllTextAsync(env.Store.FilePath));
    }

    [Fact]
    public async Task Export_then_import_gives_the_same_layout()
    {
        using var env = new Env();
        var layout = UeBarJson.Deserialize<LayoutDocument>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-layout.json")))!;
        await env.Manager.ImportAsync(layout);
        var exportPath = Path.Combine(env.Dir.Path, "export.json");

        await env.Manager.ExportAsync(exportPath);
        var imported = LayoutManager.ParseImport(await File.ReadAllTextAsync(exportPath));

        Assert.Equal(UeBarJson.Serialize(layout), UeBarJson.Serialize(imported));
    }

    [Fact]
    public async Task Import_keeps_unknown_modules_and_fields_and_backs_up_first()
    {
        using var env = new Env();
        await env.Manager.SaveAsync(new LayoutDocument { Tabs = [new LayoutTab { Id = "alt", Name = "Alt" }] }, new DeviceLayout());
        var json = """
            {
              "version": 1,
              "tabs": [
                { "id": "a", "name": "A", "zukunft": 42, "tiles": [
                  { "id": "x1", "module": "spotify", "type": "spotify.player", "size": "2x2", "entities": { "main": "abc" }, "farbe": "#ff0000" },
                  { "id": "x1", "module": "homeassistant", "type": "ha.light", "entities": { "main": "light.desk" } }
                ] }
              ]
            }
            """;

        var layout = LayoutManager.ParseImport(json);
        await env.Manager.ImportAsync(layout);
        var saved = await File.ReadAllTextAsync(env.Store.FilePath);

        Assert.Contains("\"module\": \"spotify\"", saved); // unknown module stays
        Assert.Contains("\"farbe\": \"#ff0000\"", saved); // unknown fields stay
        Assert.Contains("\"zukunft\": 42", saved);
        Assert.NotEqual(layout.Tabs[0].Tiles[0].Id, layout.Tabs[0].Tiles[1].Id); // duplicate ID repaired
        Assert.Single(Directory.GetFiles(Path.Combine(env.Dir.Path, "backups"))); // backed up first
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("""{ "version": 1, "tabs": [] }""")]
    [InlineData("""{ "version": 99, "tabs": [ { "id": "a" } ] }""")]
    public void Import_rejects_nonsense(string json) =>
        Assert.Throws<InvalidDataException>(() => LayoutManager.ParseImport(json));

    [Fact]
    public async Task Broken_device_json_is_reported_not_overwritten()
    {
        using var env = new Env();
        await env.Manager.ApplySetupAsync(new LayoutDocument { Tabs = [new LayoutTab { Id = "a", Name = "A" }] });
        await File.WriteAllTextAsync(env.Device.FilePath, "{ kaputt");

        var composed = await env.Manager.LoadAsync();

        Assert.Single(composed.Document.Tabs);
        Assert.Equal("{ kaputt", await File.ReadAllTextAsync(env.Device.FilePath));
    }

    [Fact]
    public void Sample_layout_file_is_importable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-layout.json");
        var layout = LayoutManager.ParseImport(File.ReadAllText(path));

        Assert.Equal(["zimmer", "routinen", "medien"], layout.Tabs.Select(t => t.Id));
        Assert.Equal(TileSize.Wide, layout.AllTiles.Single(t => t.Id == "t1").Size); // desk 2x1
        Assert.Equal(TileSize.Large, layout.AllTiles.Single(t => t.Id == "t3").Size); // climate 2x2
        Assert.Equal("light.office_ceiling", layout.AllTiles.Single(t => t.Id == "t2").GetEntity("main"));
        Assert.Equal("vacuum.robot", layout.AllTiles.Single(t => t.Id == "r6").GetEntity("main"));
        Assert.Single(layout.Status);
    }
}

public class LocationServiceTests
{
    private sealed class LocationModule(string id) : IModule, ILocationProvider
    {
        public string Id { get; } = id;

        public string DisplayName => Id;

        public string Icon => "Home";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public DeviceLocation CurrentLocation { get; private set; } = DeviceLocation.Unknown;

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<DeviceLocation>? LocationChanged;

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Set(DeviceLocation location)
        {
            CurrentLocation = location;
            LocationChanged?.Invoke(this, location);
        }
    }

    [Fact]
    public async Task First_running_provider_decides_and_changes_are_reported()
    {
        await using var env = new TestEnv();
        var first = new LocationModule("a");
        var second = new LocationModule("b");
        env.Registry.Register(new TestModule("ohne"));
        env.Registry.Register(first);
        env.Registry.Register(second);
        var service = new LocationService(env.Registry, env.Log);
        var seen = new List<DeviceLocation>();
        service.Changed += (_, l) => seen.Add(l);
        Assert.Equal(DeviceLocation.Unknown, service.Current); // nothing started yet

        await env.Registry.StartAllAsync();
        second.Set(DeviceLocation.Away); // the second provider does not count
        first.Set(DeviceLocation.Home);
        first.Set(DeviceLocation.Away);

        Assert.Equal(DeviceLocation.Away, service.Current);
        Assert.Equal([DeviceLocation.Home, DeviceLocation.Away], seen);

        await env.Registry.StopModuleAsync("a"); // provider gone → next one
        Assert.True(await Wait.UntilAsync(() => service.Current == DeviceLocation.Away && seen.Count == 2));
        await env.Registry.StopModuleAsync("b");
        Assert.True(await Wait.UntilAsync(() => service.Current == DeviceLocation.Unknown));
    }
}
