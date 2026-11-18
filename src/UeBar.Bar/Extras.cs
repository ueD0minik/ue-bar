// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>Modifiers of a global hotkey (values as in <c>RegisterHotKey</c>).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>Global hotkey, e.g. "Win+Alt+Space" (M6, quick search).</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey, string Text)
{
    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Leertaste"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Insert"] = 0x2D, ["Einfg"] = 0x2D, ["Delete"] = 0x2E, ["Entf"] = 0x2E, ["Home"] = 0x24, ["Pos1"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["Pause"] = 0x13, ["Oem1"] = 0xBA, ["Comma"] = 0xBC, ["Period"] = 0xBE, ["Minus"] = 0xBD, ["Plus"] = 0xBB,
    };

    /// <summary>"Win+Alt+Space", "Strg+Umschalt+K", "Ctrl+F12" … – at least one modifier and exactly one key.</summary>
    public static Hotkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "CTRL" or "CONTROL" or "STRG":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "SHIFT" or "UMSCHALT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
            }

            if (key is not null)
            {
                return null; // two keys
            }

            key = KeyCode(raw);
            if (key is null)
            {
                return null;
            }
        }

        return key is { } vk && modifiers != HotkeyModifiers.None ? new Hotkey(modifiers, vk, text.Trim()) : null;
    }

    /// <summary>
    /// Recording field (M7): build a hotkey from pressed modifiers + key. null while only
    /// modifiers are pressed, without a modifier or for an unknown key.
    /// </summary>
    public static Hotkey? FromKeys(HotkeyModifiers modifiers, int virtualKey)
    {
        if (modifiers == HotkeyModifiers.None || IsModifierKey(virtualKey) || KeyName(virtualKey) is not { } name)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(name);
        return new Hotkey(modifiers, virtualKey, string.Join("+", parts));
    }

    /// <summary>Display in the UI language, e.g. "Win + Alt + Space" / "Win + Alt + Leertaste".</summary>
    public string DisplayText => string.Join(" + ", Text.Split('+').Select(BarText.KeyName));

    private static bool IsModifierKey(int vk) => vk is >= 0x10 and <= 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;

    private static string? KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return $"F{vk - 0x70 + 1}";
        }

        // first (English) name from the table
        foreach (var (name, code) in NamedKeys)
        {
            if (code == vk)
            {
                return name;
            }
        }

        return null;
    }

    private static int? KeyCode(string name)
    {
        if (NamedKeys.TryGetValue(name, out var named))
        {
            return named;
        }

        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1;
        }

        return null;
    }
}

/// <summary>What a key press in the recording field means (M7-Fix 1).</summary>
public enum HotkeyRecordOutcome
{
    /// <summary>Only modifiers pressed – keep waiting (display e.g. "Ctrl + Alt + …").</summary>
    Waiting,

    /// <summary>Esc: cancel recording, the old hotkey stays.</summary>
    Cancel,

    /// <summary>Delete/Backspace without modifier: reset to the default.</summary>
    ResetToDefault,

    /// <summary>Key without modifier or unknown key.</summary>
    Invalid,

    /// <summary>Valid hotkey – register immediately.</summary>
    Complete,
}

/// <summary>Evaluation of a key press in the recording field.</summary>
public sealed record HotkeyRecordResult(HotkeyRecordOutcome Outcome, Hotkey? Hotkey, string Display);

/// <summary>
/// Recording of a hotkey (M7-Fix 1), WPF-free: the app passes the virtual key (for Alt
/// already resolved from <c>SystemKey</c>) and the pressed modifiers (Win counted by itself).
/// </summary>
public static class HotkeyRecording
{
    public const string DefaultHotkey = "Win+Alt+Space";

    private const int VkBack = 0x08;
    private const int VkEscape = 0x1B;
    private const int VkDelete = 0x2E;

    /// <summary>Modifier a key represents itself (Ctrl, Alt, Shift, Win), otherwise None.</summary>
    public static HotkeyModifiers ModifierOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
        0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
        0x5B or 0x5C => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    public static HotkeyRecordResult Evaluate(int vk, HotkeyModifiers modifiers)
    {
        // The currently pressed modifier key counts itself (Keyboard.Modifiers lags behind on the first press).
        modifiers |= ModifierOf(vk);
        var partial = Partial(modifiers);

        if (vk == VkEscape && modifiers == HotkeyModifiers.None)
        {
            return new HotkeyRecordResult(HotkeyRecordOutcome.Cancel, null, "");
        }

        if (vk is VkDelete or VkBack && modifiers == HotkeyModifiers.None)
        {
            return new HotkeyRecordResult(HotkeyRecordOutcome.ResetToDefault, Hotkey.TryParse(DefaultHotkey), "");
        }

        if (ModifierOf(vk) != HotkeyModifiers.None)
        {
            return new HotkeyRecordResult(HotkeyRecordOutcome.Waiting, null, partial + " + …");
        }

        if (modifiers == HotkeyModifiers.None)
        {
            return new HotkeyRecordResult(HotkeyRecordOutcome.Invalid, null, BarText.T["Hotkey_NeedsModifier"]);
        }

        return Hotkey.FromKeys(modifiers, vk) is { } hotkey
            ? new HotkeyRecordResult(HotkeyRecordOutcome.Complete, hotkey, hotkey.DisplayText)
            : new HotkeyRecordResult(HotkeyRecordOutcome.Invalid, null, BarText.T["Hotkey_KeyNotSupported"]);
    }

    /// <summary>"Ctrl + Alt" for the display during recording.</summary>
    public static string Partial(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add(BarText.T["Key_Ctrl"]);
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add(BarText.T["Key_Shift"]);
        }

        return string.Join(" + ", parts);
    }

    /// <summary>Keys for the manual choice (fallback when Windows does not let a combination through). Labels in the current UI language.</summary>
    public static IReadOnlyList<(string Label, int VirtualKey)> SelectableKeys => BuildKeys();

    private static List<(string, int)> BuildKeys()
    {
        var keys = new List<(string, int)> { (BarText.T["Key_Space"], 0x20) };
        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add((c.ToString(), c));
        }

        for (var c = '0'; c <= '9'; c++)
        {
            keys.Add((c.ToString(), c));
        }

        for (var f = 1; f <= 12; f++)
        {
            keys.Add(($"F{f}", 0x70 + f - 1));
        }

        keys.AddRange([("Enter", 0x0D), ("Tab", 0x09), (BarText.T["Key_Insert"], 0x2D), (BarText.T["Key_Home"], 0x24), (BarText.T["Key_End"], 0x23), (BarText.T["Key_PageUp"], 0x21), (BarText.T["Key_PageDown"], 0x22),
            (BarText.T["Key_Up"], 0x26), (BarText.T["Key_Down"], 0x28), (BarText.T["Key_Left"], 0x25), (BarText.T["Key_Right"], 0x27), ("Pause", 0x13)]);
        return keys;
    }
}

/// <summary>Result of registering a global hotkey.</summary>
public enum HotkeyRegistration
{
    Registered,
    Invalid,
    InUse,
}

/// <summary>
/// Registers a global hotkey (the actual Win32 registration comes from outside, so this
/// stays testable). Invalid or in use → no crash, but an understandable text.
/// </summary>
public static class HotkeyBinder
{
    /// <summary>MOD_NOREPEAT: holding the key down does not trigger repeatedly.</summary>
    public const int NoRepeat = 0x4000;

    public static (HotkeyRegistration Result, Hotkey? Hotkey, string Message) Register(string? text, Func<int, int, bool> register)
    {
        if (Hotkey.TryParse(text) is not { } hotkey)
        {
            return (HotkeyRegistration.Invalid, null, BarText.T.Format("Hotkey_Invalid", text));
        }

        return register((int)hotkey.Modifiers | NoRepeat, hotkey.VirtualKey)
            ? (HotkeyRegistration.Registered, hotkey, BarText.T.Format("Hotkey_Registered", hotkey.DisplayText))
            : (HotkeyRegistration.InUse, hotkey, BarText.T.Format("Hotkey_InUse", hotkey.DisplayText));
    }
}

/// <summary>A notification for the bar (independent of the module).</summary>
public sealed record BarNotification(string Title, string Message, string? Icon, TimeSpan Duration, DateTimeOffset Timestamp, string? Source = null)
{
    /// <summary>ID the module gave it (comes back with a button press, P2-3).</summary>
    public string? Id { get; init; }

    /// <summary>Buttons (ID, label) – shown in the panel while it is the current notification.</summary>
    public IReadOnlyList<(string Id, string Label)> Actions { get; init; } = [];
}

/// <summary>
/// Order of notifications (M6): each one lands in the history (the last <see cref="HistoryLimit"/>).
/// Only one is shown at a time, the next one only after the previous one expired. Suppressed ones (fullscreen,
/// "Do not disturb") only go to the history. Time comes from outside (<see cref="Advance"/>).
/// </summary>
public sealed class NotificationQueue
{
    public const int HistoryLimit = 20;

    private readonly Queue<BarNotification> _pending = new();
    private readonly List<BarNotification> _history = [];

    public BarNotification? Current { get; private set; }

    public DateTimeOffset? CurrentUntil { get; private set; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<BarNotification> History => _history;

    public int PendingCount => _pending.Count;

    public void Enqueue(BarNotification notification, bool suppress)
    {
        _history.Insert(0, notification);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
        }

        if (!suppress)
        {
            _pending.Enqueue(notification);
        }
    }

    /// <summary>
    /// Advance time: end an expired display, start the next one. Returns true if
    /// <see cref="Current"/> changed.
    /// </summary>
    public bool Advance(DateTimeOffset now)
    {
        var before = Current;
        if (Current is not null && now >= CurrentUntil)
        {
            Current = null;
            CurrentUntil = null;
        }

        if (Current is null && _pending.TryDequeue(out var next))
        {
            Current = next;
            CurrentUntil = now + next.Duration;
        }

        return !ReferenceEquals(before, Current);
    }

    /// <summary>End the current display immediately (e.g. clicked).</summary>
    public void Dismiss()
    {
        Current = null;
        CurrentUntil = null;
    }

    public void ClearHistory() => _history.Clear();

    /// <summary>Emergency exit: drop the current and waiting displays (they stay in the history).</summary>
    public void ClearPending()
    {
        Dismiss();
        _pending.Clear();
    }
}

/// <summary>One exchange in the Assist field: question and answer.</summary>
public sealed record AssistExchange(string Question, string Answer, bool Success);

/// <summary>
/// Display of the Assist conversation (M6-Fix 2), separate from the context at the provider: only the
/// last exchange is visible, older ones are in the expandable history (at most <see cref="HistoryLimit"/>).
/// When a new conversation starts, the display is cleared – it never shows more than the provider still knows.
/// </summary>
public sealed class AssistTranscript
{
    /// <summary>Maximum number of older entries behind "History (n)".</summary>
    public const int HistoryLimit = 10;

    private readonly List<AssistExchange> _entries = []; // newest first

    public AssistExchange? Latest => _entries.Count > 0 ? _entries[0] : null;

    /// <summary>Older entries, newest first.</summary>
    public IReadOnlyList<AssistExchange> Older => _entries.Count > 1 ? _entries.GetRange(1, _entries.Count - 1) : [];

    public bool IsHistoryExpanded { get; set; }

    /// <summary>Hint "New conversation" (until the next question or until the panel closes).</summary>
    public bool ShowsNewConversationHint { get; private set; }

    public void Add(AssistExchange exchange)
    {
        _entries.Insert(0, exchange);
        if (_entries.Count > HistoryLimit + 1)
        {
            _entries.RemoveRange(HistoryLimit + 1, _entries.Count - HistoryLimit - 1);
        }

        ShowsNewConversationHint = false;
    }

    /// <summary>New conversation: display empty, history closed. Hint only if <paramref name="announce"/>.</summary>
    public void StartNew(bool announce)
    {
        _entries.Clear();
        IsHistoryExpanded = false;
        ShowsNewConversationHint = announce;
    }

    /// <summary>Panel closes: collapse the history, remove the hint.</summary>
    public void OnPanelClosed()
    {
        IsHistoryExpanded = false;
        ShowsNewConversationHint = false;
    }

    /// <summary>
    /// Panel opens or a question is asked: if the conversation at the provider expired, start
    /// over (hint only if something was visible). Returns true if it started over.
    /// </summary>
    public bool StartNewIfExpired(bool expired)
    {
        if (!expired)
        {
            return false;
        }

        StartNew(announce: _entries.Count > 0);
        return true;
    }
}
