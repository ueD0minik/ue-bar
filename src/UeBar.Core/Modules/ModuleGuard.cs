// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;

namespace UeBar.Core.Modules;

public enum GuardOutcome
{
    Success,

    /// <summary>Module threw an exception.</summary>
    Faulted,

    /// <summary>Time limit exceeded.</summary>
    TimedOut,

    /// <summary>Cancelled by the caller.</summary>
    Canceled,

    /// <summary>Call not executed at all (e.g. placeholder tile).</summary>
    Rejected,
}

public sealed record GuardResult(GuardOutcome Outcome, Exception? Exception = null, string? Message = null)
{
    public bool Succeeded => Outcome == GuardOutcome.Success;

    public static GuardResult Ok { get; } = new(GuardOutcome.Success);

    public static GuardResult Reject(string message) => new(GuardOutcome.Rejected, null, message);
}

public sealed record GuardResult<T>(GuardOutcome Outcome, T? Value, Exception? Exception = null)
{
    public bool Succeeded => Outcome == GuardOutcome.Success;
}

public sealed class ModuleFaultEventArgs(string moduleId, string operation, Exception exception) : EventArgs
{
    public string ModuleId { get; } = moduleId;

    public string Operation { get; } = operation;

    public Exception Exception { get; } = exception;
}

public sealed class GuardOptions
{
    /// <summary>Time limit for normal calls (e.g. from the UI thread).</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Time limit for start/stop.</summary>
    public TimeSpan LifecycleTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Max. automatic restarts within <see cref="RestartWindow"/>.</summary>
    public int MaxRestarts { get; init; } = 3;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the 1st, 2nd, 3rd … restart (backoff).</summary>
    public IReadOnlyList<TimeSpan> RestartDelays { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
}

/// <summary>
/// Central fault protection (SPEC 4.6): every call from the core into a module goes through here.
/// Catches exceptions, logs them with the module ID, reports them through <see cref="ModuleFaulted"/>,
/// enforces time limits and keeps track of automatic restarts.
/// Asynchronous calls run on the thread pool – so even a synchronously blocking module
/// does not freeze the UI thread.
/// </summary>
public sealed class ModuleGuard
{
    private readonly IUeLogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, List<DateTimeOffset>> _restarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public ModuleGuard(IUeLogger log, GuardOptions? options = null, TimeProvider? time = null)
    {
        _log = log;
        Options = options ?? new GuardOptions();
        _time = time ?? TimeProvider.System;
    }

    public GuardOptions Options { get; }

    public TimeProvider Time => _time;

    /// <summary>A module threw an exception. Handler errors are swallowed and logged.</summary>
    public event EventHandler<ModuleFaultEventArgs>? ModuleFaulted;

    public async Task<GuardResult> RunAsync(
        string moduleId,
        string operation,
        Func<CancellationToken, Task> call,
        TimeSpan? timeout = null,
        bool reportFaults = true,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync<bool>(
            moduleId,
            operation,
            async ct =>
            {
                await call(ct).ConfigureAwait(false);
                return true;
            },
            timeout,
            reportFaults,
            cancellationToken).ConfigureAwait(false);

        return new GuardResult(result.Outcome, result.Exception, result.Exception?.Message);
    }

    public async Task<GuardResult<T>> RunAsync<T>(
        string moduleId,
        string operation,
        Func<CancellationToken, Task<T>> call,
        TimeSpan? timeout = null,
        bool reportFaults = true,
        CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? Options.DefaultTimeout;
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task.Run: synchronous exceptions and synchronous blocking do not reach the caller.
        var task = Task.Run(() => call(callCts.Token), CancellationToken.None);

        if (limit != Timeout.InfiniteTimeSpan)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(limit, _time, delayCts.Token);
            var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (finished != task)
            {
                callCts.Cancel();
                if (cancellationToken.IsCancellationRequested)
                {
                    ObserveLate(moduleId, operation, task);
                    return new GuardResult<T>(GuardOutcome.Canceled, default);
                }

                _log.Warn(moduleId, $"Time limit exceeded ({limit.TotalMilliseconds:0} ms): {operation}");
                ObserveLate(moduleId, operation, task);
                return new GuardResult<T>(GuardOutcome.TimedOut, default, new TimeoutException($"{operation}: time limit exceeded"));
            }

            delayCts.Cancel();
        }

        try
        {
            var value = await task.ConfigureAwait(false);
            return new GuardResult<T>(GuardOutcome.Success, value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new GuardResult<T>(GuardOutcome.Canceled, default);
        }
        catch (Exception ex)
        {
            if (reportFaults)
            {
                ReportFault(moduleId, operation, ex);
            }
            else if (ex is Abstractions.LayoutStoreUnavailableException)
            {
                _log.Info(moduleId, $"{operation}: {ex.Message}"); // expected (offline), no stack
            }
            else
            {
                _log.Warn(moduleId, $"Error (not reported) in {operation}: {ex.Message}", ex);
            }

            return new GuardResult<T>(GuardOutcome.Faulted, default, ex);
        }
    }

    /// <summary>Synchronous call for quick things (property access). No time limit.</summary>
    public GuardResult<T> Invoke<T>(string moduleId, string operation, Func<T> call, bool reportFaults = true)
    {
        try
        {
            return new GuardResult<T>(GuardOutcome.Success, call());
        }
        catch (Exception ex)
        {
            if (reportFaults)
            {
                ReportFault(moduleId, operation, ex);
            }
            else if (ex is Abstractions.LayoutStoreUnavailableException)
            {
                _log.Info(moduleId, $"{operation}: {ex.Message}"); // expected (offline), no stack
            }
            else
            {
                _log.Warn(moduleId, $"Error (not reported) in {operation}: {ex.Message}", ex);
            }

            return new GuardResult<T>(GuardOutcome.Faulted, default, ex);
        }
    }

    public GuardResult Invoke(string moduleId, string operation, Action call, bool reportFaults = true)
    {
        var r = Invoke(moduleId, operation, () =>
        {
            call();
            return true;
        }, reportFaults);
        return new GuardResult(r.Outcome, r.Exception, r.Exception?.Message);
    }

    /// <summary>Attribute an error to a module (also for background tasks).</summary>
    public void ReportFault(string moduleId, string operation, Exception exception)
    {
        _log.Error(moduleId, $"Exception in {operation}: {exception.Message}", exception);
        var handlers = ModuleFaulted;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ModuleFaultEventArgs>>())
        {
            try
            {
                handler(this, new ModuleFaultEventArgs(moduleId, operation, exception));
            }
            catch (Exception ex)
            {
                _log.Error(null, "Error in fault handler", ex);
            }
        }
    }

    /// <summary>
    /// Books an automatic restart. Returns the delay before it, or null if the
    /// limit (<see cref="GuardOptions.MaxRestarts"/> in <see cref="GuardOptions.RestartWindow"/>) is reached.
    /// </summary>
    public TimeSpan? TryAcquireRestart(string moduleId)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_restarts.TryGetValue(moduleId, out var list))
            {
                list = [];
                _restarts[moduleId] = list;
            }

            list.RemoveAll(t => now - t >= Options.RestartWindow);
            if (list.Count >= Options.MaxRestarts)
            {
                return null;
            }

            var delay = Options.RestartDelays.Count == 0
                ? TimeSpan.Zero
                : Options.RestartDelays[Math.Min(list.Count, Options.RestartDelays.Count - 1)];
            list.Add(now);
            return delay;
        }
    }

    /// <summary>Number of automatic restarts in the current time window.</summary>
    public int GetRestartCount(string moduleId)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            return _restarts.TryGetValue(moduleId, out var list)
                ? list.Count(t => now - t < Options.RestartWindow)
                : 0;
        }
    }

    /// <summary>After a manual "Restart": reset the counter.</summary>
    public void ResetRestarts(string moduleId)
    {
        lock (_lock)
        {
            _restarts.Remove(moduleId);
        }
    }

    private void ObserveLate(string moduleId, string operation, Task task)
    {
        // If the task still faults after the time limit, only log it –
        // it must not stay unobserved.
        task.ContinueWith(
            t =>
            {
                if (t.Exception is { } ex && ex.InnerException is not OperationCanceledException)
                {
                    _log.Warn(moduleId, $"Late exception after time limit in {operation}: {ex.InnerException?.Message}", ex.InnerException);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
