// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using UeBar.Sdk;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Modules;
using UeBar.Core.Settings;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>M6: notifications (mobile app), Assist, Now Playing, quick command – against the fake HA.</summary>
public class ExtrasTests
{
    private sealed class Env : IAsyncDisposable
    {
        public Env(FakeHaServer server)
        {
            Guard = new ModuleGuard(Log);
            var bus = new ModuleBus(Guard, new GrantListPermissionPolicy(), Log);
            State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyLocalUrl, server.BaseUrl.ToString());
            State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyExternalUrl, "");
            State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyToken, "good-token");
            Registry = new ModuleRegistry(Guard, bus, Log, State, Path.Combine(Path.GetTempPath(), "uebar-m6-tests"));
            Module = new HomeAssistantModule(Fast.Options());
            Module.MobileApp.GetType(); // present
            Module.NotificationReceived += (_, n) => Notifications.Enqueue(n);
            Registry.Register(Module);
        }

        public CollectingLogger Log { get; } = new();

        public ModuleGuard Guard { get; }

        public InMemoryModuleStateStore State { get; } = new();

        public ModuleRegistry Registry { get; }

        public HomeAssistantModule Module { get; }

        public ConcurrentQueue<UeNotification> Notifications { get; } = new();

        public string? Webhook => State.GetModuleSecret(HomeAssistantModule.ModuleId, HaMobileApp.SecretWebhook);

        public Task<bool> Connected() =>
            Wait.UntilAsync(() => Registry.Find(HomeAssistantModule.ModuleId)!.Status.Text == "Verbunden (lokal)");

        public async ValueTask DisposeAsync() => await Registry.DisposeAsync();
    }

    // ------------------------------------------------------------------ Notifications

    [Fact]
    public async Task Registers_once_opens_push_channel_and_confirms_notifications()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        Assert.True(await Wait.UntilAsync(() => env.Module.MobileApp.State.StartsWith("verbunden", StringComparison.Ordinal)));
        var (webhook, registration) = Assert.Single(server.MobileRegistrations);
        Assert.Equal(webhook, env.Webhook); // in the secret store
        Assert.Equal("ue Bar", registration.Body["app_name"]!.ToString());
        Assert.Equal($"ue Bar – {Environment.MachineName}", registration.Body["device_name"]!.ToString());
        Assert.False(registration.Body["supports_encryption"]!.GetValue<bool>());
        Assert.True(registration.Body["app_data"]!["push_websocket_channel"]!.GetValue<bool>()); // otherwise HA creates no service
        Assert.False(string.IsNullOrEmpty(registration.Body["device_id"]?.ToString()));
        Assert.True(server.ReceivedOfType("mobile_app/push_notification_channel").Single()["support_confirm"]!.GetValue<bool>());

        await server.PushNotificationAsync(webhook, "Waschmaschine", "Die Wäsche ist fertig", new JsonObject { ["icon"] = "mdi:bell", ["duration"] = 9 });

        Assert.True(await Wait.UntilAsync(() => !env.Notifications.IsEmpty));
        env.Notifications.TryDequeue(out var n);
        Assert.Equal("Waschmaschine", n!.Title);
        Assert.Equal("Die Wäsche ist fertig", n.Message);
        Assert.Equal("Bell", n.Icon);
        Assert.Equal(TimeSpan.FromSeconds(9), n.Duration);
        Assert.True(await Wait.UntilAsync(() => server.Confirmed.Count == 1)); // confirmed

        // Module restart: no second registration
        await env.Registry.RestartModuleAsync(HomeAssistantModule.ModuleId);
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => server.ReceivedOfType("mobile_app/push_notification_channel").Count() == 2));
        Assert.Equal(1, server.RegistrationRequests);
        Assert.Contains(env.Module.GetDiagnostics(), l => l.Contains($"Dienst notify.mobile_app_ue_bar_{HaMobileApp.Slugify(Environment.MachineName)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registration_deleted_in_ha_registers_again_cleanly()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => env.Webhook is not null && env.Module.MobileApp.State.StartsWith("verbunden", StringComparison.Ordinal)));
        var first = env.Webhook!;

        server.DeleteRegistration(first);
        server.DropAll();

        Assert.True(await Wait.UntilAsync(() => server.RegistrationRequests == 2, 8000));
        Assert.True(await Wait.UntilAsync(() => env.Webhook is { } w && w != first && server.MobileRegistrations.ContainsKey(w)));
        Assert.True(await Wait.UntilAsync(() => env.Module.MobileApp.State.StartsWith("verbunden", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Changed_device_name_renames_the_device_in_ha()
    {
        // M7: device name in the settings → update_registration, no second device.
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => env.Webhook is not null && env.Module.MobileApp.State.StartsWith("verbunden", StringComparison.Ordinal)));
        var webhook = env.Webhook!;

        env.Registry.DeviceName = () => "Laptop";
        Assert.Equal("notify.mobile_app_ue_bar_laptop", env.Module.MobileApp.ServiceName);
        server.DropAll(); // new session → reconcile the name

        Assert.True(await Wait.UntilAsync(() => server.MobileRegistrations[webhook].Body["device_name"]?.ToString() == "ue Bar – Laptop", 8000));
        Assert.Equal(1, server.RegistrationRequests); // renamed, not registered again
        Assert.True(await Wait.UntilAsync(() => env.Log.Lines.Any(l => l.Contains("renamed in HA", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Works_for_non_admin_users()
    {
        await using var server = new FakeHaServer { IsAdmin = false, UserName = "Sam" };
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => env.Webhook is not null && env.Module.MobileApp.State.StartsWith("verbunden", StringComparison.Ordinal)));

        await server.PushNotificationAsync(env.Webhook!, "Hello Sam", "Test");

        Assert.True(await Wait.UntilAsync(() => env.Notifications.Any(n => n.Title == "Hello Sam")));
    }

    [Theory]
    [InlineData("ue Bar – OFFICE-PC", "ue_bar_office_pc")]
    [InlineData("ue Bar – Sams Laptop", "ue_bar_sams_laptop")]
    [InlineData("ue Bar – Küche-PC", "ue_bar_kuche_pc")]
    [InlineData("ue Bar – Straße 2", "ue_bar_strasse_2")]
    public void Service_name_slug_like_home_assistant(string device, string slug) => Assert.Equal(slug, HaMobileApp.Slugify(device));

    // ------------------------------------------------------------------ Assist

    private static AssistService Assist(Env env, TimeSpan? timeout = null) =>
        new(env.Registry, new QuickCommandService(env.Registry, env.Log), env.Log, timeout);

    [Fact]
    public async Task Assist_uses_the_pipeline_and_keeps_the_conversation()
    {
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        var assist = Assist(env);

        var answer = await assist.AskAsync("Schalte das Desk-Licht ein");
        var second = await assist.AskAsync("Und jetzt aus");

        Assert.True(answer.Success);
        Assert.Equal("Antwort auf Schalte das Desk-Licht ein", answer.Text);
        Assert.True(second.Success);
        var runs = server.ReceivedOfType("assist_pipeline/run").ToList();
        Assert.Equal("intent", runs[0]["start_stage"]!.ToString());
        Assert.Equal("intent", runs[0]["end_stage"]!.ToString());
        Assert.Null(runs[0]["pipeline"]); // preferred pipeline
        Assert.Equal("conv-1", runs[1]["conversation_id"]!.ToString());
        Assert.Equal("Home Assistant", assist.ProviderName);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Assist_keeps_HAs_conversation_id_until_HAs_timeout_then_starts_a_new_one()
    {
        // M6-Fix 2: the core keeps the context ID, the module provides the timeout (HA: 5 min, chat_session.py).
        await using var server = new FakeHaServer();
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        var time = new ManualTime();
        var assist = new AssistService(env.Registry, new QuickCommandService(env.Registry, env.Log), env.Log, null, time);

        Assert.Equal(TimeSpan.FromMinutes(5), assist.ConversationTimeout);
        await assist.AskAsync("Schalte das Desk-Licht ein");
        Assert.Equal("conv-1", assist.ConversationId);

        time.Now += TimeSpan.FromSeconds(299);
        Assert.False(assist.IsConversationExpired());
        await assist.AskAsync("und jetzt wieder aus");

        time.Now += TimeSpan.FromMinutes(5); // timeout from the last message
        Assert.True(assist.IsConversationExpired());
        await assist.AskAsync("Wie warm ist es?");

        // The setting overrides the module's timeout; "New" drops the ID.
        assist.ConversationTimeoutOverride = () => TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.FromMinutes(1), assist.ConversationTimeout);
        time.Now += TimeSpan.FromSeconds(61);
        Assert.True(assist.IsConversationExpired());
        assist.NewConversation();
        Assert.Null(assist.ConversationId);
        Assert.False(assist.IsConversationExpired());
        await assist.AskAsync("Neue Frage");

        var runs = server.ReceivedOfType("assist_pipeline/run").ToList();
        Assert.Equal(4, runs.Count);
        Assert.Null(runs[0]["conversation_id"]);
        Assert.Equal("conv-1", runs[1]["conversation_id"]!.ToString());
        Assert.Null(runs[2]["conversation_id"]); // after 5 min: new conversation
        Assert.Null(runs[3]["conversation_id"]); // after "New"
    }

    [Fact]
    public async Task Assist_reports_errors_and_timeouts_and_falls_back_to_conversation_process()
    {
        await using var server = new FakeHaServer { Assist = FakeHaServer.AssistBehavior.Error };
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        var error = await Assist(env).AskAsync("Hallo");
        Assert.False(error.Success);
        Assert.Contains("LLM nicht erreichbar", error.Text);

        server.Assist = FakeHaServer.AssistBehavior.Silent;
        var timeout = await Assist(env, TimeSpan.FromMilliseconds(400)).AskAsync("Hallo?");
        Assert.False(timeout.Success);
        Assert.Contains("Keine Antwort", timeout.Text);
        Assert.Null(env.Registry.Find(HomeAssistantModule.ModuleId)!.LastError); // a time limit is not a module fault

        server.Assist = FakeHaServer.AssistBehavior.Unknown;
        var fallback = await Assist(env).AskAsync("Wie warm ist es?");
        Assert.True(fallback.Success);
        Assert.Equal("Antwort auf Wie warm ist es?", fallback.Text);
        Assert.Single(server.ReceivedOfType("conversation/process"));
    }

    // ------------------------------------------------------------------ Quick command

    [Fact]
    public async Task Plus_adds_to_the_shopping_list_also_from_assist()
    {
        await using var server = new FakeHaServer();
        server.AddState("todo.einkaufsliste", "3", "Einkaufsliste");
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());
        var commands = new QuickCommandService(env.Registry, env.Log);

        var match = commands.Parse("+ Testartikel")!;
        var result = await commands.ExecuteAsync(match);
        var viaAssist = await Assist(env).AskAsync("+Milch");

        Assert.True(result.Success);
        Assert.Contains("Testartikel", result.Message);
        Assert.True(viaAssist.Success);
        var calls = server.ReceivedOfType("call_service").Where(c => c["service"]!.ToString() == "add_item").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("todo", calls[0]["domain"]!.ToString());
        Assert.Equal("Testartikel", calls[0]["service_data"]!["item"]!.ToString());
        Assert.Equal("todo.einkaufsliste", calls[0]["target"]!["entity_id"]!.ToString());
        Assert.Equal("Milch", calls[1]["service_data"]!["item"]!.ToString());
        Assert.Empty(server.ReceivedOfType("assist_pipeline/run")); // the prefix does not go to Assist
    }

    [Fact]
    public async Task Without_the_default_list_the_first_todo_list_is_used()
    {
        await using var server = new FakeHaServer();
        server.AddState("todo.zzz", "0", "Später");
        server.AddState("todo.baumarkt", "0", "Baumarkt");
        await using var env = new Env(server);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        Assert.Equal("todo.baumarkt", env.Module.ResolveTodoList());
    }

    [Fact]
    public async Task A_chosen_todo_list_stays_and_no_list_at_all_gives_a_clear_answer()
    {
        // P2-1: no built-in default name any more; an existing choice (also the former default) is kept.
        await using var server = new FakeHaServer();
        server.AddState("todo.baumarkt", "0", "Baumarkt");
        server.AddState("todo.einkaufsliste", "3", "Einkaufsliste");
        await using var env = new Env(server);
        env.State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyTodoList, "todo.einkaufsliste");
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        Assert.Equal("todo.einkaufsliste", env.Module.ResolveTodoList());
        Assert.Equal("", HaSettings.DefaultTodoList);
        Assert.Equal("", HaSettings.TodoList(ModuleSettingValues.Empty));

        // A chosen list that no longer exists falls back to the first one.
        env.State.SetValue(HomeAssistantModule.ModuleId, HaSettings.KeyTodoList, "todo.deleted");
        await env.Registry.RestartModuleAsync(HomeAssistantModule.ModuleId);
        Assert.True(await env.Connected());
        Assert.True(await Wait.UntilAsync(() => env.Module.ResolveTodoList() == "todo.baumarkt"));
    }

    // ------------------------------------------------------------------ Now Playing

    private static HaEntityState Player(string id, string state, string? title, DateTimeOffset changed) =>
        new(id, state, title is null ? [] : new JsonObject { ["media_title"] = title, ["media_artist"] = "Künstler" }, changed);

    [Fact]
    public void Now_playing_prefers_playing_then_most_recent()
    {
        var now = DateTimeOffset.Now;
        var states = new Dictionary<string, HaEntityState>
        {
            ["media_player.tv"] = Player("media_player.tv", "paused", "Film", now),
            ["media_player.pc"] = Player("media_player.pc", "playing", "Song", now.AddHours(-1)),
            ["media_player.off"] = Player("media_player.off", "off", "Alt", now),
            ["media_player.leer"] = Player("media_player.leer", "playing", null, now),
        };

        var playing = HaNowPlaying.Choose(states.Keys, id => states.GetValueOrDefault(id), id => id)!;
        Assert.Equal("media_player.pc", playing.SourceId);
        Assert.True(playing.IsPlaying);
        Assert.Equal("Künstler", playing.Artist);

        states["media_player.pc"] = Player("media_player.pc", "paused", "Song", now.AddHours(-1));
        var recent = HaNowPlaying.Choose(states.Keys, id => states.GetValueOrDefault(id), id => id)!;
        Assert.Equal("media_player.tv", recent.SourceId); // zuletzt geändert
        Assert.False(recent.IsPlaying);

        Assert.Null(HaNowPlaying.Choose(["media_player.off"], id => states.GetValueOrDefault(id), id => id));
    }

    [Fact]
    public async Task Now_playing_follows_the_layout_players_live()
    {
        await using var server = new FakeHaServer();
        server.AddState("media_player.pc", "paused", "PC", new JsonObject { ["media_title"] = "Alt" });
        server.AddState("media_player.nicht_im_layout", "playing", "Andere", new JsonObject { ["media_title"] = "Fremd" });
        await using var env = new Env(server);
        await env.Registry.NotifyLayoutAsync(new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "m", Tiles = [new TileConfig { Id = "a", Module = "homeassistant", Type = "ha.media", Size = TileSize.Large, Entities = new() { ["main"] = "media_player.pc" } }] }],
        });
        var changes = 0;
        env.Module.NowPlayingChanged += (_, _) => Interlocked.Increment(ref changes);
        await env.Registry.StartAllAsync();
        Assert.True(await env.Connected());

        Assert.True(await Wait.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Alt"));
        await server.SetStateAsync("media_player.pc", "playing", new JsonObject { ["media_title"] = "Neu" });

        Assert.True(await Wait.UntilAsync(() => env.Module.CurrentNowPlaying is { Title: "Neu", IsPlaying: true }));
        Assert.True(Volatile.Read(ref changes) >= 2);

        await env.Module.ExecuteNowPlayingAsync(NowPlayingCommand.Next, CancellationToken.None);
        Assert.Contains(server.ReceivedOfType("call_service"), c => c["service"]!.ToString() == "media_next_track" && c["target"]!["entity_id"]!.ToString() == "media_player.pc");
    }
}
