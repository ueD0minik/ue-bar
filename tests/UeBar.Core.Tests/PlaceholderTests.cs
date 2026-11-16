// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;
using UeBar.Core.Tiles;
using UeBar.Modules.Dummy;

namespace UeBar.Core.Tests;

/// <summary>SPEC 4.4 / 4.5: tiles of missing/disabled modules are kept and show a placeholder.</summary>
public class PlaceholderTests
{
    [Fact]
    public async Task Tile_of_missing_module_shows_placeholder()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();
        var config = new TileConfig
        {
            Id = "t1",
            Module = "homeassistant",
            Type = "ha.light",
            Size = TileSize.Wide,
            Entities = new Dictionary<string, string> { ["main"] = "light.office_desk" },
            Label = "Desk",
        };

        var tile = await env.Registry.CreateTileAsync(config);

        Assert.Equal(TilePlaceholder.ModuleMissing, tile.Placeholder);
        Assert.True(tile.IsPlaceholder);
        Assert.Equal("Desk", tile.Title);
        Assert.Contains("homeassistant", tile.StateText);
        Assert.Equal("t1", tile.TileId);
        Assert.Empty(tile.Elements);
    }

    [Fact]
    public async Task Missing_module_tile_is_kept_when_layout_is_saved_again()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "layout.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "tabs": [
                { "id": "x", "name": "X", "tiles": [
                  { "id": "t1", "module": "gibtsnicht", "type": "foo.bar", "size": "2x2", "entities": { "main": "a.b" }, "label": "Fremd" }
                ] }
              ]
            }
            """);
        var store = new LocalFileLayoutStore(path);
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();

        var layout = (await store.LoadAsync(CancellationToken.None))!;
        var tile = await env.Registry.CreateTileAsync(layout.AllTiles.Single());
        await store.SaveAsync(layout, CancellationToken.None);
        var reloaded = (await store.LoadAsync(CancellationToken.None))!;

        Assert.Equal(TilePlaceholder.ModuleMissing, tile.Placeholder);
        var kept = Assert.Single(reloaded.AllTiles);
        Assert.Equal("gibtsnicht", kept.Module);
        Assert.Equal("foo.bar", kept.Type);
        Assert.Equal(TileSize.Large, kept.Size);
        Assert.Equal("a.b", kept.Entities["main"]);
    }

    [Fact]
    public async Task Tile_of_disabled_module_shows_disabled()
    {
        await using var env = new TestEnv();
        env.State.SetModuleEnabled("dummy", false);
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();

        var tile = await env.Registry.CreateTileAsync(new TileConfig
        {
            Id = "t", Module = "dummy", Type = "dummy.switch", Entities = new() { ["main"] = "dummy.fan" },
        });

        Assert.Equal(TilePlaceholder.ModuleDisabled, tile.Placeholder);
        Assert.Equal("Modul deaktiviert", tile.StateText);
    }

    [Fact]
    public async Task Unknown_tile_type_shows_placeholder()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();

        var tile = await env.Registry.CreateTileAsync(new TileConfig { Id = "t", Module = "dummy", Type = "dummy.gibtsnicht" });

        Assert.Equal(TilePlaceholder.UnknownType, tile.Placeholder);
    }

    [Fact]
    public async Task Tile_type_of_other_module_is_not_used()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        env.Registry.Register(new TestModule("other"));
        await env.Registry.StartAllAsync();

        // The tile claims to belong to "other", but names a dummy type
        var tile = await env.Registry.CreateTileAsync(new TileConfig { Id = "t", Module = "other", Type = "dummy.switch" });

        Assert.Equal(TilePlaceholder.UnknownType, tile.Placeholder);
    }

    [Fact]
    public async Task Interaction_on_placeholder_is_rejected_without_calling_anything()
    {
        await using var env = new TestEnv();
        var tile = TileModel.CreatePlaceholder(new TileConfig { Id = "t", Module = "x", Type = "x.y" }, TilePlaceholder.ModuleMissing);

        var result = await env.Registry.InteractAsync(tile, new TileInteraction("tap", InteractionKind.Tap));

        Assert.Equal(Modules.GuardOutcome.Rejected, result.Outcome);
    }
}
