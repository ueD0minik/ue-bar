// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Sdk;
using UeBar.Core.Bus;
using UeBar.Core.Modules;
using UeBar.Core.Settings;

namespace UeBar.Core.Tests;

public class ModuleBusTests
{
    private static ModuleBus CreateBus(out ModuleGuard guard, params BusGrant[] grants)
    {
        guard = new ModuleGuard(new ListLogger());
        return new ModuleBus(guard, new GrantListPermissionPolicy(grants), new ListLogger());
    }

    private static BusGrant Grant(string consumer, string provider, BusAccess access) =>
        new() { Consumer = consumer, Provider = provider, Access = access };

    [Fact]
    public async Task Invoke_without_grant_is_denied_by_default()
    {
        var bus = CreateBus(out _);
        var called = false;
        bus.For("ha").PublishAction("ha.call_service", (_, _) =>
        {
            called = true;
            return Task.FromResult<JsonNode?>(null);
        });

        var result = await bus.For("win").InvokeAsync("ha.call_service");

        Assert.False(result.Success);
        Assert.Equal(BusErrorKind.Denied, result.Error);
        Assert.False(called);
    }

    [Fact]
    public async Task Invoke_with_grant_works_and_passes_arguments()
    {
        var bus = CreateBus(out _, Grant("win", "ha", BusAccess.Actions));
        BusCall? received = null;
        bus.For("ha").PublishAction("ha.call_service", (call, _) =>
        {
            received = call;
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });

        var result = await bus.For("win").InvokeAsync("ha.call_service", new JsonObject { ["service"] = "light.turn_on" });

        Assert.True(result.Success);
        Assert.True(result.Value!["ok"]!.GetValue<bool>());
        Assert.Equal("win", received!.CallerModuleId);
        Assert.Equal("light.turn_on", received.Arguments!["service"]!.GetValue<string>());
    }

    [Fact]
    public async Task Grant_is_per_direction()
    {
        var bus = CreateBus(out _, Grant("win", "ha", BusAccess.Actions));
        bus.For("win").PublishAction("win.set_volume", (_, _) => Task.FromResult<JsonNode?>(null));

        var result = await bus.For("ha").InvokeAsync("win.set_volume");

        Assert.Equal(BusErrorKind.Denied, result.Error);
    }

    [Fact]
    public async Task Grant_is_per_access_kind()
    {
        var bus = CreateBus(out _, Grant("win", "ha", BusAccess.Events));
        bus.For("ha").PublishAction("ha.call_service", (_, _) => Task.FromResult<JsonNode?>(null));

        var result = await bus.For("win").InvokeAsync("ha.call_service");

        Assert.Equal(BusErrorKind.Denied, result.Error);
    }

    [Fact]
    public async Task Own_actions_are_always_allowed()
    {
        var bus = CreateBus(out _);
        bus.For("ha").PublishAction("ha.ping", (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));

        var result = await bus.For("ha").InvokeAsync("ha.ping");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Unknown_action_returns_not_found()
    {
        var bus = CreateBus(out _);

        var result = await bus.For("win").InvokeAsync("gibts.nicht");

        Assert.Equal(BusErrorKind.NotFound, result.Error);
    }

    [Fact]
    public async Task Throwing_target_gives_clean_error_and_is_reported_as_fault()
    {
        var bus = CreateBus(out var guard, Grant("win", "ha", BusAccess.Actions));
        string? faultedModule = null;
        guard.ModuleFaulted += (_, e) => faultedModule = e.ModuleId;
        bus.For("ha").PublishAction("ha.call_service", (_, _) => throw new InvalidOperationException("HA kaputt"));

        var result = await bus.For("win").InvokeAsync("ha.call_service");

        Assert.False(result.Success);
        Assert.Equal(BusErrorKind.Failed, result.Error);
        Assert.Contains("HA kaputt", result.Message);
        Assert.Equal("ha", faultedModule); // the error is attributed to the target module, not the caller
    }

    [Fact]
    public async Task Unavailable_target_gives_module_unavailable()
    {
        var bus = CreateBus(out _, Grant("win", "ha", BusAccess.Actions));
        bus.IsModuleAvailable = id => id != "ha";
        bus.For("ha").PublishAction("ha.call_service", (_, _) => Task.FromResult<JsonNode?>(null));

        var result = await bus.For("win").InvokeAsync("ha.call_service");

        Assert.Equal(BusErrorKind.ModuleUnavailable, result.Error);
    }

    [Fact]
    public async Task Hanging_target_gives_timeout()
    {
        var guard = new ModuleGuard(new ListLogger());
        var bus = new ModuleBus(guard, new GrantListPermissionPolicy(Grant("win", "ha", BusAccess.Actions)), new ListLogger())
        {
            CallTimeout = TimeSpan.FromMilliseconds(100),
        };
        bus.For("ha").PublishAction("ha.slow", async (_, ct) =>
        {
            await Task.Delay(10_000, ct);
            return null;
        });

        var result = await bus.For("win").InvokeAsync("ha.slow");

        Assert.Equal(BusErrorKind.Timeout, result.Error);
    }

    [Fact]
    public async Task Target_module_in_error_state_gives_clean_error_through_registry()
    {
        await using var env = new TestEnv(null, null, Grant("win", "ha", BusAccess.Actions));
        var ha = new TestModule("ha")
        {
            OnStart = (ctx, _) =>
            {
                ctx.Bus.PublishAction("ha.call_service", (_, _) => throw new InvalidOperationException("Absturz"));
                return Task.CompletedTask;
            },
        };
        var win = new TestModule("win");
        env.Registry.Register(ha);
        env.Registry.Register(win);
        await env.Registry.StartAllAsync();

        var first = await win.Context!.Bus.InvokeAsync("ha.call_service");
        var second = await win.Context.Bus.InvokeAsync("ha.call_service");

        Assert.Equal(BusErrorKind.Failed, first.Error);
        Assert.Equal(BusErrorKind.ModuleUnavailable, second.Error);
        Assert.Equal(ModuleState.Error, env.Registry.Find("ha")!.Status.State);
        Assert.Equal(ModuleState.Connected, env.Registry.Find("win")!.Status.State);
    }

    [Fact]
    public void Publishing_someone_elses_id_is_rejected()
    {
        var bus = CreateBus(out _);
        bus.For("ha").PublishAction("shared.action", (_, _) => Task.FromResult<JsonNode?>(null));

        var result = bus.For("win").PublishAction("shared.action", (_, _) => Task.FromResult<JsonNode?>(null));

        Assert.Equal(BusErrorKind.Invalid, result.Error);
    }

    [Fact]
    public void Subscribe_event_without_grant_is_denied()
    {
        var bus = CreateBus(out _);
        bus.For("win").PublishEvent("win.fullscreen_started");

        var sub = bus.For("ha").SubscribeEvent("win.fullscreen_started", _ => { });

        Assert.False(sub.Success);
        Assert.Equal(BusErrorKind.Denied, sub.Result.Error);
    }

    [Fact]
    public async Task Subscribe_event_with_grant_receives_events()
    {
        var bus = CreateBus(out _, Grant("ha", "win", BusAccess.Events));
        bus.For("win").PublishEvent("win.fullscreen_started");
        var received = new TaskCompletionSource<BusEvent>();

        using var sub = bus.For("ha").SubscribeEvent("win.fullscreen_started", e => received.TrySetResult(e));
        bus.For("win").RaiseEvent("win.fullscreen_started", new JsonObject { ["app"] = "Spiel" });

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sub.Success);
        Assert.Equal("win", evt.SourceModuleId);
        Assert.Equal("Spiel", evt.Payload!["app"]!.GetValue<string>());
    }

    [Fact]
    public async Task Early_subscription_without_grant_gets_nothing()
    {
        var bus = CreateBus(out _);
        var received = false;

        // Subscription before the event is published (start order) …
        var sub = bus.For("ha").SubscribeEvent("win.fullscreen_started", _ => received = true);
        bus.For("win").PublishEvent("win.fullscreen_started");
        bus.For("win").RaiseEvent("win.fullscreen_started");
        await Task.Delay(200);

        // … is still checked on delivery.
        Assert.True(sub.Success);
        Assert.False(received);
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_affect_publisher()
    {
        var bus = CreateBus(out var guard, Grant("ha", "win", BusAccess.Events));
        var faulted = new TaskCompletionSource<string>();
        guard.ModuleFaulted += (_, e) => faulted.TrySetResult(e.ModuleId);
        bus.For("win").PublishEvent("win.evt");
        bus.For("ha").SubscribeEvent("win.evt", _ => throw new InvalidOperationException());

        var result = bus.For("win").RaiseEvent("win.evt");

        Assert.True(result.Success);
        Assert.Equal("ha", await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Values_need_grant()
    {
        var bus = CreateBus(out _, Grant("ha", "win", BusAccess.Values));
        bus.For("win").PublishValue("win.cpu_percent", JsonValue.Create(12.5));

        var allowed = bus.For("ha").GetValue("win.cpu_percent");
        var denied = bus.For("other").GetValue("win.cpu_percent");

        Assert.True(allowed.Success);
        Assert.Equal(12.5, allowed.Value!.GetValue<double>());
        Assert.Equal(BusErrorKind.Denied, denied.Error);
    }

    [Fact]
    public void Stopped_module_publications_disappear()
    {
        var bus = CreateBus(out _, Grant("win", "ha", BusAccess.All));
        bus.For("ha").PublishEvent("ha.state_changed");

        bus.RemoveModule("ha");

        Assert.Empty(bus.For("win").GetVisiblePublications());
    }

    [Fact]
    public void Visible_publications_respect_grants()
    {
        var bus = CreateBus(out _, Grant("win", "ha", BusAccess.Actions));
        bus.For("ha").PublishAction("ha.call_service", (_, _) => Task.FromResult<JsonNode?>(null));
        bus.For("ha").PublishEvent("ha.state_changed");

        var visible = bus.For("win").GetVisiblePublications();

        Assert.Single(visible);
        Assert.Equal("ha.call_service", visible[0].Id);
    }

    [Fact]
    public void Permission_requests_are_recorded_not_granted()
    {
        var policy = new GrantListPermissionPolicy();
        var bus = new ModuleBus(new ModuleGuard(new ListLogger()), policy, new ListLogger());

        bus.For("win").RequestPermission("ha", BusAccess.Actions, "Licht bei Vollbild dimmen");

        Assert.Single(policy.PendingRequests);
        Assert.False(policy.IsAllowed("win", "ha", BusAccess.Actions));
    }

    [Fact]
    public void Grants_are_read_from_settings_file()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "busGrants": [
                { "consumer": "win", "provider": "homeassistant", "access": "actions, events" }
              ]
            }
            """);
        var settings = new SettingsService(path, null, new ListLogger());

        settings.Load();

        Assert.True(settings.BusPolicy.IsAllowed("win", "homeassistant", BusAccess.Actions));
        Assert.True(settings.BusPolicy.IsAllowed("win", "homeassistant", BusAccess.Events));
        Assert.False(settings.BusPolicy.IsAllowed("win", "homeassistant", BusAccess.Values));
        Assert.False(settings.BusPolicy.IsAllowed("homeassistant", "win", BusAccess.Actions));
    }

    [Fact]
    public void Missing_settings_file_means_no_grants()
    {
        using var dir = new TempDirectory();
        var settings = new SettingsService(Path.Combine(dir.Path, "settings.json"), null, new ListLogger());

        settings.Load();

        Assert.False(settings.BusPolicy.IsAllowed("win", "homeassistant", BusAccess.Actions));
    }
}
