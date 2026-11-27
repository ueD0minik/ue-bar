// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// Keys of the small bar after picking a screen area (P2-5-Fix 3): every action may bring its own key
/// ("Ctrl+C"), Enter takes the first (default) action. Only while the bar is shown – the overlay has the
/// keyboard then; nothing is registered system-wide. Esc is handled by the picker itself (cancel).
/// </summary>
public static class PickerKeys
{
    public const int VkReturn = 0x0D;

    /// <summary>The action for a key press, or null. <paramref name="actions"/> = (id, shortcut) in bar order.</summary>
    public static string? ActionFor(IReadOnlyList<(string Id, string? Shortcut)> actions, HotkeyModifiers modifiers, int virtualKey)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return null;
        }

        if (virtualKey == VkReturn && modifiers == HotkeyModifiers.None)
        {
            return actions[0].Id;
        }

        foreach (var (id, shortcut) in actions)
        {
            if (Hotkey.TryParse(shortcut) is { } key && key.Modifiers == modifiers && key.VirtualKey == virtualKey)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Tooltip of a button: "Kopieren (Strg + C)" – or only the label.</summary>
    public static string Tooltip(string label, string? shortcut) =>
        Hotkey.TryParse(shortcut) is { } key ? $"{label} ({key.DisplayText})"
        : string.Equals(shortcut, "Enter", StringComparison.OrdinalIgnoreCase) ? $"{label} ({BarText.KeyName("Enter")})"
        : label;

    /// <summary>Ctrl+A in the picker (P2-6-Fix 2): the whole monitor under the mouse.</summary>
    public static bool IsSelectAll(HotkeyModifiers modifiers, int virtualKey) => modifiers == HotkeyModifiers.Control && virtualKey == 'A';

    /// <summary>The monitor Ctrl+A takes: the one under the mouse, else the primary one.</summary>
    public static MonitorInfo? SelectAllMonitor(PixelPoint? mouse, IReadOnlyList<MonitorInfo> monitors) =>
        (mouse is { } p ? PinGeometry.MonitorAt(p, monitors) : null) ?? PinGeometry.Primary(monitors);
}
