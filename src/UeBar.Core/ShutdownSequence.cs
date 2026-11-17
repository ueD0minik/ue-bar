// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;

namespace UeBar.Core;

/// <summary>Result of one step of a <see cref="ShutdownSequence"/>.</summary>
public sealed record ShutdownStepResult(string Name, TimeSpan Duration, ShutdownStepOutcome Outcome);

public enum ShutdownStepOutcome
{
    Done,

    /// <summary>The step did not finish within its limit – the sequence went on without it.</summary>
    TimedOut,

    /// <summary>The step threw – logged, the sequence went on.</summary>
    Failed,
}

/// <summary>
/// Ordered exit (P2-1-Fix 1): runs the steps one after the other, each at most for its limit, and
/// logs how long each one took. A step that hangs or throws never stops the steps after it.
/// Steps run on the caller's thread (no ConfigureAwait(false)): the app disposes UI objects in its
/// steps and needs the UI thread for that.
/// </summary>
public sealed class ShutdownSequence
{
    private readonly IUeLogger _log;
    private readonly TimeProvider _time;
    private readonly List<(string Name, Func<CancellationToken, Task> Run, TimeSpan? Limit)> _steps = [];

    public ShutdownSequence(IUeLogger log, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<string> StepNames => [.. _steps.Select(s => s.Name)];

    /// <summary>Raised on the caller's thread right before a step runs (the app tracks where an exit hangs).</summary>
    public event Action<string>? StepStarted;

    /// <summary>Synchronous step (dispose something). Runs inline, no limit.</summary>
    public ShutdownSequence Add(string name, Action step) =>
        Add(name, _ =>
        {
            step();
            return Task.CompletedTask;
        });

    /// <summary>Asynchronous step. With a limit the sequence waits at most that long for it.</summary>
    public ShutdownSequence Add(string name, Func<CancellationToken, Task> step, TimeSpan? limit = null)
    {
        _steps.Add((name, step, limit));
        return this;
    }

    public async Task<IReadOnlyList<ShutdownStepResult>> RunAsync()
    {
        var results = new List<ShutdownStepResult>(_steps.Count);
        var total = _time.GetTimestamp();
        foreach (var (name, run, limit) in _steps)
        {
            StepStarted?.Invoke(name);
            var start = _time.GetTimestamp();
            var outcome = await RunStepAsync(name, run, limit);
            results.Add(new ShutdownStepResult(name, _time.GetElapsedTime(start), outcome));
        }

        var elapsed = _time.GetElapsedTime(total);
        var detail = string.Join(", ", results.Select(r => $"{r.Name} {Ms(r.Duration)}{(r.Outcome == ShutdownStepOutcome.Done ? "" : $" ({r.Outcome})")}"));
        _log.Info(null, $"Exit finished in {Ms(elapsed)}: {detail}");
        return results;
    }

    private async Task<ShutdownStepOutcome> RunStepAsync(string name, Func<CancellationToken, Task> run, TimeSpan? limit)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            var task = run(cts.Token);
            if (limit is not { } max || task.IsCompleted)
            {
                await task;
                return ShutdownStepOutcome.Done;
            }

            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(max, _time, delayCts.Token);
            if (await Task.WhenAny(task, delay) == task)
            {
                await delayCts.CancelAsync();
                await task;
                return ShutdownStepOutcome.Done;
            }

            await cts.CancelAsync();
            _log.Warn(null, $"Exit: '{name}' did not finish within {Ms(max)} – going on without it");
            Observe(task);
            return ShutdownStepOutcome.TimedOut;
        }
        catch (Exception ex)
        {
            _log.Warn(null, $"Exit: '{name}' failed – going on", ex);
            return ShutdownStepOutcome.Failed;
        }
    }

    /// <summary>A step left behind must not show up later as an unobserved task exception.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    internal static string Ms(TimeSpan time) => $"{time.TotalMilliseconds:0} ms";
}
