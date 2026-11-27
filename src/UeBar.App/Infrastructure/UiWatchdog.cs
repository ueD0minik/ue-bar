// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using UeBar.Bar;
using UeBar.Core.Logging;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Watchdog for the UI thread: every second it queues an empty task with input priority.
/// If it stays queued longer than <see cref="Threshold"/> – no matter whether the thread
/// is blocked or flooded by other tasks –, it logs which priorities are currently
/// filling the queue and saves a small memory dump with all thread stacks
/// (analyze with "dotnet-dump analyze … clrstack -all"). No timer on the UI thread.
/// Grace period (M5a): until the start has finished (and at least <see cref="StartupGrace"/> after process start) only
/// one warning line, no memory dump – a start right after logging in can take a minute (P2-6-Fix 2).
/// Sleep (P2-6-Fix 2): the decision is <see cref="HangClock"/> – while Windows sleeps nothing counts, and after a long
/// gap between the watchdog's own ticks (Modern Standby pauses the whole app) it starts over instead of reporting the
/// sleep as a hang. Failed dumps leave no empty file behind.
/// P2-7: the dump is written by a hidden second start of the own exe (<see cref="HangDump"/>) – the runtime's way
/// started the console program createdump.exe, and a terminal window popped up.
/// </summary>
internal sealed class UiWatchdog : IDisposable
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    /// <summary>A start that never finishes (error) does not keep the watchdog quiet for longer than this.</summary>
    public static readonly TimeSpan StartupLimit = TimeSpan.FromMinutes(5);

    /// <summary>Ticks come every second; a gap this long means the app (or Windows) did not run in between.</summary>
    public static readonly TimeSpan MaxTickGap = TimeSpan.FromSeconds(10);

    /// <summary>The hidden dump helper gets this long (P2-7).</summary>
    private static readonly TimeSpan DumpTimeout = TimeSpan.FromSeconds(60);

    private const int MaxDumps = 3;
    private const string DumpPattern = "haenger-*.dmp";

    private readonly DateTime _processStart = SafeProcessStart();
    private readonly Stopwatch _clockSource = Stopwatch.StartNew();
    private readonly Lock _lock = new();
    private readonly HangClock _clock = new(Threshold, MaxTickGap);
    private bool _quiet;
    private volatile bool _startupFinished;

    private readonly Dispatcher _dispatcher;
    private readonly IUeLogger _log;
    private readonly string _dumpDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly long[] _postedByPriority = new long[12];

    public UiWatchdog(Dispatcher dispatcher, IUeLogger log, string dumpDirectory)
    {
        _dispatcher = dispatcher;
        _log = log;
        _dumpDirectory = dumpDirectory;
        _dispatcher.Hooks.OperationPosted += OnPosted;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Start()
    {
        _ = Task.Run(CleanDumps);
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>The app has finished starting – from now on a hang gets a memory dump.</summary>
    public void StartupFinished() => _startupFinished = true;

    public void Dispose()
    {
        _cts.Cancel();
        _dispatcher.Hooks.OperationPosted -= OnPosted;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private TimeSpan Now => _clockSource.Elapsed;

    private void OnPosted(object? sender, DispatcherHookEventArgs e)
    {
        var index = (int)e.Operation.Priority;
        if (index >= 0 && index < _postedByPriority.Length)
        {
            Interlocked.Increment(ref _postedByPriority[index]);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _clock.Suspend();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _clock.Resume(Now);
            }
            else
            {
                return;
            }
        }

        _log.Info(null, $"UI watchdog: Windows {(e.Mode == PowerModes.Suspend ? "goes to sleep – paused" : "woke up – counting again")}");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                HangVerdict verdict;
                TimeSpan waiting;
                var send = false;
                lock (_lock)
                {
                    verdict = _clock.Tick(Now, out waiting);
                    if (!_clock.HasProbe && !_clock.IsSuspended)
                    {
                        _clock.ProbeSent(Now);
                        send = true;
                    }
                }

                if (send)
                {
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, OnProbeAnswered);
                }

                if (verdict == HangVerdict.Resumed)
                {
                    _log.Info(null, $"UI watchdog: the app did not run for {waiting.TotalSeconds:0} s (sleep or standby) – no hang, counting again");
                }
                else if (verdict == HangVerdict.Hang)
                {
                    var age = DateTime.Now - _processStart;
                    if (age < StartupGrace || (!_startupFinished && age < StartupLimit))
                    {
                        _quiet = true;
                        _log.Warn(null, $"UI thread not responding during startup for {waiting.TotalSeconds:0} s (no memory dump while starting)");
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
        TimeSpan? waited;
        lock (_lock)
        {
            waited = _clock.Answered(Now);
        }

        if (waited is { } w && w >= Threshold)
        {
            if (_quiet)
            {
                _quiet = false; // while starting it stays at the one warning line
            }
            else
            {
                _log.Warn(null, $"UI thread responds again (after {w.TotalSeconds:0.0} s)");
            }
        }

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

    /// <summary>Empty dumps (a dump that failed) go, and only the newest <see cref="MaxDumps"/> stay.</summary>
    internal void CleanDumps()
    {
        try
        {
            if (!Directory.Exists(_dumpDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(_dumpDirectory, DumpPattern).Select(f => new FileInfo(f)).ToList();
            var remove = files.Where(f => f.Length == 0)
                .Concat(files.Where(f => f.Length > 0).OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(MaxDumps))
                .ToList();
            foreach (var file in remove)
            {
                try
                {
                    file.Delete();
                    _log.Info(null, $"Old or empty hang dump removed: {file.Name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn(null, $"Hang dump could not be removed: {file.Name} ({ex.Message})");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Hang dumps could not be checked: {ex.Message}");
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

        Directory.CreateDirectory(_dumpDirectory);
        var path = Path.Combine(_dumpDirectory, $"haenger-{DateTime.Now:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.dmp");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var (success, message) = HangDump.Write(path, DumpTimeout);
            if (!success)
            {
                throw new IOException(message);
            }

            _log.Error(null, $"Stacks of all threads saved in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s (no window): {path} (analyze: dotnet-dump analyze <file> → clrstack -all)");
        }
        catch (Exception ex)
        {
            _log.Error(null, $"Memory dump for hang failed: {ex.Message}");
            try
            {
                File.Delete(path); // no empty file stays behind
            }
            catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
            {
                _log.Warn(null, $"Failed dump could not be removed: {deleteError.Message}");
            }
        }

        CleanDumps();
    }
}
