// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.Bar.Tests;

/// <summary>P2-5: where pins go (per monitor, scaling, missing monitor), snapping, dimming, fullscreen.</summary>
public sealed class PinGeometryTests
{
    // Laptop (primary, 150 %) and the OLED to its right, higher up (100 %).
    private static readonly MonitorInfo Laptop = new("laptop", "Laptop", new PixelRect(0, 0, 1920, 1200), new PixelRect(0, 0, 1920, 1152), true, 1.5);
    private static readonly MonitorInfo Oled = new("oled", "G8", new PixelRect(1920, -240, 5120, 1440), new PixelRect(1920, -240, 5120, 1392), false, 1.0);
    private static readonly IReadOnlyList<MonitorInfo> Both = [Laptop, Oled];

    [Fact]
    public void Placement_is_per_monitor_with_the_size_in_device_independent_pixels()
    {
        var onOled = new PixelRect(3000, 100, 300, 170);
        var placement = PinGeometry.ToPlacement(onOled, Both);

        Assert.Equal(new PinPlacement { Monitor = "oled", X = 1080, Y = 340, Width = 300, Height = 170 }, placement);
        Assert.Equal(onOled, PinGeometry.Resolve(placement, Both));

        var onLaptop = PinGeometry.ToPlacement(new PixelRect(100, 100, 450, 255), Both);
        Assert.Equal((300.0, 170.0), (onLaptop.Width, onLaptop.Height)); // 150 %: same size to the eye
        Assert.Equal(new PixelRect(100, 100, 450, 255), PinGeometry.Resolve(onLaptop, Both));
    }

    [Fact]
    public void A_missing_monitor_puts_the_pin_on_the_primary_one_fully_visible()
    {
        // Saved on the OLED far right – away from the desk only the laptop is there.
        var placement = new PinPlacement { Monitor = "oled", X = 4900, Y = 100, Width = 300, Height = 200 };

        var rect = PinGeometry.Resolve(placement, [Laptop])!.Value;

        Assert.Equal(new PixelRect(1920 - 450, 100, 450, 300), rect); // scaled to 150 %, moved into the work area
        Assert.True(Laptop.WorkArea.Contains(rect));

        // Back at the desk: the same placement lands where it was.
        Assert.Equal(new PixelRect(7040 - 300, -240 + 100, 300, 200), PinGeometry.Resolve(placement, Both)); // only shifted to stay inside
    }

    [Fact]
    public void A_screenshot_pin_covers_exactly_the_captured_area()
    {
        var pin = new PinRecord { Kind = PinKind.Image, PixelWidth = 400, PixelHeight = 300, Source = new PinScreenArea(2100, 50, 400, 300) };

        Assert.Equal(new PixelRect(2100, 50, 400, 300), PinGeometry.Initial(pin, Both, new PixelPoint(10, 10), 0));

        var tiny = new PinRecord { Kind = PinKind.Image, Source = new PinScreenArea(10, 10, 12, 8) };
        Assert.Equal(new PixelRect(10, 10, 60, 60), PinGeometry.Initial(tiny, Both, null, 0)); // at least 40 px (here 150 %)
    }

    [Fact]
    public void Images_without_a_source_show_in_real_size_and_text_top_right()
    {
        var big = new PinRecord { Kind = PinKind.Image, PixelWidth = 4000, PixelHeight = 1000 };
        var rect = PinGeometry.Initial(big, Both, new PixelPoint(3000, 0), 0);
        Assert.Equal(new PixelRect(1920 + ((5120 - 3072) / 2), -240 + ((1392 - 768) / 2), 3072, 768), rect); // 60 % of the work area at most

        var text = PinGeometry.Initial(new PinRecord { Kind = PinKind.Text, Text = "x" }, Both, new PixelPoint(100, 100), 0);
        Assert.Equal(new PixelRect(1920 - 450 - 48, 48, 450, 255), text);
        var second = PinGeometry.Initial(new PinRecord { Kind = PinKind.Text, Text = "y" }, Both, new PixelPoint(100, 100), 1);
        Assert.Equal((text.X - 42, text.Y + 42), (second.X, second.Y)); // cascades
    }

    [Fact]
    public void Moving_snaps_to_work_area_edges_within_the_distance()
    {
        var areas = Both.Select(m => m.WorkArea).ToList();

        Assert.Equal(new PixelRect(0, 300, 200, 100), PinGeometry.SnapMove(new PixelRect(12, 300, 200, 100), areas, 14));
        Assert.Equal(new PixelRect(1720, 1052, 200, 100), PinGeometry.SnapMove(new PixelRect(1709, 1047, 200, 100), areas, 14)); // bottom right
        Assert.Equal(new PixelRect(40, 300, 200, 100), PinGeometry.SnapMove(new PixelRect(40, 300, 200, 100), areas, 14)); // too far: stays
        Assert.Equal(new PixelRect(1920, 300, 200, 100), PinGeometry.SnapMove(new PixelRect(1930, 300, 200, 100), areas, 14)); // edge between the monitors
    }

    [Fact]
    public void Resizing_snaps_only_the_edges_that_move()
    {
        var areas = Both.Select(m => m.WorkArea).ToList();

        var rect = PinGeometry.SnapResize(PixelRect.FromEdges(5, 300, 1910, 500), areas, 14, left: false, top: false, right: true, bottom: false);

        Assert.Equal(PixelRect.FromEdges(5, 300, 1920, 500), rect); // the left edge would be near, but does not move
    }

    [Fact]
    public void Pins_on_a_monitor_with_a_fullscreen_app_are_hidden_only_there_and_only_if_wanted()
    {
        var onOled = new PixelRect(3000, 0, 300, 200);
        var onLaptop = new PixelRect(100, 100, 300, 200);

        Assert.True(PinGeometry.HiddenByFullscreen(onOled, Both, ["oled"], enabled: true));
        Assert.False(PinGeometry.HiddenByFullscreen(onLaptop, Both, ["oled"], enabled: true));
        Assert.False(PinGeometry.HiddenByFullscreen(onOled, Both, ["oled"], enabled: false));
        Assert.False(PinGeometry.HiddenByFullscreen(onOled, Both, [], enabled: true));
    }

    [Fact]
    public void Default_settings()
    {
        var settings = new PinSettings();

        Assert.Equal((TimeSpan.FromSeconds(60), 0.35, true, true, "Win+Alt+P"), (settings.DimAfter, settings.EffectiveDimLevel, settings.PixelShift, settings.HideInFullscreen, settings.Hotkey));
        Assert.Equal(TimeSpan.Zero, new PinSettings { DimAfterSeconds = 0 }.DimAfter);
        Assert.Equal(0.1, new PinSettings { DimLevel = 0 }.EffectiveDimLevel);
        Assert.Equal(TimeSpan.FromSeconds(5), new PinSettings { DimAfterSeconds = 1 }.DimAfter);
    }
}

/// <summary>P2-5: dimming (OLED) and pixel shift of the pins.</summary>
public sealed class PinGlowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_pin_dims_after_the_time_without_the_mouse_and_lights_up_at_once()
    {
        var glow = new PinGlow(T0) { DimAfter = TimeSpan.FromSeconds(60) };
        glow.Track("a", T0);

        Assert.Empty(glow.Update(T0.AddSeconds(59)).Changed);
        Assert.Equal(TimeSpan.FromSeconds(1), glow.NextDue(T0.AddSeconds(59)));
        Assert.Equal(["a"], glow.Update(T0.AddSeconds(60)).Changed);
        Assert.True(glow.IsDimmed("a"));

        Assert.True(glow.Hover("a", inside: true, T0.AddSeconds(70))); // bright again right away
        Assert.False(glow.IsDimmed("a"));
        Assert.Empty(glow.Update(T0.AddSeconds(500)).Changed); // stays bright while the mouse is over it

        glow.Hover("a", inside: false, T0.AddSeconds(500));
        Assert.Empty(glow.Update(T0.AddSeconds(559)).Changed);
        Assert.Equal(["a"], glow.Update(T0.AddSeconds(560)).Changed); // the time starts when the mouse leaves
    }

    [Fact]
    public void Touching_a_pin_starts_the_time_over_and_zero_never_dims()
    {
        var glow = new PinGlow(T0) { DimAfter = TimeSpan.FromSeconds(30), PixelShift = false };
        glow.Track("a", T0);
        glow.Update(T0.AddSeconds(31));
        Assert.True(glow.Touch("a", T0.AddSeconds(40)));
        Assert.Empty(glow.Update(T0.AddSeconds(69)).Changed);

        var never = new PinGlow(T0) { DimAfter = TimeSpan.Zero, PixelShift = false };
        never.Track("b", T0);
        Assert.Empty(never.Update(T0.AddHours(5)).Changed);
        Assert.Null(never.NextDue(T0)); // nothing to wait for: no timer
    }

    [Fact]
    public void Pixel_shift_moves_by_at_most_two_pixels_every_three_minutes()
    {
        var glow = new PinGlow(T0) { DimAfter = TimeSpan.Zero };
        glow.Track("a", T0);
        var offsets = new List<PixelPoint> { glow.Offset };
        for (var i = 1; i <= 12; i++)
        {
            var (_, shifted) = glow.Update(T0.AddMinutes(3 * i));
            Assert.True(shifted);
            offsets.Add(glow.Offset);
        }

        Assert.All(offsets, o => Assert.True(Math.Abs(o.X) <= 2 && Math.Abs(o.Y) <= 2));
        Assert.True(offsets.Distinct().Count() > 5);
        Assert.False(glow.Update(T0.AddMinutes(37)).Shifted); // not yet three minutes again

        glow.PixelShift = false;
        Assert.Equal(new PixelPoint(0, 0), glow.Offset);
    }
}

/// <summary>P2-5: picking a screen area across monitors with different scaling (WPF-free part).</summary>
public sealed class ScreenSelectionTests
{
    private static readonly MonitorInfo Laptop = new("laptop", "Laptop", new PixelRect(0, 0, 1920, 1200), new PixelRect(0, 0, 1920, 1152), true, 1.5);
    private static readonly MonitorInfo Oled = new("oled", "G8", new PixelRect(1920, -240, 5120, 1440), new PixelRect(1920, -240, 5120, 1392), false, 1.0);

    [Fact]
    public void Dragging_across_two_monitors_gives_one_rectangle_in_physical_pixels()
    {
        var desktop = ScreenSelection.VirtualBounds([Laptop, Oled]);
        Assert.Equal(PixelRect.FromEdges(0, -240, 7040, 1200), desktop);

        var area = ScreenSelection.FromDrag(new PixelPoint(2400, 300), new PixelPoint(1500, -100), desktop);

        Assert.Equal(PixelRect.FromEdges(1500, -100, 2401, 301), area); // both corners belong to it
        Assert.Equal(PixelRect.FromEdges(0, -240, 101, 1200), ScreenSelection.FromDrag(new PixelPoint(100, 2000), new PixelPoint(-50, -400), desktop)); // clipped to the desktop
    }

    [Fact]
    public void Each_overlay_maps_physical_pixels_to_its_own_scaling()
    {
        var area = PixelRect.FromEdges(1500, -100, 2401, 301);

        // Laptop overlay: 1920 × 1200 physical = 1280 × 800 device-independent (150 %).
        var onLaptop = ScreenSelection.ToLocal(ScreenSelection.Clip(area, Laptop.Bounds), Laptop.Bounds, 1280, 800);
        Assert.Equal((1000.0, 0.0, 280.0, 200.666667), (Math.Round(onLaptop.X, 6), Math.Round(onLaptop.Y, 6), Math.Round(onLaptop.Width, 6), Math.Round(onLaptop.Height, 6)));

        // OLED overlay at 100 %: physical = local, only shifted.
        var onOled = ScreenSelection.ToLocal(ScreenSelection.Clip(area, Oled.Bounds), Oled.Bounds, 5120, 1440);
        Assert.Equal((0.0, 140.0, 481.0, 401.0), onOled);

        Assert.Equal(new PixelPoint(1500, 600), ScreenSelection.ToPhysical(1000, 400, Laptop.Bounds, 1280, 800));
    }

    [Fact]
    public void A_click_is_a_tiny_movement_and_the_bar_goes_below_above_or_inside()
    {
        Assert.True(ScreenSelection.IsClick(new PixelPoint(10, 10), new PixelPoint(14, 6)));
        Assert.False(ScreenSelection.IsClick(new PixelPoint(10, 10), new PixelPoint(15, 10)));

        var monitor = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(new PixelPoint(200, 410), ScreenSelection.ActionBarPosition(new PixelRect(100, 100, 600, 300), 400, 50, monitor, 10)); // below
        Assert.Equal(new PixelPoint(200, 840), ScreenSelection.ActionBarPosition(new PixelRect(100, 900, 600, 170), 400, 50, monitor, 10)); // above
        Assert.Equal(new PixelPoint(760, 1020), ScreenSelection.ActionBarPosition(new PixelRect(0, 0, 1920, 1080), 400, 50, monitor, 10)); // whole screen: inside at the bottom
        Assert.Equal(10, ScreenSelection.ActionBarPosition(new PixelRect(0, 100, 50, 50), 400, 50, monitor, 10).X); // never off the screen
    }

    [Fact]
    public void Monitors_as_the_sdk_describes_them()
    {
        var sdk = ScreenSelection.ToSdk([Laptop, Oled]);

        Assert.Equal(new ScreenMonitor("oled", new ScreenRect(1920, -240, 5120, 1440), new ScreenRect(1920, -240, 5120, 1392), 1.0, false), sdk[1]);
        Assert.Equal(new PixelRect(1, 2, 3, 4), ScreenSelection.FromSdk(new ScreenRect(1, 2, 3, 4)));
    }
}

/// <summary>P2-5: several global hotkeys with names (pins, plugins).</summary>
public sealed class HotkeySetTests
{
    private readonly Dictionary<int, (int Mods, int Vk)> _registered = [];
    private readonly HashSet<int> _taken = [];

    private HotkeySet NewSet() => new(
        (id, mods, vk) =>
        {
            if (_taken.Contains(vk))
            {
                return false;
            }

            _registered[id] = (mods, vk);
            return true;
        },
        id => _registered.Remove(id));

    [Fact]
    public void Registered_hotkeys_run_their_action_and_a_taken_one_says_so()
    {
        var set = NewSet();
        var pressed = 0;

        Assert.Equal(HotkeyRegistration.Registered, set.Set("pins", "Win+Alt+P", () => pressed++).Result);
        _taken.Add('S');
        var shot = set.Set("plugin:screenshot:hotkey", "Win+Alt+S", () => { });

        Assert.Equal(HotkeyRegistration.InUse, shot.Result);
        Assert.Contains("S", shot.Message, StringComparison.Ordinal);
        Assert.Single(_registered);
        Assert.True(set.Press(_registered.Keys.Single()));
        Assert.Equal(1, pressed);
        Assert.False(set.Press(999));
        Assert.Equal(HotkeyRegistration.Invalid, set.Set("x", "", () => { }).Result); // empty = none
    }

    [Fact]
    public void Changing_keeps_the_old_hotkey_when_the_new_one_is_taken()
    {
        var set = NewSet();
        set.Set("pins", "Win+Alt+P", () => { });
        _taken.Add('Q');

        Assert.Equal(HotkeyRegistration.InUse, set.TryChange("pins", "Win+Alt+Q", () => { }).Result);
        Assert.Equal("Win+Alt+P", set.TextOf("pins"));
        Assert.Equal('P', _registered.Values.Single().Vk);

        Assert.Equal(HotkeyRegistration.Registered, set.TryChange("pins", "Ctrl+Alt+F9", () => { }).Result);
        Assert.Equal(0x78, _registered.Values.Single().Vk);
    }

    [Fact]
    public void Recording_pauses_all_and_a_change_meanwhile_is_tested_then_registered_after()
    {
        var set = NewSet();
        set.Set("pins", "Win+Alt+P", () => { });
        set.Set("shot", "Win+Alt+S", () => { });

        set.Suspend(true);
        Assert.Empty(_registered);
        Assert.Equal(HotkeyRegistration.Registered, set.TryChange("shot", "Win+Alt+X", () => { }).Result);
        Assert.Empty(_registered); // still paused

        set.Suspend(false);
        Assert.Equal(['P', 'X'], _registered.Values.Select(v => v.Vk).Order().Select(v => (char)v));
        set.Remove("pins");
        Assert.Equal('X', (char)_registered.Values.Single().Vk);
    }
}
