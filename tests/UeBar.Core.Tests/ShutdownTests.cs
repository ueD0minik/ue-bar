// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using Microsoft.Extensions.Time.Testing;
using UeBar.Sdk;
using UeBar.Core.Modules;

namespace UeBar.Core.Tests;

/// <summary>Ordered exit with time limits (P2-1-Fix 1).</summary>
public class ShutdownTests
{
    private static FakeTimeProvider NewTime() => new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));

    [Fact]
    public async Task Steps_run_in_the_given_order_and_the_exit_is_logged_with_durations()
    {
        var log = new ListLogger();
        var order = new List<string>();
        var started = new List<string>();
        var sequence = new ShutdownSequence(log)
            .Add("tray", () => order.Add("tray"))
            .Add("bar", () => order.Add("bar"))
            .Add("plugins", async _ =>
            {
                await Task.Yield();
                order.Add("plugins");
            }, TimeSpan.FromSeconds(2))
            .Add("layout", () => order.Add("layout"));
        sequence.StepStarted += started.Add;

        var results = await sequence.RunAsync();

        Assert.Equal(["tray", "bar", "plugins", "layout"], order);
        Assert.Equal(order, started);
        Assert.Equal(order, results.Select(r => r.Name));
        Assert.All(results, r => Assert.Equal(ShutdownStepOutcome.Done, r.Outcome));
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("Exit finished in", StringComparison.Ordinal));
        Assert.Contains("tray ", line.Message);
        Assert.Contains("layout ", line.Message);
    }

    [Fact]
    public async Task A_hanging_step_is_cut_off_at_its_limit_and_the_next_steps_still_run()
    {
        var time = NewTime();
        var log = new ListLogger();
        var hangingToken = CancellationToken.None;
        var after = false;
        var sequence = new ShutdownSequence(log, time)
            .Add("plugins", ct =>
            {
                hangingToken = ct;
                return new TaskCompletionSource().Task; // never finishes
            }, TimeSpan.FromSeconds(2))
            .Add("layout", () => after = true);

        var run = sequence.RunAsync();
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ShutdownStepOutcome.TimedOut, results[0].Outcome);
        Assert.True(hangingToken.IsCancellationRequested);
        Assert.True(after);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'plugins' did not finish within 2000 ms"));
    }

    [Fact]
    public async Task A_failing_step_is_logged_and_does_not_stop_the_exit()
    {
        var log = new ListLogger();
        var after = false;
        var sequence = new ShutdownSequence(log)
            .Add("bar", () => throw new InvalidOperationException("window already gone"))
            .Add("plugins", _ => Task.FromException(new IOException("disk")), TimeSpan.FromSeconds(1))
            .Add("layout", () => after = true);

        var results = await sequence.RunAsync();

        Assert.Equal([ShutdownStepOutcome.Failed, ShutdownStepOutcome.Failed, ShutdownStepOutcome.Done], results.Select(r => r.Outcome));
        Assert.True(after);
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("failed – going on")));
    }

    [Fact]
    public async Task Registry_stops_all_plugins_at_once_and_leaves_one_behind_that_does_not_stop_within_two_seconds()
    {
        var time = NewTime();
        await using var env = new TestEnv(time: time);
        var quick = new TestModule("quick");
        var slow = new TestModule("slow") { OnStop = _ => new TaskCompletionSource().Task }; // ignores its token
        var third = new TestModule("third");
        env.Registry.Register(quick);
        env.Registry.Register(slow);
        env.Registry.Register(third);
        await env.Registry.StartAllAsync();

        var shutdown = env.Registry.ShutdownAsync(TimeSpan.FromSeconds(2));
        Assert.True(await Wait.UntilAsync(() => slow.StopCount == 1 && quick.StopCount == 1 && third.StopCount == 1));
        Assert.False(shutdown.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var results = await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ShutdownStepOutcome.Done, results.Single(r => r.Name == "quick").Outcome);
        Assert.Equal(ShutdownStepOutcome.Done, results.Single(r => r.Name == "third").Outcome);
        Assert.Equal(ShutdownStepOutcome.TimedOut, results.Single(r => r.Name == "slow").Outcome);
        Assert.Equal(ModuleState.Stopped, env.Registry.Find("quick")!.Status.State);
        Assert.False(env.Registry.Find("slow")!.IsRunning);
        Assert.True(slow.Context!.Stopping.IsCancellationRequested);
        Assert.Contains(env.Log.Entries, e => e.Module == "slow" && e.Message.Contains("did not stop within 2000 ms"));

        // A second call (DisposeAsync of the test environment) does nothing more.
        Assert.Empty(await env.Registry.ShutdownAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Registry_exit_does_not_wait_for_a_start_that_hangs()
    {
        var time = NewTime();
        await using var env = new TestEnv(time: time);
        var hanging = new TestModule("hanging")
        {
            OnStart = (_, _) => new TaskCompletionSource().Task, // ignores the token, holds the module
        };
        env.Registry.Register(hanging);
        _ = env.Registry.StartModuleAsync("hanging");
        Assert.True(await Wait.UntilAsync(() => hanging.StartCount == 1));

        var shutdown = env.Registry.ShutdownAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var results = await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ShutdownStepOutcome.TimedOut, Assert.Single(results).Outcome);
        Assert.True(hanging.Context!.Stopping.IsCancellationRequested);
        Assert.Contains(env.Log.Entries, e => e.Module == "hanging" && e.Message.Contains("still busy after 2000 ms"));
    }

    [Fact]
    public async Task Modules_that_are_not_loaded_are_skipped_at_exit()
    {
        await using var env = new TestEnv();
        env.Registry.Register(new TestModule("running"));
        env.Registry.RegisterUnavailable("absent", "Absent plugin", ModuleStatus.Disabled);
        await env.Registry.StartAllAsync();

        var results = await env.Registry.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["running"], results.Select(r => r.Name));
    }
}
