// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Logging;

namespace UeBar.Modules.HomeAssistant.Tests;

internal sealed class CollectingLogger : IModuleLogger, IUeLogger
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    public void Log(LogLevel level, string message, Exception? exception = null) => Add($"[{level}] {message} {exception}");

    public void Log(LogLevel level, string? moduleId, string message, Exception? exception = null) => Add($"[{level}] [{moduleId}] {message} {exception}");

    private void Add(string line)
    {
        lock (_lock)
        {
            _lines.Add(line);
        }
    }
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}

internal static class Fast
{
    /// <summary>Short times so tests run fast.</summary>
    public static HaClientOptions Options(bool loadRegistries = true) => new()
    {
        LocalTimeout = TimeSpan.FromMilliseconds(400),
        ExternalTimeout = TimeSpan.FromSeconds(2),
        Backoff = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
        // No keep-alive ping in a test unless it tests exactly that (P2-3): with 250 ms in real time a
        // busy machine answers too late, the client drops a healthy connection and a command running
        // meanwhile fails. Tests about missing pongs set short times themselves.
        PingInterval = TimeSpan.FromHours(1),
        PongTimeout = TimeSpan.FromSeconds(5),
        CommandTimeout = TimeSpan.FromSeconds(2),
        LocalRecheckInterval = TimeSpan.FromMilliseconds(400),
        NetworkDebounce = TimeSpan.FromMilliseconds(50),
        LoadRegistries = loadRegistries,
    };
}

/// <summary>Client + run in the background, cleans up properly.</summary>
internal sealed class RunningClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public RunningClient(HaSettings settings, IReadOnlySet<string>? wanted = null, HaClientOptions? options = null)
    {
        Client = new HaClient(options ?? Fast.Options(), Log);
        Client.StatusChanged += status =>
        {
            lock (_statuses)
            {
                _statuses.Add(status);
            }
        };
        Client.UpdateSettings(settings);
        if (wanted is not null)
        {
            Client.SetWantedEntities(wanted);
        }

        Run = Task.Run(() => Client.RunAsync(_cts.Token));
    }

    public CollectingLogger Log { get; } = new();

    public HaClient Client { get; }

    public Task Run { get; }

    public Task<bool> WaitForAsync(HaConnectionState state, int timeoutMs = 5000) =>
        Wait.UntilAsync(() => Client.Status.State == state, timeoutMs);

    private readonly List<HaStatus> _statuses = [];

    /// <summary>
    /// Waits until the client has gone through <paramref name="state"/> (recorded from StatusChanged –
    /// a state that lasts only a moment, like "disconnected" before the next attempt, is never missed)
    /// and returns that status, so its text belongs to the same state (P2-3).
    /// </summary>
    public async Task<HaStatus?> WaitForStatusAsync(HaConnectionState state, int timeoutMs = 10000)
    {
        HaStatus? found = null;
        await Wait.UntilAsync(() =>
        {
            lock (_statuses)
            {
                found = _statuses.LastOrDefault(s => s.State == state);
            }

            return found is not null;
        }, timeoutMs);
        return found;
    }

    /// <summary>Forget the recorded states (e.g. before provoking the next one).</summary>
    public void ClearStatuses()
    {
        lock (_statuses)
        {
            _statuses.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}
