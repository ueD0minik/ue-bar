// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Diagnostics.NETCore.Client;
using UeBar.Core.Logging;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Watchdog for the UI thread: every second it queues an empty task with input priority.
/// If it stays queued longer than <see cref="Threshold"/> – no matter whether the thread
/// is blocked or flooded by other tasks –, it logs which priorities are currently
/// filling the queue and saves a small memory dump with all thread stacks
/// (analyze with "dotnet-dump analyze … clrstack -all"). No timer on the UI thread.
/// Grace period (M5a): in the first <see cref="StartupGrace"/> after process start only one
/// warning line, no memory dump – the first start after a build is often slow.
/// </summary>
internal sealed class UiWatchdog : IDisposable
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);
    private const int MaxDumps = 3;

    private readonly DateTime _processStart = SafeProcessStart();
    private bool _quiet;

    private readonly Dispatcher _dispatcher;
    private readonly IUeLogger _log;
    private readonly string _dumpDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly long[] _postedByPriority = new long[12];
    private long _probeSentAt;
    private long _lastAnswerAt;
    private bool _reported;

    public UiWatchdog(Dispatcher dispatcher, IUeLogger log, string dumpDirectory)
    {
        _dispatcher = dispatcher;
        _log = log;
        _dumpDirectory = dumpDirectory;
        _dispatcher.Hooks.OperationPosted += OnPosted;
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        _dispatcher.Hooks.OperationPosted -= OnPosted;
    }

    private void OnPosted(object? sender, DispatcherHookEventArgs e)
    {
        var index = (int)e.Operation.Priority;
        if (index >= 0 && index < _postedByPriority.Length)
        {
            Interlocked.Increment(ref _postedByPriority[index]);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _lastAnswerAt = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Stopwatch.GetTimestamp();
                var sent = Interlocked.Read(ref _probeSentAt);
                if (sent == 0)
                {
                    Interlocked.Exchange(ref _probeSentAt, now);
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, OnProbeAnswered);
                    continue;
                }

                var waiting = Stopwatch.GetElapsedTime(sent, now);
                if (waiting >= Threshold && !_reported)
                {
                    _reported = true;
                    if (DateTime.Now - _processStart < StartupGrace)
                    {
                        _quiet = true;
                        _log.Warn(null, $"UI thread not responding during startup for {waiting.TotalSeconds:0} s (grace period {StartupGrace.TotalSeconds:0} s after start: no memory dump)");
                    }
                    else
                    {
                        Report(waiting);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(null, "UI watchdog ended", ex);
        }
    }

    private void OnProbeAnswered()
    {
        var sent = Interlocked.Exchange(ref _probeSentAt, 0);
        if (_reported && sent != 0)
        {
            _reported = false;
            if (_quiet)
            {
                _quiet = false; // in the grace period it stays at the one warning line
            }
            else
            {
                _log.Warn(null, $"UI thread responds again (after {Stopwatch.GetElapsedTime(sent).TotalSeconds:0.0} s)");
            }
        }

        _lastAnswerAt = Stopwatch.GetTimestamp();
    }

    private static DateTime SafeProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime;
        }
        catch (InvalidOperationException)
        {
            return DateTime.Now;
        }
    }

    private void Report(TimeSpan waiting)
    {
        var counts = _postedByPriority.Select((c, i) => (Priority: (DispatcherPriority)i, Count: Interlocked.Exchange(ref _postedByPriority[i], 0)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Select(x => $"{x.Priority}={x.Count}");
        _log.Error(null, $"UI thread not responding for {waiting.TotalSeconds:0} s (input is not processed). " +
            $"Tasks queued since start per priority: {string.Join(", ", counts)}");

        try
        {
            Directory.CreateDirectory(_dumpDirectory);
            foreach (var old in Directory.GetFiles(_dumpDirectory, "haenger-*.dmp").OrderByDescending(f => f).Skip(MaxDumps - 1))
            {
                File.Delete(old);
            }

            var path = Path.Combine(_dumpDirectory, $"haenger-{DateTime.Now:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.dmp");
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Normal, path, logDumpGeneration: false);
            _log.Error(null, $"Stacks of all threads saved: {path} (analyze: dotnet-dump analyze <file> → clrstack -all)");
        }
        catch (Exception ex)
        {
            _log.Error(null, $"Memory dump for hang failed: {ex.Message}");
        }
    }
}
