// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;
using UeBar.Sdk;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Modules;
using UeBar.Core.Settings;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>
/// M5b: layout per HA user (frontend/user_data) with a local cache, suggestion from areas,
/// non-admin fallbacks – all against the fake HA.
/// </summary>
public class LayoutSyncAndAccessTests
{
    private sealed class Env : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "uebar-m5b-" + Guid.NewGuid().ToString("N")[..8]);

        public Env(FakeHaServer? server, string? localUrl = null, HaClientOptions? options = null)
        {
            Directory.CreateDirectory(_dir);
            Guard = new ModuleGuard(Log);
            var bus = new ModuleBus(Guard, new GrantListPermissionPolicy(), Log);
            State = new InMemoryModuleStateStore();
            if (server is not null || localUrl is not null)
            {
                State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyLocalUrl, localUrl ?? server!.BaseUrl.ToString());
                State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyExternalUrl, "");
                State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyToken, "good-token");
            }

            Registry = new ModuleRegistry(Guard, bus, Log, State, _dir);
            Module = new HomeAssistantModule(options ?? Fast.Options());
            Registry.Register(Module);
            Cache = new LocalFileLayoutStore(Path.Combine(_dir, "layout.json"), Backups, Log);
        }

        public CollectingLogger Log { get; } = new();

        public ModuleGuard Guard { get; }

        public InMemoryModuleStateStore State { get; }

        public ModuleRegistry Registry { get; }

        public HomeAssistantModule Module { get; }

        public LocalFileLayoutStore Cache { get; }

        public string Backups => Path.Combine(_dir, "backups");

        public string SyncFile => Path.Combine(_dir, "layout.sync.json");

        public ModuleLayoutStoreSource? Source { get; private set; }

        public SyncedLayoutStore? Store { get; private set; }

        /// <summary>Like the app: store from the modules, layout.json as cache.</summary>
        public SyncedLayoutStore CreateStore()
        {
            Source = new ModuleLayoutStoreSource(Registry, Log);
            Store = new SyncedLayoutStore(Cache, Source, SyncFile, Backups, Log);
            return Store;
        }

        public Task<bool> Connected() =>
            Wait.UntilAsync(() => Registry.Find(HomeAssistantModule.ModuleId)!.Status.Text == "Verbunden (lokal)");

        public async ValueTask DisposeAsync()
        {
            Store?.Dispose();
            Source?.Dispose();
            Cache.Dispose();
            await Registry.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static LayoutDocument Layout(string tabName, DateTimeOffset? at = null, string by = "PC-A") => new()
    {
        Tabs = [new LayoutTab { Id = "t", Name = tabName, Tiles = [new TileConfig { Id = "k1", Module = "homeassistant", Type = "ha.light", Size = TileSize.Wide, Entities = new() { ["main"] = "light.desk" } }] }],
        UpdatedAt = at ?? DateTimeOffset.Now,
        UpdatedBy = by,
    };

    private static string? TabNameIn(JsonNode? value) => value?["tabs"]?[0]?["name"]?.ToString();

    // ------------------------------------------------------------------ HA store

    [Fact]
    public async Task Layout_store_writes_per_user_and_reads_back()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        var store = env.Module.LayoutStores.Single();
        await store.SaveAsync(Layout("Zimmer"), CancellationToken.None);

        Assert.Equal("Zimmer", TabNameIn(server.GetUserData(HaLayoutStore.Key)));
        Assert.Null(server.GetUserData(HaLayoutStore.Key, token: "sam-token")); // another user does not see it
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("Zimmer", loaded!.Tabs[0].Name);
        Assert.Equal("PC-A", loaded.UpdatedBy);
        Assert.Contains(env.Module.GetDiagnostics(), l => l.StartsWith("Layout in HA: gespeichert ", StringComparison.Ordinal) && l.Contains("live", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Layout_store_reports_changes_from_elsewhere_but_not_its_own_echo()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        var changes = 0;
        env.Module.LayoutStore.Changed += (_, _) => Interlocked.Increment(ref changes);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref changes) == 1)); // when connecting: trigger a reconcile
        var store = Assert.Single(env.Module.LayoutStores);

        await store.SaveAsync(Layout("Eigen"), CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref changes)); // the own echo does not count

        await server.SetUserDataAsync(HaLayoutStore.Key, JsonNode.Parse(UeBarJson.Serialize(Layout("Laptop", by: "LAPTOP"))));
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref changes) == 2));
    }

    [Fact]
    public async Task Without_live_subscription_change_is_reported_on_reconnect()
    {
        await using var server = new FakeHaServer { SupportsUserDataSubscribe = false };
        await using var env = new Env(server);
        var changes = 0;
        env.Module.LayoutStore.Changed += (_, _) => Interlocked.Increment(ref changes);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref changes) == 1));
        Assert.False(env.Module.LayoutStore.LiveSubscription);

        server.DropAll();

        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref changes) == 2, 8000));
    }

    [Fact]
    public async Task Layout_store_is_unavailable_without_connection_and_not_offered_without_setup()
    {
        await using (var unconfigured = new Env(null))
        {
            await unconfigured.Registry.StartAllAsync();
            Assert.Empty(unconfigured.Module.LayoutStores); // not set up → local only
        }

        await using var env = new Env(null, localUrl: BlackholeServer.ClosedPortUrl().ToString());
        await env.Registry.StartAllAsync();
        Assert.True(await Wait.UntilAsync(() => env.Module.Client.Status.State == HaConnectionState.Disconnected));

        var store = Assert.Single(env.Module.LayoutStores);
        await Assert.ThrowsAsync<LayoutStoreUnavailableException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal(ModuleState.Disconnected, env.Registry.Find("homeassistant")!.Status.State); // no module fault
    }

    // ------------------------------------------------------------------ Reconcile with cache (core + HA)

    [Fact]
    public async Task First_connect_uploads_the_local_layout_and_deleted_cache_comes_back_from_ha()
    {
        // P2-1-Fix 1: fake clock for the connection loop – no ping with a 250 ms pong limit can drop the
        // connection on a busy machine between upload and reload. Waiting only on signals.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        await using var server = new FakeHaServer();
        await using var env = new Env(server, options: Fast.Options() with { Time = time });
        await env.Cache.SaveAsync(Layout("Home", DateTimeOffset.Now.AddDays(-3)), CancellationToken.None); // from M5a (import)
        // Store first, then start: the sync is triggered by the connection coming up – created afterwards,
        // a fast connection could already be there and the store would never hear of it.
        var store = env.CreateStore();
        await env.Registry.StartAllAsync();

        // Migration: HA has nothing yet → upload the local one. Until the session runs the fake clock may
        // move: if the first attempt missed its (real-time) local limit on a busy machine, the reconnect
        // pause runs on the fake clock (P2-3, same cause as the network-change test). Once connected it
        // stays still – no ping/pong limit can run out.
        Assert.True(await Wait.UntilAsync(() =>
        {
            if (env.Module.Client?.IsSessionReady != true)
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
            }

            return TabNameIn(server.GetUserData(HaLayoutStore.Key)) == "Home";
        }, 30000));
        Assert.True(await Wait.UntilAsync(() => !store.IsPending && store.LastSync.IsCompleted, 15000));
        await store.LastSync;
        Assert.False(store.IsPending);

        // delete layout.json, restart → comes back from HA
        File.Delete(Path.Combine(Path.GetDirectoryName(env.SyncFile)!, "layout.json"));
        var again = new SyncedLayoutStore(env.Cache, env.Source!, env.SyncFile, env.Backups, env.Log);
        var loaded = await again.LoadAsync(CancellationToken.None);
        again.Dispose();

        Assert.Equal("Home", loaded!.Tabs[0].Name);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(env.SyncFile)!, "layout.json")));
    }

    [Fact]
    public async Task Offline_change_stays_pending_and_is_uploaded_after_reconnect()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        var store = env.CreateStore();
        await store.SaveAsync(Layout("Online"), CancellationToken.None);
        await store.LastSync;
        Assert.Equal("Online", TabNameIn(server.GetUserData(HaLayoutStore.Key)));

        server.AcceptConnections = false;
        server.DropAll();
        Assert.True(await Wait.UntilAsync(() => !env.Module.Client.IsConnected));
        await store.SaveAsync(Layout("Offline"), CancellationToken.None);
        await store.LastSync;
        Assert.True(store.IsPending);
        Assert.Equal("Online", TabNameIn(server.GetUserData(HaLayoutStore.Key)));

        server.AcceptConnections = true;
        Assert.True(await Wait.UntilAsync(() => TabNameIn(server.GetUserData(HaLayoutStore.Key)) == "Offline", 10000));
        await Wait.UntilAsync(() => !store.IsPending);
        Assert.False(store.IsPending);
    }

    [Fact]
    public async Task Change_on_another_device_replaces_the_cache_and_is_reported()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        var store = env.CreateStore();
        await store.SaveAsync(Layout("Hier"), CancellationToken.None);
        await store.LastSync;
        var reloads = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref reloads);

        await server.SetUserDataAsync(HaLayoutStore.Key, JsonNode.Parse(UeBarJson.Serialize(Layout("Laptop", DateTimeOffset.Now.AddMinutes(1), "LAPTOP"))));

        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref reloads) == 1));
        Assert.Equal("Laptop", (await env.Cache.LoadAsync(CancellationToken.None))!.Tabs[0].Name);
        Assert.NotEmpty(Directory.GetFiles(env.Backups, "layout.backup-*.json")); // old state backed up
    }

    [Fact]
    public async Task Conflict_newer_local_wins_and_the_ha_version_is_backed_up()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await server.SetUserDataAsync(HaLayoutStore.Key, JsonNode.Parse(UeBarJson.Serialize(Layout("HA alt", DateTimeOffset.Now.AddHours(-2), "LAPTOP"))));
        await env.Cache.SaveAsync(Layout("Lokal neu", DateTimeOffset.Now.AddMinutes(-1)), CancellationToken.None);
        await env.Registry.StartAllAsync();
        var store = env.CreateStore();

        Assert.True(await Wait.UntilAsync(() => TabNameIn(server.GetUserData(HaLayoutStore.Key)) == "Lokal neu"));
        var backup = Assert.Single(Directory.GetFiles(env.Backups, "layout.backup-*-homeassistant.json"));
        Assert.Contains("HA alt", await File.ReadAllTextAsync(backup));
        Assert.Contains(env.Log.Lines, l => l.Contains("Layout conflict", StringComparison.Ordinal));
        await store.LastSync;
    }

    [Fact]
    public async Task Conflict_newer_ha_wins_and_the_local_version_is_backed_up()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await server.SetUserDataAsync(HaLayoutStore.Key, JsonNode.Parse(UeBarJson.Serialize(Layout("HA neu", DateTimeOffset.Now.AddMinutes(-1), "LAPTOP"))));
        await env.Cache.SaveAsync(Layout("Lokal alt", DateTimeOffset.Now.AddHours(-2)), CancellationToken.None);
        await env.Registry.StartAllAsync();
        var store = env.CreateStore();

        Assert.True(await Wait.UntilAsync(() => env.Cache.LoadAsync(CancellationToken.None).Result?.Tabs[0].Name == "HA neu"));
        Assert.Equal("HA neu", TabNameIn(server.GetUserData(HaLayoutStore.Key)));
        var backup = Assert.Single(Directory.GetFiles(env.Backups, "layout.backup-*.json"));
        Assert.Contains("Lokal alt", await File.ReadAllTextAsync(backup));
        await store.LastSync;
    }

    // ------------------------------------------------------------------ Suggestion from areas

    private static void AddHome(FakeHaServer server)
    {
        server.Areas.Clear();
        server.Areas.Add(new JsonObject { ["area_id"] = "wohnzimmer", ["name"] = "Wohnzimmer", ["icon"] = "mdi:sofa" });
        server.Areas.Add(new JsonObject { ["area_id"] = "kueche", ["name"] = "Küche" });
        server.Areas.Add(new JsonObject { ["area_id"] = "keller", ["name"] = "Keller" }); // without devices
        server.AddState("light.wohnzimmer_decke", "on", "Wohnzimmer Decke");
        server.AddState("climate.wohnzimmer", "heat", "Heizung");
        server.AddState("media_player.tv", "off", "Fernseher");
        server.AddState("switch.wohnzimmer_steckdose", "off", "Steckdose");
        server.AddState("switch.wohnzimmer_led_config", "on", "LED-Modus");
        server.AddState("light.wohnzimmer_versteckt", "off", "Versteckt");
        server.AddState("sensor.wohnzimmer_temp", "21", "Temperatur");
        server.AddState("light.kueche", "off", "Küche Licht");
        server.AddState("script.good_night", "off", "Good night");
        server.AddState("scene.kino", "unknown", "Kino");
        server.AddState("weather.home", "sunny", "Weather");
        foreach (var id in new[] { "light.wohnzimmer_decke", "climate.wohnzimmer", "media_player.tv", "switch.wohnzimmer_steckdose", "switch.wohnzimmer_led_config", "light.wohnzimmer_versteckt", "sensor.wohnzimmer_temp" })
        {
            server.RegistryOverrides[id] = new JsonObject { ["area_id"] = "wohnzimmer" };
        }

        server.RegistryOverrides["switch.wohnzimmer_led_config"]!["entity_category"] = "config";
        server.RegistryOverrides["light.wohnzimmer_versteckt"]!["hidden_by"] = "user";
        server.RegistryOverrides["light.kueche"] = new JsonObject { ["area_id"] = "kueche" };
    }

    [Fact]
    public async Task Areas_become_tabs_with_filtered_tiles_and_routines()
    {
        await using var server = new FakeHaServer();
        AddHome(server);
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        var options = env.Module.GetSuggestionOptions()!;
        Assert.Equal("Welche Bereiche willst du in der Bar?", options.Question);
        Assert.Equal(["area:kueche", "area:wohnzimmer", "routinen"], options.Choices.Select(c => c.Id)); // the cellar without devices is missing
        Assert.Equal("Home", options.Choices[0].Icon);
        Assert.Equal("4 Kacheln", options.Choices.Single(c => c.Id == "area:wohnzimmer").Detail);
        Assert.True(options.Choices.Single(c => c.Id == "routinen").Selected);

        var layout = env.Module.CreateSuggestedLayout(["area:wohnzimmer", "routinen"])!;

        Assert.Equal(["Wohnzimmer", "Routinen"], layout.Tabs.Select(t => t.Name));
        var living = layout.Tabs[0];
        Assert.Equal(["light.wohnzimmer_decke", "climate.wohnzimmer", "media_player.tv", "switch.wohnzimmer_steckdose"], living.Tiles.Select(t => t.GetEntity("main")));
        Assert.Equal([TileSize.Wide, TileSize.Large, TileSize.Large, TileSize.Small], living.Tiles.Select(t => t.Size));
        Assert.Equal(["ha.light", "ha.climate", "ha.media", "ha.toggle"], living.Tiles.Select(t => t.Type));
        Assert.Equal("Decke", living.Tiles[0].Label); // "Wohnzimmer Decke" in the tab "Wohnzimmer"
        Assert.Equal(["script.good_night", "scene.kino"], layout.Tabs[1].Tiles.Select(t => t.GetEntity("main"))); // by name
        Assert.Equal("weather.home", Assert.Single(layout.Status).Entity);
        Assert.Equal(layout.AllTiles.Count(), layout.AllTiles.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task At_most_twelve_tiles_per_tab()
    {
        await using var server = new FakeHaServer();
        server.Areas.Clear();
        server.Areas.Add(new JsonObject { ["area_id"] = "flur", ["name"] = "Flur" });
        for (var i = 0; i < 20; i++)
        {
            server.AddState($"light.flur_{i:00}", "off", $"Flur {i:00}");
            server.RegistryOverrides[$"light.flur_{i:00}"] = new JsonObject { ["area_id"] = "flur" };
            server.AddState($"script.s{i:00}", "off", $"Skript {i:00}");
        }

        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        var options = env.Module.GetSuggestionOptions()!;
        Assert.Equal("12 von 20 Kacheln", options.Choices.Single(c => c.Id == "area:flur").Detail);
        var layout = env.Module.CreateSuggestedLayout(["area:flur", "routinen"])!;
        Assert.All(layout.Tabs, t => Assert.Equal(HaLayoutSuggestion.MaxTilesPerTab, t.Tiles.Count));
        Assert.Equal("Skript 00", env.Module.DisplayNameFor(layout.Tabs[1].Tiles[0].GetEntity("main"))); // by name
    }

    [Fact]
    public async Task Setup_through_the_core_builds_the_layout_or_starts_empty()
    {
        await using var server = new FakeHaServer();
        AddHome(server);
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        var section = Assert.Single(LayoutSetup.GetSections(env.Registry));
        Assert.Equal("homeassistant", section.ModuleId);
        Assert.Contains(section.Choices, c => c.Name == "Küche");

        var chosen = LayoutSetup.Build(env.Registry, new Dictionary<string, IReadOnlyCollection<string>> { ["homeassistant"] = ["area:kueche"] });
        Assert.Equal(["Küche"], chosen.Tabs.Select(t => t.Name));

        var empty = LayoutSetup.Build(env.Registry, new Dictionary<string, IReadOnlyCollection<string>>()); // "Start empty"
        Assert.Empty(empty.Tabs);
    }

    [Fact]
    public async Task Setup_section_says_when_ha_is_not_ready()
    {
        await using var env = new Env(null, localUrl: BlackholeServer.ClosedPortUrl().ToString());
        await env.Registry.StartAllAsync();

        var section = Assert.Single(LayoutSetup.GetSections(env.Registry));
        Assert.Empty(section.Choices);
        Assert.Contains("gerade keine Auswahl", section.Question);
    }

    // ------------------------------------------------------------------ Non-admin

    [Fact]
    public async Task Non_admin_without_registries_uses_list_for_display_and_stays_fully_usable()
    {
        await using var server = new FakeHaServer { IsAdmin = false, UserName = "Sam" };
        server.Unauthorized.UnionWith(["config/entity_registry/list", "config/device_registry/list"]);
        AddHome(server);
        server.AddState("light.desk", "on", "Desk");
        await using var env = new Env(server);
        await env.Registry.NotifyLayoutAsync(new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "x", Tiles = [new TileConfig { Id = "a", Module = "homeassistant", Type = "ha.light", Entities = new() { ["main"] = "light.desk" } }] }],
        });
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        Assert.Equal(EntityIndexSource.Display, env.Module.Client.Index.Source);
        Assert.Contains("Benutzer: Sam (kein Admin)", env.Module.GetDiagnostics());

        // Search with area (comes from list_for_display + area registry)
        var hit = Assert.Single(await env.Module.SearchAsync("decke", 5, CancellationToken.None));
        Assert.Equal("Wohnzimmer", hit.Area);

        // Filters work in the short form as well
        var layout = env.Module.CreateSuggestedLayout(["area:wohnzimmer"])!;
        Assert.DoesNotContain(layout.AllTiles, t => t.GetEntity("main") is "switch.wohnzimmer_led_config" or "light.wohnzimmer_versteckt");

        // tile operable
        var tile = await env.Registry.CreateTileAsync(new TileConfig { Id = "a", Module = "homeassistant", Type = "ha.light", Entities = new() { ["main"] = "light.desk" } });
        Assert.True(await Wait.UntilAsync(() => !tile.IsUnavailable));
        var tap = tile.Elements.OfType<TapAction>().First();
        var result = await env.Registry.InteractAsync(tile, new TileInteraction(tap.Id, InteractionKind.Tap));
        Assert.True(result.Succeeded);
        Assert.True(await Wait.UntilAsync(() => server.ReceivedOfType("call_service").Any()));
    }

    [Fact]
    public async Task Non_admin_without_any_registry_falls_back_to_states_and_offers_device_kinds()
    {
        await using var server = new FakeHaServer { IsAdmin = false, UserName = "Gast" };
        server.Unauthorized.UnionWith(["config/entity_registry/list", "config/entity_registry/list_for_display", "config/device_registry/list", "config/area_registry/list"]);
        AddHome(server);
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        Assert.Equal(EntityIndexSource.States, env.Module.Client.Index.Source);
        var hit = Assert.Single(await env.Module.SearchAsync("Fernseher", 5, CancellationToken.None)); // name from friendly_name
        Assert.Null(hit.Area);

        var options = env.Module.GetSuggestionOptions()!;
        Assert.Equal("Welche Gerätearten willst du in der Bar?", options.Question);
        Assert.Contains("nicht lesbar", options.Hint);
        Assert.Contains(options.Choices, c => c.Id == "art:lichter");
        var layout = env.Module.CreateSuggestedLayout(["art:lichter", "art:klima"])!;
        Assert.Equal(["Lichter", "Klima"], layout.Tabs.Select(t => t.Name));
        Assert.Contains(env.Module.GetDiagnostics(), l => l.Contains("nur Zustände", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_admin_layout_in_ha_works_because_user_data_is_allowed()
    {
        await using var server = new FakeHaServer { IsAdmin = false, UserName = "Sam" };
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        var store = env.CreateStore();

        await store.SaveAsync(Layout("Leas Tab"), CancellationToken.None);
        await store.LastSync;

        Assert.Equal("Leas Tab", TabNameIn(server.GetUserData(HaLayoutStore.Key)));
        Assert.False(store.IsPending);
    }

    [Fact]
    public async Task Fake_ha_refuses_custom_events_for_non_admins_like_the_real_one()
    {
        // Note for M6: ue_bar_notify through subscribe_events is blocked for non-admins (HA 2026.10).
        await using var server = new FakeHaServer { IsAdmin = false };
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        var (result, _) = await env.Module.Client.SubscribeAsync(new JsonObject { ["type"] = "subscribe_events", ["event_type"] = "ue_bar_notify" }, _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unauthorized", result.ErrorCode);
    }
}
