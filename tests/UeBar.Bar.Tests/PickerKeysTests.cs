// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Bar.Tests;

/// <summary>P2-5-Fix 3: keys of the small bar after picking – only its own actions, Enter = the first one.</summary>
public sealed class PickerKeysTests
{
    private static readonly (string Id, string? Shortcut)[] Bar =
        [("pin", null), ("copy", "Ctrl+C"), ("save", null), ("saveas", "Ctrl+S"), ("ocr", null)];

    private const int VkC = 0x43;
    private const int VkS = 0x53;

    [Fact]
    public void Ctrl_C_copies_Ctrl_S_saves_as_and_Enter_takes_the_default()
    {
        Assert.Equal("copy", PickerKeys.ActionFor(Bar, HotkeyModifiers.Control, VkC));
        Assert.Equal("saveas", PickerKeys.ActionFor(Bar, HotkeyModifiers.Control, VkS));
        Assert.Equal("pin", PickerKeys.ActionFor(Bar, HotkeyModifiers.None, PickerKeys.VkReturn));
    }

    [Fact]
    public void Other_keys_and_other_modifiers_do_nothing()
    {
        Assert.Null(PickerKeys.ActionFor(Bar, HotkeyModifiers.None, VkC)); // a plain "c"
        Assert.Null(PickerKeys.ActionFor(Bar, HotkeyModifiers.Control | HotkeyModifiers.Shift, VkC));
        Assert.Null(PickerKeys.ActionFor(Bar, HotkeyModifiers.Alt, VkS));
        Assert.Null(PickerKeys.ActionFor([], HotkeyModifiers.None, PickerKeys.VkReturn));
    }

    [Fact]
    public void The_key_is_in_the_tooltip()
    {
        Assert.Equal($"Kopieren ({Hotkey.TryParse("Ctrl+C")!.DisplayText})", PickerKeys.Tooltip("Kopieren", "Ctrl+C"));
        Assert.StartsWith("Anpinnen (", PickerKeys.Tooltip("Anpinnen", "Enter"), StringComparison.Ordinal);
        Assert.Equal("Speichern", PickerKeys.Tooltip("Speichern", null));
    }

    [Fact]
    public void Contract_1_6_adds_the_key_of_an_action() =>
        Assert.Equal("Ctrl+C", new ScreenAreaAction("copy", "Copy") { Shortcut = "Ctrl+C" }.Shortcut);
}
