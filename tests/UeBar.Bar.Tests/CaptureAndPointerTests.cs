// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar.Tests;

/// <summary>
/// P2-5-Fix 1, error 1: after a screen capture the bar no longer showed. Hiding for the capture set the opacity
/// to 0 even when the bar was collapsed, and the restore ran only when it had been visible. These tests run
/// every way a capture can end against a bar window model and check the bar can be seen when it expands.
/// </summary>
public sealed class CaptureSafetyTests
{
    /// <summary>The bar window as Windows sees it, with the same hide/restore steps as the app.</summary>
    private sealed class FakeBar : ICaptureHideTarget
    {
        public bool Visible { get; set; }

        public double Opacity { get; private set; } = 1;

        public bool Topmost { get; private set; } = true;

        public int Restores { get; private set; }

        public bool HideForCapture()
        {
            var was = Visible;
            Opacity = 0;
            return was;
        }

        public void RestoreAfterCapture()
        {
            Opacity = 1;
            Restores++;
        }

        /// <summary>Pill → panel: shown and placed topmost again (what BarWindow.Place does).</summary>
        public void Expand()
        {
            Visible = true;
            Topmost = true;
        }

        public BarWindowFacts Facts => new(Visible, false, Opacity, Topmost, false);
    }

    public enum Ending
    {
        Pin,
        Copy,
        Save,
        SaveAs,
        Ocr,
        Escape,
        Error,
        Exception,
        Cancelled,
    }

    private static Task<string?> Capture(Ending ending) => ending switch
    {
        Ending.Escape or Ending.Error => Task.FromResult<string?>(null),
        Ending.Exception => throw new InvalidOperationException("picker broke"),
        Ending.Cancelled => throw new OperationCanceledException(),
        _ => Task.FromResult<string?>(ending.ToString().ToLowerInvariant()),
    };

    public static TheoryData<Ending, bool> AllEndings()
    {
        var data = new TheoryData<Ending, bool>();
        foreach (var ending in Enum.GetValues<Ending>())
        {
            data.Add(ending, false); // the bar was collapsed – the case of the bug
            data.Add(ending, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllEndings))]
    public async Task After_every_capture_the_bar_expands_visibly(Ending ending, bool barWasVisible)
    {
        var bar = new FakeBar { Visible = barWasVisible };
        bool? seenVisible = null;

        try
        {
            await CaptureSafety.RunWithBarHiddenAsync(bar, wasVisible =>
            {
                seenVisible = wasVisible;
                Assert.Equal(0.0, bar.Opacity); // out of sight while the screen is frozen
                return Capture(ending);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Assert.True(ending is Ending.Exception or Ending.Cancelled);
        }

        Assert.Equal(barWasVisible, seenVisible);
        Assert.Equal(1, bar.Restores);
        bar.Expand();
        Assert.Empty(BarWindowHealth.Problems(bar.Facts));
    }

    [Fact]
    public async Task Several_captures_in_a_row_leave_the_bar_visible()
    {
        var bar = new FakeBar();
        for (var i = 0; i < 3; i++)
        {
            await CaptureSafety.RunWithBarHiddenAsync(bar, _ => Task.FromResult(i));
        }

        bar.Expand();
        Assert.Equal(3, bar.Restores);
        Assert.Empty(BarWindowHealth.Problems(bar.Facts));
    }

    [Fact]
    public void The_visibility_check_names_every_problem()
    {
        Assert.Empty(BarWindowHealth.Problems(new BarWindowFacts(true, false, 1, true, false)));
        Assert.Equal(
            ["not visible", "cloaked", "opacity 0", "not topmost", "display affinity set"],
            BarWindowHealth.Problems(new BarWindowFacts(false, true, 0, false, true)));
        Assert.Equal(["opacity 0"], BarWindowHealth.Problems(new BarWindowFacts(true, false, 0, true, false))); // what 1.4.0 left behind
    }
}

/// <summary>P2-5-Fix 1: the one slider behaviour – dragging changes the value all the time, releasing commits once.</summary>
public sealed class SliderDragTests
{
    [Fact]
    public void Dragging_changes_the_value_continuously_and_commits_once_on_release()
    {
        var drag = new SliderDrag(20, 100, 1, 100);
        var live = new List<double>();
        var committed = new List<double>();
        drag.Live += live.Add;
        drag.Committed += committed.Add;

        drag.Down(0.5);
        drag.Move(0.4);
        drag.Move(0.25);
        drag.Move(0.25); // same place: no new value
        Assert.Empty(committed);
        drag.Up();

        Assert.Equal([60, 52, 40], live);
        Assert.Equal([40], committed);
        Assert.False(drag.IsDragging);
    }

    [Fact]
    public void Without_fixed_stops_every_percent_is_reachable_and_the_ends_hold()
    {
        var drag = new SliderDrag(20, 100, 1, 50);
        Assert.Equal(21, drag.Down(1.0 / 80));
        Assert.Equal(20, drag.Move(-3));
        Assert.Equal(100, drag.Move(7));
        Assert.Equal(73, drag.Move(53.0 / 80));
        drag.Up();
    }

    [Fact]
    public void The_wheel_moves_by_five_percent_and_a_value_from_outside_is_taken_only_when_not_dragging()
    {
        var drag = new SliderDrag(0, 100, 1, 50);
        Assert.Equal(55, drag.Wheel(120));
        Assert.Equal(50, drag.Wheel(-120));

        drag.Down(0.9);
        drag.SetExternal(10); // a module update while the user drags must not jump
        Assert.Equal(90, drag.Value);
        drag.Lost(); // capture lost (menu closed) = commit what was reached
        drag.SetExternal(10);
        Assert.Equal(10, drag.Value);
    }
}

/// <summary>P2-5-Fix 1: the mouse polling also watches pins that let clicks through – only while there are any.</summary>
public sealed class PointerWatchTests
{
    private static readonly PixelRect PinA = new(100, 100, 200, 100);
    private static readonly PixelRect PinB = new(500, 100, 200, 100);

    [Fact]
    public void Without_areas_nothing_is_watched()
    {
        var watch = new PointerWatch();
        Assert.False(watch.IsActive);
        Assert.Null(watch.Check(new PixelPoint(150, 150), ctrl: true));
    }

    [Fact]
    public void Ctrl_over_a_pin_makes_it_usable_and_releasing_or_leaving_ends_it()
    {
        var watch = new PointerWatch();
        Assert.True(watch.SetAreas([("a", PinA), ("b", PinB)]));
        Assert.True(watch.IsActive);

        Assert.Equal(new PointerChange("a", false, null), watch.Check(new PixelPoint(150, 150), ctrl: false)); // over it: brighten
        Assert.Null(watch.Check(new PixelPoint(160, 150), ctrl: false)); // nothing new
        Assert.Equal(new PointerChange("a", true, null), watch.Check(new PixelPoint(160, 150), ctrl: true)); // Ctrl: usable
        Assert.Equal(new PointerChange("a", false, null), watch.Check(new PixelPoint(160, 150), ctrl: false)); // released: through again
        Assert.Equal(new PointerChange("b", true, "a"), watch.Check(new PixelPoint(550, 150), ctrl: true)); // over the other one
        Assert.Equal(new PointerChange(null, false, "b"), watch.Check(new PixelPoint(900, 900), ctrl: true)); // Ctrl elsewhere counts for nothing
        Assert.Null(watch.Check(new PixelPoint(901, 900), ctrl: true));
    }

    [Fact]
    public void The_watch_stops_when_the_last_click_through_pin_is_gone()
    {
        var watch = new PointerWatch();
        watch.SetAreas([("a", PinA)]);
        watch.Check(new PixelPoint(150, 150), ctrl: true);

        Assert.True(watch.SetAreas([])); // active → inactive
        Assert.False(watch.IsActive);
        Assert.Null(watch.Check(new PixelPoint(150, 150), ctrl: true));
        Assert.False(watch.SetAreas([])); // no change
        Assert.True(watch.SetAreas([("a", PinA)]));
        Assert.Equal(new PointerChange("a", true, null), watch.Check(new PixelPoint(150, 150), ctrl: true)); // starts fresh
    }
}

/// <summary>P2-5-Fix 1: feedback on the user's own action comes out also during "Do not disturb" (setting, on by default).</summary>
public sealed class ActionFeedbackGateTests
{
    private static readonly AnnounceConditions DoNotDisturb = new(Paused: false, LockedOrDisplayOff: false, Fullscreen: false, DoNotDisturb: true);

    [Fact]
    public void Feedback_comes_out_during_do_not_disturb_as_the_apps_own_message()
    {
        var decision = NotificationGate.Decide(new NotificationRequest(false) { ActionFeedback = true }, DoNotDisturb, new BarSettings());

        Assert.True(decision.Show);
        Assert.True(decision.AsActionFeedback);
        Assert.True(new BarSettings().ActionFeedbackDuringDoNotDisturb);
    }

    [Fact]
    public void Other_notifications_stay_in_the_history_during_do_not_disturb()
    {
        var decision = NotificationGate.Decide(new NotificationRequest(false), DoNotDisturb, new BarSettings());

        Assert.False(decision.Show);
        Assert.False(decision.AsActionFeedback);
        Assert.Equal("do not disturb", decision.Reason);
    }

    [Fact]
    public void Switched_off_feedback_is_a_normal_notification_and_nothing_comes_out_while_locked()
    {
        var off = new BarSettings { ActionFeedbackDuringDoNotDisturb = false };
        var feedback = new NotificationRequest(false) { ActionFeedback = true };
        Assert.Equal((false, false), Pair(NotificationGate.Decide(feedback, DoNotDisturb, off)));
        Assert.Equal((true, false), Pair(NotificationGate.Decide(feedback, DoNotDisturb with { DoNotDisturb = false }, off)));
        Assert.Equal((false, false), Pair(NotificationGate.Decide(feedback, DoNotDisturb with { LockedOrDisplayOff = true }, new BarSettings())));

        static (bool, bool) Pair(NotificationDecision d) => (d.Show, d.AsActionFeedback);
    }
}

/// <summary>P2-5-Fix 1: "Save as" starts in the folder picked last time – per owner, per PC.</summary>
public sealed class SaveFoldersTests
{
    [Fact]
    public void The_last_folder_wins_while_it_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "uebar-savefolders", Guid.NewGuid().ToString("N"));
        var chosen = Path.Combine(root, "chosen");
        var initial = Path.Combine(root, "initial");
        Directory.CreateDirectory(chosen);
        Directory.CreateDirectory(initial);
        try
        {
            var folders = new SaveFolders();
            Assert.Equal(initial, folders.StartFolder("screenshot", initial, "fallback"));
            Assert.True(folders.Remember("screenshot", Path.Combine(chosen, "a.png")));
            Assert.False(folders.Remember("screenshot", Path.Combine(chosen, "b.png")));
            Assert.Equal(chosen, folders.StartFolder("screenshot", initial, "fallback"));
            Assert.Equal(initial, folders.StartFolder("pins", initial, "fallback")); // another owner has its own

            Directory.Delete(chosen);
            Assert.Equal(initial, folders.StartFolder("screenshot", initial, "fallback")); // gone → the given one
            Assert.Equal("fallback", folders.StartFolder("screenshot", Path.Combine(root, "missing"), "fallback"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
