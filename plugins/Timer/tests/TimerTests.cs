// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.Modules.Timer.Tests;

public sealed class EngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 18);

    [Fact]
    public void Two_timers_run_at_the_same_time_and_finish_in_order()
    {
        var engine = new TimerEngine();
        var pizza = engine.Start(TimeSpan.FromMinutes(10), "Pizza", T0);
        var pause = engine.Start(TimeSpan.FromMinutes(5), "Pause", T0);

        Assert.Empty(engine.Process(T0.AddMinutes(4), Today));
        Assert.Equal(TimeSpan.FromMinutes(6), pizza.Remaining(T0.AddMinutes(4)));

        var first = Assert.IsType<TimerFinished>(Assert.Single(engine.Process(T0.AddMinutes(5), Today)));
        Assert.Equal("Pause", first.Timer.Name);
        Assert.False(first.WhileAway);
        Assert.Equal(pizza.Id, Assert.Single(engine.Timers).Id);

        var second = Assert.IsType<TimerFinished>(Assert.Single(engine.Process(T0.AddMinutes(10), Today)));
        Assert.Equal("Pizza", second.Timer.Name);
        Assert.Empty(engine.Timers);
        Assert.Null(engine.NextDue());
    }

    [Fact]
    public void Pause_resume_reset_and_add()
    {
        var engine = new TimerEngine();
        var timer = engine.Start(TimeSpan.FromMinutes(10), null, T0);

        Assert.True(engine.Pause(timer.Id, T0.AddMinutes(3)));
        Assert.Equal(TimeSpan.FromMinutes(7), timer.Remaining(T0.AddHours(5))); // paused: does not run down
        Assert.Null(engine.NextDue());

        Assert.True(engine.Resume(timer.Id, T0.AddHours(5)));
        Assert.Equal(T0.AddHours(5).AddMinutes(7), timer.EndsAt);

        Assert.True(engine.Add(timer.Id, TimeSpan.FromMinutes(1), T0.AddHours(5)));
        Assert.Equal(TimeSpan.FromMinutes(8), timer.Remaining(T0.AddHours(5)));

        Assert.True(engine.Reset(timer.Id));
        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), timer.Remaining(T0));
        Assert.True(engine.Remove(timer.Id));
    }

    [Fact]
    public void Timer_over_midnight_and_a_standby_jump()
    {
        var engine = new TimerEngine();
        var late = new DateTimeOffset(2026, 11, 18, 23, 58, 0, TimeSpan.Zero);
        engine.Start(TimeSpan.FromMinutes(5), "Late", late);

        // The PC slept from 23:59 to 01:00 – on waking up the timer is reported with its real end.
        var finished = Assert.IsType<TimerFinished>(Assert.Single(engine.Process(new DateTimeOffset(2026, 11, 19, 1, 0, 0, TimeSpan.Zero), new DateOnly(2026, 11, 19))));
        Assert.True(finished.WhileAway);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 0, 3, 0, TimeSpan.Zero), finished.At);
    }

    [Fact]
    public void Pomodoro_goes_through_work_and_breaks_and_counts_the_day()
    {
        var engine = new TimerEngine { PomodoroSettings = new PomodoroSettings(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), 2) };
        engine.StartPomodoro(T0);
        Assert.Equal(PomodoroPhase.Work, engine.Pomodoro.Phase);

        var now = T0;
        var phases = new List<PomodoroPhase>();
        for (var i = 0; i < 4; i++)
        {
            now = engine.NextDue()!.Value;
            var change = Assert.IsType<PomodoroPhaseChanged>(Assert.Single(engine.Process(now, Today)));
            phases.Add(change.Next);
        }

        Assert.Equal([PomodoroPhase.ShortBreak, PomodoroPhase.Work, PomodoroPhase.LongBreak, PomodoroPhase.Work], phases);
        Assert.Equal(2, engine.CompletedToday(Today));
        Assert.Equal(T0.AddMinutes(25 + 5 + 25 + 15), now); // phases follow without drift
        Assert.Equal(1, engine.Pomodoro.Round);

        // The next day the counter starts at 0.
        Assert.Equal(0, engine.CompletedToday(Today.AddDays(1)));

        // Skipping does not count as finished work.
        engine.SkipPomodoroPhase(now, Today);
        Assert.Equal(2, engine.CompletedToday(Today));
        engine.StopPomodoro();
        Assert.Equal(PomodoroPhase.Idle, engine.Pomodoro.Phase);
        Assert.Equal(2, engine.CompletedToday(Today)); // stopping keeps the count
    }

    [Fact]
    public void Pomodoro_after_a_long_standby_starts_the_next_phase_now_instead_of_catching_up()
    {
        var engine = new TimerEngine();
        engine.StartPomodoro(T0);
        var wake = T0.AddHours(3);

        var change = Assert.IsType<PomodoroPhaseChanged>(Assert.Single(engine.Process(wake, Today)));

        Assert.True(change.WhileAway);
        Assert.Equal(T0.AddMinutes(25), change.At);
        Assert.Equal(PomodoroPhase.ShortBreak, engine.Pomodoro.Phase);
        Assert.Equal(wake.AddMinutes(5), engine.Pomodoro.EndsAt);
    }

    [Fact]
    public void Stopwatch_with_laps()
    {
        var engine = new TimerEngine();
        engine.StartStopwatch(T0);
        Assert.Equal(TimeSpan.FromSeconds(40), engine.Lap(T0.AddSeconds(40)));
        Assert.Equal(TimeSpan.FromSeconds(45), engine.Lap(T0.AddSeconds(85)));
        engine.PauseStopwatch(T0.AddSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(90), engine.Stopwatch.Elapsed(T0.AddHours(1)));
        Assert.Null(engine.Lap(T0.AddHours(1))); // no lap while paused

        Assert.Equal([(2, TimeSpan.FromSeconds(45)), (1, TimeSpan.FromSeconds(40))], engine.LapSplits());
        engine.StartStopwatch(T0.AddHours(1));
        Assert.Equal(TimeSpan.FromSeconds(100), engine.Stopwatch.Elapsed(T0.AddHours(1).AddSeconds(10)));
        engine.ResetStopwatch();
        Assert.Equal(TimeSpan.Zero, engine.Stopwatch.Elapsed(T0.AddHours(2)));
    }
}

public sealed class CommandTests
{
    [Theory]
    [InlineData("10", 600, null)]
    [InlineData("1:30", 5400, null)]
    [InlineData("25 Lernen", 1500, "Lernen")]
    [InlineData("25 Lernen für Mathe", 1500, "Lernen für Mathe")]
    [InlineData("90s", 90, null)]
    [InlineData("2,5", 150, null)]
    [InlineData("2.5", 150, null)]
    [InlineData("1h30", 5400, null)]
    [InlineData("1h", 3600, null)]
    [InlineData("45min Pause", 2700, "Pause")]
    public void Valid_input(string text, int seconds, string? name)
    {
        Assert.True(TimerCommand.TryParse(text, out var request));
        Assert.Equal(TimeSpan.FromSeconds(seconds), request.Duration);
        Assert.Equal(name, request.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("Lernen 25")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("25:00")]
    [InlineData("1:75")]
    [InlineData("1441")]
    public void Wrong_input_is_refused(string text) => Assert.False(TimerCommand.TryParse(text, out _));

    [Fact]
    public void Presets_from_the_setting()
    {
        Assert.Equal([5, 10, 90.0 / 60], TimerCommand.ParsePresets("5, 10, 90s, 10, x").Select(p => p.TotalMinutes));
        Assert.Equal(TimerCommand.Defaults, TimerCommand.ParsePresets("nonsense"));
    }

    [Fact]
    public void Quick_commands_are_recognized_without_catching_other_words()
    {
        var commands = new TimerModule().QuickCommands.Select(c => ("timer", c)).ToList();

        Assert.Equal("10", QuickCommandService.Parse("t 10", commands)!.Argument);
        Assert.Equal("25 Lernen", QuickCommandService.Parse("T 25 Lernen", commands)!.Argument);
        Assert.Null(QuickCommandService.Parse("toaster", commands)); // a word, not "t" + "oaster"
        Assert.Null(QuickCommandService.Parse("t", commands)); // "t" needs a duration
        Assert.Equal("", QuickCommandService.Parse("pomo", commands)!.Argument); // "pomo" works alone
        Assert.Equal("stop", QuickCommandService.Parse("pomo stop", commands)!.Argument);
        Assert.Null(QuickCommandService.Parse("pomodoro", commands));
        Assert.Null(QuickCommandService.Parse("tv", [("x", new QuickCommandInfo("t", "x"))]));
        Assert.Equal("Milch", QuickCommandService.Parse("+Milch", [("x", new QuickCommandInfo("+", "list"))])!.Argument); // symbol prefixes as before
    }
}

public sealed class ModuleTests
{
    [Fact]
    public async Task Quick_command_starts_a_named_timer_and_the_pill_shows_it()
    {
        await using var env = await new TimerEnv().StartAsync();

        var result = await env.Module.ExecuteQuickCommandAsync("t", "25 Lernen", default);

        Assert.True(result.Success);
        Assert.Equal("Lernen: 25 min", result.Message);
        var activity = env.Module.CurrentPillActivity!;
        Assert.Equal("Lernen", activity.Title);
        Assert.Equal("25:00", activity.Text);

        var bad = await env.Module.ExecuteQuickCommandAsync("t", "abc", default);
        Assert.False(bad.Success);
        Assert.Contains("t 1:30", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_runs_out_with_notification_sound_and_plus_five()
    {
        await using var env = await new TimerEnv().StartAsync();
        await env.SettingsAsync((TimerSettings.VolumeKey, "40"));
        await env.Module.ExecuteQuickCommandAsync("t", "10 Pizza", default);

        await env.AdvanceAsync(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));

        var notification = Assert.Single(await WaitForNotificationsAsync(env, 1));
        Assert.Equal("Pizza", notification.Title);
        Assert.Equal("10 min sind um", notification.Message);
        Assert.Equal(TimeSpan.FromSeconds(60), notification.Duration);
        Assert.Equal(UeNotification.DefaultSound, notification.Sound);
        Assert.Equal(0.4, notification.SoundVolume, 3);
        Assert.Equal(NotificationPriority.Important, notification.Priority); // P2-4: the bar decides the rest
        Assert.False(notification.IgnoreDoNotDisturb);
        Assert.False(notification.ShowInFullscreen);
        var plusFive = Assert.Single(notification.Actions!);
        Assert.Equal("+5 min", plusFive.Label);
        Assert.Null(env.Module.CurrentPillActivity);

        await env.Module.HandleNotificationActionAsync(notification.Id!, plusFive.Id, default);

        var again = Assert.Single(env.Module.With(e => e.Timers));
        Assert.Equal("Pizza", again.Name);
        Assert.Equal(TimeSpan.FromMinutes(5), again.Remaining(env.Time.GetUtcNow()));

        // A second press on the same notification does nothing.
        await env.Module.HandleNotificationActionAsync(notification.Id!, plusFive.Id, default);
        Assert.Single(env.Module.With(e => e.Timers));
    }

    [Fact]
    public async Task Running_timer_survives_a_restart_and_one_that_ran_out_meanwhile_is_reported()
    {
        await using var env = await new TimerEnv().StartAsync();
        await env.Module.ExecuteQuickCommandAsync("t", "10 Long", default);
        await env.Module.ExecuteQuickCommandAsync("t", "3 Short", default);

        await env.Registry.StopModuleAsync(TimerModule.ModuleId); // "ue Bar closed"
        env.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await env.Registry.StartModuleAsync(TimerModule.ModuleId));

        var away = Assert.Single(await WaitForNotificationsAsync(env, 1));
        Assert.Equal("Short", away.Title);
        Assert.Equal("Abgelaufen um 14:03", away.Message);
        var left = Assert.Single(env.Module.With(e => e.Timers));
        Assert.Equal("Long", left.Name);
        Assert.Equal(TimeSpan.FromMinutes(5), left.Remaining(env.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Display_ticks_only_while_visible()
    {
        await using var env = await new TimerEnv().StartAsync();
        var config = TimerEnv.Config("timer.running", TimerIds.Running);
        var tile = await env.Registry.CreateTileAsync(config);
        await env.Module.ExecuteQuickCommandAsync("t", "10", default);
        await Task.Delay(50);

        // Collapsed: the worker sleeps until the timer is due – no per-second updates.
        var before = env.Module.Ticks;
        await env.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.Equal(before, env.Module.Ticks);
        Assert.False(env.Module.IsTicking);

        // Panel open on the tile: once a second.
        await env.Registry.NotifyVisibilityAsync(true, [config], []);
        Assert.True(await Wait.UntilAsync(() => env.Module.Ticks > before));
        Assert.True(env.Module.IsTicking);
        var visible = env.Module.Ticks;
        await env.AdvanceAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.InRange(env.Module.Ticks - visible, 4, 8);
        Assert.True(await Wait.UntilAsync(() => tile.Find<ValueDisplayElement>("time")!.Value == "9:25"));

        // Only the pill out: the pill shows the time, so it ticks as well.
        await env.Registry.NotifyVisibilityAsync(false, [], [], barVisible: true);
        Assert.True(env.Module.IsTicking);

        // Collapsed again: stops.
        await env.Registry.NotifyVisibilityAsync(false, [], [], barVisible: false);
        Assert.False(env.Module.IsTicking);
    }

    [Fact]
    public async Task Tiles_start_pause_and_stop_timers()
    {
        await using var env = await new TimerEnv().StartAsync();
        var start = await env.TileAsync("timer.start", TimerIds.Start);
        var running = await env.TileAsync("timer.running", TimerIds.Running);

        await env.InteractAsync(start, "presets", InteractionKind.Select, text: "300");
        await env.InteractAsync(start, "custom", InteractionKind.Increment);
        await env.InteractAsync(start, "start", InteractionKind.Press, text: "start");

        Assert.Equal([5, 21], env.Module.With(e => e.Timers.Select(t => t.Duration.TotalMinutes).Order().ToList()));
        Assert.True(await Wait.UntilAsync(() => running.Find<ChoiceElement>("select")?.Options.Count == 2));
        Assert.Equal("5:00", running.Find<ValueDisplayElement>("time")!.Value); // the one due first

        await env.InteractAsync(running, "toggle", InteractionKind.Tap);
        Assert.False(env.Module.With(e => e.Timers.Single(t => t.Duration.TotalMinutes == 5).IsRunning));

        await env.InteractAsync(running, "controls", InteractionKind.Press, text: "stop");
        Assert.Single(env.Module.With(e => e.Timers));
    }

    [Fact]
    public async Task Pomodoro_tile_runs_through_phases_with_notifications()
    {
        await using var env = await new TimerEnv().StartAsync();
        await env.SettingsAsync((TimerSettings.WorkKey, "1"), (TimerSettings.ShortKey, "0.5"));
        var tile = await env.TileAsync("timer.pomodoro", TimerIds.Pomodoro);

        await env.InteractAsync(tile, "toggle", InteractionKind.Tap);
        Assert.Equal(PomodoroPhase.Work, env.Module.With(e => e.Pomodoro.Phase));

        await env.AdvanceAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20));
        var pause = Assert.Single(await WaitForNotificationsAsync(env, 1));
        Assert.Equal("Kurze Pause", pause.Title);
        Assert.Null(pause.Actions);
        Assert.True(await Wait.UntilAsync(() => tile.StateText == "Heute erledigt: 1"));

        await env.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        Assert.Equal("Weiter geht's", (await WaitForNotificationsAsync(env, 2))[1].Title);
        Assert.Equal(PomodoroPhase.Work, env.Module.With(e => e.Pomodoro.Phase));
    }

    [Fact]
    public async Task Stopwatch_tile()
    {
        await using var env = await new TimerEnv().StartAsync();
        var tile = await env.TileAsync("timer.stopwatch", TimerIds.Stopwatch);

        await env.InteractAsync(tile, "toggle", InteractionKind.Tap);
        env.Time.Advance(TimeSpan.FromSeconds(41));
        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "lap");
        env.Time.Advance(TimeSpan.FromSeconds(5));
        await env.InteractAsync(tile, "controls", InteractionKind.Press, text: "toggle");

        Assert.True(await Wait.UntilAsync(() => tile.Find<ValueDisplayElement>("time") is { Value: "0:46", Label: "R1 0:41" }));
    }

    private static async Task<IReadOnlyList<UeNotification>> WaitForNotificationsAsync(TimerEnv env, int count)
    {
        Assert.True(await Wait.UntilAsync(() => env.Notifications.Count >= count), $"only {env.Notifications.Count} notifications; {env.Time.GetUtcNow():T}; " + string.Join(" | ", env.Module.GetDiagnostics()) + " || " + string.Join(" | ", env.Log.Lines));
        return env.Notifications;
    }
}

public sealed class BusTests
{
    [Fact]
    public async Task Other_plugins_can_start_timers_and_hear_about_them_with_permission()
    {
        await using var env = new TimerEnv();
        var consumer = new ConsumerPlugin();
        env.Registry.Register(consumer);
        env.Grant("consumer", TimerModule.ModuleId, BusAccess.All);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(consumer.Id));

        var finished = new List<BusEvent>();
        using var subscription = consumer.Bus!.SubscribeEvent("timer.finished", e =>
        {
            lock (finished)
            {
                finished.Add(e);
            }
        });
        var started = await consumer.Bus.InvokeAsync("timer.start", new JsonObject { ["duration"] = "0:02", ["name"] = "Tee" });
        Assert.True(started.Success, started.Message);
        Assert.True((await consumer.Bus.InvokeAsync("timer.start", new JsonObject { ["seconds"] = 30 })).Success);
        Assert.Equal(2, env.Module.With(e => e.Timers.Count));

        await env.AdvanceAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));
        Assert.True(await Wait.UntilAsync(() =>
        {
            lock (finished)
            {
                return finished.Count == 2;
            }
        }));

        Assert.True((await consumer.Bus.InvokeAsync("pomodoro.start")).Success);
        Assert.Equal(PomodoroPhase.Work, env.Module.With(e => e.Pomodoro.Phase));
        Assert.True((await consumer.Bus.InvokeAsync("pomodoro.stop")).Success);
        Assert.Equal(PomodoroPhase.Idle, env.Module.With(e => e.Pomodoro.Phase));

        Assert.False((await consumer.Bus.InvokeAsync("timer.start", new JsonObject { ["minutes"] = 0 })).Success);
    }

    [Fact]
    public async Task Pomodoro_runs_the_chosen_action_only_while_it_is_permitted()
    {
        await using var env = new TimerEnv();
        var home = new ProviderPlugin();
        env.Registry.Register(home);
        Assert.True(await env.Registry.StartModuleAsync(home.Id));
        await env.StartAsync();

        // Without permission the action is not even offered.
        Assert.DoesNotContain(Choices(env), c => c.Value == "home|home.run");

        env.Grant(TimerModule.ModuleId, "home", BusAccess.Actions);
        Assert.Contains(Choices(env), c => c.Value == "home|home.run");
        await env.SettingsAsync((TimerSettings.WorkActionKey, "home|home.run"), (TimerSettings.WorkArgumentsKey, "{ \"entity\": \"focus\" }"));

        await env.Module.StartPomodoroAsync();
        Assert.Equal(1, home.Calls);
        Assert.Equal("focus", home.LastArguments!["entity"]!.GetValue<string>());

        // Permission revoked: nothing happens – and the timer asks for it again.
        env.Module.StopPomodoro();
        env.Grants.Clear();
        await env.Module.StartPomodoroAsync();
        Assert.Equal(1, home.Calls);
        var request = Assert.Single(env.Policy.PendingRequests);
        Assert.Equal((TimerModule.ModuleId, "home", BusAccess.Actions), (request.ConsumerModuleId, request.ProviderModuleId, request.Access));

        // Allowed again (the settings page does this): the request is settled, the next start runs it.
        env.Grant(TimerModule.ModuleId, "home", BusAccess.Actions);
        env.Policy.SettleRequests(TimerModule.ModuleId, "home");
        Assert.Empty(env.Policy.PendingRequests);
        env.Module.StopPomodoro();
        await env.Module.StartPomodoroAsync();
        Assert.Equal(2, home.Calls);
    }

    private static IReadOnlyList<SettingChoice> Choices(TimerEnv env) =>
        env.Module.SettingFields.Single(f => f.Key == TimerSettings.WorkActionKey).Choices!;
}

public sealed class CoreServiceTests
{
    [Fact]
    public void Pill_shows_the_activity_due_first()
    {
        var now = DateTimeOffset.Now;
        var a = new PillActivityEntry("a", new PillActivity("1", "Later", "9:00") { Due = now.AddMinutes(9) });
        var b = new PillActivityEntry("b", new PillActivity("2", "Soon", "1:00") { Due = now.AddMinutes(1) });
        var c = new PillActivityEntry("c", new PillActivity("3", "Whenever", "–"));

        Assert.Equal("Soon", PillActivityService.Choose([a, c, b])!.Activity.Title);
        Assert.Equal("Whenever", PillActivityService.Choose([c])!.Activity.Title);
        Assert.Null(PillActivityService.Choose([]));
    }

    [Fact]
    public async Task Pill_activity_service_follows_the_timer()
    {
        await using var env = await new TimerEnv().StartAsync();
        var service = new PillActivityService(env.Registry, new ListLogger());
        Assert.Null(service.Current);

        await env.Module.ExecuteQuickCommandAsync("t", "5 Tee", default);

        Assert.True(await Wait.UntilAsync(() => service.Current?.Activity.Title == "Tee"));
        Assert.Equal(TimerModule.ModuleId, service.Current!.ModuleId);
    }

    [Fact]
    public async Task Changed_settings_apply_live_without_a_restart_and_running_timers_go_on()
    {
        // P2-4: no flicker – the plugin keeps running, the tile shows the new presets right away.
        await using var env = await new TimerEnv().StartAsync();
        await env.Module.ExecuteQuickCommandAsync("t", "10", default);
        var start = await env.TileAsync("timer.start", TimerIds.Start);

        env.State.SetValue(TimerModule.ModuleId, TimerSettings.PresetsKey, "3, 7");
        Assert.True(await env.Registry.ApplyChangedSettingsAsync(TimerModule.ModuleId));

        Assert.True(await Wait.UntilAsync(() => start.Find<ChoiceElement>("presets")?.Options.Select(o => o.Label).SequenceEqual(["3", "7"]) == true));
        Assert.Single(env.Module.With(e => e.Timers.ToList()));
        Assert.DoesNotContain(env.Log.Lines, l => l.Contains("Manual restart", StringComparison.Ordinal));
    }
}
