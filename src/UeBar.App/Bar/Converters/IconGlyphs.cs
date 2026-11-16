// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;

namespace UeBar.App.Bar.Converters;

/// <summary>
/// Icon name → character in "Segoe Fluent Icons". Modules give icons as names (SPEC 4.2);
/// this is the shared vocabulary. Unknown names are shown as a puzzle icon. For special cases
/// a code point works directly as well: "U+E80F". (Code points checked against SegoeIcons.ttf, M4.)
/// </summary>
public static class IconGlyphs
{
    public const string Fallback = ""; // Puzzle

    private static readonly Dictionary<string, char> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // General / UI
        ["Home"] = '',
        ["Puzzle"] = '',
        ["Error"] = '',
        ["Warning"] = '',
        ["Info"] = '',
        ["Pin"] = '',
        ["Unpin"] = '',
        ["Settings"] = '',
        ["Add"] = '',
        ["Remove"] = '',
        ["Search"] = '',
        ["Star"] = '',
        ["Heart"] = '',
        ["Clock"] = '',
        ["Calendar"] = '',
        ["Checkmark"] = '',
        ["ToggleRight"] = '',
        ["TestBeaker"] = '',
        ["Bell"] = '',
        ["Eye"] = '',
        ["Person"] = '',
        ["Phone"] = '',
        ["Camera"] = '',
        ["Mic"] = '',
        ["Wifi"] = '',
        ["Car"] = '',
        ["Leaf"] = '',
        ["Gauge"] = '',
        ["Palette"] = '',

        // Devices
        ["Lightbulb"] = '',
        ["Lightning"] = '',
        ["Power"] = '',
        ["Tv"] = '',
        ["Monitor"] = '',
        ["Laptop"] = '',
        ["Speaker"] = '',
        ["Headphones"] = '',
        ["Robot"] = '',
        ["Broom"] = '',
        ["Battery"] = '',
        ["Lock"] = '',
        ["Unlock"] = '',
        ["Door"] = '',
        ["Window"] = '',
        ["Thermometer"] = '',
        ["Fan"] = '',
        ["Fire"] = '',
        ["Snow"] = '',
        ["Drop"] = '',
        ["Walk"] = '',
        ["Run"] = '',

        // Media
        ["Play"] = '',
        ["Pause"] = '',
        ["Stop"] = '',
        ["Previous"] = '',
        ["Next"] = '',
        ["MusicNote"] = '',
        ["Volume"] = '',
        ["Mute"] = '',

        // Weather (Segoe Fluent has only a few weather icons)
        ["Sun"] = '',
        ["Moon"] = '',
        ["Cloud"] = '',
        ["PartlyCloudy"] = '',
        ["Rain"] = '',
        ["Storm"] = '',
    };

    public static string Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        if (Map.TryGetValue(name, out var glyph))
        {
            return glyph.ToString();
        }

        // Direct code point: "U+E80F"
        if (name.Length is >= 6 and <= 7 && name.StartsWith("U+", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            && code is >= 0xE000 and <= 0xF8FF)
        {
            return ((char)code).ToString();
        }

        return Fallback;
    }
}
