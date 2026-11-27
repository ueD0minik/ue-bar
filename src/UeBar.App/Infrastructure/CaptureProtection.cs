// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using UeBar.App.Settings;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Privacy;

namespace UeBar.App.Infrastructure;

/// <summary>
/// "Hide from screen captures" (P2-7) for every window of the process – bar with pill, pins, the Win+V choice,
/// quick search, settings, welcome, own notifications and feedback, context menus, tooltips and popups (they are
/// windows of their own), the tray menu, dialogs of the app. One place for all, only while the setting is on: a
/// thread hook on the UI thread sees every window right before Windows shows it (so not even the first frame is on a
/// capture), and a WinEvent hook on <c>EVENT_OBJECT_SHOW</c> for the own process catches windows of other threads;
/// both hand the window to <see cref="CaptureShield"/>. Switching applies at once to all open windows. Plugins do
/// nothing for it.
/// Not touched: the picking overlay of the own screenshot (it only shows the frozen picture, in which the hidden
/// windows are already missing) and dialogs that belong to Windows (save file, message boxes).
/// Logged only when switching (on/off, result, number of windows), never per window.
/// </summary>
internal sealed class CaptureProtection : ICaptureHiding, IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const int WhCallWndProc = 4;
    private const uint WmShowWindow = 0x0018;
    private const uint WmWindowPosChanging = 0x0046;
    private const int SwpShowWindow = 0x0040;

    /// <summary>WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags.</summary>
    private static readonly int WindowPosFlagsOffset = (2 * IntPtr.Size) + (4 * sizeof(int));

    private readonly Dispatcher _dispatcher;
    private readonly GeneralSettingsStore _general;
    private readonly IUeLogger _log;
    private readonly CaptureShield _shield;
    private readonly WinEventProc _callback;
    private readonly HookProc _showCallback;
    private IntPtr _hook;
    private IntPtr _showHook;

    public CaptureProtection(Dispatcher dispatcher, GeneralSettingsStore general, IUeLogger log)
        : this(dispatcher, general, log, CaptureShieldSupport.Best(Environment.OSVersion.Version))
    {
    }

    internal CaptureProtection(Dispatcher dispatcher, GeneralSettingsStore general, IUeLogger log, CaptureShieldMode best)
    {
        _dispatcher = dispatcher;
        _general = general;
        _log = log;
        _shield = new CaptureShield(new Win32ShieldWindows(), best);
        _callback = OnWinEvent;
        _showCallback = OnCallWndProc;
        Current = this;
    }

    /// <summary>The one instance of the running app (windows that need an exception or repair their affinity ask it).</summary>
    public static CaptureProtection? Current { get; private set; }

    /// <summary>Switched (on the UI thread) – tray, status icon and settings follow.</summary>
    public event EventHandler? Changed;

    public bool IsAvailable => _shield.IsAvailable;

    public bool IsHidden => _shield.Enabled;

    public CaptureShieldMode Mode => _shield.Best;

    /// <summary>The display affinity a normal window of the app should have now (0 = none).</summary>
    public uint ExpectedAffinity => _shield.ExpectedAffinity;

    /// <summary>At the start: the saved setting (default off) – before the first window.</summary>
    public void Start()
    {
        if (_general.Current.HideFromCapture)
        {
            Set(true, "start", save: false);
        }
        else
        {
            _log.Info(null, $"Hide from screen captures: off ({ModeText(_shield.Best)} possible)");
        }
    }

    public Task<bool> SetHiddenAsync(bool hidden, string source) =>
        _dispatcher.CheckAccess() ? Task.FromResult(Set(hidden, source)) : _dispatcher.InvokeAsync(() => Set(hidden, source)).Task;

    /// <summary>Switch on the UI thread: saved, applied to every open window at once, logged once.</summary>
    public bool Set(bool hidden, string source) => Set(hidden, source, save: true);

    public bool Toggle(string source) => Set(!IsHidden, source);

    /// <summary>Leave this window out (before it is shown) – the screenshot overlay.</summary>
    public void Exempt(IntPtr window) => _shield.Exempt(window);

    public void Release(IntPtr window) => _shield.Release(window);

    public bool IsExempt(IntPtr window) => _shield.IsExempt(window);

    public void Dispose()
    {
        Unhook();
        if (Current == this)
        {
            Current = null;
        }
    }

    private bool Set(bool hidden, string source, bool save)
    {
        if (hidden && !IsAvailable)
        {
            _log.Warn(null, $"Hide from screen captures: not possible on this Windows ({Environment.OSVersion.Version}) – asked by {source}");
            return false;
        }

        if (hidden == _shield.Enabled)
        {
            return hidden;
        }

        if (save && _general.Current.HideFromCapture != hidden)
        {
            _general.Update(g => g.HideFromCapture = hidden);
        }

        if (hidden)
        {
            Hook(); // first: a window that appears while switching is caught as well
        }

        var result = _shield.SetEnabled(hidden);
        if (!hidden)
        {
            Unhook();
        }

        var fallback = _shield.BlackFallbacks > 0 ? $", {_shield.BlackFallbacks} of them only black (WDA_MONITOR)" : "";
        var failed = result.Failed > 0 ? $", {result.Failed} refused by Windows (error {result.LastError})" : "";
        var line = $"Hide from screen captures: {(hidden ? $"on ({ModeText(result.Mode)})" : "off")} by {source} – {result.Windows} windows{fallback}{failed}{(hidden && (_hook == IntPtr.Zero || _showHook == IntPtr.Zero) ? ", new windows NOT fully covered (hook failed)" : "")}";
        if (result.Failed > 0 || (hidden && (_hook == IntPtr.Zero || _showHook == IntPtr.Zero)))
        {
            _log.Warn(null, line);
        }
        else
        {
            _log.Info(null, line);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return _shield.Enabled;
    }

    private static string ModeText(CaptureShieldMode mode) => mode switch
    {
        CaptureShieldMode.Exclude => "excluded from captures",
        CaptureShieldMode.Black => "black on captures (older Windows)",
        _ => "not possible",
    };

    private void Hook()
    {
        // 1. Synchronous, before a window of the UI thread becomes visible: the out-of-context WinEvent alone arrives
        //    after the first frame (measured: a new Win+V choice was on exactly one frame of a capture).
        if (_showHook == IntPtr.Zero)
        {
            _showHook = SetWindowsHookEx(WhCallWndProc, _showCallback, IntPtr.Zero, GetCurrentThreadId());
        }

        // 2. The net for windows of other threads: out of context, only the own process, through this thread's loop.
        if (_hook == IntPtr.Zero)
        {
            _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _callback, (uint)Environment.ProcessId, 0, WinEventOutOfContext);
        }
    }

    private void Unhook()
    {
        if (_showHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_showHook);
            _showHook = IntPtr.Zero;
        }

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Every message sent to a window of the UI thread passes here (only while the setting is on). Only "about to
    /// show" counts: <c>WM_SHOWWINDOW</c> (shown) and <c>WM_WINDOWPOSCHANGING</c> with <c>SWP_SHOWWINDOW</c> – both come
    /// before Windows draws the window, so it never appears on a capture, not even for one frame.
    /// </summary>
    private IntPtr OnCallWndProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && lParam != IntPtr.Zero)
        {
            try
            {
                var message = Marshal.PtrToStructure<CwpStruct>(lParam);
                var showing = message.Message switch
                {
                    WmShowWindow => message.WParam != IntPtr.Zero,
                    WmWindowPosChanging => message.LParam != IntPtr.Zero
                        && (Marshal.ReadInt32(message.LParam, WindowPosFlagsOffset) & SwpShowWindow) != 0,
                    _ => false,
                };
                if (showing && Win32ShieldWindows.IsTopLevel(message.Window))
                {
                    _shield.WindowShown(message.Window);
                }
            }
            catch (Exception ex)
            {
                _log.Error(null, "Hide from screen captures: a window could not be covered before showing", ex);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != ObjIdWindow || childId != ChildIdSelf || window == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (Win32ShieldWindows.IsTopLevel(window))
            {
                _shield.WindowShown(window);
            }
        }
        catch (Exception ex)
        {
            _log.Error(null, "Hide from screen captures: a new window could not be covered", ex);
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CwpStruct
    {
        public IntPtr LParam;
        public IntPtr WParam;
        public uint Message;
        public IntPtr Window;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
}

/// <summary>The Win32 side of <see cref="CaptureShield"/>: the own top-level windows and their display affinity.</summary>
internal sealed class Win32ShieldWindows : IShieldWindows
{
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const string DialogClass = "#32770";

    private static readonly uint OwnProcess = (uint)Environment.ProcessId;

    public int LastError { get; private set; }

    public static bool IsTopLevel(IntPtr window) => GetAncestor(window, GaRoot) == window && IsOwn(window);

    public IReadOnlyList<IntPtr> OwnTopLevelWindows(bool visibleOnly)
    {
        var list = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (IsOwn(window) && (!visibleOnly || IsWindowVisible(window)))
            {
                list.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    public bool IsSystemDialog(IntPtr window)
    {
        // A Windows dialog itself, or a window it owns (its tooltips, drop-downs) – a few steps up the owner chain.
        var current = window;
        for (var i = 0; i < 4 && current != IntPtr.Zero; i++)
        {
            if (ClassOf(current) == DialogClass)
            {
                return true;
            }

            current = GetWindow(current, GwOwner);
        }

        return false;
    }

    public uint GetAffinity(IntPtr window) =>
        GetWindowDisplayAffinity(window, out var affinity) ? affinity : CaptureShieldSupport.AffinityNone;

    public bool SetAffinity(IntPtr window, uint affinity)
    {
        if (SetWindowDisplayAffinity(window, affinity))
        {
            return true;
        }

        LastError = Marshal.GetLastWin32Error();
        return false;
    }

    private static bool IsOwn(IntPtr window) => GetWindowThreadProcessId(window, out var process) != 0 && process == OwnProcess;

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
