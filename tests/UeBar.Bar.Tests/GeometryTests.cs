// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Tiles;

namespace UeBar.Bar.Tests;

public class GeometryTests
{
    // Monitor A on the left: 2560x1440 at 100 %, monitor B on the right: 3840x2160 at 150 %,
    // monitor C left of A with negative coordinates at 125 %.
    private static readonly MonitorInfo A = new("a", "A", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), true, 1.0);
    private static readonly MonitorInfo B = new("b", "B", new PixelRect(2560, -200, 3840, 2160), new PixelRect(2560, -200, 3840, 2112), false, 1.5);
    private static readonly MonitorInfo C = new("c", "C", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), false, 1.25);

    [Fact]
    public void Trigger_zone_is_middle_60_percent_of_top_rows()
    {
        var rect = BarGeometry.TriggerRect(A, new BarSettings());

        Assert.Equal(new PixelRect(512, 0, 1536, 2), rect);
    }

    [Fact]
    public void Trigger_zone_respects_monitor_offset()
    {
        var rect = BarGeometry.TriggerRect(B, new BarSettings { TriggerWidthPercent = 50 });

        Assert.Equal(new PixelRect(2560 + 960, -200, 1920, 2), rect);
    }

    [Fact]
    public void Top_center_uses_scale_of_target_monitor()
    {
        var onA = BarGeometry.TopCenter(A, 430, 300);
        var onB = BarGeometry.TopCenter(B, 430, 300);
        var onC = BarGeometry.TopCenter(C, 430, 300);

        Assert.Equal(new PixelRect(1065, 0, 430, 300), onA);
        Assert.Equal(new PixelRect(2560 + 1597, -200, 645, 450), onB);
        Assert.Equal(new PixelRect(-1920 + 691, 0, 538, 375), onC);
    }

    [Fact]
    public void Pinned_offset_is_clamped_into_monitor()
    {
        var rect = BarGeometry.AtOffset(A, new PixelPoint(2500, 1400), 430, 300);

        Assert.Equal(new PixelRect(2560 - 430, 1440 - 300, 430, 300), rect);
    }

    [Fact]
    public void Pinned_offset_round_trips()
    {
        var placed = BarGeometry.AtOffset(B, new PixelPoint(100, 50), 430, 300);

        Assert.Equal(new PixelPoint(100, 50), BarGeometry.OffsetIn(B, placed));
    }

    [Fact]
    public void Hit_area_extends_up_to_monitor_edge()
    {
        var area = BarGeometry.HitArea(new PixelRect(1000, 12, 200, 40), A, 6);

        Assert.True(area.Contains(new PixelPoint(1100, 0)));
        Assert.True(area.Contains(new PixelPoint(995, 30)));
        Assert.False(area.Contains(new PixelPoint(1100, 60)));
    }

    [Fact]
    public void Selector_falls_back_to_primary()
    {
        var monitors = new[] { C, A, B };

        Assert.Same(A, MonitorSelector.Primary(monitors));
        Assert.Same(A, MonitorSelector.TriggerCandidates(monitors, new BarSettings { MonitorMode = MonitorMode.Specific, MonitorId = "x" }).Single());
        Assert.Same(B, MonitorSelector.TriggerCandidates(monitors, new BarSettings { MonitorMode = MonitorMode.Specific, MonitorId = "B" }).Single());
        Assert.Equal(3, MonitorSelector.TriggerCandidates(monitors, new BarSettings { MonitorMode = MonitorMode.UnderMouse }).Count);
        Assert.Same(C, MonitorSelector.FromPoint(monitors, new PixelPoint(-5, 10)));
        Assert.Empty(MonitorSelector.TriggerCandidates([], new BarSettings()));
    }

    [Fact]
    public void Pinned_monitor_selection()
    {
        var monitors = new[] { A, B };

        Assert.Same(B, MonitorSelector.ForPinned(monitors, new BarSettings { PinnedMonitorId = "b" }));
        Assert.Same(A, MonitorSelector.ForPinned(monitors, new BarSettings { PinnedMonitorId = "weg" }));
    }

    [Fact]
    public void Fullscreen_rule_needs_whole_monitor()
    {
        Assert.True(FullscreenRules.CoversMonitor(new PixelRect(0, 0, 2560, 1440), A.Bounds));
        Assert.True(FullscreenRules.CoversMonitor(new PixelRect(-8, -8, 2576, 1456), A.Bounds));
        Assert.False(FullscreenRules.CoversMonitor(A.WorkArea, A.Bounds)); // maximized, taskbar visible
    }

    [Fact]
    public void Maximized_window_with_autohide_taskbar_is_not_fullscreen()
    {
        // Taskbar hides automatically: a maximized window covers the whole monitor
        Assert.False(FullscreenRules.IsFullscreenWindow(A.Bounds, A.Bounds, isMaximized: true, hasCaption: true));
        Assert.False(FullscreenRules.IsFullscreenWindow(A.Bounds, A.Bounds, isMaximized: true, hasCaption: false));
        Assert.False(FullscreenRules.IsFullscreenWindow(A.Bounds, A.Bounds, isMaximized: false, hasCaption: true));

        // Browser F11 / borderless game
        Assert.True(FullscreenRules.IsFullscreenWindow(A.Bounds, A.Bounds, isMaximized: false, hasCaption: false));
        Assert.False(FullscreenRules.IsFullscreenWindow(A.WorkArea, A.Bounds, isMaximized: false, hasCaption: false));
    }

    [Fact]
    public void Spring_overshoots_less_than_five_percent_and_ends_at_one()
    {
        Assert.Equal(0, Spring.Evaluate(0));
        Assert.Equal(1, Spring.Evaluate(1));
        Assert.InRange(Spring.MaxOvershoot, 0.001, 0.05);
        Assert.True(Spring.Evaluate(0.1) < Spring.Evaluate(0.2));
    }

    [Fact]
    public void Grid_packs_densely()
    {
        // Dummy suggestion: 1x1, 2x1, 2x2, 2x2, 1x1 in 4 columns
        var sizes = new[] { TileSize.Small, TileSize.Wide, TileSize.Large, TileSize.Large, TileSize.Small };

        var placements = GridPacker.Pack(sizes, 4, out var rows);

        Assert.Equal(new GridPlacement(0, 0, 1, 1), placements[0]);
        Assert.Equal(new GridPlacement(1, 0, 2, 1), placements[1]);
        Assert.Equal(new GridPlacement(0, 1, 2, 2), placements[2]);
        Assert.Equal(new GridPlacement(2, 1, 2, 2), placements[3]);
        Assert.Equal(new GridPlacement(3, 0, 1, 1), placements[4]);
        Assert.Equal(3, rows);
    }

    [Fact]
    public void Grid_clamps_too_wide_tiles()
    {
        var placements = GridPacker.Pack([new TileSize(5, 1)], 4, out var rows);

        Assert.Equal(new GridPlacement(0, 0, 4, 1), placements[0]);
        Assert.Equal(1, rows);
    }

    [Fact]
    public void Empty_grid_has_no_rows()
    {
        GridPacker.Pack([], 4, out var rows);

        Assert.Equal(0, rows);
    }
}
