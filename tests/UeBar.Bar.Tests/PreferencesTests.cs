// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar.Tests;

/// <summary>M7 in the bar logic: hotkey recording, accent color, OLED protection, welcome rule, columns.</summary>
public class PreferencesTests
{
    [Fact]
    public void Recorded_hotkey_is_formatted_and_parses_back()
    {
        var hotkey = Hotkey.FromKeys(HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x20);

        Assert.NotNull(hotkey);
        Assert.Equal("Win+Alt+Space", hotkey.Text);
        Assert.Equal("Win + Alt + Leertaste", hotkey.DisplayText);
        var parsed = Hotkey.TryParse(hotkey.Text)!;
        Assert.Equal((hotkey.Modifiers, hotkey.VirtualKey), (parsed.Modifiers, parsed.VirtualKey));

        Assert.Equal("Ctrl+Shift+F5", Hotkey.FromKeys(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x74)!.Text);
        Assert.Equal("Win+K", Hotkey.FromKeys(HotkeyModifiers.Win, 'K')!.Text);
        Assert.Null(Hotkey.FromKeys(HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x12)); // only Alt pressed – still waiting
        Assert.Null(Hotkey.FromKeys(HotkeyModifiers.None, 'K')); // without modifier
        Assert.Null(Hotkey.FromKeys(HotkeyModifiers.Alt, 0xFF)); // unknown key
    }

    [Fact]
    public void Recorded_hotkey_that_is_in_use_gives_a_hint()
    {
        var hotkey = Hotkey.FromKeys(HotkeyModifiers.Win | HotkeyModifiers.Alt, 'N')!;
        var (result, _, message) = HotkeyBinder.Register(hotkey.Text, (_, _) => false);

        Assert.Equal(HotkeyRegistration.InUse, result);
        Assert.Contains("belegt", message);
    }

    [Theory]
    [InlineData("#3e91ff", "#3E91FF")]
    [InlineData("3E91FF", "#3E91FF")]
    [InlineData(" #39f ", "#3399FF")]
    [InlineData("#12345", null)]
    [InlineData("blau", null)]
    [InlineData("", null)]
    public void Accent_colors_are_normalized(string input, string? expected) => Assert.Equal(expected, AccentColors.Normalize(input));

    [Fact]
    public void Accent_rgb_falls_back_to_default()
    {
        Assert.Equal(((byte)0x3E, (byte)0x91, (byte)0xFF), AccentColors.Rgb("kaputt"));
        Assert.Equal(((byte)0xE5, (byte)0x48, (byte)0x4D), AccentColors.Rgb("#E5484D"));
        Assert.All(AccentColors.Suggestions, s => Assert.Equal(s.Hex, AccentColors.Normalize(s.Hex)));
    }

    [Fact]
    public void Oled_dims_a_pinned_panel_after_inactivity_and_wakes_on_interaction()
    {
        var t = DateTimeOffset.UtcNow;
        var guard = new OledGuard(t) { DimAfter = TimeSpan.FromMinutes(5), PixelShift = false };

        guard.Update(t.AddMinutes(10));
        Assert.False(guard.IsDimmed); // not pinned: never

        guard.SetPinned(true, t.AddMinutes(10));
        Assert.False(guard.Update(t.AddMinutes(14.9)));
        Assert.True(guard.Update(t.AddMinutes(15)));
        Assert.True(guard.IsDimmed);

        Assert.True(guard.Interaction(t.AddMinutes(16))); // mouse over it → bright right away
        Assert.False(guard.IsDimmed);
        guard.Update(t.AddMinutes(20.5));
        Assert.False(guard.IsDimmed);
        guard.Update(t.AddMinutes(21));
        Assert.True(guard.IsDimmed);

        guard.SetPinned(false, t.AddMinutes(22));
        Assert.False(guard.IsDimmed);

        var off = new OledGuard(t) { DimAfter = TimeSpan.Zero };
        off.SetPinned(true, t);
        off.Update(t.AddHours(5));
        Assert.False(off.IsDimmed); // 0 = off
    }

    [Fact]
    public void Pixel_shift_moves_a_pinned_panel_by_at_most_two_pixels()
    {
        var t = DateTimeOffset.UtcNow;
        var guard = new OledGuard(t) { DimAfter = TimeSpan.Zero, PixelShift = true };
        Assert.Equal(new PixelPoint(0, 0), guard.Offset); // not pinned

        guard.SetPinned(true, t);
        var offsets = new HashSet<PixelPoint> { guard.Offset };
        for (var i = 1; i <= 12; i++)
        {
            Assert.True(guard.Update(t + (OledGuard.ShiftInterval * i)));
            offsets.Add(guard.Offset);
            Assert.InRange(Math.Abs(guard.Offset.X), 0, 2);
            Assert.InRange(Math.Abs(guard.Offset.Y), 0, 2);
        }

        Assert.True(offsets.Count >= 5);
        guard.PixelShift = false;
        Assert.Equal(new PixelPoint(0, 0), guard.Offset);
    }

    [Theory]
    [InlineData(false, false, false, true)] // first start without settings.json
    [InlineData(true, false, false, false)] // existing user: everything set up
    [InlineData(true, false, true, true)] // file present, but module not set up yet
    [InlineData(false, true, true, false)] // skipped
    public void Welcome_is_shown_only_when_needed(bool fileExisted, bool done, bool needsSetup, bool expected) =>
        Assert.Equal(expected, WelcomeRules.ShouldShow(fileExisted, done, needsSetup));

    [Fact]
    public void General_settings_clamp_columns_and_fall_back_to_the_computer_name()
    {
        Assert.Equal(3, new GeneralSettings { Columns = 1 }.EffectiveColumns);
        Assert.Equal(6, new GeneralSettings { Columns = 9 }.EffectiveColumns);
        Assert.Equal("PC", new GeneralSettings { DeviceName = "  " }.EffectiveDeviceName("PC"));
        Assert.Equal("Laptop", new GeneralSettings { DeviceName = " Laptop " }.EffectiveDeviceName("PC"));
    }
}

/// <summary>M7: welcome flow without settings.</summary>
public class WelcomeFlowTests
{
    [Fact]
    public void Welcome_goes_address_token_test_layout_and_needs_input_for_each_step()
    {
        var flow = new WelcomeFlow();
        Assert.Equal(WelcomeStep.Address, flow.Step);
        Assert.False(flow.Next(hasAddress: false, hasToken: false)); // no next without an address
        Assert.True(flow.Next(hasAddress: true, hasToken: false));
        Assert.Equal(WelcomeStep.Token, flow.Step);
        Assert.False(flow.Next(true, false)); // no next without a token

        flow.Back();
        Assert.Equal(WelcomeStep.Address, flow.Step);
        flow.Next(true, false);
        Assert.True(flow.Next(true, true));
        Assert.Equal(WelcomeStep.Test, flow.Step);
        Assert.Null(flow.TestPassed);
        Assert.False(flow.Next(true, true)); // test first

        flow.TestFinished(false);
        Assert.False(flow.Next(true, true));
        flow.ContinueAnyway(); // server off right now – continue anyway
        Assert.Equal(WelcomeStep.Layout, flow.Step);
        flow.LayoutDone();
        Assert.Equal(WelcomeStep.Done, flow.Step);
        Assert.False(flow.Skipped);
    }

    [Fact]
    public void Successful_test_continues_and_skip_ends_at_any_time()
    {
        var flow = new WelcomeFlow();
        flow.Next(true, true);
        flow.Next(true, true);
        flow.TestFinished(true);
        Assert.True(flow.Next(true, true));
        Assert.Equal(WelcomeStep.Layout, flow.Step);

        var skipped = new WelcomeFlow();
        skipped.Skip();
        Assert.Equal(WelcomeStep.Done, skipped.Step);
        Assert.True(skipped.Skipped);
    }
}
