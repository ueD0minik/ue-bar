// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using global::Windows.System.Power;

namespace UeBar.Modules.Windows.Platform;

/// <summary>
/// Battery (event driven through <c>PowerManager</c>), CPU/memory/disk (read on request – the module
/// asks only while a value is visible) and the brightness of the built-in display (WMI).
/// </summary>
internal sealed partial class SystemPlatform : ISystemPlatform
{
    /// <summary>A CPU baseline older than this is useless (average over a long time): take a fresh one.</summary>
    private static readonly TimeSpan BaselineMaxAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BaselineWait = TimeSpan.FromMilliseconds(300);

    private readonly Lock _lock = new();
    private (ulong Idle, ulong Total, long At)? _cpu;
    private bool _brightnessMissing;
    private bool _disposed;

    public SystemPlatform()
    {
        PowerManager.BatteryStatusChanged += OnPower;
        PowerManager.PowerSupplyStatusChanged += OnPower;
        PowerManager.RemainingChargePercentChanged += OnPower;
        PowerManager.RemainingDischargeTimeChanged += OnPower;
    }

    public event EventHandler? PowerChanged;

    public PowerState GetPower()
    {
        var status = PowerManager.BatteryStatus;
        if (status == BatteryStatus.NotPresent)
        {
            return PowerState.NoBattery;
        }

        var percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
        var onAc = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        var charging = status == BatteryStatus.Charging;
        var remaining = PowerManager.RemainingDischargeTime;
        TimeSpan? untilEmpty = !onAc && remaining > TimeSpan.Zero && remaining < TimeSpan.FromDays(2) ? remaining : null;
        TimeSpan? untilFull = null;
        if (charging)
        {
            try
            {
                var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
                if (report.ChargeRateInMilliwatts is > 0 and var rate
                    && report.FullChargeCapacityInMilliwattHours is { } full
                    && report.RemainingCapacityInMilliwattHours is { } left
                    && full > left)
                {
                    untilFull = TimeSpan.FromHours((full - left) / (double)rate);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // No report – without "until full" then.
            }
        }

        return new PowerState(true, percent, charging, onAc, untilEmpty, untilFull);
    }

    public async Task<SystemSample> SampleAsync(CancellationToken cancellationToken)
    {
        bool stale;
        lock (_lock)
        {
            stale = _cpu is not { } last || Stopwatch.GetElapsedTime(last.At) > BaselineMaxAge;
        }

        if (stale)
        {
            ReadCpu();
            await Task.Delay(BaselineWait, cancellationToken).ConfigureAwait(false);
        }

        var cpu = ReadCpu();

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref memory);
        var totalGb = memory.TotalPhys / 1073741824.0;
        var usedGb = (memory.TotalPhys - memory.AvailPhys) / 1073741824.0;

        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        double free = 0;
        double total = 0;
        try
        {
            var drive = new DriveInfo(root);
            free = drive.AvailableFreeSpace / 1073741824.0;
            total = drive.TotalSize / 1073741824.0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drive not ready – shows 0.
        }

        return new SystemSample(cpu, memory.MemoryLoad, usedGb, totalGb, root.TrimEnd('\\'), free, total);
    }

    /// <summary>CPU use since the previous call in percent.</summary>
    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = idleTime.Value;
        var total = kernelTime.Value + userTime.Value; // kernel time includes idle time
        lock (_lock)
        {
            var previous = _cpu;
            _cpu = (idle, total, Stopwatch.GetTimestamp());
            if (previous is not { } p || total <= p.Total)
            {
                return 0;
            }

            var busy = 1.0 - (idle - p.Idle) / (double)(total - p.Total);
            return Math.Clamp(busy * 100, 0, 100);
        }
    }

    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    if (item["Active"] is true && item["CurrentBrightness"] is byte level)
                    {
                        _brightnessMissing = false;
                        return level;
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // "Not supported": no built-in panel (desktop, lid closed, external monitor only).
        }
        catch (COMException)
        {
        }

        _brightnessMissing = true;
        return null;
    }

    public void SetBrightness(int percent)
    {
        if (_brightnessMissing)
        {
            return;
        }

        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (var item in searcher.Get().Cast<ManagementObject>())
        {
            using (item)
            {
                if (item["Active"] is true)
                {
                    item.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
                    return;
                }
            }
        }
    }

    private void OnPower(object? sender, object args) => PowerChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        PowerManager.BatteryStatusChanged -= OnPower;
        PowerManager.PowerSupplyStatusChanged -= OnPower;
        PowerManager.RemainingChargePercentChanged -= OnPower;
        PowerManager.RemainingDischargeTimeChanged -= OnPower;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>Lock, sleep, screen off, Windows settings pages.</summary>
internal sealed partial class ShellPlatform : IShellPlatform
{
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;

    public void Lock()
    {
        if (!LockWorkStation())
        {
            throw new InvalidOperationException($"LockWorkStation failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>
    /// Classic standby (S3) through SetSuspendState. Laptops with Modern Standby (S0 low power idle) do
    /// not offer it to programs (the call fails, or it would hibernate) – there the screen goes off
    /// instead, which lets Windows enter Modern Standby by itself (SPEC decision P2-2).
    /// </summary>
    public SleepResult Sleep()
    {
        if (!HasModernStandby())
        {
            EnableShutdownPrivilege();
            if (SetSuspendState(false, false, false))
            {
                return SleepResult.Sleeping;
            }
        }

        ScreenOff();
        return SleepResult.ScreenOff;
    }

    public void ScreenOff()
    {
        // Broadcast without waiting: a hanging window must not block us.
        if (!PostMessageW(new IntPtr(0xFFFF), WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(2)))
        {
            throw new InvalidOperationException($"Switching the screen off failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    public void OpenSettings(string uri)
    {
        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only ms-settings: pages", nameof(uri));
        }

        using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    /// <summary>
    /// SYSTEM_POWER_CAPABILITIES (winnt.h, level 4 = SystemPowerCapabilities): no S3 (offset 5) or
    /// AoAc "always on, always connected" (offset 20) = Modern Standby.
    /// </summary>
    internal static bool HasModernStandby()
    {
        var buffer = new byte[76];
        return CallNtPowerInformation(4, IntPtr.Zero, 0, buffer, (uint)buffer.Length) == 0 && (buffer[5] == 0 || buffer[20] != 0);
    }

    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028, out var token))
        {
            return;
        }

        try
        {
            if (LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
            {
                var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 };
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    [LibraryImport("powrprof.dll")]
    private static partial uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, [Out] byte[] output, uint outputLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint length, IntPtr previous, IntPtr returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
