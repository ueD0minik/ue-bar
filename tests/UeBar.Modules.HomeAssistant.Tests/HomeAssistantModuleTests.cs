// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Core.Tiles;

namespace UeBar.Modules.HomeAssistant.Tests;

public class HomeAssistantModuleTests
{
    private sealed class Env : IAsyncDisposable
    {
        public Env(FakeHaServer? server, string token = "good-token")
        {
            Guard = new ModuleGuard(Log);
            Policy = new GrantListPermissionPolicy(new BusGrant { Consumer = "test", Provider = HomeAssistantModule.ModuleId, Access = BusAccess.All });
            Bus = new ModuleBus(Guard, Policy, Log);
            State = new InMemoryModuleStateStore();
            if (server is not null)
            {
                State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyLocalUrl, server.BaseUrl.ToString());
                State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyExternalUrl, "");
                State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyToken, token);
            }

            Registry = new ModuleRegistry(Guard, Bus, Log, State, Path.Combine(Path.GetTempPath(), "uebar-ha-tests"));
            Module = new HomeAssistantModule(Fast.Options());
            Registry.Register(Module);
            Registry.Register(new ConsumerModule()); // the bus only delivers to running modules
        }

        public CollectingLogger Log { get; } = new();

        public ModuleGuard Guard { get; }

        public GrantListPermissionPolicy Policy { get; }

        public ModuleBus Bus { get; }

        public InMemoryModuleStateStore State { get; }

        public ModuleRegistry Registry { get; }

        public HomeAssistantModule Module { get; }

        public Task<bool> WaitForStatus(string text) =>
            Wait.UntilAsync(() => Registry.Find(HomeAssistantModule.ModuleId)!.Status.DisplayText == text);

        public async ValueTask DisposeAsync() => await Registry.DisposeAsync();
    }

    private sealed class ConsumerModule : IModule
    {
        public string Id => "test";

        public string DisplayName => "Verbraucher";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Without_token_reports_not_configured()
    {
        await using var env = new Env(null);

        await env.Registry.StartAllAsync();

        Assert.True(await env.WaitForStatus("Nicht konfiguriert"));
        Assert.Equal(ModuleState.NotConfigured, env.Registry.Find("homeassistant")!.Status.State);
    }

    [Fact]
    public async Task Provides_settings_fields_and_tile_types_from_spec()
    {
        await using var env = new Env(null);

        Assert.Equal(["localUrl", "externalUrl", "token", "preferLocal", "nowPlayingPlayers", "todoList"], env.Module.SettingFields.Select(f => f.Key));
        Assert.True(env.Module.SettingFields.Single(f => f.Key == "token").IsSecret);
        Assert.Null(env.Module.SettingFields[0].DefaultValue); // M7: empty, only an example
        Assert.Equal("http://homeassistant.local:8123", env.Module.SettingFields[0].Placeholder);
        Assert.Equal(["localUrl", "externalUrl", "token"], env.Module.SettingFields.Where(f => f.Essential).Select(f => f.Key));
        foreach (var type in new[] { "ha.light", "ha.toggle", "ha.script", "ha.climate_ir", "ha.climate", "ha.media", "ha.vacuum", "ha.sensor", "ha.select" })
        {
            Assert.NotNull(env.Registry.FindTileType(type));
        }
    }

    [Fact]
    public async Task Connects_through_registry_and_subscribes_layout_entities()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.office_desk", "on", "Desk");
        server.AddState("weather.forecast_home", "cloudy", "Forecast", new JsonObject { ["temperature"] = 8.5, ["temperature_unit"] = "°C" });
        await using var env = new Env(server);
        var layout = SampleLayout.Create();
        await env.Registry.NotifyLayoutAsync(layout);

        await env.Registry.StartAllAsync();

        Assert.True(await env.WaitForStatus("Verbunden (lokal)"));
        var request = server.ReceivedOfType("subscribe_entities").Single();
        var ids = request["entity_ids"]!.AsArray().Select(n => n!.ToString()).ToList();
        Assert.Contains("light.office_ceiling", ids);
        Assert.DoesNotContain("light.office_ceiling_2", ids);
        Assert.Contains("weather.forecast_home", ids); // the status line counts too
        Assert.Contains("select.office_ac_fan", ids); // all slots

        Assert.Contains("sensor.office_temperature", ids); // room temperature (M4)
        Assert.Contains("sensor.robot_battery", ids); // battery slot of the robot vacuum (M4)

        // Status value from HA: weather = icon by state + temperature
        var weather = layout.Status.Single(s => s.Entity == "weather.forecast_home");
        var status = await env.Registry.GetStatusValueAsync(weather);
        Assert.True(await Wait.UntilAsync(() => status!.Text == "8,5 °C"));
        Assert.Equal("Cloud", status!.Icon);
        Assert.Contains("Bewölkt", status.Tooltip);
    }

    [Fact]
    public async Task Tiles_without_configuration_are_greyed_out()
    {
        await using var env = new Env(null);
        await env.Registry.StartAllAsync();
        var config = SampleLayout.Create().AllTiles.First(t => t.Id == "r1");

        var tile = await env.Registry.CreateTileAsync(config);

        Assert.Equal(TilePlaceholder.None, tile.Placeholder);
        Assert.True(tile.IsUnavailable);
        Assert.Equal("Nicht eingerichtet", tile.StateText);
        Assert.Equal("script.focus", tile.Title); // without a label and without a connection: entity ID
    }

    [Fact]
    public async Task Publishes_bus_items_and_raises_state_changed()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.desk", "on");
        await using var env = new Env(server);
        await env.Registry.NotifyLayoutAsync(new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "x", Tiles = [new TileConfig { Id = "a", Module = "homeassistant", Type = "ha.light", Entities = new() { ["main"] = "light.desk" } }] }],
        });
        await env.Registry.StartAllAsync();
        Assert.True(await env.WaitForStatus("Verbunden (lokal)"));

        var consumer = env.Bus.For("test");
        var visible = consumer.GetVisiblePublications().Select(p => p.Id).ToList();
        Assert.Contains("ha.call_service", visible);
        Assert.Contains("ha.state_changed", visible);
        Assert.Contains("ha.connection", visible);
        Assert.Equal("ConnectedLocal", consumer.GetValue("ha.connection").Value!["state"]!.ToString());

        Assert.True(await Wait.UntilAsync(() => env.Module.Client.Store.Get("light.desk") is not null));
        var received = new TaskCompletionSource<BusEvent>();
        using var sub = consumer.SubscribeEvent("ha.state_changed", e => received.TrySetResult(e));
        await server.PushEntitiesEventAsync(new JsonObject { ["c"] = new JsonObject { ["light.desk"] = new JsonObject { ["+"] = new JsonObject { ["s"] = "off" } } } });
        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("light.desk", evt.Payload!["entity_id"]!.ToString());
        Assert.Equal("off", evt.Payload["state"]!.ToString());
        Assert.Equal("on", evt.Payload["old_state"]!.ToString());

        var call = await consumer.InvokeAsync("ha.call_service", new JsonObject { ["domain"] = "light", ["service"] = "toggle", ["target"] = new JsonObject { ["entity_id"] = "light.desk" } });
        Assert.True(call.Success);
        Assert.True(call.Value!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Diagnostics_show_connection_and_recent_changes()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.desk", "on");
        await using var env = new Env(server);
        await env.Registry.NotifyLayoutAsync(new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "x", Tiles = [new TileConfig { Id = "a", Module = "homeassistant", Type = "ha.light", Entities = new() { ["main"] = "light.desk" } }] }],
        });
        await env.Registry.StartAllAsync();
        Assert.True(await env.WaitForStatus("Verbunden (lokal)"));
        await server.PushEntitiesEventAsync(new JsonObject { ["c"] = new JsonObject { ["light.desk"] = new JsonObject { ["+"] = new JsonObject { ["s"] = "off" } } } });
        await Wait.UntilAsync(() => env.Module.GetDiagnostics().Any(l => l.Contains("on → off", StringComparison.Ordinal)));

        var lines = env.Module.GetDiagnostics();

        Assert.Contains(lines, l => l.StartsWith("Server: 127.0.0.1:", StringComparison.Ordinal) && l.EndsWith("(lokal)", StringComparison.Ordinal));
        Assert.Contains("HA-Version: 2026.10.1", lines);
        Assert.Contains("Entitäten im Layout: 1 · davon in HA gefunden: 1", lines);
        Assert.Contains(lines, l => l.Contains("light.desk: on → off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_test_reports_each_url()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(null);
        var values = new ModuleSettingValues(new Dictionary<string, string>
        {
            ["localUrl"] = server.BaseUrl.ToString(),
            ["externalUrl"] = BlackholeServer.ClosedPortUrl().ToString(),
            ["token"] = "good-token",
        });

        var lines = await env.Module.TestSettingsAsync(values, CancellationToken.None);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Success);
        Assert.Contains("Token gültig", lines[0].Detail);
        Assert.Contains("HA 2026.10.1", lines[0].Detail);
        Assert.False(lines[1].Success);
        Assert.Contains("nicht erreichbar", lines[1].Detail);
    }

    [Fact]
    public async Task Invalid_token_shows_error_status()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server, token: "falsch");

        await env.Registry.StartAllAsync();

        Assert.True(await env.WaitForStatus("Token ungültig"));
        Assert.Equal(ModuleState.Error, env.Registry.Find("homeassistant")!.Status.State);
    }

    [Fact]
    public async Task Search_returns_name_area_and_state_on_demand()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.desk", "on", "Desk");
        await using var env = new Env(server);
        await env.Registry.NotifyLayoutAsync(new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "x", Tiles = [new TileConfig { Id = "a", Module = "homeassistant", Type = "ha.light", Entities = new() { ["main"] = "light.desk" } }] }],
        });
        await env.Registry.StartAllAsync();
        Assert.True(await env.WaitForStatus("Verbunden (lokal)"));
        await Wait.UntilAsync(() => env.Module.Client.Store.Get("light.desk") is not null);

        var results = await env.Module.SearchAsync("desk", 5, CancellationToken.None);

        var hit = Assert.Single(results);
        Assert.Equal("Desk", hit.Name);
        Assert.Equal("Office", hit.Area);
        Assert.Equal("An", hit.StateText);
        Assert.Equal("ha.light", env.Module.SuggestTile(hit)!.Type);
    }

    [Fact]
    public void Url_parsing_and_websocket_uri()
    {
        Assert.Equal("http://192.168.1.10:8123/", HaSettings.ParseUrl("192.168.1.10:8123")!.ToString());
        Assert.Equal("https://ha.example.com/", HaSettings.ParseUrl(" https://ha.example.com/ ")!.ToString());
        Assert.Null(HaSettings.ParseUrl("ftp://x"));
        Assert.Equal("wss://ha.example.com/api/websocket", HaSettings.WebSocketUri(new Uri("https://ha.example.com")).ToString());
        Assert.Equal("ws://192.168.1.10:8123/api/websocket", HaSettings.WebSocketUri(new Uri("http://192.168.1.10:8123/")).ToString());
        Assert.Equal("ha.example.com:443", HaSettings.Describe(new Uri("https://user:pw@ha.example.com")));
    }

    [Fact]
    public void State_formatting()
    {
        var weather = new HaEntityState("weather.home", "partlycloudy", new JsonObject { ["temperature"] = 12.0, ["temperature_unit"] = "°C" }, null);
        var sensor = new HaEntityState("sensor.temp", "21.456", new JsonObject { ["unit_of_measurement"] = "°C" }, null);

        Assert.Equal("12 °C Teils bewölkt", StateFormatter.Format(weather));
        Assert.Equal("21,5 °C", StateFormatter.Format(sensor));
        Assert.Equal("An", StateFormatter.Format(new HaEntityState("light.x", "on", [], null)));
        Assert.Equal("Nicht verfügbar", StateFormatter.Format(new HaEntityState("light.x", "unavailable", [], null)));
    }
}
