// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;

namespace UeBar.App.Interop;

/// <summary>Win32 for the extras (M6): global hotkey and Windows "Do not disturb".</summary>
internal static class ExtrasInterop
{
    public const int WM_HOTKEY = 0x0312;

    /// <summary>QUNS_QUIET_TIME: Windows reports "quiet" (e.g. the first hour after setup).</summary>
    private const int QUNS_QUIET_TIME = 6;

    /// <summary>WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED – active "Do not disturb" profile (0 = off).</summary>
    private const ulong WnfQuietHoursProfile = 0x0D83063EA3BF1C75;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope, out uint changeStamp, out int buffer, ref uint bufferSize);

    /// <summary>
    /// Is Windows "Do not disturb" on? Reads the active profile through WNF (other programs do it
    /// the same way; not officially documented) and additionally SHQueryUserNotificationState.
    /// Unknown → false (rather show than swallow silently).
    /// </summary>
    public static bool IsDoNotDisturbActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0 && state == QUNS_QUIET_TIME)
            {
                return true;
            }

            var name = WnfQuietHoursProfile;
            uint size = sizeof(int);
            return NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, out var profile, ref size) == 0 && size >= sizeof(int) && profile != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
