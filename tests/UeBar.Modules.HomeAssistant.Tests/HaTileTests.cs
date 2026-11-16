// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Core.Tiles;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>
/// M4: tile types from SPEC 7.1 against the fake HA with realistic attributes (example home) –
/// building, interaction → call_service, optimistic state + reset. German texts (pinned culture).
/// </summary>
public class HaTileTests
{
    private sealed class TileEnv : IAsyncDisposable
    {
        private readonly CollectingLogger _log = new();

        public TileEnv(bool localUnreachable = false)
        {
            SampleStates.AddAll(Server);
            var guard = new ModuleGuard(_log);
            var bus = new ModuleBus(guard, new GrantListPermissionPolicy(), _log);
            var state = new InMemoryModuleStateStore();
            state.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyLocalUrl, localUnreachable ? BlackholeServer.ClosedPortUrl().ToString() : Server.BaseUrl.ToString());
            state.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyExternalUrl, localUnreachable ? Server.BaseUrl.ToString() : "");
            state.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyToken, "good-token");
            Registry = new ModuleRegistry(guard, bus, _log, state, Path.Combine(Path.GetTempPath(), "uebar-ha-tile-tests"));
            Module = new HomeAssistantModule(Fast.Options() with
            {
                OptimisticTimeout = TimeSpan.FromMilliseconds(400),
                MediaPlayerOptimisticMax = TimeSpan.FromMilliseconds(1500),
                AlertDuration = TimeSpan.FromMilliseconds(300),
                FlashDuration = TimeSpan.FromMilliseconds(200),
            });
            Registry.Register(Module);
        }

        public FakeHaServer Server { get; } = new();

        public ModuleRegistry Registry { get; }

        public HomeAssistantModule Module { get; }

        public IReadOnlyList<string> Log => _log.Lines;

        public async Task StartAsync(LayoutDocument? layout = null)
        {
            await Registry.NotifyLayoutAsync(layout ?? SampleLayout.Create());
            await Registry.StartAllAsync();
            Assert.True(await Wait.UntilAsync(() => Module.Client.IsConnected && Module.Client.InitialStatesReceived), "nicht verbunden");
        }

        public async Task<TileModel> TileAsync(string id, TileSize? size = null)
        {
            var config = SampleLayout.Create().AllTiles.Single(t => t.Id == id);
            if (size is { } s)
            {
                config.Size = s;
            }

            return await Registry.CreateTileAsync(config);
        }

        public Task<TileModel> TileAsync(string type, string entityId, TileSize size, string slot = "main") =>
            Registry.CreateTileAsync(new TileConfig
            {
                Id = "x" + Guid.NewGuid().ToString("N")[..6],
                Module = HomeAssistantModule.ModuleId,
                Type = type,
                Size = size,
                Entities = new() { [slot] = entityId },
            });

        public async Task InteractAsync(TileModel tile, string element, InteractionKind kind, double? number = null, string? text = null)
        {
            var result = await Registry.InteractAsync(tile, new TileInteraction(element, kind, number, text));
            Assert.True(result.Succeeded, result.Message);
        }

        public async Task<JsonObject> CallAsync(string domain, string service)
        {
            JsonObject? found = null;
            Assert.True(
                await Wait.UntilAsync(() => (found = Calls.FirstOrDefault(m => m["domain"]?.ToString() == domain && m["service"]?.ToString() == service)) is not null),
                $"no call_service {domain}.{service}");
            return found!;
        }

        public IReadOnlyList<JsonObject> Calls => [.. Server.ReceivedOfType("call_service")];

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static string Target(JsonObject call) => call["target"]!["entity_id"]!.ToString();

    private static T El<T>(TileModel tile, string id)
        where T : TileElement => tile.FindElement<T>(id) ?? throw new Xunit.Sdk.XunitException($"Baustein '{id}' fehlt");

    // ------------------------------------------------------------------ Light

    [Fact]
    public async Task Light_desk_2x2_builds_from_real_attributes()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var tile = await env.TileAsync("t1", TileSize.Large);

        Assert.Equal("Desk", tile.Title);
        Assert.Equal("Lightbulb", tile.Icon);
        Assert.True(tile.IsActive);
        Assert.False(tile.IsUnavailable);
        Assert.Equal("An · 40 %", tile.StateText); // brightness 102 → 40 %
        Assert.Equal(["toggle", "brightness", "color_temp"], tile.Elements.Select(e => e.Id));
        Assert.Equal(40, El<SliderElement>(tile, "brightness").Value);
        var ct = El<SliderElement>(tile, "color_temp");
        Assert.Equal(2702, ct.Min);
        Assert.Equal(5000, ct.Max);
        Assert.Equal(2702, ct.Value);
    }

    [Fact]
    public async Task Light_2x1_has_no_color_temperature_and_off_shows_zero()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var desk = await env.TileAsync("t1"); // 2x1 according to the start layout
        var ceiling = await env.TileAsync("t2");

        Assert.Equal(["toggle", "brightness"], desk.Elements.Select(e => e.Id));
        Assert.Equal("Aus", ceiling.StateText);
        Assert.False(ceiling.IsActive);
        Assert.Equal(0, El<SliderElement>(ceiling, "brightness").Value);
    }

    [Fact]
    public async Task Light_without_dimming_is_tap_only()
    {
        await using var env = new TileEnv();
        env.Server.AddState("light.simple", "on", "Simpel", new JsonObject { ["supported_color_modes"] = new JsonArray("onoff") });
        await env.StartAsync(Layout(("ha.light", "light.simple")));

        var tile = await env.TileAsync("ha.light", "light.simple", TileSize.Large);

        Assert.Equal(["toggle"], tile.Elements.Select(e => e.Id));
        Assert.Equal("An", tile.StateText);
    }

    [Fact]
    public async Task Light_toggle_is_optimistic_and_reverts_without_confirmation()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var tile = await env.TileAsync("t1");

        await env.InteractAsync(tile, "toggle", InteractionKind.Tap);

        // the expected state right away
        Assert.False(tile.IsActive);
        Assert.Equal("Aus", tile.StateText);
        var call = await env.CallAsync("light", "toggle");
        Assert.Equal("light.office_desk", Target(call));

        // no state change from HA → back after the time limit + hint
        Assert.True(await Wait.UntilAsync(() => tile.Alert == "Keine Rückmeldung"));
        Assert.True(tile.IsActive);
        Assert.Equal("An · 40 %", tile.StateText);
        Assert.True(await Wait.UntilAsync(() => tile.Alert is null)); // the hint disappears again
        Assert.Equal(0, env.Module.TileHost.PendingCount);
    }

    [Fact]
    public async Task Light_brightness_sends_percent_and_confirmation_keeps_state()
    {
        await using var env = new TileEnv();
        env.Server.CallServiceHandler = msg =>
        {
            var attrs = SampleStates.Desk();
            attrs["brightness"] = 178; // 70 %
            _ = env.Server.SetStateAsync("light.office_desk", "on", attrs);
            return null;
        };
        await env.StartAsync();
        var tile = await env.TileAsync("t1");

        await env.InteractAsync(tile, "brightness", InteractionKind.SetValue, 70);

        Assert.Equal(70, El<SliderElement>(tile, "brightness").Value); // optimistic
        var call = await env.CallAsync("light", "turn_on");
        Assert.Equal(70, call["service_data"]!["brightness_pct"]!.GetValue<double>());
        Assert.True(await Wait.UntilAsync(() => env.Module.TileHost.PendingCount == 0));
        await Task.Delay(600); // beyond the time limit: no jump back, no hint
        Assert.Null(tile.Alert);
        Assert.Equal("An · 70 %", tile.StateText);
    }

    [Fact]
    public async Task Light_brightness_zero_turns_off_and_color_temp_sends_kelvin()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var tile = await env.TileAsync("t1", TileSize.Large);

        await env.InteractAsync(tile, "color_temp", InteractionKind.SetValue, 4012);
        var ct = await env.CallAsync("light", "turn_on");
        Assert.Equal(4012, ct["service_data"]!["color_temp_kelvin"]!.GetValue<double>());
        Assert.Equal(4012, El<SliderElement>(tile, "color_temp").Value);

        await env.InteractAsync(tile, "brightness", InteractionKind.SetValue, 0);
        await env.CallAsync("light", "turn_off");
        Assert.Equal("Aus", tile.StateText);
    }

    [Fact]
    public async Task Service_error_reverts_immediately_with_alert()
    {
        await using var env = new TileEnv();
        env.Server.CallServiceHandler = _ => new JsonObject { ["error"] = new JsonObject { ["code"] = "home_assistant_error", ["message"] = "IR-Sender antwortet nicht" } };
        await env.StartAsync();
        var tile = await env.TileAsync("t3");

        await env.InteractAsync(tile, "power", InteractionKind.Tap);

        Assert.True(await Wait.UntilAsync(() => tile.Alert == "Fehler", 1000));
        Assert.False(tile.IsActive); // back to "off"
        Assert.StartsWith("Aus", tile.StateText);
        Assert.Contains(env.Log, l => l.Contains("call_service switch.toggle failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unavailable_entity_is_greyed_and_not_operable()
    {
        await using var env = new TileEnv();
        env.Server.AddState("light.office_ceiling", "unavailable", "Deckenlampe");
        await env.StartAsync();
        var tile = await env.TileAsync("t2");

        Assert.True(tile.IsUnavailable);
        Assert.Equal("Nicht verfügbar", tile.StateText);
        Assert.All(tile.Elements, e => Assert.False(e.IsEnabled));

        await env.InteractAsync(tile, "toggle", InteractionKind.Tap);
        await Task.Delay(150);
        Assert.Empty(env.Calls);

        // back again → operable
        await env.Server.SetStateAsync("light.office_ceiling", "off", SampleStates.Json("""{ "supported_color_modes": ["color_temp"] }"""));
        Assert.True(await Wait.UntilAsync(() => !tile.IsUnavailable && tile.StateText == "Aus"));
        Assert.All(tile.Elements, e => Assert.True(e.IsEnabled));
    }

    [Fact]
    public async Task Missing_entity_says_not_found_and_disconnect_greys_all_tiles()
    {
        await using var env = new TileEnv();
        await env.StartAsync(Layout(("ha.light", "light.gibt_es_nicht"), ("ha.light", "light.office_desk")));
        var missing = await env.TileAsync("ha.light", "light.gibt_es_nicht", TileSize.Wide);
        var desk = await env.TileAsync("ha.light", "light.office_desk", TileSize.Wide);
        Assert.Equal("Nicht gefunden", missing.StateText);
        Assert.True(missing.IsUnavailable);
        Assert.Contains(env.Module.GetDiagnostics(), l => l == "Fehlen in HA (1): light.gibt_es_nicht");

        env.Server.AcceptConnections = false;
        env.Server.DropAll();

        Assert.True(await Wait.UntilAsync(() => desk.StateText == "Getrennt" && desk.IsUnavailable));
        Assert.Equal("Getrennt", missing.StateText);
    }

    // ------------------------------------------------------------------ Switches, scripts, select

    [Fact]
    public async Task Toggle_uses_homeassistant_toggle()
    {
        await using var env = new TileEnv();
        await env.StartAsync(Layout(("ha.toggle", "switch.office_ac_power")));
        var tile = await env.TileAsync("ha.toggle", "switch.office_ac_power", TileSize.Small);
        Assert.Equal("Aus", tile.StateText);

        await env.InteractAsync(tile, "toggle", InteractionKind.Tap);

        Assert.Equal("switch.office_ac_power", Target(await env.CallAsync("homeassistant", "toggle")));
        Assert.True(tile.IsActive);
    }

    [Fact]
    public async Task Script_runs_non_blocking_shows_running_and_confirms_via_last_triggered()
    {
        await using var env = new TileEnv();
        env.Server.CallServiceHandler = msg =>
        {
            // The script finishes right away: HA only reports a new last_triggered, the state stays off.
            _ = env.Server.SetStateAsync("script.focus", "off", new JsonObject { ["last_triggered"] = "2026-11-05T21:00:00+00:00", ["icon"] = "mdi:desk" });
            return null;
        };
        await env.StartAsync();
        var tile = await env.TileAsync("r1");
        Assert.Equal("Focus", tile.Title); // friendly_name
        Assert.Equal("Laptop", tile.Icon); // mdi:desk
        Assert.Equal("Bereit", tile.StateText);

        await env.InteractAsync(tile, "run", InteractionKind.Tap);

        Assert.True(tile.IsActive);
        Assert.Equal("Läuft", tile.StateText);
        var call = await env.CallAsync("script", "turn_on"); // not script.focus (blocking)
        Assert.Equal("script.focus", Target(call));
        Assert.True(await Wait.UntilAsync(() => tile.StateText == "Ausgeführt"));
        Assert.Null(tile.Alert);
        Assert.True(await Wait.UntilAsync(() => tile.StateText == "Bereit"));
    }

    [Fact]
    public async Task Scene_and_button_use_their_services_and_unknown_is_fine()
    {
        await using var env = new TileEnv();
        env.Server.AddState("scene.kino", "unknown", "Kino");
        env.Server.AddState("button.neustart", "2026-11-01T10:00:00+00:00", "Neustart");
        await env.StartAsync(Layout(("ha.script", "scene.kino"), ("ha.script", "button.neustart")));
        var scene = await env.TileAsync("ha.script", "scene.kino", TileSize.Small);
        var button = await env.TileAsync("ha.script", "button.neustart", TileSize.Small);

        Assert.False(scene.IsUnavailable); // "unknown" = never activated
        Assert.Equal("Szene", scene.StateText);
        Assert.Equal("Palette", scene.Icon);

        await env.InteractAsync(scene, "run", InteractionKind.Tap);
        await env.InteractAsync(button, "run", InteractionKind.Tap);

        Assert.Equal("scene.kino", Target(await env.CallAsync("scene", "turn_on")));
        Assert.Equal("button.neustart", Target(await env.CallAsync("button", "press")));
    }

    [Fact]
    public async Task Select_tile_offers_options_and_selects()
    {
        await using var env = new TileEnv();
        env.Server.AddState("input_select.modus", "Normal", "Modus", new JsonObject { ["options"] = new JsonArray("Normal", "Urlaub") });
        await env.StartAsync(Layout(("ha.select", "select.office_ac_fan"), ("ha.select", "input_select.modus")));
        var fan = await env.TileAsync("ha.select", "select.office_ac_fan", TileSize.Wide);
        var mode = await env.TileAsync("ha.select", "input_select.modus", TileSize.Wide);

        var choice = El<ChoiceElement>(fan, "option");
        Assert.Equal(["Auto", "Low", "Medium", "High"], choice.Options.Select(o => o.Id));
        Assert.Equal("Auto", choice.SelectedId);

        await env.InteractAsync(fan, "option", InteractionKind.Select, text: "Medium");
        await env.InteractAsync(mode, "option", InteractionKind.Select, text: "Urlaub");
        await env.InteractAsync(fan, "option", InteractionKind.Select, text: "Turbo"); // does not exist → nothing

        Assert.Equal("Medium", (await env.CallAsync("select", "select_option"))["service_data"]!["option"]!.ToString());
        Assert.Equal("Urlaub", (await env.CallAsync("input_select", "select_option"))["service_data"]!["option"]!.ToString());
        Assert.Equal("Medium", choice.SelectedId);
        await Task.Delay(100);
        Assert.Equal(2, env.Calls.Count);
    }

    // ------------------------------------------------------------------ Climate

    [Fact]
    public async Task Climate_ir_builds_from_number_and_select_attributes()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var tile = await env.TileAsync("t3");

        Assert.Equal("Klima", tile.Title);
        Assert.Equal("Aus", tile.StateText);
        Assert.False(tile.IsActive);
        var target = El<StepperElement>(tile, "target");
        Assert.Equal((16, 30, 1, 18, 0), (target.Min, target.Max, target.Step, target.Value, target.Decimals));
        Assert.Equal(" °C", target.Unit);
        var fan = El<ChoiceElement>(tile, "fan");
        Assert.Equal(["Auto", "Low", "Medium", "High"], fan.Options.Select(o => o.Label));
        Assert.Equal("Auto", fan.SelectedId);
    }

    [Fact]
    public async Task Climate_ir_maps_power_target_and_fan_to_their_entities()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var tile = await env.TileAsync("t3");

        await env.InteractAsync(tile, "power", InteractionKind.Tap);
        await env.InteractAsync(tile, "target", InteractionKind.Increment);
        await env.InteractAsync(tile, "fan", InteractionKind.Select, text: "High");

        Assert.Equal("switch.office_ac_power", Target(await env.CallAsync("switch", "toggle")));
        var number = await env.CallAsync("number", "set_value");
        Assert.Equal("number.office_ac_target", Target(number));
        Assert.Equal(19, number["service_data"]!["value"]!.GetValue<double>());
        var select = await env.CallAsync("select", "select_option");
        Assert.Equal("select.office_ac_fan", Target(select));
        Assert.Equal("High", select["service_data"]!["option"]!.ToString());

        // optimistic: on, 19 °C, high
        Assert.True(tile.IsActive);
        Assert.Equal("An · 19 °C · High", tile.StateText);
        Assert.Equal(19, El<StepperElement>(tile, "target").Value);
    }

    [Fact]
    public async Task Climate_ir_stepper_respects_limits_and_rapid_steps_keep_last_value()
    {
        await using var env = new TileEnv();
        env.Server.AddState("number.office_ac_target", "16.0", null, SampleStates.Json("""{ "min": 16, "max": 30, "step": 1 }"""));
        await env.StartAsync();
        var tile = await env.TileAsync("t3");

        await env.InteractAsync(tile, "target", InteractionKind.Decrement); // already at 16
        await Task.Delay(100);
        Assert.Empty(env.Calls);

        await env.InteractAsync(tile, "target", InteractionKind.Increment);
        await env.InteractAsync(tile, "target", InteractionKind.Increment);
        Assert.Equal(18, El<StepperElement>(tile, "target").Value); // builds on the optimistic value
        Assert.True(await Wait.UntilAsync(() => env.Calls.Count == 2));
        Assert.Equal([17.0, 18.0], env.Calls.Select(c => c["service_data"]!["value"]!.GetValue<double>()));
    }

    [Fact]
    public async Task Climate_ir_without_optional_fan_and_missing_number_attributes()
    {
        await using var env = new TileEnv();
        env.Server.AddState("number.ziel", "21", "Ziel"); // without min/max/step
        await env.StartAsync(new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "a", Tiles = [IrConfig()] }],
        });

        var tile = await env.Registry.CreateTileAsync(IrConfig());

        Assert.Equal(["power", "target"], tile.Elements.Select(e => e.Id));
        var target = El<StepperElement>(tile, "target");
        Assert.Equal(1, target.Step);
        Assert.Equal(21, target.Value);

        static TileConfig IrConfig() => new()
        {
            Id = "ir",
            Module = HomeAssistantModule.ModuleId,
            Type = "ha.climate_ir",
            Size = TileSize.Large,
            Entities = new() { ["power"] = "switch.office_ac_power", ["target"] = "number.ziel" },
        };
    }

    [Fact]
    public async Task Climate_entity_uses_hvac_modes_and_set_temperature()
    {
        await using var env = new TileEnv();
        env.Server.AddState("climate.bad", "heat", "Bad", SampleStates.Json("""
            { "hvac_modes": ["off", "heat", "auto"], "temperature": 21.5, "current_temperature": 20.1,
              "min_temp": 7, "max_temp": 30, "target_temp_step": 0.5, "supported_features": 385 }
            """));
        await env.StartAsync(Layout(("ha.climate", "climate.bad")));
        var tile = await env.TileAsync("ha.climate", "climate.bad", TileSize.Large);

        Assert.Equal("Heizen · 21,5 °C (ist 20,1 °C)", tile.StateText);
        Assert.Equal(["Aus", "Heizen", "Automatik"], El<ChoiceElement>(tile, "mode").Options.Select(o => o.Label));

        await env.InteractAsync(tile, "target", InteractionKind.Increment);
        await env.InteractAsync(tile, "mode", InteractionKind.Select, text: "off");

        Assert.Equal(22, (await env.CallAsync("climate", "set_temperature"))["service_data"]!["temperature"]!.GetValue<double>());
        Assert.Equal("off", (await env.CallAsync("climate", "set_hvac_mode"))["service_data"]!["hvac_mode"]!.ToString());
    }

    // ------------------------------------------------------------------ Media

    [Fact]
    public async Task Media_laptop_shows_only_supported_buttons_progress_and_cover_id()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var tile = await env.TileAsync("m2"); // media_player.office_pc, 153151

        Assert.Equal("PC", tile.Title);
        Assert.Equal("Spielt", tile.StateText);
        Assert.True(tile.IsActive);
        Assert.Equal(["info", "controls", "volume"], tile.Elements.Select(e => e.Id));
        Assert.Equal(["previous", "playpause", "next", "mute"], El<ButtonRowElement>(tile, "controls").Buttons.Select(b => b.Id)); // no TURN_ON/OFF
        Assert.Equal("Pause", El<ButtonRowElement>(tile, "controls").Buttons[1].Icon);
        var info = El<MediaInfoElement>(tile, "info");
        Assert.Equal(("Example Song", "Example Artist"), (info.Title, info.Artist));
        Assert.Equal(182.125, info.Duration);
        Assert.Equal(40.591, info.Position);
        Assert.Equal("2026-11-05 20:13:38", info.PositionUpdatedAt!.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(info.IsPlaying);
        Assert.StartsWith("ha-image:media_player.office_pc?v=", info.ImageUrl);
        Assert.DoesNotContain(SampleStates.FakeProxyToken, info.ImageUrl); // no token in the model
        Assert.Equal(30, El<SliderElement>(tile, "volume").Value);
    }

    [Fact]
    public async Task Media_buttons_and_volume_map_to_media_player_services()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var tile = await env.TileAsync("m2");

        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "playpause");
        Assert.Equal("Pausiert", tile.StateText); // optimistic
        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "next");
        await env.InteractAsync(tile, "volume", InteractionKind.SetValue, 55);
        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "mute");

        await env.CallAsync("media_player", "media_play_pause");
        await env.CallAsync("media_player", "media_next_track");
        Assert.Equal(0.55, (await env.CallAsync("media_player", "volume_set"))["service_data"]!["volume_level"]!.GetValue<double>(), 3);
        Assert.True((await env.CallAsync("media_player", "volume_mute"))["service_data"]!["is_volume_muted"]!.GetValue<bool>());
        Assert.Equal(55, El<SliderElement>(tile, "volume").Value);
    }

    [Fact]
    public async Task Media_player_stays_optimistic_past_the_timeout_until_the_next_state_arrives()
    {
        // M6-Fix 1: HASS.Agent often reports play/pause back only after > 3 s.
        await using var env = new TileEnv();
        await env.StartAsync();
        var tile = await env.TileAsync("m2"); // playing

        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "playpause");
        await env.CallAsync("media_player", "media_play_pause");
        await Task.Delay(800); // beyond the normal 400 ms
        Assert.Equal("Pausiert", tile.StateText); // stays optimistic
        Assert.Null(tile.Alert);
        Assert.Equal(1, env.Module.TileHost.PendingCount);

        // The next state does not match (the player keeps playing) → real state, without an error hint.
        await env.Server.SetStateAsync("media_player.office_pc", "playing");
        Assert.True(await Wait.UntilAsync(() => tile.StateText == "Spielt"));
        Assert.Equal(0, env.Module.TileHost.PendingCount);
        await Task.Delay(300);
        Assert.Null(tile.Alert);
    }

    [Fact]
    public async Task Media_player_without_any_answer_reverts_after_the_maximum_and_errors_revert_at_once()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var tile = await env.TileAsync("m2");
        var started = DateTimeOffset.UtcNow;

        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "playpause");
        Assert.Equal("Pausiert", tile.StateText);
        Assert.True(await Wait.UntilAsync(() => tile.Alert == "Keine Rückmeldung"));
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(1400), "jumped back too early");
        Assert.Equal("Spielt", tile.StateText);
        Assert.True(await Wait.UntilAsync(() => tile.Alert is null));

        // Error in the service call: back right away as before.
        env.Server.CallServiceHandler = _ => new JsonObject { ["error"] = new JsonObject { ["code"] = "home_assistant_error", ["message"] = "kaputt" } };
        started = DateTimeOffset.UtcNow;
        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "playpause");
        Assert.True(await Wait.UntilAsync(() => tile.Alert is not null && tile.Alert != "Keine Rückmeldung"));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(1000));
        Assert.Equal("Spielt", tile.StateText);
    }

    [Fact]
    public async Task Media_monitor_has_power_and_source_in_state()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var monitor = await env.TileAsync("t4"); // 23997, on, hdmi2
        var tv = await env.TileAsync("m1"); // off

        Assert.Equal(["power", "previous", "playpause", "next", "mute"], El<ButtonRowElement>(monitor, "controls").Buttons.Select(b => b.Id));
        Assert.Equal("An · HDMI 2", monitor.StateText);
        Assert.Equal(40, El<SliderElement>(monitor, "volume").Value);
        Assert.Equal("Aus", tv.StateText);
        Assert.False(tv.IsUnavailable); // off ≠ unavailable
        Assert.False(El<SliderElement>(tv, "volume").IsEnabled);

        await env.InteractAsync(monitor, "controls", InteractionKind.Press, text: "power");
        await env.InteractAsync(tv, "controls", InteractionKind.Press, text: "power");

        Assert.Equal("media_player.monitor", Target(await env.CallAsync("media_player", "turn_off")));
        Assert.Equal("media_player.living_room_tv", Target(await env.CallAsync("media_player", "turn_on")));
    }

    [Fact]
    public async Task Cover_is_loaded_via_active_url_without_auth_header_when_url_has_token()
    {
        await using var env = new TileEnv();
        env.Server.AddState("media_player.radio", "playing", "Radio", new JsonObject { ["entity_picture"] = "/api/image/radio.png", ["supported_features"] = 1 });
        env.Server.HttpHandler = path => path.StartsWith("/api/", StringComparison.Ordinal) ? (200, [1, 2, 3, 4], "image/png") : (404, [], "text/plain");
        await env.StartAsync(Layout(("ha.media", "media_player.office_pc"), ("ha.media", "media_player.radio")));
        var laptop = await env.TileAsync("ha.media", "media_player.office_pc", TileSize.Large);
        var radio = await env.TileAsync("ha.media", "media_player.radio", TileSize.Large);

        var withToken = await env.Module.LoadImageAsync(El<MediaInfoElement>(laptop, "info").ImageUrl!, CancellationToken.None);
        var withHeader = await env.Module.LoadImageAsync(El<MediaInfoElement>(radio, "info").ImageUrl!, CancellationToken.None);
        var unknown = await env.Module.LoadImageAsync("ha-image:media_player.gibt_es_nicht?v=1", CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], withToken!.Data);
        Assert.Equal("image/png", withToken.ContentType);
        Assert.NotNull(withHeader);
        Assert.Null(unknown);
        var requests = env.Server.HttpRequests.ToList();
        var proxy = requests.Single(r => r.PathAndQuery.StartsWith("/api/media_player_proxy/", StringComparison.Ordinal));
        Assert.Null(proxy.Authorization); // the URL has its own token → no header
        Assert.Equal("Bearer good-token", requests.Single(r => r.PathAndQuery == "/api/image/radio.png").Authorization);
        Assert.DoesNotContain(env.Log, l => l.Contains(SampleStates.FakeProxyToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cover_from_foreign_host_never_gets_the_token()
    {
        await using var other = new FakeHaServer();
        other.HttpHandler = _ => (200, [9], "image/jpeg");
        await using var env = new TileEnv();
        env.Server.AddState("media_player.spotify", "playing", "Spotify", new JsonObject
        {
            ["entity_picture"] = new Uri(other.BaseUrl, "/cover.jpg").ToString(),
            ["supported_features"] = 1,
        });
        await env.StartAsync(Layout(("ha.media", "media_player.spotify")));
        var tile = await env.TileAsync("ha.media", "media_player.spotify", TileSize.Large);

        var image = await env.Module.LoadImageAsync(El<MediaInfoElement>(tile, "info").ImageUrl!, CancellationToken.None);

        Assert.Equal([9], image!.Data);
        Assert.Null(other.HttpRequests.Single().Authorization);
    }

    // ------------------------------------------------------------------ Vacuum, sensor, fallback

    [Fact]
    public async Task Vacuum_with_battery_sensor()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var tile = await env.TileAsync("r6"); // 13084 = START, PAUSE, RETURN_HOME (+ STATE, LOCATE …)

        Assert.Equal("Robot", tile.Title);
        Assert.Equal("Angedockt · 90 %", tile.StateText);
        Assert.Equal(["controls", "battery"], tile.Elements.Select(e => e.Id));
        Assert.Equal(["start", "pause", "return"], El<ButtonRowElement>(tile, "controls").Buttons.Select(b => b.Id));
        Assert.Equal("90", El<ValueDisplayElement>(tile, "battery").Value);

        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "return");
        Assert.Equal("Kehrt zurück", tile.StateText!.Split(" · ")[0]);
        Assert.Equal("vacuum.robot", Target(await env.CallAsync("vacuum", "return_to_base")));
        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "start");
        await env.CallAsync("vacuum", "start");
    }

    [Fact]
    public async Task Vacuum_without_battery_shows_no_battery_display()
    {
        await using var env = new TileEnv();
        await env.StartAsync(Layout(("ha.vacuum", "vacuum.robot")));

        var tile = await env.TileAsync("ha.vacuum", "vacuum.robot", TileSize.Wide);

        Assert.Equal(["controls"], tile.Elements.Select(e => e.Id));
        Assert.Equal("Angedockt", tile.StateText);
    }

    [Fact]
    public async Task Sensor_values_are_rounded_and_binary_sensors_use_device_class()
    {
        await using var env = new TileEnv();
        env.Server.AddState("binary_sensor.tuer", "on", "Tür", new JsonObject { ["device_class"] = "door" });
        env.Server.AddState("sensor.strom", "1234.567", "Strom", new JsonObject { ["unit_of_measurement"] = "W", ["device_class"] = "power" });
        await env.StartAsync(Layout(("ha.sensor", "sensor.office_temperature"), ("ha.sensor", "binary_sensor.tuer"), ("ha.sensor", "sensor.strom")));

        var temp = await env.TileAsync("ha.sensor", "sensor.office_temperature", TileSize.Wide);
        var door = await env.TileAsync("ha.sensor", "binary_sensor.tuer", TileSize.Wide);
        var power = await env.TileAsync("ha.sensor", "sensor.strom", TileSize.Small);

        Assert.Equal(("20,2", "°C"), (El<ValueDisplayElement>(temp, "value").Value, El<ValueDisplayElement>(temp, "value").Unit));
        Assert.Equal("Thermometer", temp.Icon);
        Assert.Equal("Offen", door.StateText);
        Assert.Equal("Door", door.Icon);
        Assert.Equal("1235 W", power.StateText);
    }

    [Fact]
    public async Task Generic_tile_toggles_only_switchable_domains()
    {
        await using var env = new TileEnv();
        env.Server.AddState("automation.licht", "on", "Licht-Automation");
        env.Server.AddState("lock.haustuer", "locked", "Haustür");
        await env.StartAsync(Layout(("ha.entity", "automation.licht"), ("ha.entity", "lock.haustuer")));
        var automation = await env.TileAsync("ha.entity", "automation.licht", TileSize.Small);
        var door = await env.TileAsync("ha.entity", "lock.haustuer", TileSize.Small);

        Assert.Equal("Verriegelt", door.StateText);
        Assert.Empty(door.Elements); // no toggling for locks

        await env.InteractAsync(automation, "toggle", InteractionKind.Tap);
        await env.InteractAsync(door, "toggle", InteractionKind.Tap);

        Assert.Equal("automation.licht", Target(await env.CallAsync("homeassistant", "toggle")));
        await Task.Delay(100);
        Assert.Single(env.Calls);
    }

    [Fact]
    public void Suggests_tile_types_per_domain_with_generic_fallback()
    {
        Assert.Equal("ha.light", HaTileType.ForDomain("light").Type);
        Assert.Equal("ha.script", HaTileType.ForDomain("button").Type);
        Assert.Equal("ha.entity", HaTileType.ForDomain("lock").Type);
    }

    // ------------------------------------------------------------------ Status line, diagnostics, icons

    [Fact]
    public async Task Status_line_shows_room_temperature_and_weather()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var layout = SampleLayout.Create();

        var temperature = await env.Registry.GetStatusValueAsync(layout.Status[0]);
        var weather = await env.Registry.GetStatusValueAsync(layout.Status[1]);

        Assert.Equal(("Thermometer", "20,2 °C"), (temperature!.Icon, temperature.Text));
        Assert.Equal(("Moon", "6,3 °C"), (weather!.Icon, weather.Text)); // clear-night
        Assert.Equal("Weather: Klar, 6,3 °C", weather.Tooltip);

        await env.Server.SetStateAsync("sensor.office_temperature", "21");
        Assert.True(await Wait.UntilAsync(() => temperature.Text == "21,0 °C"));
        await env.Server.SetStateAsync("weather.forecast_home", "rainy");
        Assert.True(await Wait.UntilAsync(() => weather.Icon == "Rain"));
    }

    [Fact]
    public async Task Diagnostics_count_all_layout_entities_including_status_and_battery()
    {
        await using var env = new TileEnv();
        await env.StartAsync();

        var lines = env.Module.GetDiagnostics();

        // 14 tile entities + battery slot + 2 status values = 17, all present
        Assert.Contains("Entitäten im Layout: 17 · davon in HA gefunden: 17", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Fehlen in HA", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("mdi:desk", "Laptop")]
    [InlineData("mdi:shower", "Drop")]
    [InlineData("mdi:weather-night", "Moon")]
    [InlineData("mdi:exit-run", "Run")]
    [InlineData("mdi:laptop-off", "Laptop")]
    [InlineData("mdi:lightning-bolt", "Lightning")]
    [InlineData("mdi:light-switch", "Power")]
    [InlineData("mdi:ceiling-light-multiple", "Lightbulb")]
    [InlineData("mdi:robot-vacuum", "Robot")]
    [InlineData("mdi:gibtsnicht", null)]
    public void Mdi_icons_map_to_app_symbols(string mdi, string? expected) =>
        Assert.Equal(expected, HaIcons.FromMdi(mdi));

    [Fact]
    public void State_texts_are_german()
    {
        Assert.Equal("Saugt", StateFormatter.Format(new HaEntityState("vacuum.r", "cleaning", [], null)));
        Assert.Equal("Kehrt zurück", StateFormatter.Format(new HaEntityState("vacuum.r", "returning", [], null)));
        Assert.Equal("Bereit", StateFormatter.Format(new HaEntityState("media_player.m", "idle", [], null)));
        Assert.Equal("Geschlossen", StateFormatter.Format(new HaEntityState("binary_sensor.f", "off", new JsonObject { ["device_class"] = "window" }, null)));
        Assert.Equal("Keine Bewegung", StateFormatter.Format(new HaEntityState("binary_sensor.b", "off", new JsonObject { ["device_class"] = "motion" }, null)));
        Assert.Equal("Szene", StateFormatter.Format(new HaEntityState("scene.s", "unknown", [], null)));
        Assert.Equal("Unbekannt", StateFormatter.Format(new HaEntityState("sensor.s", "unknown", [], null)));
    }

    // ------------------------------------------------------------------ M5a: location and edit help

    [Fact]
    public async Task Location_is_home_when_local_and_away_when_disconnected()
    {
        await using var env = new TileEnv();
        var seen = new List<DeviceLocation>();
        env.Module.LocationChanged += (_, l) => seen.Add(l);
        Assert.Equal(DeviceLocation.Unknown, env.Module.CurrentLocation);

        await env.StartAsync();
        Assert.Equal(DeviceLocation.Home, env.Module.CurrentLocation); // fake HA = local URL

        env.Server.AcceptConnections = false;
        env.Server.DropAll();
        Assert.True(await Wait.UntilAsync(() => env.Module.CurrentLocation == DeviceLocation.Away));

        env.Server.AcceptConnections = true;
        Assert.True(await Wait.UntilAsync(() => env.Module.CurrentLocation == DeviceLocation.Home));
        Assert.Equal([DeviceLocation.Home, DeviceLocation.Away, DeviceLocation.Home], seen);

        await env.Registry.StopModuleAsync(HomeAssistantModule.ModuleId);
        Assert.True(await Wait.UntilAsync(() => env.Module.CurrentLocation == DeviceLocation.Unknown));
    }

    [Fact]
    public async Task External_connection_counts_as_away()
    {
        await using var env = new TileEnv(localUnreachable: true);
        await env.Registry.NotifyLayoutAsync(SampleLayout.Create());
        await env.Registry.StartAllAsync();

        Assert.True(await Wait.UntilAsync(() => env.Module.Client.Status.State == HaConnectionState.ConnectedExternal));
        Assert.Equal(DeviceLocation.Away, env.Module.CurrentLocation);
    }

    [Fact]
    public async Task Climate_ir_slots_are_completed_from_the_same_device_name()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        var draft = new TileConfig { Module = HomeAssistantModule.ModuleId, Type = "ha.climate_ir", Entities = new() { ["power"] = "switch.office_ac_power" } };

        var completed = env.Module.CompleteSlots(draft);

        Assert.Equal("number.office_ac_target", completed.GetEntity("target"));
        Assert.Equal("select.office_ac_fan", completed.GetEntity("fan"));
        Assert.Single(draft.Entities); // the draft stays unchanged
    }

    [Fact]
    public async Task Picked_entity_moves_into_the_fitting_slot()
    {
        await using var env = new TileEnv();
        await env.StartAsync();
        // The target temperature was searched and chosen – it first lands in the first slot (power).
        var draft = new TileConfig { Module = HomeAssistantModule.ModuleId, Type = "ha.climate_ir", Entities = new() { ["power"] = "number.office_ac_target" } };

        var completed = env.Module.CompleteSlots(draft);

        Assert.Equal("number.office_ac_target", completed.GetEntity("target"));
        Assert.Equal("switch.office_ac_power", completed.GetEntity("power"));
    }

    [Fact]
    public async Task Slot_suggestions_filter_by_domain_and_rank_the_same_device_first()
    {
        await using var env = new TileEnv();
        env.Server.AddState("number.anderes_ding", "5", "Anderes Ding");
        await env.StartAsync();
        var draft = new TileConfig { Module = HomeAssistantModule.ModuleId, Type = "ha.climate_ir", Entities = new() { ["power"] = "switch.office_ac_power" } };

        var all = await env.Module.SuggestForSlotAsync(draft, "target", "", 10, CancellationToken.None);
        var filtered = await env.Module.SuggestForSlotAsync(draft, "target", "anderes", 10, CancellationToken.None);

        Assert.Equal("number.office_ac_target", all[0].Id);
        Assert.All(all, e => Assert.StartsWith("number.", e.Id));
        Assert.Equal(["number.anderes_ding"], filtered.Select(e => e.Id));
    }

    private static LayoutDocument Layout(params (string Type, string Entity)[] tiles) => new()
    {
        Tabs =
        [
            new LayoutTab
            {
                Id = "t",
                Tiles = [.. tiles.Select((t, i) => new TileConfig { Id = "k" + i, Module = HomeAssistantModule.ModuleId, Type = t.Type, Entities = new() { ["main"] = t.Entity } })],
            },
        ],
    };
}
