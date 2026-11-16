// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What the bar window has to do on a state change.</summary>
public enum BarWindowAction
{
    /// <summary>Nothing (the change does not happen).</summary>
    None,

    /// <summary>Slide the pill in from the top.</summary>
    ShowPill,

    /// <summary>Expand the pill to the panel.</summary>
    Expand,

    /// <summary>Expand the pill to the panel and pin it (quick search/tray while the pill is visible).</summary>
    ExpandPinned,

    /// <summary>Pin the open panel (window stays as it is).</summary>
    Pin,

    /// <summary>Unpin: panel back to the top edge.</summary>
    Unpin,

    /// <summary>Show directly as a pinned panel (restart, quick search from "hidden", monitor change).</summary>
    ShowPinned,

    /// <summary>Hide.</summary>
    Hide,
}

/// <summary>
/// Mapping state change → window action (WPF-free, testable). Every change the
/// state machine can trigger must have an action here that fits the new state –
/// otherwise the window shows something other than the machine thinks (M6-Fix 1: pill → pinned).
/// </summary>
public static class BarWindowTransitions
{
    public static BarWindowAction For(BarState oldState, BarState newState) => (oldState, newState) switch
    {
        (_, BarState.Hidden) => BarWindowAction.Hide,
        (BarState.Hidden, BarState.Pill) => BarWindowAction.ShowPill,
        (BarState.Pill, BarState.Open) => BarWindowAction.Expand,
        (BarState.Pinned, BarState.Open) => BarWindowAction.Unpin,
        (BarState.Pill, BarState.Pinned) => BarWindowAction.ExpandPinned,
        (BarState.Open, BarState.Pinned) => BarWindowAction.Pin,
        (BarState.Hidden or BarState.Pinned, BarState.Pinned) => BarWindowAction.ShowPinned,
        _ => BarWindowAction.None,
    };
}
