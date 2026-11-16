// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Bus;
using UeBar.Core.Localization;
using UeBar.Core.Modules;
using UeBar.Core.Settings;

namespace UeBar.Modules.HomeAssistant.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Language switching";
}

/// <summary>R1: the HA module brings its own texts, follows the language of its context and asks Assist in it.</summary>
[Collection(LanguageCollection.Name)]
public sealed class LanguageTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private sealed class Env : IAsyncDisposable
    {
        public Env(FakeHaServer server, CultureInfo culture)
        {
            var guard = new ModuleGuard(Log);
            var bus = new ModuleBus(guard, new GrantListPermissionPolicy(), Log);
            State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyLocalUrl, server.BaseUrl.ToString());
            State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyToken, "good-token");
            Culture = new UiCultureSource(culture);
            Registry = new ModuleRegistry(guard, bus, Log, State, Path.Combine(Path.GetTempPath(), "uebar-r1-tests")) { UiCulture = Culture };
            Module = new HomeAssistantModule(Fast.Options());
            Registry.Register(Module);
        }

        public CollectingLogger Log { get; } = new();

        public InMemoryModuleStateStore State { get; } = new();

        public UiCultureSource Culture { get; }

        public ModuleRegistry Registry { get; }

        public HomeAssistantModule Module { get; }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            HaText.Culture = null; // back to the pinned test culture (German)
        }
    }

    [Fact]
    public void Every_known_state_has_a_text_in_both_languages()
    {
        (string Domain, string State, string? DeviceClass)[] states =
        [
            ("light", "on", null), ("light", "off", null), ("media_player", "playing", null), ("media_player", "paused", null),
            ("media_player", "idle", null), ("media_player", "buffering", null), ("media_player", "standby", null),
            ("media_player", "off", null), ("media_player", "on", null), ("script", "on", null), ("script", "off", null),
            ("scene", "2026-11-01T10:00:00+00:00", null), ("button", "unknown", null), ("lock", "locked", null), ("lock", "unlocked", null),
            ("lock", "locking", null), ("lock", "unlocking", null), ("lock", "jammed", null), ("lock", "open", null),
            ("cover", "open", null), ("cover", "closed", null), ("cover", "opening", null), ("cover", "closing", null),
            ("person", "home", null), ("person", "not_home", null), ("light", "unavailable", null), ("light", "unknown", null),
        ];
        string[] vacuum = ["cleaning", "docked", "paused", "idle", "returning", "error"];
        string[] hvac = ["off", "heat", "cool", "heat_cool", "auto", "dry", "fan_only"];
        string[] weather = ["sunny", "clear-night", "cloudy", "partlycloudy", "fog", "rainy", "pouring", "snowy", "snowy-rainy", "hail", "lightning", "lightning-rainy", "windy", "windy-variant", "exceptional"];
        string[] binary = ["door", "garage_door", "opening", "window", "lock", "motion", "occupancy", "presence", "moisture", "battery",
            "battery_charging", "connectivity", "plug", "power", "problem", "safety", "smoke", "gas", "carbon_monoxide",
            "heat", "cold", "light", "sound", "vibration", "moving", "running", "tamper", "update"];

        try
        {
            foreach (var culture in new[] { German, English })
            {
                HaText.Culture = culture;
                var texts = new List<string>();
                texts.AddRange(states.Select(s => StateFormatter.Format(new HaEntityState($"{s.Domain}.x", s.State, [], null))));
                texts.AddRange(vacuum.Select(StateFormatter.VacuumState));
                texts.AddRange(hvac.Select(StateFormatter.HvacMode));
                texts.AddRange(weather.Select(StateFormatter.WeatherCondition));
                foreach (var deviceClass in binary)
                {
                    foreach (var value in new[] { "on", "off" })
                    {
                        texts.Add(StateFormatter.Format(new HaEntityState("binary_sensor.x", value, new JsonObject { ["device_class"] = deviceClass }, null)));
                    }
                }

                Assert.DoesNotContain(texts, t => t.StartsWith("State_", StringComparison.Ordinal)); // a missing key would show itself
            }
        }
        finally
        {
            HaText.Culture = null;
        }
    }

    [Fact]
    public void States_numbers_and_tile_types_switch_language()
    {
        try
        {
            HaText.Culture = English;
            Assert.Equal("Docked", StateFormatter.VacuumState("docked"));
            Assert.Equal("On", StateFormatter.OnOff("on"));
            Assert.Equal("12 °C Partly cloudy", StateFormatter.Format(new HaEntityState("weather.home", "partlycloudy", new JsonObject { ["temperature"] = 12.0 }, null)));
            Assert.Equal("21,5", StateFormatter.FixedNumber(21.5, 1)); // English texts, German region (de-DE in the tests)
            Assert.Equal("6,3 °C Clear", StateFormatter.Format(new HaEntityState("weather.home", "clear-night", new JsonObject { ["temperature"] = 6.3 }, null)));
            Assert.Equal("Disconnected – next attempt in 5 s", new HaStatus(HaConnectionState.Disconnected, TimeSpan.FromSeconds(5)).Text);

            HaText.Culture = German;
            Assert.Equal("Angedockt", StateFormatter.VacuumState("docked"));
            Assert.Equal("An", StateFormatter.OnOff("on"));
            Assert.Equal("21,5", StateFormatter.FixedNumber(21.5, 1));
            Assert.Equal("Verbunden (lokal)", new HaStatus(HaConnectionState.ConnectedLocal).Text);
        }
        finally
        {
            HaText.Culture = null;
        }
    }

    [Fact]
    public async Task Module_texts_follow_the_context_language()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server, English);

        await env.Registry.StartAllAsync();
        Assert.True(await Wait.UntilAsync(() => env.Registry.Find(HomeAssistantModule.ModuleId)!.Status.DisplayText == "Connected (local)"));
        Assert.Equal("Local URL", env.Module.SettingFields[0].Label);
        Assert.Equal("Light", env.Module.TileTypes.Single(t => t.Id == "ha.light").DisplayName);
        Assert.Equal("Entity", env.Module.TileTypes.Single(t => t.Id == "ha.light").Slots[0].DisplayName);
        Assert.Equal("Add to the shopping list", env.Module.QuickCommands[0].Description);
        Assert.Contains(env.Module.GetDiagnostics(), l => l.StartsWith("Status: Connected (local)", StringComparison.Ordinal));

        env.Culture.Set(German);

        Assert.True(await Wait.UntilAsync(() => env.Registry.Find(HomeAssistantModule.ModuleId)!.Status.DisplayText == "Verbunden (lokal)"));
        Assert.Equal("Lokale URL", env.Module.SettingFields[0].Label);
        Assert.Equal("Licht", env.Module.TileTypes.Single(t => t.Id == "ha.light").DisplayName);
        Assert.Contains(env.Module.GetDiagnostics(), l => l.StartsWith("Status: Verbunden (lokal)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assist_uses_the_pipeline_of_the_ui_language()
    {
        await using var server = new FakeHaServer
        {
            Pipelines = new JsonObject
            {
                ["pipelines"] = new JsonArray(
                    new JsonObject { ["id"] = "p-de", ["name"] = "Deutsch", ["language"] = "de" },
                    new JsonObject { ["id"] = "p-en", ["name"] = "English", ["language"] = "en-US" }),
                ["preferred_pipeline"] = "p-de",
            },
        };
        await using var env = new Env(server, English);
        await env.Registry.StartAllAsync();
        Assert.True(await Wait.UntilAsync(() => env.Module.Client.IsConnected));

        var english = await env.Module.AskAsync("Turn on the desk light", null, CancellationToken.None);
        env.Culture.Set(German);
        var german = await env.Module.AskAsync("Schalte das Desk-Licht ein", null, CancellationToken.None);

        Assert.True(english.Success);
        Assert.True(german.Success);
        var runs = server.ReceivedOfType("assist_pipeline/run").ToList();
        Assert.Equal("p-en", runs[0]["pipeline"]?.ToString()); // English UI → English pipeline
        Assert.Null(runs[1]["pipeline"]); // German UI → HA's preferred pipeline is German already
    }

    [Fact]
    public async Task Fallback_conversation_passes_the_language()
    {
        await using var server = new FakeHaServer { Assist = FakeHaServer.AssistBehavior.Unknown };
        await using var env = new Env(server, English);
        await env.Registry.StartAllAsync();
        Assert.True(await Wait.UntilAsync(() => env.Module.Client.IsConnected));

        await env.Module.AskAsync("How warm is it?", null, CancellationToken.None);

        Assert.Equal("en", server.ReceivedOfType("conversation/process").Single()["language"]?.ToString());
    }

    [Theory]
    [InlineData("de", "p-de", null)] // preferred has the language
    [InlineData("en", "p-de", "p-en")]
    [InlineData("fr", "p-de", null)] // no pipeline in that language → preferred
    [InlineData("de", "p-en", "p-de")] // "de" also matches "de-AT"
    public void Pipeline_choice(string language, string preferred, string? expected)
    {
        var list = new JsonObject
        {
            ["pipelines"] = new JsonArray(
                new JsonObject { ["id"] = "p-de", ["language"] = "de-AT" },
                new JsonObject { ["id"] = "p-en", ["language"] = "en" }),
            ["preferred_pipeline"] = preferred,
        };

        Assert.Equal(expected, HaAssist.ChoosePipeline(list, language));
    }
}
