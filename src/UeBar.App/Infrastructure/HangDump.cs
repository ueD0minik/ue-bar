// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UeBar.Bar;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Hang dumps without a visible window (P2-7). Until 1.5.2 the runtime wrote them with its console program
/// <c>createdump.exe</c> – a terminal window popped up for it (always right after waking up, when the watchdog's
/// false alarms happened). Now the own exe writes the dump in a second, hidden start (<see cref="HangDumpCommand"/>):
/// a Windows program without a console, started with <c>CreateNoWindow</c>, which calls <c>MiniDumpWriteDump</c> on
/// the hanging process from outside – the same kind of dump as createdump's "normal" one (stacks of all threads).
/// </summary>
internal static class HangDump
{
    // createdump's "normal" dump on Windows: MiniDumpNormal | WithDataSegs | WithHandleData | WithThreadInfo.
    private const uint DumpType = 0x0000_0000 | 0x0000_0001 | 0x0000_0004 | 0x0000_1000;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessDupHandle = 0x0040;

    /// <summary>The hanging process (watchdog thread): start the helper and wait for it. Never on the UI thread.</summary>
    public static (bool Success, string Message) Write(string path, TimeSpan timeout)
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe)
        {
            return (false, "own exe not known");
        }

        using var helper = Process.Start(HangDumpCommand.StartInfo(exe, Environment.ProcessId, path));
        if (helper is null)
        {
            return (false, "helper did not start");
        }

        if (!helper.WaitForExit(timeout))
        {
            try
            {
                helper.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return (false, $"helper did not finish within {timeout.TotalSeconds:0} s");
        }

        return helper.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0
            ? (true, "")
            : (false, $"helper ended with code {helper.ExitCode}");
    }

    /// <summary>
    /// The helper start (<c>ueBar.exe --hang-dump &lt;pid&gt; &lt;file&gt;</c>): write the dump and return the exit code –
    /// 0 = written, otherwise the Win32 error. Runs before anything of the app (no log, no window, no single instance).
    /// </summary>
    public static int RunHelper(int processId, string path)
    {
        using var process = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessDupHandle, false, (uint)processId);
        if (process.IsInvalid)
        {
            return Marshal.GetLastWin32Error() is var e and > 0 ? e : 1;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            if (MiniDumpWriteDump(process, (uint)processId, file.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                return 0;
            }

            var error = Marshal.GetLastWin32Error();
            return error is > 0 ? error : 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 3;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(SafeProcessHandle process, uint processId, SafeFileHandle file, uint dumpType, IntPtr exception, IntPtr userStream, IntPtr callback);
}
