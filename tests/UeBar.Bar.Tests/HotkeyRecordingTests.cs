// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar.Tests;

/// <summary>M7-Fix 1: key presses in the recording field → hotkey or error (WPF-free). German texts (pinned culture).</summary>
public class HotkeyRecordingTests
{
    private const HotkeyModifiers Ctrl = HotkeyModifiers.Control;
    private const HotkeyModifiers Alt = HotkeyModifiers.Alt;
    private const HotkeyModifiers Win = HotkeyModifiers.Win;

    [Fact]
    public void Ctrl_alt_k_typed_key_by_key()
    {
        // Ctrl down (Keyboard.Modifiers may not know it yet on the first press)
        var step1 = HotkeyRecording.Evaluate(0xA2, HotkeyModifiers.None);
        Assert.Equal(HotkeyRecordOutcome.Waiting, step1.Outcome);
        Assert.Equal("Strg + …", step1.Display);

        // Alt added (comes in WPF as Key.System – the app resolves SystemKey before)
        var step2 = HotkeyRecording.Evaluate(0xA4, Ctrl);
        Assert.Equal("Strg + Alt + …", step2.Display);

        var done = HotkeyRecording.Evaluate('K', Ctrl | Alt);
        Assert.Equal(HotkeyRecordOutcome.Complete, done.Outcome);
        Assert.Equal("Ctrl+Alt+K", done.Hotkey!.Text);
        Assert.Equal("Strg + Alt + K", done.Display);
    }

    [Fact]
    public void Win_counts_as_modifier_even_when_wpf_does_not_report_it()
    {
        Assert.Equal("Win + …", HotkeyRecording.Evaluate(0x5B, HotkeyModifiers.None).Display);
        var done = HotkeyRecording.Evaluate(0x20, Win | Alt);
        Assert.Equal(HotkeyRecordOutcome.Complete, done.Outcome);
        Assert.Equal("Win+Alt+Space", done.Hotkey!.Text);
    }

    [Fact]
    public void Esc_cancels_and_delete_or_backspace_reset_to_default()
    {
        Assert.Equal(HotkeyRecordOutcome.Cancel, HotkeyRecording.Evaluate(0x1B, HotkeyModifiers.None).Outcome);

        var reset = HotkeyRecording.Evaluate(0x2E, HotkeyModifiers.None);
        Assert.Equal(HotkeyRecordOutcome.ResetToDefault, reset.Outcome);
        Assert.Equal(HotkeyRecording.DefaultHotkey, reset.Hotkey!.Text);
        Assert.Equal(HotkeyRecordOutcome.ResetToDefault, HotkeyRecording.Evaluate(0x08, HotkeyModifiers.None).Outcome);

        // with a modifier, Delete is a normal key (Windows catches Ctrl+Alt+Del anyway)
        Assert.Equal(HotkeyRecordOutcome.Complete, HotkeyRecording.Evaluate(0x2E, Ctrl | HotkeyModifiers.Shift).Outcome);
    }

    [Fact]
    public void Plain_keys_and_unknown_keys_are_rejected_with_a_hint()
    {
        var plain = HotkeyRecording.Evaluate('K', HotkeyModifiers.None);
        Assert.Equal(HotkeyRecordOutcome.Invalid, plain.Outcome);
        Assert.Contains("kombinieren", plain.Display);

        Assert.Equal(HotkeyRecordOutcome.Invalid, HotkeyRecording.Evaluate(0xFF, Ctrl).Outcome);
    }

    [Fact]
    public void Every_selectable_key_gives_a_valid_hotkey_for_the_manual_fallback()
    {
        foreach (var (label, vk) in HotkeyRecording.SelectableKeys)
        {
            var hotkey = Hotkey.FromKeys(Win | Alt, vk);
            Assert.True(hotkey is not null, label);
            Assert.Equal(vk, Hotkey.TryParse(hotkey!.Text)!.VirtualKey);
        }
    }
}
