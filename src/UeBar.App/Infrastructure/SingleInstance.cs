// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows.Threading;
using UeBar.Sdk;
using UeBar.Core.Logging;

namespace UeBar.App.Infrastructure;

public enum OtherInstanceState
{
    /// <summary>The other instance answered within the time limit.</summary>
    Responsive,

    /// <summary>The other instance holds the lock but does not answer (hangs).</summary>
    NotResponding,
}

/// <summary>
/// One ue Bar per user session. Debug and release build deliberately share the same lock
/// (two bars at the same edge would disturb each other). The first instance answers a "ping"
/// through a named pipe – only after its UI thread really responded. A second instance
/// waits for it at most <see cref="HandshakeTimeout"/>; it never waits endlessly.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// For developers (P2-2): with its own data folder (UEBAR_DATA_DIR) ue Bar is a separate instance
    /// with its own lock – e.g. for screenshots with demo data while the normal ue Bar keeps running.
    /// Without the variable Debug and Release still share one lock (M2-Fix 1).
    /// </summary>
    private static readonly string DataDirSuffix =
        UeBar.Core.AppPaths.IsCustom
            ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(UeBar.Core.AppPaths.Default.Root.ToUpperInvariant())))[..12]
            : "";

    private static readonly string Suffix = $"{Environment.UserName}-{Process.GetCurrentProcess().SessionId}{DataDirSuffix}";
    private static readonly string MutexName = $@"Local\ueBar-SingleInstance{DataDirSuffix}";
    private static readonly string PipeName = $"ueBar-ping-{Suffix}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>A second instance reported itself (e.g. to show "already running").</summary>
    public event Action<string>? SecondInstanceStarted;

    /// <summary>Returns null if an instance already holds the lock.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstance(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // The predecessor crashed/was ended – the lock belongs to us now.
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the running instance whether it responds – with a time limit.</summary>
    public static async Task<(OtherInstanceState State, string? Reply)> PingOtherAsync(string variant)
    {
        using var cts = new CancellationTokenSource(HandshakeTimeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cts.Token);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync($"PING {variant} {Environment.ProcessId}".AsMemory(), cts.Token);
            var reply = await reader.ReadLineAsync(cts.Token);
            return reply is not null && reply.StartsWith("OK", StringComparison.Ordinal)
                ? (OtherInstanceState.Responsive, reply)
                : (OtherInstanceState.NotResponding, reply);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return (OtherInstanceState.NotResponding, null);
        }
    }

    /// <summary>Other ueBar processes of this session (for "end hanging instance").</summary>
    public static IReadOnlyList<Process> OtherProcesses()
    {
        var self = Process.GetCurrentProcess();
        return
        [
            .. Process.GetProcessesByName(self.ProcessName)
                .Where(p => p.Id != self.Id && SafeSessionId(p) == self.SessionId),
        ];
    }

    /// <summary>Answers pings; only answers once the UI thread has responded.</summary>
    public void StartServer(Dispatcher dispatcher, string variant, IUeLogger log) =>
        _ = Task.Run(() => ServeAsync(dispatcher, variant, log, _cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by the current thread – does not matter when ending.
        }

        _mutex.Dispose();
    }

    private async Task ServeAsync(Dispatcher dispatcher, string variant, IUeLogger log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                var request = await reader.ReadLineAsync(ct).AsTask().WaitAsync(HandshakeTimeout, ct);
                if (request?.StartsWith("PING", StringComparison.Ordinal) != true)
                {
                    continue;
                }

                // Only answer when the UI thread is really free.
                var uiAlive = await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input).Task
                    .WaitAsync(TimeSpan.FromMilliseconds(1500), ct)
                    .ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default);
                await writer.WriteLineAsync(uiAlive ? $"OK {variant} {Environment.ProcessId}" : "BUSY");
                if (uiAlive)
                {
                    _ = dispatcher.BeginInvoke(() => SecondInstanceStarted?.Invoke(request));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warn(null, $"Single-Instance-Pipe: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private static int SafeSessionId(Process p)
    {
        try
        {
            return p.SessionId;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
