// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using UeBar.Sdk;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Log sign of life (M5a): one minute after the start and then once per hour a line with uptime, memory, location and status
/// of all modules. That way a gap in the log (like on 05.11.) is noticed right away. Since M5-Fix 1 with GC heap and LOH; if private
/// memory is above <see cref="MemoryLimitBytes"/>, a warning line with the largest items follows (GC info API, no dump).
/// </summary>
internal sealed class Heartbeat(ModuleRegistry registry, IUeLogger log, LocationService? location = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// From here on something is to be done according to the SPEC (private memory; target below 100 MB,
    /// act from 150 MB since 09.11.2026 – before 80 MB, which warned on normal use, P2-4-Fix 1).
    /// </summary>
    public const long MemoryLimitBytes = 150L * 1024 * 1024;

    private Timer? _timer;

    /// <summary>First sign of life shortly after the start (baseline), then hourly.</summary>
    public static readonly TimeSpan FirstBeat = TimeSpan.FromMinutes(1);

    public void Start() => _timer = new Timer(_ => Beat(), null, FirstBeat, Interval);

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    internal void Beat()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            var gc = GC.GetGCMemoryInfo();
            var modules = string.Join("; ", registry.Modules.Select(m => $"{m.Id} = {m.Status.State}"));
            var where = location is null ? "" : $", location {LocationService.Describe(location.Current)}";
            var logError = log is FileLogger { LastWriteError: { } error } ? $", last write error: {error}" : "";
            log.Info(null,
                $"Sign of life: running for {(int)uptime.TotalHours} h {uptime.Minutes} min, " +
                $"RAM private {Mb(process.PrivateMemorySize64)} MB, working set {Mb(process.WorkingSet64)} MB, " +
                $"GC heap {Mb(gc.HeapSizeBytes)} MB (LOH {Mb(Generation(gc, 3))} MB, committed {Mb(gc.TotalCommittedBytes)} MB), " +
                $"CPU total {process.TotalProcessorTime.TotalSeconds:0} s{where} | {modules}{logError}");

            if (process.PrivateMemorySize64 > MemoryLimitBytes)
            {
                log.Warn(null, MemoryReport(process, gc));
            }
        }
        catch (Exception ex)
        {
            log.Warn(null, $"Sign of life failed: {ex.Message}");
        }
    }

    /// <summary>Largest items from the GC info API; the rest is native memory (WPF, runtime, images, DLL data).</summary>
    internal static string MemoryReport(Process process, GCMemoryInfo gc)
    {
        var managed = gc.TotalCommittedBytes;
        var posts = new (string Name, long Bytes)[]
        {
            ("Gen0", Generation(gc, 0)),
            ("Gen1", Generation(gc, 1)),
            ("Gen2", Generation(gc, 2)),
            ("LOH", Generation(gc, 3)),
            ("POH", Generation(gc, 4)),
            ("fragmentation", gc.FragmentedBytes),
            ("GC committed but free", Math.Max(0, managed - gc.HeapSizeBytes)),
            ("native/DLL data (rest)", Math.Max(0, process.PrivateMemorySize64 - managed)),
        };
        var top = string.Join(", ", posts.OrderByDescending(p => p.Bytes).Select(p => $"{p.Name} {Mb(p.Bytes)} MB"));
        return $"Memory above {Mb(MemoryLimitBytes)} MB: private {Mb(process.PrivateMemorySize64)} MB – {top}; " +
            $"threads {process.Threads.Count}, GC runs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
    }

    private static long Generation(GCMemoryInfo gc, int index) =>
        gc.GenerationInfo.Length > index ? gc.GenerationInfo[index].SizeAfterBytes : 0;

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
