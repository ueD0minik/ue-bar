// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Interop;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Logging;

namespace UeBar.App.Infrastructure;

/// <summary>
/// Global hotkeys besides the quick search (P2-5): show/hide all pins and the hotkeys of plugins. An own
/// message-only window receives WM_HOTKEY; the logic (names, status, pausing) is <see cref="HotkeySet"/>.
/// UI thread only.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _window;
    private readonly IUeLogger _log;

    public GlobalHotkeys(IUeLogger log)
    {
        _log = log;
        var parameters = new HwndSourceParameters("ueBar.Hotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: no visible window
            WindowStyle = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
        Set = new HotkeySet(
            (id, mods, vk) => ExtrasInterop.RegisterHotKey(_window.Handle, id, mods, vk),
            id => ExtrasInterop.UnregisterHotKey(_window.Handle, id));
    }

    public HotkeySet Set { get; }

    /// <summary>Registers (or replaces) a named hotkey and logs the result.</summary>
    public (HotkeyRegistration Result, string Message) Bind(string name, string? text, Action pressed)
    {
        var status = Set.Set(name, text, pressed);
        if (status.Result == HotkeyRegistration.Registered)
        {
            _log.Info(null, $"Hotkey {name}: {status.Message}");
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            _log.Warn(null, $"Hotkey {name}: {status.Message}");
        }

        return status;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ExtrasInterop.WM_HOTKEY)
        {
            try
            {
                handled = Set.Press(wParam.ToInt32());
            }
            catch (Exception ex)
            {
                _log.Error(null, "Hotkey action failed", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Set.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
