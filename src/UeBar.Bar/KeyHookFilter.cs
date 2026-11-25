// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What the keyboard hook does with one key event.</summary>
public enum HookVerdict
{
    /// <summary>Let Windows and the apps have it (almost every key).</summary>
    Pass,

    /// <summary>Swallow it (the rest of a taken-over hotkey: key repeat, key up).</summary>
    Swallow,

    /// <summary>Swallow it and report the hotkey.</summary>
    SwallowAndFire,
}

/// <summary>
/// The logic of the low-level keyboard hook for hotkeys taken over from Windows (P2-6, e.g. Win+V). Decides per key
/// event, nothing else – no waiting, no I/O, so the hook returns at once. Tracks the modifiers from the events
/// themselves. Injected keys (from other tools, or our own mask key) are never reported.
/// </summary>
public sealed class KeyHookFilter
{
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;

    /// <summary>
    /// A key without a meaning (reserved). Pressed while Win is down after a swallowed hotkey, so that releasing Win
    /// does not open the start menu.
    /// </summary>
    public const int VkMask = 0xE8;

    private readonly Lock _lock = new();
    private readonly HashSet<Hotkey> _hotkeys = [];
    private bool _lWin, _rWin, _lShift, _rShift, _lCtrl, _rCtrl, _lAlt, _rAlt;
    private int _swallowing; // virtual key of the hotkey that is held right now (0 = none)

    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _hotkeys.Count == 0;
            }
        }
    }

    public void Add(Hotkey hotkey)
    {
        lock (_lock)
        {
            _hotkeys.Add(hotkey);
        }
    }

    public void Remove(Hotkey hotkey)
    {
        lock (_lock)
        {
            _hotkeys.Remove(hotkey);
        }
    }

    /// <summary>Modifiers held right now, as the hook saw them.</summary>
    public HotkeyModifiers Modifiers
    {
        get
        {
            lock (_lock)
            {
                return CurrentModifiers();
            }
        }
    }

    /// <summary>
    /// One key event. <paramref name="fired"/> = the hotkey that was pressed (with <see cref="HookVerdict.SwallowAndFire"/>).
    /// </summary>
    public HookVerdict OnKey(int virtualKey, bool down, bool injected, out Hotkey? fired)
    {
        fired = null;
        lock (_lock)
        {
            if (TrackModifier(virtualKey, down))
            {
                return HookVerdict.Pass; // modifiers always go through – Windows needs them for everything else
            }

            if (_swallowing != 0 && virtualKey == _swallowing)
            {
                if (!down)
                {
                    _swallowing = 0;
                }

                return HookVerdict.Swallow; // key repeat and key up of a taken-over hotkey
            }

            if (!down || injected || _hotkeys.Count == 0)
            {
                return HookVerdict.Pass;
            }

            var modifiers = CurrentModifiers();
            foreach (var hotkey in _hotkeys)
            {
                if (hotkey.VirtualKey == virtualKey && hotkey.Modifiers == modifiers)
                {
                    _swallowing = virtualKey;
                    fired = hotkey;
                    return HookVerdict.SwallowAndFire;
                }
            }

            return HookVerdict.Pass;
        }
    }

    /// <summary>Forget held keys (e.g. after the hook was set again – key ups may have been missed).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lWin = _rWin = _lShift = _rShift = _lCtrl = _rCtrl = _lAlt = _rAlt = false;
            _swallowing = 0;
        }
    }

    private HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (_lWin || _rWin)
        {
            m |= HotkeyModifiers.Win;
        }

        if (_lCtrl || _rCtrl)
        {
            m |= HotkeyModifiers.Control;
        }

        if (_lAlt || _rAlt)
        {
            m |= HotkeyModifiers.Alt;
        }

        if (_lShift || _rShift)
        {
            m |= HotkeyModifiers.Shift;
        }

        return m;
    }

    private bool TrackModifier(int vk, bool down)
    {
        switch (vk)
        {
            case VkLWin: _lWin = down; return true;
            case VkRWin: _rWin = down; return true;
            case VkLShift or 0x10: _lShift = down; return true;
            case VkRShift: _rShift = down; return true;
            case VkLControl or 0x11: _lCtrl = down; return true;
            case VkRControl: _rCtrl = down; return true;
            case VkLMenu or 0x12: _lAlt = down; return true;
            case VkRMenu: _rAlt = down; return true;
            default: return false;
        }
    }
}

/// <summary>
/// When the keyboard hook is set (again) (P2-6): Windows silently removes a hook that answers too slowly, and setting
/// one can fail. Tries at start, then after 2 s and 10 s; after three failures the takeover is "failed" (the module
/// falls back to its normal hotkey) – a later refresh may still succeed. A working hook is set again every few
/// minutes, so a removed one comes back.
/// </summary>
public sealed class HookSchedule
{
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    public int Failures { get; private set; }

    public bool IsInstalled { get; private set; }

    /// <summary>Three tries in a row failed.</summary>
    public bool HasFailed => !IsInstalled && Failures > RetryDelays.Length;

    /// <summary>No hook any more (nothing taken over) – the next one starts with fresh tries.</summary>
    public void Reset()
    {
        IsInstalled = false;
        Failures = 0;
    }

    /// <summary>Result of a try; returns when to try (or refresh) next.</summary>
    public TimeSpan Report(bool installed)
    {
        IsInstalled = installed;
        if (installed)
        {
            Failures = 0;
            return Refresh;
        }

        Failures++;
        return Failures <= RetryDelays.Length ? RetryDelays[Failures - 1] : Refresh;
    }
}
