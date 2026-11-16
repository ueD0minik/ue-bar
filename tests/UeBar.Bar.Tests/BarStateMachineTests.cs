// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar.Tests;

public class BarStateMachineTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-05T12:00:00Z");

    private static readonly MonitorInfo Primary =
        new("mon-a", "A", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), true, 1.0);

    private static readonly MonitorInfo Second =
        new("mon-b", "B", new PixelRect(2560, 0, 3840, 2160), new PixelRect(2560, 0, 3840, 2112), false, 1.5);

    /// <summary>Center of the trigger zone at the top edge of A.</summary>
    private static readonly PixelPoint EdgeA = new(1280, 0);

    private static readonly PixelPoint Away = new(1280, 800);

    private static BarStateMachine Create(BarSettings? settings = null, params MonitorInfo[] monitors)
    {
        var machine = new BarStateMachine();
        machine.Configure(monitors.Length == 0 ? [Primary, Second] : monitors, settings ?? new BarSettings());
        return machine;
    }

    /// <summary>Simulates polling every 10 ms.</summary>
    private static DateTimeOffset Hold(BarStateMachine m, DateTimeOffset from, PixelPoint p, int ms)
    {
        var t = from;
        for (var elapsed = 0; elapsed <= ms; elapsed += 10)
        {
            t = from.AddMilliseconds(elapsed);
            m.Update(t, p);
        }

        return t;
    }

    /// <summary>Brings the bar into the pill state and sets the pill area like the window.</summary>
    private static DateTimeOffset ToPill(BarStateMachine m)
    {
        var t = Hold(m, T0, EdgeA, 160);
        m.BarBounds = new PixelRect(1180, 8, 200, 40);
        return t;
    }

    [Fact]
    public void Mouse_100ms_at_edge_does_nothing()
    {
        var m = Create();

        Hold(m, T0, EdgeA, 100);

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Mouse_150ms_at_edge_shows_pill()
    {
        var m = Create();
        var changes = new List<BarState>();
        m.StateChanged += (_, e) => changes.Add(e.NewState);

        Hold(m, T0, EdgeA, 150);

        Assert.Equal(BarState.Pill, m.State);
        Assert.Equal("mon-a", m.ActiveMonitor!.Id);
        Assert.Equal([BarState.Pill], changes);
    }

    [Fact]
    public void Leaving_edge_before_dwell_resets_timer()
    {
        var m = Create();

        var t = Hold(m, T0, EdgeA, 100);
        m.Update(t.AddMilliseconds(10), Away);
        Hold(m, t.AddMilliseconds(20), EdgeA, 100);

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Edge_outside_middle_60_percent_does_not_trigger()
    {
        var m = Create();

        Hold(m, T0, new PixelPoint(100, 0), 500); // far top left

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Second_pixel_row_still_triggers_third_does_not()
    {
        var m1 = Create();
        Hold(m1, T0, new PixelPoint(1280, 1), 200);
        Assert.Equal(BarState.Pill, m1.State);

        var m2 = Create();
        Hold(m2, T0, new PixelPoint(1280, 2), 200);
        Assert.Equal(BarState.Hidden, m2.State);
    }

    [Fact]
    public void Mouse_on_pill_opens_panel()
    {
        var m = Create();
        var t = ToPill(m);

        m.Update(t.AddMilliseconds(20), new PixelPoint(1280, 20));

        Assert.Equal(BarState.Open, m.State);
    }

    [Fact]
    public void Pill_hides_after_delay_when_mouse_leaves()
    {
        var m = Create();
        var t = ToPill(m);

        var end = Hold(m, t.AddMilliseconds(10), Away, 990);
        Assert.Equal(BarState.Pill, m.State);

        m.Update(end.AddMilliseconds(20), Away);
        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Mouse_leaving_panel_hides_after_one_second()
    {
        var m = Create();
        var t = ToPill(m);
        m.Update(t, new PixelPoint(1280, 20));
        m.BarBounds = new PixelRect(1065, 8, 430, 400);
        Assert.Equal(BarState.Open, m.State);

        var leave = t.AddMilliseconds(50);
        Hold(m, leave, Away, 900);
        Assert.Equal(BarState.Open, m.State);

        m.Update(leave.AddMilliseconds(1000), Away);
        Assert.Equal(BarState.Hidden, m.State);
        Assert.Null(m.ActiveMonitor);
    }

    [Fact]
    public void Returning_to_panel_cancels_hide()
    {
        var m = Create();
        var t = ToPill(m);
        m.Update(t, new PixelPoint(1280, 20));
        m.BarBounds = new PixelRect(1065, 8, 430, 400);

        Hold(m, t.AddMilliseconds(10), Away, 600);
        Hold(m, t.AddMilliseconds(620), new PixelPoint(1200, 200), 50);
        Hold(m, t.AddMilliseconds(700), Away, 900);

        Assert.Equal(BarState.Open, m.State);
    }

    [Fact]
    public void Gap_between_edge_and_pill_counts_as_on_bar()
    {
        var m = Create();
        var t = ToPill(m);

        // Mouse between the edge (y=0) and the pill (y=8): must not hide
        Hold(m, t.AddMilliseconds(10), new PixelPoint(1280, 5), 2000);

        Assert.Equal(BarState.Pill, m.State);
    }

    [Fact]
    public void Hide_delay_zero_hides_immediately()
    {
        var m = Create(new BarSettings { HideDelayMs = 0 });
        var t = ToPill(m);

        m.Update(t.AddMilliseconds(10), Away);

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Pinned_never_hides()
    {
        var m = Create();
        var t = ToPill(m);
        m.Update(t, new PixelPoint(1280, 20));
        Assert.True(m.Pin());

        Hold(m, t.AddMilliseconds(10), Away, 60_000);

        Assert.Equal(BarState.Pinned, m.State);
    }

    [Fact]
    public void Unpin_returns_to_open_and_hides_after_delay()
    {
        var m = Create();
        var t = ToPill(m);
        m.Update(t, new PixelPoint(1280, 20));
        m.Pin();

        var unpinAt = t.AddSeconds(10);
        Assert.True(m.Unpin(unpinAt));
        Assert.Equal(BarState.Open, m.State);

        m.Update(unpinAt.AddMilliseconds(1000), Away);
        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Fullscreen_blocks_trigger()
    {
        var m = Create();
        m.IsFullscreen = _ => true;

        Hold(m, T0, EdgeA, 1000);

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Fullscreen_block_can_be_disabled()
    {
        var m = Create(new BarSettings { BlockInFullscreen = false });
        m.IsFullscreen = _ => true;

        Hold(m, T0, EdgeA, 200);

        Assert.Equal(BarState.Pill, m.State);
    }

    [Fact]
    public void Fullscreen_is_only_checked_after_dwell()
    {
        var m = Create();
        var checks = 0;
        m.IsFullscreen = _ =>
        {
            checks++;
            return false;
        };

        Hold(m, T0, EdgeA, 100);
        Assert.Equal(0, checks);
        Hold(m, T0.AddMilliseconds(110), EdgeA, 50);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void Paused_never_triggers_and_hides_visible_bar()
    {
        var m = Create();
        ToPill(m);
        Assert.Equal(BarState.Pill, m.State);

        m.Configure(m.Monitors, new BarSettings { Paused = true });
        Assert.Equal(BarState.Hidden, m.State);

        Hold(m, T0.AddSeconds(5), EdgeA, 500);
        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Primary_mode_ignores_second_monitor_edge()
    {
        var m = Create();

        Hold(m, T0, new PixelPoint(2560 + 1920, 0), 500);

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Under_mouse_mode_triggers_on_any_monitor()
    {
        var m = Create(new BarSettings { MonitorMode = MonitorMode.UnderMouse });

        Hold(m, T0, new PixelPoint(2560 + 1920, 0), 200);

        Assert.Equal(BarState.Pill, m.State);
        Assert.Equal("mon-b", m.ActiveMonitor!.Id);
    }

    [Fact]
    public void Specific_monitor_mode_uses_that_monitor()
    {
        var m = Create(new BarSettings { MonitorMode = MonitorMode.Specific, MonitorId = "mon-b" });

        Hold(m, T0, EdgeA, 300);
        Assert.Equal(BarState.Hidden, m.State);

        Hold(m, T0.AddSeconds(1), new PixelPoint(2560 + 1920, 0), 200);
        Assert.Equal(BarState.Pill, m.State);
        Assert.Equal("mon-b", m.ActiveMonitor!.Id);
    }

    [Fact]
    public void Missing_specific_monitor_falls_back_to_primary()
    {
        var m = Create(new BarSettings { MonitorMode = MonitorMode.Specific, MonitorId = "does-not-exist" }, Primary);

        Hold(m, T0, EdgeA, 200);

        Assert.Equal(BarState.Pill, m.State);
        Assert.Equal("mon-a", m.ActiveMonitor!.Id);
    }

    [Fact]
    public void Unplugged_monitor_hides_open_bar()
    {
        var m = Create(new BarSettings { MonitorMode = MonitorMode.UnderMouse });
        Hold(m, T0, new PixelPoint(2560 + 1920, 0), 200);
        Assert.Equal("mon-b", m.ActiveMonitor!.Id);

        m.Configure([Primary], m.Settings);

        Assert.Equal(BarState.Hidden, m.State);
    }

    [Fact]
    public void Unplugged_monitor_moves_pinned_bar_to_primary()
    {
        var m = Create();
        m.ShowPinned(Second);
        BarStateChangedEventArgs? args = null;
        m.StateChanged += (_, e) => args = e;

        m.Configure([Primary], m.Settings);

        Assert.Equal(BarState.Pinned, m.State);
        Assert.Equal("mon-a", m.ActiveMonitor!.Id);
        Assert.Equal("mon-a", args!.Monitor!.Id);
    }

    [Fact]
    public void Show_pinned_on_startup()
    {
        var m = Create();

        m.ShowPinned(Second);

        Assert.Equal(BarState.Pinned, m.State);
        Assert.Equal("mon-b", m.ActiveMonitor!.Id);
    }
}
