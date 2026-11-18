// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using Microsoft.Extensions.Time.Testing;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace UeBar.Modules.HomeAssistant.Tests;

public class HaClientTests
{
    private const string Token = "good-token";

    private static HaSettings Settings(Uri? local, Uri? external, string token = Token, bool preferLocal = true) =>
        new(local, external, token, preferLocal);

    private static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Connects_locally_with_valid_token()
    {
        await using var server = new FakeHaServer();
        await using var run = new RunningClient(Settings(server.BaseUrl, BlackholeServer.ClosedPortUrl()));

        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));
        Assert.Equal("Verbunden (lokal)", run.Client.Status.Text);
        Assert.Equal("2026.10.1", run.Client.HaVersion);
    }

    [Fact]
    public async Task Invalid_token_stops_without_retry_storm_until_settings_change()
    {
        await using var server = new FakeHaServer();
        await using var run = new RunningClient(Settings(server.BaseUrl, null, token: "falsch"));

        Assert.True(await run.WaitForAsync(HaConnectionState.AuthInvalid));
        Assert.Equal("Token ungültig", run.Client.Status.Text);
        await Task.Delay(800);
        Assert.Equal(1, server.AuthAttempts); // no constant firing

        run.Client.UpdateSettings(Settings(server.BaseUrl, null));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));
        Assert.Equal(2, server.AuthAttempts);
    }

    [Fact]
    public async Task Unreachable_local_falls_back_to_external()
    {
        await using var external = new FakeHaServer();
        await using var run = new RunningClient(Settings(BlackholeServer.ClosedPortUrl(), external.BaseUrl));

        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedExternal));
        Assert.Equal("Verbunden (extern)", run.Client.Status.Text);
    }

    [Fact]
    public async Task Hanging_local_times_out_quickly_and_uses_external()
    {
        using var hanging = new BlackholeServer();
        await using var external = new FakeHaServer();
        var sw = Stopwatch.StartNew();
        await using var run = new RunningClient(Settings(hanging.BaseUrl, external.BaseUrl));

        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedExternal));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"dauerte {sw.Elapsed}");
    }

    [Fact]
    public async Task Nothing_reachable_shows_disconnected_with_next_attempt_and_retries()
    {
        await using var run = new RunningClient(Settings(BlackholeServer.ClosedPortUrl(), BlackholeServer.ClosedPortUrl()));

        // State and text from the same status change: the client may already be trying again (P2-3).
        var disconnected = await run.WaitForStatusAsync(HaConnectionState.Disconnected);
        Assert.NotNull(disconnected);
        Assert.StartsWith("Getrennt – nächster Versuch in", disconnected.Text);
        Assert.True(await Wait.UntilAsync(() => run.Client.ConnectAttempts >= 6, 15000)); // 3 rounds × 2 URLs
    }

    [Fact]
    public async Task Not_configured_without_token()
    {
        await using var server = new FakeHaServer();
        await using var run = new RunningClient(new HaSettings(server.BaseUrl, null, null, true));

        Assert.True(await run.WaitForAsync(HaConnectionState.NotConfigured));
        await Task.Delay(200);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Subscribes_only_wanted_entities_and_applies_compact_deltas()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.desk", "on", "Desk", new JsonObject { ["brightness"] = 200, ["color_mode"] = "brightness" });
        server.AddState("switch.klima", "off", "Klima");
        server.AddState("light.other", "on", "Nicht im Layout");
        await using var run = new RunningClient(Settings(server.BaseUrl, null), Ids("light.desk", "switch.klima"));

        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));
        var store = run.Client.Store;
        Assert.True(await Wait.UntilAsync(() => store.Count == 2));

        var request = server.ReceivedOfType("subscribe_entities").Single();
        Assert.Equal(["light.desk", "switch.klima"], request["entity_ids"]!.AsArray().Select(n => n!.ToString()));
        Assert.Null(store.Get("light.other"));
        Assert.Equal("Desk", store.Get("light.desk")!.FriendlyName);

        // c: change state + attribute, remove an attribute
        await server.PushEntitiesEventAsync(new JsonObject
        {
            ["c"] = new JsonObject
            {
                ["light.desk"] = new JsonObject
                {
                    ["+"] = new JsonObject { ["s"] = "off", ["a"] = new JsonObject { ["brightness"] = 0 } },
                    ["-"] = new JsonObject { ["a"] = new JsonArray("color_mode") },
                },
            },
        });
        Assert.True(await Wait.UntilAsync(() => store.Get("light.desk")?.State == "off"));
        var desk = store.Get("light.desk")!;
        Assert.Equal(0, desk.Attribute<int>("brightness"));
        Assert.Null(desk.Attributes["color_mode"]);
        Assert.Equal("Desk", desk.FriendlyName); // unchanged attributes stay

        // r: removed
        await server.PushEntitiesEventAsync(new JsonObject { ["r"] = new JsonArray("switch.klima") });
        Assert.True(await Wait.UntilAsync(() => store.Get("switch.klima") is null));

        var recent = store.Recent(5);
        Assert.Contains(recent, c => c.EntityId == "light.desk" && c.OldState?.State == "on" && c.NewState?.State == "off");
    }

    [Fact]
    public async Task Changing_wanted_entities_resubscribes()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.desk", "on");
        server.AddState("light.decke", "off");
        await using var run = new RunningClient(Settings(server.BaseUrl, null), Ids("light.desk"));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        run.Client.SetWantedEntities(Ids("light.decke"));

        Assert.True(await Wait.UntilAsync(() => server.ReceivedOfType("subscribe_entities").Count() == 2));
        Assert.Single(server.ReceivedOfType("unsubscribe_events"));
        Assert.True(await Wait.UntilAsync(() => run.Client.Store.Get("light.decke") is not null && run.Client.Store.Get("light.desk") is null));
    }

    [Fact]
    public async Task Dropped_connection_reconnects_with_backoff()
    {
        await using var server = new FakeHaServer();
        await using var run = new RunningClient(Settings(server.BaseUrl, null));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        run.ClearStatuses();
        server.AcceptConnections = false;
        server.DropAll(); // HA restarts

        var disconnected = await run.WaitForStatusAsync(HaConnectionState.Disconnected);
        Assert.NotNull(disconnected);
        Assert.StartsWith("Getrennt", disconnected.Text);
        await Task.Delay(300);
        server.AcceptConnections = true;

        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));
        Assert.True(server.Connections >= 2);
    }

    [Fact]
    public async Task Missing_pong_is_detected_and_triggers_reconnect()
    {
        await using var server = new FakeHaServer();
        var options = Fast.Options() with { PingInterval = TimeSpan.FromMilliseconds(250), PongTimeout = TimeSpan.FromMilliseconds(250) };
        await using var run = new RunningClient(Settings(server.BaseUrl, null), options: options);
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        server.RespondToPing = false;

        Assert.True(await Wait.UntilAsync(() => server.Connections >= 2));
        Assert.Contains(run.Log.Lines, l => l.Contains("No pong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Call_service_reports_success_and_error()
    {
        await using var server = new FakeHaServer();
        server.CallServiceHandler = msg => msg["domain"]!.ToString() == "bad"
            ? new JsonObject { ["error"] = new JsonObject { ["code"] = "not_found", ["message"] = "Service not found." } }
            : null;
        await using var run = new RunningClient(Settings(server.BaseUrl, null));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        var ok = await run.Client.CallServiceAsync("light", "toggle", new JsonObject { ["brightness_pct"] = 50 }, new JsonObject { ["entity_id"] = "light.desk" }, CancellationToken.None);
        var bad = await run.Client.CallServiceAsync("bad", "x", null, null, CancellationToken.None);

        Assert.True(ok.Success);
        var sent = server.ReceivedOfType("call_service").First();
        Assert.Equal("light.desk", sent["target"]!["entity_id"]!.ToString());
        Assert.Equal(50, sent["service_data"]!["brightness_pct"]!.GetValue<int>());
        Assert.False(bad.Success);
        Assert.Equal("not_found", bad.ErrorCode);
        Assert.Equal("Service not found.", bad.ErrorMessage);
    }

    [Fact]
    public async Task Call_service_without_connection_fails_cleanly()
    {
        var client = new HaClient(Fast.Options(), new CollectingLogger());

        var result = await client.CallServiceAsync("light", "toggle", null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("not_connected", result.ErrorCode);
    }

    [Fact]
    public async Task Falls_back_to_get_states_and_state_changed()
    {
        await using var server = new FakeHaServer { SupportsSubscribeEntities = false };
        server.AddState("light.desk", "on");
        server.AddState("light.other", "on");
        await using var run = new RunningClient(Settings(server.BaseUrl, null), Ids("light.desk"));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        Assert.True(await Wait.UntilAsync(() => run.Client.Store.Get("light.desk")?.State == "on"));
        Assert.Null(run.Client.Store.Get("light.other"));

        await server.PushStateChangedAsync("light.desk", "off");
        await server.PushStateChangedAsync("light.other", "off");
        Assert.True(await Wait.UntilAsync(() => run.Client.Store.Get("light.desk")?.State == "off"));
        Assert.Null(run.Client.Store.Get("light.other"));
    }

    [Fact]
    public async Task Switches_back_to_local_when_it_becomes_reachable()
    {
        await using var local = new FakeHaServer { AcceptConnections = false };
        await using var external = new FakeHaServer();
        await using var run = new RunningClient(Settings(local.BaseUrl, external.BaseUrl));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedExternal));

        local.AcceptConnections = true;

        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal, 8000));
    }

    [Fact]
    public async Task Network_change_with_healthy_connection_does_not_reconnect()
    {
        // P2-3: deterministic. Root cause of the failures: on a busy machine the very first connection
        // attempt can miss its (real-time) local limit; the client then waits out its reconnect pause
        // on the *fake* clock – which nobody moved, so it never connected. Before, the test also drove
        // the fake clock in a real-time loop no matter what the client was doing.
        // Now: (1) until the session runs, the clock may move (it only shortens reconnect pauses);
        // (2) after the network events it moves only while the client waits for exactly the debounce
        // (nothing else on the fake clock is due – the next ping is an hour away); (3) the test checks
        // that the network change adds no connection. Real time only bounds the test.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        var options = Fast.Options() with { Time = time, PingInterval = TimeSpan.FromHours(1), PongTimeout = TimeSpan.FromHours(1) };
        await using var server = new FakeHaServer();
        await using var run = new RunningClient(Settings(server.BaseUrl, null), options: options);
        Assert.True(await Wait.UntilAsync(() =>
        {
            if (!run.Client.IsSessionReady)
            {
                time.Advance(TimeSpan.FromMilliseconds(250)); // a reconnect pause runs out
            }

            return run.Client.IsSessionReady && run.Client.Status.State == HaConnectionState.ConnectedLocal;
        }, 30000));
        var connections = server.Connections;

        for (var i = 0; i < 5; i++)
        {
            run.Client.NotifyNetworkChanged(); // several events shortly one after another
        }

        bool StillResponds() => run.Log.Lines.Any(l => l.Contains("connection still responds", StringComparison.Ordinal));
        var ok = await Wait.UntilAsync(() =>
        {
            if (run.Client.IsDebouncingNetworkChange)
            {
                time.Advance(options.NetworkDebounce);
            }

            return StillResponds();
        }, 30000);
        var diag = $"debouncing={run.Client.IsDebouncingNetworkChange} connections={server.Connections} state={run.Client.Status.State} :: " + string.Join(" || ", run.Log.Lines);
        Assert.True(ok, diag);
        Assert.True(connections == server.Connections, diag); // no new connection
        Assert.Equal(HaConnectionState.ConnectedLocal, run.Client.Status.State);
        Assert.Single(run.Log.Lines, l => l.Contains("Network change: waiting", StringComparison.Ordinal)); // the five events count as one
        Assert.DoesNotContain(run.Log.Lines, l => l.Contains("no longer responds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_change_with_dead_connection_reconnects_immediately()
    {
        await using var server = new FakeHaServer();
        var options = Fast.Options() with { PingInterval = TimeSpan.FromMinutes(5), PongTimeout = TimeSpan.FromMilliseconds(250) };
        await using var run = new RunningClient(Settings(server.BaseUrl, null), options: options);
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        server.RespondToPing = false;
        run.Client.NotifyNetworkChanged();

        Assert.True(await Wait.UntilAsync(() => server.Connections >= 2, 3000));
    }

    [Fact]
    public async Task Registry_index_supports_search_by_name_id_and_area()
    {
        await using var server = new FakeHaServer();
        server.AddState("light.office_desk", "on", "Desk");
        server.AddState("switch.kaffee", "off", "Kaffeemaschine");
        await using var run = new RunningClient(Settings(server.BaseUrl, null));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));

        var index = run.Client.Index;
        Assert.Equal(2, index.Count);
        Assert.Equal("light.office_desk", Assert.Single(index.Search("desk", 10)).EntityId);
        Assert.Equal("switch.kaffee", Assert.Single(index.Search("kaffee", 10)).EntityId);
        Assert.Single(index.Search("Office", 10));
        Assert.Equal("Office", index.Get("light.office_desk")!.Area);
    }

    [Fact]
    public async Task Token_never_appears_in_log()
    {
        await using var server = new FakeHaServer(validToken: "super-geheimes-token-123");
        await using (var bad = new RunningClient(Settings(server.BaseUrl, null, token: "falsches-geheimes-token-456")))
        {
            Assert.True(await bad.WaitForAsync(HaConnectionState.AuthInvalid));
            Assert.DoesNotContain(bad.Log.Lines, l => l.Contains("geheimes", StringComparison.Ordinal));
        }

        await using var run = new RunningClient(Settings(server.BaseUrl, BlackholeServer.ClosedPortUrl(), token: "super-geheimes-token-123"));
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));
        server.DropAll();
        Assert.True(await run.WaitForAsync(HaConnectionState.ConnectedLocal));
        Assert.DoesNotContain(run.Log.Lines, l => l.Contains("geheimes", StringComparison.Ordinal) || l.Contains("access_token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_reports_reachability_token_and_version()
    {
        await using var server = new FakeHaServer();

        var good = await HaClient.ProbeAsync(server.BaseUrl, Token, TimeSpan.FromSeconds(2), CancellationToken.None);
        var badToken = await HaClient.ProbeAsync(server.BaseUrl, "x", TimeSpan.FromSeconds(2), CancellationToken.None);
        var gone = await HaClient.ProbeAsync(BlackholeServer.ClosedPortUrl(), Token, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(good.TokenValid);
        Assert.Equal("2026.10.1", good.Version);
        Assert.True(badToken.Reachable);
        Assert.False(badToken.TokenValid);
        Assert.False(gone.Reachable);
    }
}
