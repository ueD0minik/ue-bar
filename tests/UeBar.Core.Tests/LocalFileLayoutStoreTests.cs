// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Layout;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

public class LocalFileLayoutStoreTests
{
    private static string SpecJson => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-layout.json"));

    [Fact]
    public async Task Spec_example_round_trips_unchanged()
    {
        using var dir = new TempDirectory();
        var source = Path.Combine(dir.Path, "in.json");
        var target = Path.Combine(dir.Path, "out.json");
        File.WriteAllText(source, SpecJson);

        var layout = await new LocalFileLayoutStore(source).LoadAsync(CancellationToken.None);
        await new LocalFileLayoutStore(target).SaveAsync(layout!, CancellationToken.None);

        var original = JsonNode.Parse(SpecJson);
        var saved = JsonNode.Parse(File.ReadAllText(target));
        Assert.True(JsonNode.DeepEquals(original, saved), $"Unterschied:\n{saved}");
    }

    [Fact]
    public async Task Spec_example_is_read_correctly()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "layout.json");
        File.WriteAllText(path, SpecJson);

        var layout = (await new LocalFileLayoutStore(path).LoadAsync(CancellationToken.None))!;

        Assert.Equal(1, layout.Version);
        Assert.Equal(["zimmer", "routinen", "medien"], layout.Tabs.Select(t => t.Id));
        var klima = layout.AllTiles.Single(t => t.Id == "t3");
        Assert.Equal("ha.climate_ir", klima.Type);
        Assert.Equal(TileSize.Large, klima.Size);
        Assert.Equal(3, klima.Entities.Count);
        Assert.Equal("select.office_ac_fan", klima.GetEntity("fan"));
        Assert.Equal("light.office_ceiling", layout.AllTiles.Single(t => t.Id == "t2").GetEntity("main"));
        Assert.Null(layout.AllTiles.Single(t => t.Id == "r1").Label);
        Assert.Equal("weather.forecast_home", Assert.Single(layout.Status).Entity);
    }

    [Fact]
    public async Task Missing_file_returns_null()
    {
        using var dir = new TempDirectory();

        var layout = await new LocalFileLayoutStore(Path.Combine(dir.Path, "nix.json")).LoadAsync(CancellationToken.None);

        Assert.Null(layout);
    }

    [Fact]
    public async Task Invalid_json_throws_instead_of_returning_empty_layout()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "layout.json");
        File.WriteAllText(path, "{ kaputt");

        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalFileLayoutStore(path).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_fields_survive_a_round_trip()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "layout.json");
        File.WriteAllText(path, """
            { "version": 1, "zukunft": 42,
              "tabs": [ { "id": "a", "name": "A", "farbe": "#112233",
                "tiles": [ { "id": "t", "module": "m", "type": "m.x", "size": "1x1", "entities": {}, "optionen": { "x": true } } ] } ] }
            """);
        var store = new LocalFileLayoutStore(path);

        var layout = (await store.LoadAsync(CancellationToken.None))!;
        await store.SaveAsync(layout, CancellationToken.None);
        var saved = JsonNode.Parse(File.ReadAllText(path))!;

        Assert.Equal(42, saved["zukunft"]!.GetValue<int>());
        Assert.Equal("#112233", saved["tabs"]![0]!["farbe"]!.GetValue<string>());
        Assert.True(saved["tabs"]![0]!["tiles"]![0]!["optionen"]!["x"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Save_creates_backups_and_keeps_only_ten()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "layout.json");
        var backups = Path.Combine(dir.Path, "backups");
        var store = new LocalFileLayoutStore(path, backups);

        for (var i = 0; i < 14; i++)
        {
            await store.SaveAsync(new LayoutDocument { Tabs = [new LayoutTab { Id = $"tab{i}", Name = $"Tab {i}" }] }, CancellationToken.None);
            await Task.Delay(2); // distinguish the timestamps in the file name
        }

        var files = Directory.GetFiles(backups, "layout.backup-*.json");
        Assert.Equal(LocalFileLayoutStore.MaxBackups, files.Length);
        var current = (await store.LoadAsync(CancellationToken.None))!;
        Assert.Equal("tab13", current.Tabs[0].Id);
    }

    [Fact]
    public async Task External_change_raises_changed_but_own_save_does_not()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "layout.json");
        using var store = new LocalFileLayoutStore(path);
        await store.SaveAsync(new LayoutDocument(), CancellationToken.None);
        var changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);

        await store.SaveAsync(new LayoutDocument { Tabs = [new LayoutTab { Id = "eigen" }] }, CancellationToken.None);
        await Task.Delay(800);
        Assert.Equal(0, Volatile.Read(ref changes));

        File.WriteAllText(path, """{ "version": 1, "tabs": [ { "id": "vonhand", "name": "Von Hand", "tiles": [] } ] }""");

        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref changes) > 0));
    }
}
