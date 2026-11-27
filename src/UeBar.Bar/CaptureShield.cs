// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>How the windows are kept out of screen captures (P2-7).</summary>
public enum CaptureShieldMode
{
    /// <summary>Not possible on this Windows – the switch is greyed out.</summary>
    None,

    /// <summary><c>WDA_EXCLUDEFROMCAPTURE</c> (Windows 10 2004 and newer): captures show what is behind the window.</summary>
    Exclude,

    /// <summary><c>WDA_MONITOR</c> (older Windows): captures show a black area instead of the window.</summary>
    Black,
}

/// <summary>Which way this Windows offers, and the display affinity value for it (P2-7).</summary>
public static class CaptureShieldSupport
{
    public const uint AffinityNone = 0x00;
    public const uint AffinityMonitor = 0x01;
    public const uint AffinityExcludeFromCapture = 0x11;

    /// <summary>Windows 10 version 2004 (build 19041) brought <c>WDA_EXCLUDEFROMCAPTURE</c>.</summary>
    public const int ExcludeFromCaptureBuild = 19041;

    /// <summary>The best way for a Windows version (<see cref="Environment.OSVersion"/>).</summary>
    public static CaptureShieldMode Best(Version os)
    {
        ArgumentNullException.ThrowIfNull(os);
        if (os.Major > 10 || (os.Major == 10 && os.Build >= ExcludeFromCaptureBuild))
        {
            return CaptureShieldMode.Exclude;
        }

        // WDA_MONITOR exists since Windows 7 (6.1).
        return os.Major > 6 || (os.Major == 6 && os.Minor >= 1) ? CaptureShieldMode.Black : CaptureShieldMode.None;
    }

    public static uint Affinity(CaptureShieldMode mode) => mode switch
    {
        CaptureShieldMode.Exclude => AffinityExcludeFromCapture,
        CaptureShieldMode.Black => AffinityMonitor,
        _ => AffinityNone,
    };
}

/// <summary>What the shield needs from Windows – the app implements it with Win32, the tests with a fake.</summary>
public interface IShieldWindows
{
    /// <summary>All top-level windows of the own process; <paramref name="visibleOnly"/> = only those on the screen.</summary>
    IReadOnlyList<IntPtr> OwnTopLevelWindows(bool visibleOnly);

    /// <summary>A dialog that belongs to Windows (save file, message box) or a window owned by one – left alone.</summary>
    bool IsSystemDialog(IntPtr window);

    uint GetAffinity(IntPtr window);

    /// <summary>False if Windows refused (the reason goes to <see cref="LastError"/>).</summary>
    bool SetAffinity(IntPtr window, uint affinity);

    /// <summary>The Win32 error of the last refused <see cref="SetAffinity"/>.</summary>
    int LastError { get; }
}

/// <summary>Result of switching: what was used and how many windows took it.</summary>
public readonly record struct ShieldSwitchResult(bool Enabled, CaptureShieldMode Mode, int Windows, int Failed, int LastError);

/// <summary>
/// "Hide from screen captures" (P2-7) – the logic: every window of the process gets the display affinity while the
/// setting is on, the ones that exist when switching and every one that appears later (<see cref="WindowShown"/>).
/// Exceptions: windows registered with <see cref="Exempt"/> (the picking overlay of the own screenshot) and dialogs
/// that belong to Windows. If Windows refuses <c>WDA_EXCLUDEFROMCAPTURE</c> for a window, it gets <c>WDA_MONITOR</c>
/// (black on captures). Single-threaded (the UI thread); logging is the caller's job – once per switch.
/// </summary>
public sealed class CaptureShield
{
    private readonly IShieldWindows _windows;
    private readonly HashSet<IntPtr> _exempt = [];

    public CaptureShield(IShieldWindows windows, CaptureShieldMode best)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        Best = best;
    }

    /// <summary>The way this Windows offers (<see cref="CaptureShieldSupport.Best"/>).</summary>
    public CaptureShieldMode Best { get; }

    public bool IsAvailable => Best != CaptureShieldMode.None;

    public bool Enabled { get; private set; }

    /// <summary>The affinity a (not exempt) window of the app should have right now.</summary>
    public uint ExpectedAffinity => Enabled ? CaptureShieldSupport.Affinity(Best) : CaptureShieldSupport.AffinityNone;

    /// <summary>Windows that had to fall back to <c>WDA_MONITOR</c> since the last switch.</summary>
    public int BlackFallbacks { get; private set; }

    public bool IsExempt(IntPtr window) => _exempt.Contains(window);

    /// <summary>Leave this window as it is (call before it is shown). Taken back with <see cref="Release"/>.</summary>
    public void Exempt(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        _exempt.Add(window);
        if (_windows.GetAffinity(window) != CaptureShieldSupport.AffinityNone)
        {
            _windows.SetAffinity(window, CaptureShieldSupport.AffinityNone);
        }
    }

    /// <summary>The exempt window is gone (handles are reused by Windows).</summary>
    public void Release(IntPtr window) => _exempt.Remove(window);

    /// <summary>
    /// Switch on or off – applies at once to every open window. On: the visible ones (hidden ones get it when they
    /// appear). Off: every window that still has an affinity, visible or not.
    /// </summary>
    public ShieldSwitchResult SetEnabled(bool enabled)
    {
        if (enabled && !IsAvailable)
        {
            return new ShieldSwitchResult(false, CaptureShieldMode.None, 0, 0, 0);
        }

        Enabled = enabled;
        BlackFallbacks = 0;
        int done = 0, failed = 0, error = 0;
        foreach (var window in _windows.OwnTopLevelWindows(visibleOnly: enabled))
        {
            if (_exempt.Contains(window) || _windows.IsSystemDialog(window))
            {
                continue;
            }

            if (!enabled && _windows.GetAffinity(window) == CaptureShieldSupport.AffinityNone)
            {
                continue;
            }

            if (Apply(window))
            {
                done++;
            }
            else
            {
                failed++;
                error = _windows.LastError;
            }
        }

        return new ShieldSwitchResult(Enabled, enabled ? Best : CaptureShieldMode.None, done, failed, error);
    }

    /// <summary>A window of the process became visible (new or shown again). True if something was set.</summary>
    public bool WindowShown(IntPtr window)
    {
        if (!Enabled || window == IntPtr.Zero || _exempt.Contains(window) || _windows.IsSystemDialog(window))
        {
            return false;
        }

        return _windows.GetAffinity(window) != ExpectedAffinity && Apply(window);
    }

    private bool Apply(IntPtr window)
    {
        var wanted = ExpectedAffinity;
        if (_windows.SetAffinity(window, wanted))
        {
            return true;
        }

        // Refused: an older build or a window kind Windows does not exclude – black is the next best thing.
        if (wanted == CaptureShieldSupport.AffinityExcludeFromCapture && _windows.SetAffinity(window, CaptureShieldSupport.AffinityMonitor))
        {
            BlackFallbacks++;
            return true;
        }

        return false;
    }
}
