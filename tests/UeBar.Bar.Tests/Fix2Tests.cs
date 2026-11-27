// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Bar;

namespace UeBar.Bar.Tests;

/// <summary>P2-6-Fix 2: click or drag, Ctrl+A, maximized windows, corners for old programs, the watchdog and sleep.</summary>
public sealed class Fix2Tests
{
    private static readonly IReadOnlyList<MonitorInfo> Monitors =
    [
        new("laptop", "Laptop", new PixelRect(0, 0, 1920, 1200), new PixelRect(0, 0, 1920, 1152), true, 1.5),
        new("oled", "OLED", new PixelRect(1920, -240, 5120, 1440), new PixelRect(1920, -240, 5120, 1392), false, 1.0),
    ];

    private static CapturedPixels Solid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            (data[i], data[i + 1], data[i + 2], data[i + 3]) = (b, g, r, a);
        }

        return new CapturedPixels(data, width, height);
    }

    private static (byte B, byte G, byte R, byte A) Pixel(CapturedPixels image, int x, int y)
    {
        var at = (y * image.Stride) + (x * 4);
        return (image.Bgra[at], image.Bgra[at + 1], image.Bgra[at + 2], image.Bgra[at + 3]);
    }

    [Fact]
    public void Up_to_4_px_is_a_click_that_takes_the_window_from_5_px_on_it_is_a_drag()
    {
        var start = new PixelPoint(500, 500);
        Assert.True(ScreenSelection.IsClick(start, new PixelPoint(504, 496)));
        Assert.False(ScreenSelection.IsClick(start, new PixelPoint(505, 500)));
        Assert.False(ScreenSelection.IsClick(start, new PixelPoint(500, 495)));
    }

    [Fact]
    public void Ctrl_a_takes_the_monitor_under_the_mouse_with_its_size_and_scale()
    {
        Assert.True(PickerKeys.IsSelectAll(HotkeyModifiers.Control, 'A'));
        Assert.False(PickerKeys.IsSelectAll(HotkeyModifiers.None, 'A'));                          // plain "a"
        Assert.False(PickerKeys.IsSelectAll(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'A'));

        var oled = PickerKeys.SelectAllMonitor(new PixelPoint(4000, 100), Monitors)!;
        Assert.Equal((new PixelRect(1920, -240, 5120, 1440), 1.0), (oled.Bounds, oled.Scale));
        var laptop = PickerKeys.SelectAllMonitor(new PixelPoint(100, 100), Monitors)!;
        Assert.Equal((new PixelRect(0, 0, 1920, 1200), 1.5), (laptop.Bounds, laptop.Scale));
        Assert.Equal("laptop", PickerKeys.SelectAllMonitor(null, Monitors)!.Id); // no mouse position: the primary one
    }

    [Fact]
    public void A_maximized_window_loses_the_part_beyond_its_monitor()
    {
        // Maximized on the laptop: the frame reaches 8 px over every edge.
        var frame = new PixelRect(-8, -8, 1936, 1168);
        var image = Solid(1936, 1168, 10, 20, 30);
        image.Bgra[((8 * 1936) + 8) * 4] = 99; // first pixel on the monitor
        var (cut, bounds) = WindowCaptureRules.ClipToMonitor(image, frame, new PixelRect(0, 0, 1920, 1152));
        Assert.Equal(new PixelRect(0, 0, 1920, 1152), bounds);
        Assert.Equal((1920, 1152), (cut.Width, cut.Height));
        Assert.Equal(99, Pixel(cut, 0, 0).B);

        // A normal window on the monitor stays as it is.
        var inside = Solid(10, 10, 1, 2, 3);
        Assert.Same(inside, WindowCaptureRules.ClipToMonitor(inside, new PixelRect(100, 100, 10, 10), Monitors[0].Bounds).Image);
    }

    [Fact]
    public void Old_bitmap_programs_get_the_corners_in_the_window_color_png_keeps_them_transparent()
    {
        var image = Solid(40, 30, 200, 100, 50);
        WindowCaptureRules.CleanEdges(image, 8);
        Assert.True(WindowCaptureRules.HasTransparency(image));

        var flat = WindowCaptureRules.FlattenCorners(image);
        Assert.False(WindowCaptureRules.HasTransparency(flat));
        Assert.Equal((200, 100, 50, 255), Pixel(flat, 0, 0));    // corner in the edge color, not black
        Assert.Equal((200, 100, 50, 255), Pixel(flat, 39, 29));
        Assert.Equal((0, 0, 0, 0), Pixel(image, 0, 0));           // the original (PNG) stays transparent
        Assert.False(WindowCaptureRules.HasTransparency(Solid(5, 5, 1, 1, 1)));

        // Top corners take the top row's color (title bar), bottom corners the bottom row's.
        var twoTone = Solid(20, 20, 0, 0, 255);
        for (var y = 10; y < 20; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                var at = (y * twoTone.Stride) + (x * 4);
                (twoTone.Bgra[at], twoTone.Bgra[at + 1], twoTone.Bgra[at + 2]) = (255, 0, 0);
            }
        }

        WindowCaptureRules.CleanEdges(twoTone, 4);
        var flatTwo = WindowCaptureRules.FlattenCorners(twoTone);
        Assert.Equal((0, 0, 255, 255), Pixel(flatTwo, 0, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(flatTwo, 0, 19));
    }

    // ------------------------------------------------------------------ watchdog and sleep

    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(10);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void A_real_hang_is_reported_once()
    {
        var clock = new HangClock(Threshold, Gap);
        clock.Tick(S(0), out _);
        clock.ProbeSent(S(0));
        for (var t = 1; t < 5; t++)
        {
            Assert.Equal(HangVerdict.None, clock.Tick(S(t), out _));
        }

        Assert.Equal(HangVerdict.Hang, clock.Tick(S(5), out var waiting));
        Assert.Equal(S(5), waiting);
        Assert.Equal(HangVerdict.None, clock.Tick(S(6), out _)); // not again
        Assert.Equal(S(7), clock.Answered(S(7)));
        Assert.False(clock.HasProbe);
    }

    [Fact]
    public void Sleep_for_54_minutes_is_no_hang()
    {
        // Like 26.11.: the probe is queued, then Modern Standby pauses the whole app (watchdog included).
        var clock = new HangClock(Threshold, Gap);
        clock.Tick(S(0), out _);
        clock.ProbeSent(S(0));
        clock.Tick(S(1), out _);
        Assert.Equal(HangVerdict.Resumed, clock.Tick(S(1 + 3255), out var gap)); // first tick after waking up
        Assert.Equal(S(3255), gap);
        Assert.False(clock.HasProbe);                                              // the old probe does not count
        clock.ProbeSent(S(3256));
        Assert.Equal(HangVerdict.None, clock.Tick(S(3257), out _));
        Assert.Equal(S(1), clock.Answered(S(3257)));                               // the UI thread answers normally
    }

    [Fact]
    public void Suspend_pauses_and_resume_counts_from_new()
    {
        var clock = new HangClock(Threshold, Gap);
        clock.Tick(S(0), out _);
        clock.ProbeSent(S(0));
        clock.Suspend();
        Assert.True(clock.IsSuspended);
        Assert.False(clock.HasProbe);
        for (var t = 1; t <= 8; t++)
        {
            Assert.Equal(HangVerdict.None, clock.Tick(S(t), out _)); // ticks while going to sleep count nothing
        }

        clock.Resume(S(600));
        Assert.False(clock.IsSuspended);
        Assert.Equal(HangVerdict.None, clock.Tick(S(601), out _));
        clock.ProbeSent(S(601));
        Assert.Equal(HangVerdict.None, clock.Tick(S(605), out _));
        Assert.Equal(HangVerdict.Hang, clock.Tick(S(606), out _)); // a real hang after waking up is still found
    }

    [Fact]
    public void Display_off_without_sleep_changes_nothing()
    {
        // The app keeps running with the screen off: regular ticks, the UI thread answers – never a report.
        var clock = new HangClock(Threshold, Gap);
        for (var t = 0; t < 600; t++)
        {
            Assert.NotEqual(HangVerdict.Hang, clock.Tick(S(t), out _));
            if (!clock.HasProbe)
            {
                clock.ProbeSent(S(t));
            }
            else
            {
                clock.Answered(S(t));
            }
        }
    }
}
