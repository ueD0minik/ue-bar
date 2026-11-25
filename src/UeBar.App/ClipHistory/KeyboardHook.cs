// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;
using UeBar.App.Interop;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.ClipHistory;

/// <summary>
/// Hotkeys taken over from Windows (P2-6, <see cref="IHotkeyTakeoverService"/>, e.g. Win+V). A low-level keyboard
/// hook on its own thread with its own message loop – it exists only while at least one hotkey is taken over. The
/// callback asks <see cref="KeyHookFilter"/> (pure logic, no waiting) and returns at once; the press goes out with
/// <c>ThreadPool</c>, the start menu mask key is sent from the loop after the callback returned. Windows silently
/// removes slow hooks, and setting one can fail: <see cref="HookSchedule"/> retries and sets it again every few
/// minutes.
/// </summary>
internal sealed class KeyboardHook : IHostHotkeyTakeover, IDisposable
{
    private const int MsgMask = ClipInterop.WM_APP + 1;
    private const int MsgInstall = ClipInterop.WM_APP + 2;
    private const int MsgUninstall = ClipInterop.WM_APP + 3;

    private readonly ManualResetEventSlim _ready = new();
    private readonly IUeLogger _log;
    private nuint _timer;
    private readonly Lock _lock = new();
    private readonly KeyHookFilter _filter = new();
    private readonly List<Takeover> _takeovers = [];
    private readonly HookSchedule _schedule = new();
    private readonly ClipInterop.LowLevelKeyboardProc _callback;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private int _presses;

    public KeyboardHook(IUeLogger log)
    {
        _log = log;
        _callback = OnKey; // kept alive as long as the hook exists
    }

    /// <summary>For tests and the diagnostics: hook set, presses, failures.</summary>
    public string Diagnostics => $"Keyboard hook: installed {_schedule.IsInstalled}, failures {_schedule.Failures}, presses {Volatile.Read(ref _presses)}, hotkeys {_takeovers.Count}";

    public IHotkeyTakeover? TakeOver(string moduleId, string hotkey, Action pressed)
    {
        if (Hotkey.TryParse(hotkey) is not { } key)
        {
            return null;
        }

        var takeover = new Takeover(this, moduleId, key, pressed);
        bool first;
        lock (_lock)
        {
            first = _takeovers.Count == 0;
            _takeovers.Add(takeover);
            _filter.Add(key);
            if (_thread is null)
            {
                // The thread stays once started (it only waits in GetMessage); the hook itself exists only while needed.
                _ready.Reset();
                _thread = new Thread(Loop) { IsBackground = true, Name = "ueBar keyboard hook", Priority = ThreadPriority.AboveNormal };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        _ready.Wait(TimeSpan.FromSeconds(2));
        if (first)
        {
            ClipInterop.PostThreadMessage(_threadId, MsgInstall, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            takeover.SetState(_schedule.IsInstalled ? HotkeyTakeoverState.Active : _schedule.HasFailed ? HotkeyTakeoverState.Failed : HotkeyTakeoverState.Retrying, null);
        }

        _log.Info(moduleId, $"Hotkey {key.Text} taken over from Windows");
        return takeover;
    }

    private void Release(Takeover takeover)
    {
        bool last;
        lock (_lock)
        {
            if (!_takeovers.Remove(takeover))
            {
                return;
            }

            if (!_takeovers.Any(t => t.Key == takeover.Key))
            {
                _filter.Remove(takeover.Key);
            }

            last = _takeovers.Count == 0;
        }

        _log.Info(takeover.ModuleId, $"Hotkey {takeover.Key.Text} given back to Windows");
        if (last && _threadId != 0)
        {
            ClipInterop.PostThreadMessage(_threadId, MsgUninstall, IntPtr.Zero, IntPtr.Zero); // no hook while nothing is taken over
        }
    }

    public void Dispose()
    {
        List<Takeover> all;
        lock (_lock)
        {
            all = [.. _takeovers];
        }

        foreach (var takeover in all)
        {
            takeover.Dispose();
        }
    }

    // ------------------------------------------------------------------ hook thread

    private void Loop()
    {
        // Creates the thread's message queue before anyone posts to it.
        ClipInterop.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _threadId = ClipInterop.GetCurrentThreadId();
        _ready.Set();
        while (ClipInterop.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            switch (message.message)
            {
                case MsgMask:
                    // Win is still held: a key without meaning, so releasing Win does not open the start menu.
                    ClipInterop.Send(ClipInterop.Key(KeyHookFilter.VkMask, up: false), ClipInterop.Key(KeyHookFilter.VkMask, up: true));
                    break;
                case MsgInstall:
                case ClipInterop.WM_TIMER:
                    if (HasTakeovers)
                    {
                        Install();
                    }
                    else
                    {
                        StopTimer();
                        Uninstall();
                    }

                    break;
                case MsgUninstall:
                    if (!HasTakeovers)
                    {
                        StopTimer();
                        Uninstall();
                        _schedule.Reset();
                    }

                    break;
            }
        }

        StopTimer();
        Uninstall();
    }

    private bool HasTakeovers
    {
        get
        {
            lock (_lock)
            {
                return _takeovers.Count > 0;
            }
        }
    }

    private void StopTimer()
    {
        if (_timer != 0)
        {
            ClipInterop.KillTimer(IntPtr.Zero, _timer);
            _timer = 0;
        }
    }

    private void Install()
    {
        Uninstall();
        StopTimer();
        _filter.Reset();
        _hook = ClipInterop.SetWindowsHookExW(ClipInterop.WH_KEYBOARD_LL, _callback, ClipInterop.GetModuleHandle(null), 0);
        var installed = _hook != IntPtr.Zero;
        var wasFailing = _schedule.Failures > 0;
        var next = _schedule.Report(installed);
        if (!installed)
        {
            _log.Warn(null, $"Keyboard hook could not be set (error {Marshal.GetLastWin32Error()}, try {_schedule.Failures})");
        }
        else if (wasFailing)
        {
            _log.Info(null, "Keyboard hook set");
        }

        // Timer of the message loop: retry soon after a failure, otherwise set it again every few minutes.
        _timer = ClipInterop.SetTimer(IntPtr.Zero, 0, (uint)next.TotalMilliseconds, IntPtr.Zero);
        var state = installed ? HotkeyTakeoverState.Active : _schedule.HasFailed ? HotkeyTakeoverState.Failed : HotkeyTakeoverState.Retrying;
        var problem = installed ? null : Loc.Get(_schedule.HasFailed ? "Hook_Failed" : "Hook_Retrying");
        List<Takeover> all;
        lock (_lock)
        {
            all = [.. _takeovers];
        }

        foreach (var takeover in all)
        {
            takeover.SetState(state, problem);
        }
    }

    private void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            ClipInterop.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>The hook itself: decide, hand off, return – nothing that could wait.</summary>
    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var info = Marshal.PtrToStructure<ClipInterop.KBDLLHOOKSTRUCT>(lParam);
            var down = message is ClipInterop.WM_KEYDOWN or ClipInterop.WM_SYSKEYDOWN;
            var injected = (info.flags & ClipInterop.LLKHF_INJECTED) != 0;
            var verdict = _filter.OnKey((int)info.vkCode, down, injected, out var fired);
            if (verdict == HookVerdict.SwallowAndFire && fired is { } hotkey)
            {
                Interlocked.Increment(ref _presses);
                if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win))
                {
                    ClipInterop.PostThreadMessage(_threadId, MsgMask, IntPtr.Zero, IntPtr.Zero);
                }

                ThreadPool.QueueUserWorkItem(_ => Fire(hotkey));
            }

            if (verdict != HookVerdict.Pass)
            {
                return new IntPtr(1);
            }
        }

        return ClipInterop.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Fire(Hotkey hotkey)
    {
        List<Takeover> all;
        lock (_lock)
        {
            all = [.. _takeovers.Where(t => t.Key == hotkey)];
        }

        foreach (var takeover in all)
        {
            try
            {
                takeover.Pressed();
            }
            catch (Exception ex)
            {
                _log.Warn(takeover.ModuleId, $"Hotkey {hotkey.Text} failed: {ex.Message}");
            }
        }
    }

    private sealed class Takeover(KeyboardHook owner, string moduleId, Hotkey key, Action pressed) : IHotkeyTakeover
    {
        private int _disposed;

        public string ModuleId { get; } = moduleId;

        public Hotkey Key { get; } = key;

        public Action Pressed { get; } = pressed;

        public HotkeyTakeoverState State { get; private set; } = HotkeyTakeoverState.Retrying;

        public string? Problem { get; private set; }

        public event EventHandler? StateChanged;

        public void SetState(HotkeyTakeoverState state, string? problem)
        {
            if (State == state && Problem == problem)
            {
                return;
            }

            State = state;
            Problem = problem;
            if (Volatile.Read(ref _disposed) == 0)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }
}
