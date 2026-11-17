// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Modules;
using UeBar.Modules.Dummy;

namespace UeBar.Core.Tests;

public class ModuleRegistryTests
{
    private static TileConfig DummyTile(string id, string type, string entity) => new()
    {
        Id = id,
        Module = DummyModule.ModuleId,
        Type = type,
        Entities = new Dictionary<string, string> { ["main"] = entity },
    };

    [Fact]
    public async Task Core_runs_with_dummy_module_only()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());

        await env.Registry.StartAllAsync();

        var entry = Assert.Single(env.Registry.Modules);
        Assert.Equal("dummy", entry.Id);
        Assert.Equal(ModuleState.Connected, entry.Status.State);
        Assert.True(entry.IsAvailable);
    }

    [Fact]
    public void Duplicate_module_id_is_rejected()
    {
        var env = new TestEnv();
        env.Registry.Register(new TestModule("x"));

        Assert.Throws<InvalidOperationException>(() => env.Registry.Register(new TestModule("x")));
    }

    [Fact]
    public void Module_throwing_on_id_is_skipped()
    {
        var env = new TestEnv();

        var result = env.Registry.Register(new BrokenIdModule());

        Assert.Null(result);
        Assert.Empty(env.Registry.Modules);
    }

    [Fact]
    public void Finds_tile_type_by_id()
    {
        var env = new TestEnv();
        env.Registry.Register(new DummyModule());

        var reg = env.Registry.FindTileType("dummy.dimmer");

        Assert.NotNull(reg);
        Assert.Equal("dummy", reg!.Module.Id);
        Assert.Null(env.Registry.FindTileType("ha.light"));
    }

    [Fact]
    public void Duplicate_tile_type_in_second_module_is_ignored()
    {
        var env = new TestEnv();
        var a = new TestModule("a");
        a.Types.Add(new TestTileType("same.type"));
        var b = new TestModule("b");
        b.Types.Add(new TestTileType("same.type"));

        env.Registry.Register(a);
        env.Registry.Register(b);

        Assert.Equal("a", env.Registry.FindTileType("same.type")!.Module.Id);
        Assert.Empty(env.Registry.Find("b")!.TileTypes);
    }

    [Fact]
    public async Task Stop_and_start_module()
    {
        await using var env = new TestEnv();
        var module = new TestModule("m");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        await env.Registry.StopModuleAsync("m");
        Assert.Equal(ModuleState.Stopped, env.Registry.Find("m")!.Status.State);
        Assert.Equal(1, module.StopCount);

        await env.Registry.StartModuleAsync("m");
        Assert.Equal(ModuleState.Connected, env.Registry.Find("m")!.Status.State);
        Assert.Equal(2, module.StartCount);
    }

    [Fact]
    public async Task Disabled_module_is_not_started_and_can_be_enabled()
    {
        await using var env = new TestEnv();
        env.State.SetModuleEnabled("m", false);
        var module = new TestModule("m");
        env.Registry.Register(module);

        await env.Registry.StartAllAsync();

        Assert.Equal(0, module.StartCount);
        Assert.Equal(ModuleState.Disabled, env.Registry.Find("m")!.Status.State);

        await env.Registry.SetEnabledAsync("m", true);

        Assert.Equal(1, module.StartCount);
        Assert.True(env.State.IsModuleEnabled("m"));
        Assert.Equal(ModuleState.Connected, env.Registry.Find("m")!.Status.State);
    }

    [Fact]
    public async Task Disabling_a_running_module_stops_it()
    {
        await using var env = new TestEnv();
        var module = new TestModule("m");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        await env.Registry.SetEnabledAsync("m", false);

        Assert.Equal(1, module.StopCount);
        Assert.False(env.State.IsModuleEnabled("m"));
        Assert.Equal(ModuleState.Disabled, env.Registry.Find("m")!.Status.State);
    }

    [Fact]
    public async Task Module_status_changes_are_forwarded()
    {
        await using var env = new TestEnv();
        var module = new TestModule("m");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();
        var seen = new List<ModuleState>();
        env.Registry.ModuleStatusChanged += (_, e) => seen.Add(e.Status.State);

        module.SetStatus(ModuleStatus.Disconnected);

        Assert.Equal(ModuleState.Disconnected, env.Registry.Find("m")!.Status.State);
        Assert.Contains(ModuleState.Disconnected, seen);
    }

    [Fact]
    public async Task Failing_start_only_affects_that_module()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new TestModule("bad") { OnStart = (_, _) => throw new InvalidOperationException("Start kaputt") });
        env.Registry.Register(new DummyModule());

        await env.Registry.StartAllAsync();

        Assert.Equal(ModuleState.Error, env.Registry.Find("bad")!.Status.State);
        Assert.Contains("Start kaputt", env.Registry.Find("bad")!.Status.Text);
        Assert.Equal(ModuleState.Connected, env.Registry.Find("dummy")!.Status.State);
    }

    [Fact]
    public async Task Background_task_exception_is_assigned_to_module()
    {
        await using var env = new TestEnv();
        var module = new TestModule("m")
        {
            OnStart = (ctx, _) =>
            {
                ctx.RunBackground("Worker", async ct =>
                {
                    await Task.Delay(20, ct);
                    throw new InvalidOperationException("Hintergrund kaputt");
                });
                return Task.CompletedTask;
            },
        };
        env.Registry.Register(module);
        env.Registry.Register(new DummyModule());

        await env.Registry.StartAllAsync();

        Assert.True(await Wait.UntilAsync(() => env.Registry.Find("m")!.Status.State == ModuleState.Error));
        Assert.Equal(ModuleState.Connected, env.Registry.Find("dummy")!.Status.State);
    }

    [Fact]
    public async Task Stopping_cancels_background_tasks()
    {
        await using var env = new TestEnv();
        var canceled = new TaskCompletionSource();
        var module = new TestModule("m")
        {
            OnStart = (ctx, _) =>
            {
                ctx.RunBackground("Worker", async ct =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    finally
                    {
                        canceled.TrySetResult();
                    }
                });
                return Task.CompletedTask;
            },
        };
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        await env.Registry.StopModuleAsync("m");

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ModuleState.Stopped, env.Registry.Find("m")!.Status.State);
    }

    [Fact]
    public async Task Hanging_start_times_out_and_sets_error()
    {
        var options = new GuardOptions { LifecycleTimeout = TimeSpan.FromMilliseconds(100) };
        await using var env = new TestEnv(options);
        env.Registry.Register(new TestModule("slow") { OnStart = (_, ct) => Task.Delay(Timeout.Infinite, ct) });

        await env.Registry.StartAllAsync();

        Assert.Equal(ModuleState.Error, env.Registry.Find("slow")!.Status.State);
    }

    [Fact]
    public async Task Dummy_tiles_react_to_interaction()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();

        var tile = await env.Registry.CreateTileAsync(DummyTile("t1", "dummy.dimmer", "dummy.desk"));
        var result = await env.Registry.InteractAsync(tile, new TileInteraction("brightness", InteractionKind.SetValue, 25));

        Assert.True(result.Succeeded);
        Assert.Equal(25, tile.FindElement<SliderElement>("brightness")!.Value);
        Assert.True(tile.IsActive);
        Assert.Contains("25", tile.StateText);
        Assert.Equal("t1", tile.TileId);
        Assert.Equal("dummy", tile.ModuleId);
    }

    [Fact]
    public async Task Two_tiles_of_same_dummy_device_update_live()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();

        var a = await env.Registry.CreateTileAsync(DummyTile("a", "dummy.switch", "dummy.fan"));
        var b = await env.Registry.CreateTileAsync(DummyTile("b", "dummy.switch", "dummy.fan"));
        var changed = false;
        b.PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(TileModel.IsActive);

        await env.Registry.InteractAsync(a, new TileInteraction("toggle", InteractionKind.Tap));

        Assert.True(a.IsActive);
        Assert.True(b.IsActive);
        Assert.True(changed);
    }

    [Fact]
    public async Task Crash_tile_puts_only_dummy_into_error_and_auto_restart_recovers()
    {
        var options = new GuardOptions { RestartDelays = [TimeSpan.FromMilliseconds(50)] };
        await using var env = new TestEnv(options);
        var other = new TestModule("other");
        env.Registry.Register(new DummyModule());
        env.Registry.Register(other);
        await env.Registry.StartAllAsync();
        var crash = await env.Registry.CreateTileAsync(DummyTile("c", "dummy.crash", "dummy.crash"));

        var result = await env.Registry.InteractAsync(crash, new TileInteraction("crash", InteractionKind.Tap));

        Assert.Equal(GuardOutcome.Faulted, result.Outcome);
        var dummy = env.Registry.Find("dummy")!;
        Assert.Equal(ModuleState.Error, dummy.Status.State);
        Assert.Equal(ModuleState.Connected, env.Registry.Find("other")!.Status.State);

        // Tiles of a faulted module show "Module error"
        var duringError = await env.Registry.CreateTileAsync(DummyTile("s", "dummy.switch", "dummy.fan"));
        Assert.Equal(TilePlaceholder.ModuleError, duringError.Placeholder);

        // automatic restart
        Assert.True(await Wait.UntilAsync(() => dummy.Status.State == ModuleState.Connected));
        Assert.Null(dummy.LastError);
        var afterRestart = await env.Registry.CreateTileAsync(DummyTile("s", "dummy.switch", "dummy.fan"));
        Assert.Equal(TilePlaceholder.None, afterRestart.Placeholder);
    }

    [Fact]
    public async Task Label_and_icon_from_layout_override_module_values()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();
        var config = DummyTile("t", "dummy.switch", "dummy.fan");
        config.Label = "Mein Ventilator";
        config.Icon = "Star";

        var tile = await env.Registry.CreateTileAsync(config);

        Assert.Equal("Mein Ventilator", tile.Title);
        Assert.Equal("Star", tile.Icon);
    }

    [Fact]
    public async Task Throwing_tile_factory_gives_error_placeholder()
    {
        await using var env = new TestEnv();
        var module = new TestModule("m");
        module.Types.Add(new TestTileType("m.bad", _ => throw new InvalidOperationException()));
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        var tile = await env.Registry.CreateTileAsync(new TileConfig { Id = "x", Module = "m", Type = "m.bad" });

        Assert.Equal(TilePlaceholder.ModuleError, tile.Placeholder);
    }

    [Fact]
    public async Task Suggested_layout_comes_from_running_modules()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();

        var layouts = await env.Registry.GetSuggestedLayoutsAsync();
        var merged = LayoutComposer.Merge(layouts);

        Assert.Equal(2, merged.Tabs.Count);
        Assert.Equal(2, merged.Status.Count);
        Assert.Contains(merged.AllTiles, t => t.Type == "dummy.crash");
        foreach (var config in merged.AllTiles)
        {
            var tile = await env.Registry.CreateTileAsync(config);
            Assert.Equal(TilePlaceholder.None, tile.Placeholder);
        }
    }

    [Fact]
    public async Task Status_values_are_live()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new DummyModule());
        await env.Registry.StartAllAsync();
        var value = await env.Registry.GetStatusValueAsync(new StatusItemConfig { Module = "dummy", Entity = "dummy.thermostat" });
        var tile = await env.Registry.CreateTileAsync(DummyTile("t", "dummy.thermostat", "dummy.thermostat"));

        Assert.Equal("Klima aus", value!.Text);
        await env.Registry.InteractAsync(tile, new TileInteraction("power", InteractionKind.Tap));

        Assert.EndsWith("°C", value.Text);
        Assert.Null(await env.Registry.GetStatusValueAsync(new StatusItemConfig { Module = "fehlt", Entity = "x" }));
    }

    [Fact]
    public void Settings_sections_round_trip()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "settings.json");
        var settings = new Settings.SettingsService(path, null, new ListLogger());
        settings.Load();

        settings.SetSection("bar", new SectionSample { Delay = 500, Name = "Ö" });
        var reloaded = new Settings.SettingsService(path, null, new ListLogger());
        reloaded.Load();

        Assert.Equal(500, reloaded.GetSection<SectionSample>("bar").Delay);
        Assert.Equal("Ö", reloaded.GetSection<SectionSample>("bar").Name);
        Assert.Equal(0, reloaded.GetSection<SectionSample>("fehlt").Delay);
        Assert.Contains("\"bar\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task Layout_aware_module_gets_only_its_entries_and_again_on_restart()
    {
        await using var env = new TestEnv();
        var module = new LayoutAwareModule("m");
        env.Registry.Register(module);
        env.Registry.Register(new DummyModule());
        var layout = new LayoutDocument
        {
            Tabs =
            [
                new LayoutTab
                {
                    Id = "x",
                    Tiles =
                    [
                        new TileConfig { Id = "a", Module = "m", Type = "m.t", Entities = new() { ["main"] = "e.one", ["extra"] = "e.two" } },
                        new TileConfig { Id = "b", Module = "dummy", Type = "dummy.switch", Entities = new() { ["main"] = "dummy.fan" } },
                    ],
                },
            ],
            Status = [new StatusItemConfig { Module = "m", Entity = "e.status" }, new StatusItemConfig { Module = "dummy", Entity = "dummy.player" }],
        };

        await env.Registry.NotifyLayoutAsync(layout);
        await env.Registry.StartAllAsync();
        await env.Registry.RestartModuleAsync("m");

        Assert.True(module.Calls >= 3); // Notify + start + restart
        Assert.Equal(["e.one", "e.status", "e.two"], module.Last!.ReferencedEntities.Order());
        Assert.Single(module.Last.Tiles);
    }

    [Fact]
    public async Task Guarded_invoke_catches_module_errors()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new TestModule("m"));

        var ok = env.Registry.Invoke("m", "Test", m => m.DisplayName);
        var bad = env.Registry.Invoke<int>("m", "Test", _ => throw new InvalidOperationException());
        var missing = await env.Registry.InvokeAsync("fehlt", "Test", (_, _) => Task.FromResult(1));

        Assert.Equal("Test m", ok.Value);
        Assert.Equal(GuardOutcome.Faulted, bad.Outcome);
        Assert.Equal(GuardOutcome.Rejected, missing.Outcome);
    }

    [Fact]
    public void Secret_left_empty_keeps_stored_value()
    {
        using var dir = new TempDirectory();
        var settings = new Settings.SettingsService(Path.Combine(dir.Path, "settings.json"), new ReverseProtector(), new ListLogger());
        settings.Load();

        settings.SetModuleValues("m", [("url", "http://a", false), ("token", "geheim", true)]);
        settings.SetModuleValues("m", [("url", "http://b", false), ("token", null, true)]);

        var values = settings.GetModuleSettings("m");
        Assert.Equal("http://b", values.GetString("url"));
        Assert.Equal("geheim", values.GetString("token"));
        Assert.True(settings.HasSecret("m", "token"));
        Assert.DoesNotContain("geheim", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
    }

    private sealed class ReverseProtector : Settings.ISecretProtector
    {
        public string Protect(string plainText) => new([.. plainText.Reverse()]);

        public string Unprotect(string protectedText) => new([.. protectedText.Reverse()]);
    }

    private sealed class LayoutAwareModule(string id) : IModule, ILayoutAware
    {
        public int Calls { get; private set; }

        public ModuleLayoutInfo? Last { get; private set; }

        public string Id { get; } = id;

        public string DisplayName => Id;

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void OnLayoutChanged(ModuleLayoutInfo layout)
        {
            Calls++;
            Last = layout;
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SectionSample
    {
        public int Delay { get; set; }

        public string? Name { get; set; }
    }

    private sealed class BrokenIdModule : IModule
    {
        public string Id => throw new InvalidOperationException();

        public string DisplayName => "";

        public string Icon => "";

        public ModuleStatus Status => ModuleStatus.Stopped;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
