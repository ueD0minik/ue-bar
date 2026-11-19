// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>Contract 1.3 (P2-4): live settings, dropping on the panel, list fields, icon grid.</summary>
public sealed class Contract13Tests
{
    [Fact]
    public async Task Live_settings_reach_a_running_module_without_a_restart()
    {
        await using var env = new TestEnv();
        var live = new LiveModule("live");
        var classic = new SettingsModule("classic");
        env.Registry.Register(live);
        env.Registry.Register(classic);
        await env.Registry.StartAllAsync();

        env.State.SetValue("live", "name", "neu");
        env.State.SetValue("classic", "name", "neu");
        Assert.True(await env.Registry.ApplyChangedSettingsAsync("live"));
        Assert.False(await env.Registry.ApplyChangedSettingsAsync("classic"));

        Assert.Equal((1, 0, 2), (live.Starts, live.Stops, live.Applied.Count)); // start + live
        Assert.Equal("neu", live.Applied[^1]);
        Assert.Equal((2, 1), (classic.Starts, classic.Stops)); // restarted as before
        Assert.Equal("neu", classic.Applied[^1]);
    }

    [Fact]
    public async Task A_module_whose_live_settings_fail_is_restarted()
    {
        await using var env = new TestEnv();
        var live = new LiveModule("live");
        env.Registry.Register(live);
        await env.Registry.StartAllAsync();
        live.FailLive = true;

        Assert.False(await env.Registry.ApplyChangedSettingsAsync("live"));

        Assert.Equal(2, live.Starts);
        Assert.Contains(env.Log.Entries, e => e.Message.Contains("Applying the settings live failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dropped_things_go_to_the_first_module_that_takes_them()
    {
        await using var env = new TestEnv();
        var picky = new DropModule("picky", _ => null);
        var broken = new DropModule("broken", _ => throw new InvalidOperationException("boom"));
        var files = new DropModule("files", item => item.Kind == DropKind.File ? new TileConfig { Type = "files.item", Module = "wrong" } : null);
        env.Registry.Register(picky);
        env.Registry.Register(broken);
        env.Registry.Register(files);
        await env.Registry.StartAllAsync();

        var tile = await env.Registry.CreateTileForDropAsync(new DroppedItem(DropKind.File, @"C:\x.txt"));

        Assert.Equal(("files", "files.item"), (tile!.Module, tile.Type)); // the core sets the module
        Assert.Equal(1, picky.Asked);
        Assert.Null(await env.Registry.CreateTileForDropAsync(new DroppedItem(DropKind.Text, "hello")));
        Assert.Equal(2, files.Asked);
    }

    [Fact]
    public void List_values_round_trip_and_broken_ones_give_an_empty_list()
    {
        var value = ModuleSettingValues.FormatList(
        [
            new Dictionary<string, string> { ["id"] = "a1", ["name"] = "Mathe", ["folder"] = @"C:\Schule\Mathe" },
            new Dictionary<string, string> { ["id"] = "b2", ["name"] = "Wiki \"neu\"" },
        ]);
        var values = new ModuleSettingValues(new Dictionary<string, string> { ["entries"] = value, ["broken"] = "[{", ["object"] = "{\"a\":1}" });

        var list = values.GetList("entries");
        Assert.Equal(["a1", "b2"], list.Select(i => i["id"]));
        Assert.Equal(@"C:\Schule\Mathe", list[0]["folder"]);
        Assert.Equal("Wiki \"neu\"", list[1]["name"]);
        Assert.Empty(values.GetList("broken"));
        Assert.Empty(values.GetList("object"));
        Assert.Empty(values.GetList("missing"));
        Assert.Equal("1", ModuleSettingValues.ParseList("[{\"n\":1,\"t\":true}]")[0]["n"]); // numbers as text
    }

    [Fact]
    public void Fields_show_only_while_the_field_they_depend_on_fits()
    {
        var file = new SettingField("file", "File", SettingFieldKind.File) { ShowWhen = "kind=file" };
        var appOrCommand = new SettingField("admin", "Admin", SettingFieldKind.Toggle) { ShowWhen = "kind = app | command" };
        var always = new SettingField("name", "Name", SettingFieldKind.Text);

        Assert.True(file.IsShown(_ => "File"));
        Assert.False(file.IsShown(_ => "folder"));
        Assert.False(file.IsShown(_ => null));
        Assert.True(appOrCommand.IsShown(k => k == "kind" ? "command" : null));
        Assert.True(always.IsShown(_ => null));
        Assert.True(new SettingField("x", "X", SettingFieldKind.Text) { ShowWhen = "broken" }.IsShown(_ => null)); // no "=": always
    }

    [Fact]
    public void Icon_grid_and_the_priority_are_part_of_the_contract()
    {
        var tile = new TileModel { Title = "Schule", ImageUrl = "icon:folder:x" };
        tile.Elements.Add(new IconGridElement
        {
            Id = "items",
            Columns = 3,
            Items = [new IconGridItem { Id = "item1", Label = "Mathe", ImageUrl = "icon:a", StateText = "ok" }, new IconGridItem { Id = "item2", Label = "Weg", IsEnabled = false }],
        });

        var json = UeBarJson.Serialize(tile);
        var back = UeBarJson.Deserialize<TileModel>(json)!;

        Assert.Equal("iconGrid", JsonNode.Parse(json)!["elements"]![0]!["kind"]!.GetValue<string>());
        var grid = Assert.IsType<IconGridElement>(Assert.Single(back.Elements));
        Assert.Equal(["Mathe", "Weg"], grid.Items.Select(i => i.Label));
        Assert.False(grid.Items[1].IsEnabled);
        Assert.Equal(json, UeBarJson.Serialize(back));
        Assert.Equal(NotificationPriority.Normal, new UeNotification("a", "b").Priority);
        Assert.Equal(new Version(1, 3), CoreApiVersion.Version);
    }

    private class SettingsModule(string id) : IModule, IModuleSettings
    {
        private int _starts;
        private int _stops;

        public string Id => id;

        public string DisplayName => id;

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public IReadOnlyList<SettingField> SettingFields => [new("name", "Name", SettingFieldKind.Text)];

        public List<string?> Applied { get; } = [];

        public bool FailLive { get; set; }

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        private bool _running;

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
        {
            if (_running && FailLive)
            {
                throw new InvalidOperationException("cannot apply live");
            }

            lock (Applied)
            {
                Applied.Add(values.GetString("name"));
            }

            return Task.CompletedTask;
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            _running = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stops);
            _running = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>The same module, but able to apply settings while it runs.</summary>
    private sealed class LiveModule(string id) : SettingsModule(id), ILiveModuleSettings
    {
    }

    private sealed class DropModule(string id, Func<DroppedItem, TileConfig?> answer) : IModule, IDropHandler
    {
        private int _asked;

        public string Id => id;

        public string DisplayName => id;

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public int Asked => Volatile.Read(ref _asked);

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public Task<TileConfig?> CreateTileForDropAsync(DroppedItem item, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _asked);
            return Task.FromResult(answer(item));
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
