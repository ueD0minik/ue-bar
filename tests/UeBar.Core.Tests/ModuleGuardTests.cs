// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using UeBar.Sdk;
using UeBar.Core.Modules;

namespace UeBar.Core.Tests;

public class ModuleGuardTests
{
    [Fact]
    public async Task Exception_is_caught_logged_and_reported()
    {
        var log = new ListLogger();
        var guard = new ModuleGuard(log);
        ModuleFaultEventArgs? fault = null;
        guard.ModuleFaulted += (_, e) => fault = e;

        var result = await guard.RunAsync("m1", "Test", _ => throw new InvalidOperationException("kaputt"));

        Assert.Equal(GuardOutcome.Faulted, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.NotNull(fault);
        Assert.Equal("m1", fault!.ModuleId);
        Assert.Contains(log.Entries, e => e.Module == "m1" && e.Message.Contains("kaputt"));
    }

    [Fact]
    public async Task Async_exception_is_caught()
    {
        var guard = new ModuleGuard(new ListLogger());

        var result = await guard.RunAsync<int>("m1", "Test", async ct =>
        {
            await Task.Yield();
            throw new FormatException();
        });

        Assert.Equal(GuardOutcome.Faulted, result.Outcome);
    }

    [Fact]
    public async Task Exception_in_fault_handler_does_not_escape()
    {
        var guard = new ModuleGuard(new ListLogger());
        guard.ModuleFaulted += (_, _) => throw new Exception("Handler kaputt");

        var result = await guard.RunAsync("m1", "Test", _ => throw new InvalidOperationException());

        Assert.Equal(GuardOutcome.Faulted, result.Outcome);
    }

    [Fact]
    public async Task Timeout_returns_quickly_for_hanging_async_call()
    {
        var guard = new ModuleGuard(new ListLogger());
        var sw = Stopwatch.StartNew();

        var result = await guard.RunAsync("m1", "Hängt", ct => Task.Delay(TimeSpan.FromSeconds(30), ct), TimeSpan.FromMilliseconds(100));

        Assert.Equal(GuardOutcome.TimedOut, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"dauerte {sw.Elapsed}");
    }

    [Fact]
    public async Task Timeout_also_protects_against_synchronously_blocking_module()
    {
        var guard = new ModuleGuard(new ListLogger());
        var sw = Stopwatch.StartNew();

        // The module blocks synchronously (e.g. .Result in its code) – the caller (UI thread) must not hang.
        var result = await guard.RunAsync("m1", "Blockiert", _ =>
        {
            Thread.Sleep(2000);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(100));

        Assert.Equal(GuardOutcome.TimedOut, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"dauerte {sw.Elapsed}");
    }

    [Fact]
    public async Task Timeout_is_not_reported_as_fault()
    {
        var guard = new ModuleGuard(new ListLogger());
        var faults = 0;
        guard.ModuleFaulted += (_, _) => faults++;

        await guard.RunAsync("m1", "Hängt", ct => Task.Delay(5000, ct), TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, faults);
    }

    [Fact]
    public void Sync_invoke_catches_exception()
    {
        var guard = new ModuleGuard(new ListLogger());

        var result = guard.Invoke<string>("m1", "Property", () => throw new NullReferenceException());

        Assert.Equal(GuardOutcome.Faulted, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Restart_limit_three_in_five_minutes_with_backoff()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T12:00:00Z"));
        var guard = new ModuleGuard(new ListLogger(), new GuardOptions(), time);

        Assert.Equal(TimeSpan.FromSeconds(2), guard.TryAcquireRestart("m1"));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(10), guard.TryAcquireRestart("m1"));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), guard.TryAcquireRestart("m1"));
        time.Advance(TimeSpan.FromSeconds(30));

        // 4th attempt within 5 minutes: refused
        Assert.Null(guard.TryAcquireRestart("m1"));
        Assert.Equal(3, guard.GetRestartCount("m1"));

        // another module is independent
        Assert.NotNull(guard.TryAcquireRestart("m2"));

        // allowed again after the window expired
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(guard.TryAcquireRestart("m1"));
    }

    [Fact]
    public void Reset_after_manual_restart_allows_new_attempts()
    {
        var guard = new ModuleGuard(new ListLogger());
        for (var i = 0; i < 3; i++)
        {
            guard.TryAcquireRestart("m1");
        }

        Assert.Null(guard.TryAcquireRestart("m1"));
        guard.ResetRestarts("m1");
        Assert.NotNull(guard.TryAcquireRestart("m1"));
    }

    [Fact]
    public async Task Registry_stops_auto_restart_after_limit_and_manual_restart_works()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T12:00:00Z"));
        var options = new GuardOptions { RestartDelays = [TimeSpan.FromSeconds(1)] };
        await using var env = new TestEnv(options, time);
        var module = new TestModule("crashy") { OnStart = (_, _) => throw new InvalidOperationException("Start kaputt") };
        var healthy = new TestModule("healthy");
        env.Registry.Register(module);
        env.Registry.Register(healthy);

        await env.Registry.StartAllAsync();
        var entry = env.Registry.Find("crashy")!;

        // Fast-forward time until the automatic restart is given up.
        var gaveUp = await Wait.UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return entry.AutoRestartGivenUp;
        });

        Assert.True(gaveUp);
        Assert.Equal(1 + 3, module.StartCount); // first start + 3 restarts
        Assert.Equal(ModuleState.Error, entry.Status.State);

        // no further attempt
        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);
        Assert.Equal(4, module.StartCount);

        // other modules run unaffected
        Assert.Equal(ModuleState.Connected, env.Registry.Find("healthy")!.Status.State);

        // a manual "Restart" tries again
        await env.Registry.RestartModuleAsync("crashy");
        Assert.Equal(5, module.StartCount);
        Assert.False(entry.AutoRestartGivenUp);
    }
}
