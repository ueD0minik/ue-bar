// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Localization;

namespace UeBar.Bar;

/// <summary>UI texts of the bar logic (<c>UeBar.Bar/Resources/Strings*.resx</c>, R1).</summary>
public static class BarText
{
    public static TextCatalog T { get; } = TextCatalog.For(typeof(BarText).Assembly, "UeBar.Bar", () => UiCultureSource.Shared.Current);

    /// <summary>Localized name of a key from a hotkey text part ("Ctrl", "Space", "PageUp" …); unknown parts stay as they are.</summary>
    public static string KeyName(string part) => part.Trim().ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "STRG" => T["Key_Ctrl"],
        "SHIFT" or "UMSCHALT" => T["Key_Shift"],
        "SPACE" or "LEERTASTE" => T["Key_Space"],
        "INSERT" or "EINFG" => T["Key_Insert"],
        "DELETE" or "ENTF" => T["Key_Delete"],
        "HOME" or "POS1" => T["Key_Home"],
        "END" => T["Key_End"],
        "PAGEUP" => T["Key_PageUp"],
        "PAGEDOWN" => T["Key_PageDown"],
        "UP" => T["Key_Up"],
        "DOWN" => T["Key_Down"],
        "LEFT" => T["Key_Left"],
        "RIGHT" => T["Key_Right"],
        _ => part.Trim(),
    };
}
