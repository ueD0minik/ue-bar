// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;

namespace UeBar.App.Infrastructure;

/// <summary>
/// One clean-up after heavy work (P2-6, generalized in P2-6-Fix 2): screenshots (a frozen copy of every monitor),
/// clipboard images, the choice popup and the caret lookup (accessibility objects of other apps) leave native memory
/// behind that is freed only when the .NET objects holding it are finalized – and an idle app allocates nothing, so the
/// runtime would keep it for hours. 5 s after the last such work: collect, run the finalizers, collect again and give
/// freed memory back to Windows. Never in a loop; a new call within the 5 s only moves it.
/// </summary>
internal static class MemoryTrim
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);

    private static int _generation;

    public static IUeLogger? Log { get; set; }

    /// <summary>Asks for a clean-up once things have been quiet for <see cref="Quiet"/>.</summary>
    public static void Soon(string reason)
    {
        var generation = Interlocked.Increment(ref _generation);
        _ = Task.Delay(Quiet).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _generation) != generation)
            {
                return; // more work came – that one cleans up
            }

            var before = Private();
            GC.Collect();
            GC.WaitForPendingFinalizers(); // native image buffers, accessibility nodes, COM wrappers
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Log?.Debug(null, $"Memory cleaned after {reason}: private {before / (1024 * 1024)} → {Private() / (1024 * 1024)} MB");
        }, TaskScheduler.Default);
    }

    private static long Private()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
}
