// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

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
        PingInterval = TimeSpan.FromMilliseconds(250),
        PongTimeout = TimeSpan.FromMilliseconds(250),
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
