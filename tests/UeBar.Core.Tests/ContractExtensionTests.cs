// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>Contract 1.1 (P2-2): visibility, device batteries, adding a plugin's suggestion.</summary>
public sealed class ContractExtensionTests
{
    private static TileConfig Tile(string module, string id, string entity) => new()
    {
        Id = id,
        Module = module,
        Type = module + ".x",
        Entities = new Dictionary<string, string> { ["main"] = entity },
    };

    [Fact]
    public async Task Visibility_reaches_each_module_with_its_own_part_and_only_when_it_changed()
    {
        await using var env = new TestEnv();
        var a = new VisibleModule("a");
        var b = new VisibleModule("b");
        env.Registry.Register(a);
        env.Registry.Register(b);
        await env.Registry.StartAllAsync();
        Assert.All([a, b], m => Assert.False(Assert.Single(m.Seen).IsPanelOpen)); // passed before the start

        var tiles = new[] { Tile("a", "t1", "a.cpu"), Tile("b", "t2", "b.light") };
        var status = new[] { new StatusItemConfig { Module = "a", Entity = "a.ram" } };
        await env.Registry.NotifyVisibilityAsync(true, tiles, status);

        var seenA = a.Seen[^1];
        Assert.True(seenA.IsPanelOpen);
        Assert.Equal(["t1"], seenA.VisibleTiles.Select(t => t.Id));
        Assert.True(seenA.IsVisible("a.cpu") && seenA.IsVisible("a.ram"));
        Assert.False(seenA.IsVisible("b.light"));
        Assert.Equal(["t2"], b.Seen[^1].VisibleTiles.Select(t => t.Id));

        // Same again: nobody is called.
        await env.Registry.NotifyVisibilityAsync(true, tiles, status);
        Assert.Equal(2, a.Seen.Count);

        // Another tab with only b's tiles: a learns it has nothing visible any more, b does not change.
        await env.Registry.NotifyVisibilityAsync(true, [tiles[1]], []);
        Assert.Equal(3, a.Seen.Count);
        Assert.False(a.Seen[^1].IsAnythingVisible);
        Assert.Equal(2, b.Seen.Count);

        await env.Registry.NotifyVisibilityAsync(false, [], []);
        Assert.Same(ModuleVisibility.Hidden, a.Seen[^1]);
    }

    [Fact]
    public async Task Visibility_is_passed_again_after_a_restart()
    {
        await using var env = new TestEnv();
        var a = new VisibleModule("a");
        env.Registry.Register(a);
        await env.Registry.StartAllAsync();
        await env.Registry.NotifyVisibilityAsync(true, [Tile("a", "t1", "a.cpu")], []);

        await env.Registry.RestartModuleAsync("a");

        Assert.True(a.Seen[^1].IsVisible("a.cpu"));
    }

    [Fact]
    public async Task Modules_see_the_device_batteries_of_the_others_but_not_their_own()
    {
        await using var env = new TestEnv();
        var vendor = new BatteryModule("vendor", new DeviceBatteryInfo("h1", "Headset", 40));
        var phone = new BatteryModule("phone", new DeviceBatteryInfo("p1", "Phone", 90));
        env.Registry.Register(vendor);
        env.Registry.Register(phone);
        await env.Registry.StartAllAsync();

        var catalog = Assert.IsAssignableFrom<IDeviceBatteryCatalog>(phone.Context);
        var seen = Assert.Single(catalog.GetDeviceBatteries());
        Assert.Equal(("Headset", 40, "vendor"), (seen.Name, seen.Percent, seen.Source));

        var changes = 0;
        catalog.DeviceBatteriesChanged += (_, _) => Interlocked.Increment(ref changes);
        vendor.Report(new DeviceBatteryInfo("h1", "Headset", 35));
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref changes) == 1));
        Assert.Equal(35, catalog.GetDeviceBatteries()[0].Percent);

        // Its own report does not come back to it.
        phone.Report(new DeviceBatteryInfo("p1", "Phone", 80));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref changes));

        // A stopped module's batteries disappear.
        await env.Registry.StopModuleAsync("vendor");
        Assert.Empty(catalog.GetDeviceBatteries());
    }

    [Fact]
    public void Adding_a_suggestion_keeps_the_layout_and_makes_ids_unique()
    {
        var current = new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "home", Name = "Home", Tiles = [Tile("ha", "w1", "light.x")] }],
            Status = [new StatusItemConfig { Module = "ha", Entity = "sensor.t" }],
        };
        var suggestion = new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "home", Name = "PC", Tiles = [Tile("win", "w1", "win.volume"), Tile("win", "w2", "win.media")] }],
            Status = [new StatusItemConfig { Module = "win", Entity = "win.cpu" }],
        };

        var merged = LayoutSetup.Append(current, suggestion);

        Assert.Equal(["home", "home-2"], merged.Tabs.Select(t => t.Id));
        Assert.Equal(["w1", "w1-2", "w2"], merged.AllTiles.Select(t => t.Id));
        Assert.Equal(["sensor.t"], merged.Status.Select(s => s.Entity)); // the status line stays
        Assert.Single(current.Tabs);
    }

    [Fact]
    public async Task Permissions_granted_revoked_and_requested_through_the_settings()
    {
        using var dir = new TempDirectory();
        var settings = new SettingsService(Path.Combine(dir.Path, "settings.json"), null, new ListLogger());
        var guard = new ModuleGuard(new ListLogger());
        var bus = new UeBar.Core.Bus.ModuleBus(guard, settings.BusPolicy, new ListLogger());
        var provider = bus.For("provider");
        var consumer = bus.For("consumer");
        var calls = 0;
        provider.PublishAction("provider.do", (_, _) =>
        {
            calls++;
            return Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null);
        });
        provider.PublishValue("provider.level", 3);

        // Default: denied – the plugin asks, the settings show the request.
        Assert.Equal(BusErrorKind.Denied, (await consumer.InvokeAsync("provider.do")).Error);
        var changes = 0;
        settings.BusPolicy.RequestsChanged += (_, _) => changes++;
        consumer.RequestPermission("provider", BusAccess.Actions, "for testing");
        consumer.RequestPermission("provider", BusAccess.Actions, "again"); // not twice
        Assert.Single(settings.BusPolicy.PendingRequests);
        Assert.Equal(1, changes);

        // "Allow": saved, request settled, works.
        settings.SetBusGrant("consumer", "provider", BusAccess.Actions);
        Assert.Empty(settings.BusPolicy.PendingRequests);
        Assert.True((await consumer.InvokeAsync("provider.do")).Success);
        Assert.Equal(1, calls);
        Assert.False(consumer.GetValue("provider.level").Success); // only what was allowed

        // Saved in settings.json – survives a restart.
        var reloaded = new SettingsService(Path.Combine(dir.Path, "settings.json"), null, new ListLogger());
        reloaded.Load();
        Assert.Equal(BusAccess.Actions, reloaded.GetBusGrant("consumer", "provider"));

        // Values added, then everything revoked.
        settings.SetBusGrant("consumer", "provider", BusAccess.Actions | BusAccess.Values);
        Assert.True(consumer.GetValue("provider.level").Success);
        settings.SetBusGrant("consumer", "provider", BusAccess.None);
        Assert.Equal(BusErrorKind.Denied, (await consumer.InvokeAsync("provider.do")).Error);
        Assert.Empty(settings.Current.BusGrants);

        // All publications for the settings page, whatever is permitted.
        Assert.Equal(["provider.do", "provider.level"], bus.GetAllPublications().Select(p => p.Id).Order());
    }

    private sealed class VisibleModule(string id) : IModule, IVisibilityAware
    {
        private readonly List<ModuleVisibility> _seen = [];

        public List<ModuleVisibility> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public string Id => id;

        public string DisplayName => id;

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public void OnVisibilityChanged(ModuleVisibility visibility)
        {
            lock (_seen)
            {
                _seen.Add(visibility);
            }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BatteryModule(string id, DeviceBatteryInfo battery) : IModule, IDeviceBatterySource
    {
        private DeviceBatteryInfo _battery = battery;

        public IModuleContext? Context { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public string Icon => "Battery";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public IReadOnlyList<DeviceBatteryInfo> DeviceBatteries => [Volatile.Read(ref _battery)];

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public event EventHandler? DeviceBatteriesChanged;

        public void Report(DeviceBatteryInfo battery)
        {
            Volatile.Write(ref _battery, battery);
            DeviceBatteriesChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
