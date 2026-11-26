// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using UeBar.Bar;

namespace UeBar.Bar.Tests;

/// <summary>P2-6-Fix 1: the picture of a single window and the text cursor through accessibility.</summary>
public sealed class WindowCaptureAndCaretTests
{
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
    public void Only_windows_really_on_the_screen_can_be_picked()
    {
        var bounds = new PixelRect(100, 100, 800, 600);
        Assert.True(WindowCaptureRules.IsSelectable(true, false, false, "Chrome_WidgetWin_1", bounds));
        Assert.False(WindowCaptureRules.IsSelectable(false, false, false, "Chrome_WidgetWin_1", bounds)); // hidden
        Assert.False(WindowCaptureRules.IsSelectable(true, true, false, "Notepad", bounds));              // minimized
        Assert.False(WindowCaptureRules.IsSelectable(true, false, true, "Notepad", bounds));              // cloaked (other desktop)
        Assert.False(WindowCaptureRules.IsSelectable(true, false, false, "Progman", bounds));             // the desktop
        Assert.False(WindowCaptureRules.IsSelectable(true, false, false, "WorkerW", bounds));
        Assert.False(WindowCaptureRules.IsSelectable(true, false, false, "Tiny", new PixelRect(0, 0, 8, 300)));
    }

    [Fact]
    public void The_invisible_frame_and_the_shadow_are_cut_off()
    {
        // Window rectangle with the invisible resize frame of 7 px left/right/bottom (Windows 10/11, 100 %).
        var window = new PixelRect(193, 100, 914, 607);
        var frame = new PixelRect(200, 100, 900, 600);
        Assert.Equal(new PixelRect(7, 0, 900, 600), WindowCaptureRules.VisiblePart(window, frame, 914, 607)); // PrintWindow, Windows 10

        // Windows.Graphics.Capture on Windows 11 gives only the visible frame: nothing is cut (found in the real test).
        Assert.Equal(new PixelRect(0, 0, 900, 600), WindowCaptureRules.VisiblePart(window, frame, 900, 600));
        Assert.Equal(new PixelRect(0, 0, 901, 599), WindowCaptureRules.VisiblePart(window, frame, 901, 599));

        // A surface of another size (scaling): proportional.
        Assert.Equal(new PixelRect(14, 0, 1800, 1200), WindowCaptureRules.VisiblePart(window, frame, 1828, 1214));

        // Maximized: the frame reaches beyond the monitor – clamped to the surface.
        Assert.Equal(new PixelRect(0, 0, 100, 50), WindowCaptureRules.VisiblePart(new PixelRect(0, 0, 100, 50), new PixelRect(-8, -8, 116, 66), 100, 50));
        Assert.Null(WindowCaptureRules.VisiblePart(window, new PixelRect(5000, 5000, 10, 10), 914, 607));
        Assert.Null(WindowCaptureRules.VisiblePart(window, frame, 0, 0));

        var image = Solid(10, 6, 1, 2, 3);
        image.Bgra[((2 * 10) + 3) * 4] = 99; // pixel (3, 2)
        var cut = WindowCaptureRules.Crop(image, new PixelRect(3, 2, 4, 3));
        Assert.Equal((4, 3), (cut.Width, cut.Height));
        Assert.Equal(99, Pixel(cut, 0, 0).B);
    }

    [Fact]
    public void Rounded_corners_only_on_windows_11_and_not_when_maximized()
    {
        Assert.Equal(8, WindowCaptureRules.CornerRadiusPixels(true, false, 0, 1.0));     // default
        Assert.Equal(12, WindowCaptureRules.CornerRadiusPixels(true, false, 2, 1.5));    // round, 150 %
        Assert.Equal(4, WindowCaptureRules.CornerRadiusPixels(true, false, 3, 1.0));     // small
        Assert.Equal(0, WindowCaptureRules.CornerRadiusPixels(true, false, 1, 1.0));     // do not round
        Assert.Equal(0, WindowCaptureRules.CornerRadiusPixels(true, true, 0, 1.0));      // maximized
        Assert.Equal(0, WindowCaptureRules.CornerRadiusPixels(false, false, null, 1.0)); // Windows 10
    }

    [Fact]
    public void Edges_are_clean_opaque_inside_transparent_outside_the_corners()
    {
        var image = Solid(40, 30, 200, 100, 50, a: 0); // apps sometimes leave alpha 0 in their surface
        WindowCaptureRules.CleanEdges(image, 8);
        Assert.Equal((200, 100, 50, 255), Pixel(image, 20, 15));     // inside: fully opaque
        Assert.Equal((0, 0, 0, 0), Pixel(image, 0, 0));               // the very corners: transparent, no shadow/desktop
        Assert.Equal((0, 0, 0, 0), Pixel(image, 39, 0));
        Assert.Equal((0, 0, 0, 0), Pixel(image, 0, 29));
        Assert.Equal((0, 0, 0, 0), Pixel(image, 39, 29));
        Assert.Equal(255, Pixel(image, 8, 0).A);                      // the edge after the curve
        Assert.Equal(255, Pixel(image, 4, 4).A);                      // inside the curve
        Assert.InRange(Pixel(image, 1, 3).A, (byte)1, (byte)254);     // soft edge on the curve

        var square = Solid(10, 10, 1, 1, 1, a: 7);
        WindowCaptureRules.CleanEdges(square, 0);
        Assert.All(Enumerable.Range(0, 100), i => Assert.Equal(255, square.Bgra[(i * 4) + 3]));
    }

    [Fact]
    public void An_empty_capture_is_recognized()
    {
        Assert.True(WindowCaptureRules.IsBlank(Solid(300, 200, 0, 0, 0)));
        Assert.False(WindowCaptureRules.IsBlank(Solid(300, 200, 255, 255, 255))); // an empty white page is a picture
        Assert.True(WindowCaptureRules.IsBlank(new CapturedPixels([], 0, 0)));
        var content = Solid(300, 200, 0, 0, 0);
        content.Bgra[((137 * 300) + 211) * 4] = 1; // a single pixel that is not black
        Assert.False(WindowCaptureRules.IsBlank(content));
    }

    [Fact]
    public void Capture_ways_in_order_graphics_capture_then_print_window_then_the_screen()
    {
        var picture = Solid(20, 20, 0, 0, 0);
        picture.Bgra[((10 * 20) + 10) * 4] = 200;
        var calls = new List<string>();
        var failures = new List<(WindowCaptureMethod, string)>();

        var first = WindowCaptureRules.FirstWorking(() => { calls.Add("wgc"); return picture; }, () => { calls.Add("pw"); return picture; });
        Assert.Equal(WindowCaptureMethod.GraphicsCapture, first!.Value.Method);
        Assert.Equal(["wgc"], calls); // PrintWindow is not even tried

        calls.Clear();
        var second = WindowCaptureRules.FirstWorking(
            () => { calls.Add("wgc"); throw new InvalidOperationException("not supported"); },
            () => { calls.Add("pw"); return picture; },
            (m, why) => failures.Add((m, why)));
        Assert.Equal(WindowCaptureMethod.PrintWindow, second!.Value.Method);
        Assert.Equal((WindowCaptureMethod.GraphicsCapture, "InvalidOperationException"), Assert.Single(failures));

        // Black from both (protected or GPU windows) → nothing: the picker cuts from the screen and says so.
        failures.Clear();
        Assert.Null(WindowCaptureRules.FirstWorking(() => Solid(20, 20, 0, 0, 0), () => null, (m, why) => failures.Add((m, why))));
        Assert.Equal([(WindowCaptureMethod.GraphicsCapture, "empty image"), (WindowCaptureMethod.PrintWindow, "no image")], failures);
    }

    // ------------------------------------------------------------------ text cursor

    private static readonly PixelRect Window = new(0, 0, 1920, 1080);

    [Fact]
    public void Old_interface_wins_where_it_works()
    {
        var locator = new CaretLocator();
        var automationAsked = false;
        var found = locator.Locate(() => new PixelRect(300, 200, 2, 20), () => { automationAsked = true; return new PixelRect(900, 900, 1, 20); }, Window);
        Assert.Equal((new PixelRect(300, 200, 2, 20), CaretSource.SystemCaret), found);
        Assert.False(automationAsked); // Notepad, Word, Photoshop stay exactly as in 1.5.0
    }

    [Fact]
    public void Ui_automation_comes_second_and_the_mouse_last()
    {
        var locator = new CaretLocator();
        Assert.Equal((new PixelRect(500, 349, 1, 28), CaretSource.UiAutomation), locator.Locate(() => null, () => new PixelRect(500, 349, 0, 28), Window));
        Assert.Equal(((PixelRect?)null, CaretSource.Mouse), locator.Locate(() => null, () => null, Window));
        Assert.Equal(((PixelRect?)null, CaretSource.Mouse), locator.Locate(() => throw new InvalidOperationException(), () => throw new InvalidOperationException("app error"), Window));
    }

    [Fact]
    public void Implausible_positions_count_as_nothing()
    {
        Assert.Null(CaretLocator.Plausible(new PixelRect(0, 0, 0, 0), Window));            // empty
        Assert.Null(CaretLocator.Plausible(new PixelRect(10, 10, 1, 900), Window));         // a whole document, no caret
        Assert.Null(CaretLocator.Plausible(new PixelRect(5000, 300, 1, 20), Window));       // not on the window
        Assert.Equal(new PixelRect(1950, 300, 1, 20), CaretLocator.Plausible(new PixelRect(1950, 300, 0, 20), Window)); // just beside it is fine
        Assert.Equal(new PixelRect(5000, 300, 1, 20), CaretLocator.Plausible(new PixelRect(5000, 300, 1, 20), null));
    }

    [Fact]
    public void A_slow_app_is_given_up_after_150_ms_and_never_piles_up_threads()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), CaretLocator.AutomationLimit);
        var locator = new CaretLocator();
        using var release = new ManualResetEventSlim();
        var started = 0;
        var watch = Stopwatch.StartNew();
        var found = locator.Locate(() => null, () =>
        {
            Interlocked.Increment(ref started);
            release.Wait(); // an app that hangs
            return new PixelRect(1, 1, 1, 20);
        }, Window);
        watch.Stop();
        Assert.Equal(CaretSource.Mouse, found.Source);
        Assert.InRange(watch.ElapsedMilliseconds, 140, 600);
        Assert.True(locator.IsBusy);

        // While it still hangs, the next opening does not start a second lookup – straight to the mouse.
        watch.Restart();
        Assert.Equal(CaretSource.Mouse, locator.Locate(() => null, () => { Interlocked.Increment(ref started); return new PixelRect(1, 1, 1, 20); }, Window).Source);
        Assert.True(watch.ElapsedMilliseconds < 100);
        Assert.Equal(1, Volatile.Read(ref started));

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => !locator.IsBusy, 2000));
        Assert.Equal(CaretSource.UiAutomation, locator.Locate(() => null, () => new PixelRect(1, 1, 1, 20), Window).Source);
    }
}
