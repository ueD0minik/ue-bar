// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// A key of the choice popup (P2-6): like a hotkey, but a modifier is not required – "Delete", "Enter",
/// "Shift+Enter", "Ctrl+M".
/// </summary>
public sealed record KeyChord(HotkeyModifiers Modifiers, int VirtualKey, string Text)
{
    public static KeyChord? TryParse(string? text)
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

            if (key is not null || Hotkey.KeyCode(raw) is not { } code)
            {
                return null; // two keys, or an unknown one
            }

            key = code;
        }

        return key is { } vk ? new KeyChord(modifiers, vk, text.Trim()) : null;
    }

    /// <summary>"Strg + M", "Umschalt + Enter", "Entf".</summary>
    public string DisplayText => string.Join(" + ", Text.Split('+').Select(BarText.KeyName));
}

/// <summary>Keys of the choice popup (P2-6): Enter = first action, otherwise the action's own key.</summary>
public static class ChoiceKeys
{
    public const int VkReturn = 0x0D;

    /// <summary>The action for a key press, or null. <paramref name="actions"/> = (id, shortcut) in popup order.</summary>
    public static string? ActionFor(IReadOnlyList<(string Id, string? Shortcut)> actions, HotkeyModifiers modifiers, int virtualKey)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return null;
        }

        // An action may name Enter itself (e.g. "Shift+Enter") – exact matches win over the plain Enter default.
        foreach (var (id, shortcut) in actions)
        {
            if (KeyChord.TryParse(shortcut) is { } key && key.Modifiers == modifiers && key.VirtualKey == virtualKey)
            {
                return id;
            }
        }

        return virtualKey == VkReturn && modifiers == HotkeyModifiers.None ? actions[0].Id : null;
    }

    /// <summary>"Merken (Strg + M)" – or only the label.</summary>
    public static string Tooltip(string label, string? shortcut) =>
        KeyChord.TryParse(shortcut) is { } key ? $"{label} ({key.DisplayText})" : label;
}

/// <summary>Search of the choice popup (P2-6): every word must appear (any order, case and accents ignored).</summary>
public static class ChoiceSearch
{
    /// <summary>Indexes of the matching items in their order; an empty query matches everything.</summary>
    public static IReadOnlyList<int> Filter<T>(IReadOnlyList<T> items, Func<T, string?> text, string? query)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(text);
        var words = Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = new List<int>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (words.Length == 0)
            {
                hits.Add(i);
                continue;
            }

            var haystack = Fold(text(items[i]) ?? "");
            if (words.All(w => haystack.Contains(w, StringComparison.Ordinal)))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    /// <summary>Lower case without accents (the sharp s becomes "ss"), whitespace collapsed.</summary>
    public static string Fold(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c == 'ß')
            {
                builder.Append("ss");
            }
            else
            {
                builder.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Which row is selected after the list changed: the same ID if it is still there, otherwise the row at the same
    /// position (or the last one); -1 for an empty list.
    /// </summary>
    public static int Reselect(IReadOnlyList<string> ids, string? previousId, int previousIndex, string? wantedId = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return -1;
        }

        foreach (var id in new[] { wantedId, previousId })
        {
            if (id is not null && ids.IndexOf(id) is var at and >= 0)
            {
                return at;
            }
        }

        return Math.Clamp(previousIndex, 0, ids.Count - 1);
    }

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Where the choice popup goes (P2-6) – all in physical pixels.</summary>
public static class ChoicePlacementRules
{
    /// <summary>
    /// Under the text cursor (a little gap), above it if there is no room below, otherwise at the mouse; always
    /// completely inside the work area of the monitor the anchor is on.
    /// </summary>
    /// <param name="caret">Caret rectangle on the screen, null if unknown.</param>
    /// <param name="mouse">Mouse position (fallback anchor).</param>
    /// <param name="center">True = in the middle of the monitor instead.</param>
    public static PixelPoint Place(PixelRect? caret, PixelPoint mouse, int width, int height, IReadOnlyList<MonitorInfo> monitors, bool center = false)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var anchor = caret is { } c && !center ? new PixelPoint(c.X, c.Bottom) : mouse;
        var monitor = PinGeometry.MonitorAt(anchor, monitors) ?? PinGeometry.MonitorAt(mouse, monitors) ?? PinGeometry.Primary(monitors);
        if (monitor is null)
        {
            return anchor;
        }

        var scale = monitor.Scale <= 0 ? 1 : monitor.Scale;
        var gap = (int)Math.Round(6 * scale);
        var area = monitor.WorkArea;
        int x, y;
        if (center)
        {
            x = area.X + ((area.Width - width) / 2);
            y = area.Y + ((area.Height - height) / 3);
        }
        else if (caret is { } r)
        {
            x = r.X;
            y = r.Bottom + gap;
            if (y + height > area.Bottom && r.Y - gap - height >= area.Y)
            {
                y = r.Y - gap - height; // no room below the line → above it
            }
        }
        else
        {
            x = mouse.X + gap;
            y = mouse.Y + gap;
            if (y + height > area.Bottom && mouse.Y - gap - height >= area.Y)
            {
                y = mouse.Y - gap - height;
            }
        }

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        return new PixelPoint(x, y);
    }
}
