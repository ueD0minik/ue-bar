// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Core.Modules;
using UeBar.Core.Privacy;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>P2-7: "hide from screen captures" through the quick command <c>privat</c> and the bus action.</summary>
public sealed class CapturePrivacyTests
{
    private sealed class FakeHiding(bool available = true) : ICaptureHiding
    {
        public List<string> Sources { get; } = [];

        public bool IsAvailable => available;

        public bool IsHidden { get; private set; }

        public Task<bool> SetHiddenAsync(bool hidden, string source)
        {
            Sources.Add(source);
            IsHidden = hidden;
            return Task.FromResult(hidden);
        }
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    [InlineData("umschalten", true, false)]
    [InlineData("toggle", false, true)]
    [InlineData("an", false, true)]
    [InlineData("ON", true, true)]
    [InlineData("aus", true, false)]
    [InlineData("off", false, false)]
    public void Wanted_state_from_the_text(string text, bool current, bool expected) =>
        Assert.Equal(expected, CapturePrivacyCommands.Wanted(text, current));

    [Fact]
    public void Unknown_text_is_no_state() => Assert.Null(CapturePrivacyCommands.Wanted("vielleicht", false));

    [Fact]
    public async Task Quick_command_privat_alone_switches_over_and_takes_on_and_off()
    {
        await using var env = new TestEnv();
        var hiding = new FakeHiding();
        var quick = new QuickCommandService(env.Registry, env.Log);
        CapturePrivacyCommands.Register(hiding, quick, env.Bus);

        var alone = quick.Parse("privat")!; // no text needed
        Assert.Equal((CapturePrivacyCommands.HostId, ""), (alone.ModuleId, alone.Argument));
        Assert.Null(quick.Parse("privatsache")); // only as a word

        var first = await quick.ExecuteAsync(alone);
        Assert.True(first.Success);
        Assert.True(hiding.IsHidden);
        Assert.Equal("Die ue Bar ist jetzt vor Bildschirmaufnahmen verborgen.", first.Message);

        Assert.True((await quick.ExecuteAsync(quick.Parse("privat")!)).Success);
        Assert.False(hiding.IsHidden);
        await quick.ExecuteAsync(quick.Parse("privat an")!);
        await quick.ExecuteAsync(quick.Parse("privat an")!);
        Assert.True(hiding.IsHidden); // "an" stays on
        Assert.False((await quick.ExecuteAsync(quick.Parse("privat bitte")!)).Success);
        Assert.True(hiding.IsHidden);
        Assert.All(hiding.Sources, s => Assert.Equal("quick command", s));
    }

    [Fact]
    public async Task Quick_command_says_so_if_windows_cannot_hide()
    {
        var hiding = new FakeHiding(available: false);

        var result = await CapturePrivacyCommands.RunCommandAsync(hiding, "");

        Assert.False(result.Success);
        Assert.False(hiding.IsHidden);
        Assert.Empty(hiding.Sources);
    }

    [Fact]
    public async Task Bus_action_switches_only_with_permission()
    {
        var log = new ListLogger();
        var guard = new ModuleGuard(log);
        var grants = new List<BusGrant>();
        var bus = new ModuleBus(guard, new GrantListPermissionPolicy(() => grants), log);
        using var dir = new TempDirectory();
        await using var registry = new ModuleRegistry(guard, bus, log, new Settings.InMemoryModuleStateStore(), dir.Path);
        var hiding = new FakeHiding();
        CapturePrivacyCommands.Register(hiding, new QuickCommandService(registry, log), bus);
        var caller = new TestModule("automation");
        registry.Register(caller);
        await registry.StartAllAsync();

        var denied = await caller.Context!.Bus.InvokeAsync(CapturePrivacyCommands.ActionId, new JsonObject { ["state"] = "on" });
        Assert.Equal(BusErrorKind.Denied, denied.Error);
        Assert.False(hiding.IsHidden);
        Assert.Contains(bus.GetAllPublications(), p => p.Id == CapturePrivacyCommands.ActionId && p.OwnerModuleId == CapturePrivacyCommands.HostId);

        grants.Add(new BusGrant { Consumer = "automation", Provider = CapturePrivacyCommands.HostId, Access = BusAccess.Actions });
        var on = await caller.Context.Bus.InvokeAsync(CapturePrivacyCommands.ActionId, new JsonObject { ["state"] = "on" });
        Assert.True(on.Success, on.Message);
        Assert.True(on.Value!["hidden"]!.GetValue<bool>());

        var toggled = await caller.Context.Bus.InvokeAsync(CapturePrivacyCommands.ActionId); // no arguments = switch over
        Assert.False(toggled.Value!["hidden"]!.GetValue<bool>());
        var asBool = await caller.Context.Bus.InvokeAsync(CapturePrivacyCommands.ActionId, new JsonObject { ["state"] = true });
        Assert.True(asBool.Value!["hidden"]!.GetValue<bool>());
        Assert.False((await caller.Context.Bus.InvokeAsync(CapturePrivacyCommands.ActionId, new JsonObject { ["state"] = "maybe" })).Success);
        Assert.True(hiding.IsHidden);
        Assert.Equal("bus (automation)", hiding.Sources[^1]);
    }

    [Fact]
    public async Task No_plugin_may_take_the_id_of_privacy()
    {
        await using var env = new TestEnv();

        Assert.Null(env.Registry.Register(new TestModule("privacy")));
        Assert.Contains(env.Log.Entries, e => e.Message.Contains("reserved ID 'privacy'", StringComparison.Ordinal));
    }
}
