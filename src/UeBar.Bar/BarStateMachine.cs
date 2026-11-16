// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>States of the bar (SPEC 5.1).</summary>
public enum BarState
{
    /// <summary>Nothing visible, no rendering.</summary>
    Hidden,

    /// <summary>Small pill at the top center.</summary>
    Pill,

    /// <summary>Panel open, hides after leaving.</summary>
    Open,

    /// <summary>Panel open, never hides, freely movable.</summary>
    Pinned,
}

public sealed class BarStateChangedEventArgs(BarState oldState, BarState newState, MonitorInfo? monitor) : EventArgs
{
    public BarState OldState { get; } = oldState;

    public BarState NewState { get; } = newState;

    public MonitorInfo? Monitor { get; } = monitor;
}

/// <summary>
/// State machine of the bar – pure logic without WPF/Win32. Gets time and mouse position from outside
/// (<see cref="Update"/>), the monitor list through <see cref="Configure"/> and the currently visible
/// area of the bar through <see cref="BarBounds"/>.
/// </summary>
public sealed class BarStateMachine
{
    /// <summary>Tolerance around the bar in physical pixels.</summary>
    public const int HitTolerancePx = 6;

    private IReadOnlyList<MonitorInfo> _monitors = [];
    private IReadOnlyList<TriggerZone> _zones = [];
    private DateTimeOffset? _dwellStart;
    private string? _dwellMonitorId;
    private DateTimeOffset? _leftAt;
    private DateTimeOffset? _announceUntil;
    private bool _held;
    private bool _heldFromHidden;

    public BarStateMachine(BarSettings? settings = null)
    {
        Settings = settings ?? new BarSettings();
    }

    public BarSettings Settings { get; private set; }

    public BarState State { get; private set; } = BarState.Hidden;

    /// <summary>Monitor the bar is currently on (null when hidden).</summary>
    public MonitorInfo? ActiveMonitor { get; private set; }

    /// <summary>Visible area of the bar (pill or panel) in physical pixels; set by the window.</summary>
    public PixelRect? BarBounds { get; set; }

    /// <summary>Checks whether a fullscreen app runs on the monitor (only called when needed).</summary>
    public Func<MonitorInfo, bool>? IsFullscreen { get; set; }

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public IReadOnlyList<TriggerZone> TriggerZones => _zones;

    public bool IsVisible => State != BarState.Hidden;

    public event EventHandler<BarStateChangedEventArgs>? StateChanged;

    /// <summary>Monitors or settings have changed.</summary>
    public void Configure(IReadOnlyList<MonitorInfo> monitors, BarSettings settings)
    {
        _monitors = monitors;
        Settings = settings;
        _zones = BarGeometry.TriggerZones(monitors, settings);
        _dwellStart = null;

        if (settings.Paused)
        {
            Hide();
            return;
        }

        if (ActiveMonitor is null)
        {
            return;
        }

        var still = MonitorSelector.FindById(monitors, ActiveMonitor.Id);
        if (still is not null)
        {
            ActiveMonitor = still;
            return;
        }

        // Monitor is gone (unplugged): pinned → to the primary monitor, otherwise hide.
        if (State == BarState.Pinned && MonitorSelector.Primary(monitors) is { } primary)
        {
            ActiveMonitor = primary;
            Raise(BarState.Pinned, BarState.Pinned);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>The pill is currently shown without the mouse (notification).</summary>
    public bool IsAnnouncing => _announceUntil is not null && State == BarState.Pill;

    /// <summary>The panel is held open for input (quick search).</summary>
    public bool IsHeld => _held;

    public void Update(DateTimeOffset now, PixelPoint cursor)
    {
        if (Settings.Paused && !_held)
        {
            Hide();
            return;
        }

        switch (State)
        {
            case BarState.Hidden:
                UpdateHidden(now, cursor);
                break;

            case BarState.Pill:
                if (BarBounds is { } pill && pill.Contains(cursor))
                {
                    _leftAt = null;
                    _announceUntil = null;
                    SetState(BarState.Open);
                }
                else if (_announceUntil is { } until && now < until)
                {
                    _leftAt = null; // notification stays until it expires
                }
                else
                {
                    _announceUntil = null;
                    UpdateLeave(now, IsOverBar(cursor));
                }

                break;

            case BarState.Open:
                UpdateLeave(now, IsOverBar(cursor));
                break;

            case BarState.Pinned:
                break;
        }
    }

    /// <summary>
    /// Show the pill without the mouse until <paramref name="until"/> (notification, M6). Only from "hidden"
    /// or "pill"; an open panel shows the message itself. Afterwards the pill hides as
    /// usual (stays while the mouse is on it).
    /// </summary>
    public bool Announce(MonitorInfo monitor, DateTimeOffset until)
    {
        if (Settings.Paused || State is BarState.Open or BarState.Pinned)
        {
            return false;
        }

        _announceUntil = until;
        _leftAt = null;
        if (State == BarState.Hidden)
        {
            _dwellStart = null;
            ActiveMonitor = monitor;
            SetState(BarState.Pill);
        }

        return true;
    }

    /// <summary>End the display of a notification early.</summary>
    public void EndAnnounce() => _announceUntil = null;

    /// <summary>
    /// Open the panel and hold it, even if the bar is paused (quick search, M6). If the bar is
    /// already pinned, nothing happens (it stays as it is). From the pill the window has to
    /// expand (<see cref="BarWindowAction.ExpandPinned"/>).
    /// </summary>
    public bool OpenHeld(MonitorInfo monitor)
    {
        if (State == BarState.Pinned)
        {
            return false;
        }

        // If the bar only came as a message pill (without mouse), it disappears completely after holding.
        _heldFromHidden = State == BarState.Hidden || IsAnnouncing;
        _held = true;
        _announceUntil = null;
        _leftAt = null;
        if (_heldFromHidden)
        {
            ActiveMonitor = monitor;
        }

        SetState(BarState.Pinned);
        return true;
    }

    /// <summary>End holding: if the bar came from "hidden", it disappears; otherwise it hides normally.</summary>
    public void ReleaseHeld(DateTimeOffset now)
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        if (_heldFromHidden || Settings.Paused)
        {
            Hide();
        }
        else
        {
            Unpin(now);
        }
    }

    /// <summary>Open → pinned.</summary>
    public bool Pin()
    {
        if (State is not (BarState.Open or BarState.Pill))
        {
            return false;
        }

        _leftAt = null;
        SetState(BarState.Pinned);
        return true;
    }

    /// <summary>Pinned → open; hides normally again afterwards.</summary>
    public bool Unpin(DateTimeOffset now)
    {
        if (State != BarState.Pinned)
        {
            return false;
        }

        _leftAt = now;
        SetState(BarState.Open);
        return true;
    }

    /// <summary>Show pinned directly (e.g. after a restart).</summary>
    public void ShowPinned(MonitorInfo monitor)
    {
        if (Settings.Paused)
        {
            return;
        }

        ActiveMonitor = monitor;
        _leftAt = null;
        SetState(BarState.Pinned);
    }

    public void Hide()
    {
        _dwellStart = null;
        _leftAt = null;
        _announceUntil = null;
        _held = false;
        if (State != BarState.Hidden)
        {
            SetState(BarState.Hidden);
        }

        ActiveMonitor = null;
        BarBounds = null;
    }

    private void UpdateHidden(DateTimeOffset now, PixelPoint cursor)
    {
        var zone = _zones.FirstOrDefault(z => z.Rect.Contains(cursor));
        if (zone is null)
        {
            _dwellStart = null;
            _dwellMonitorId = null;
            return;
        }

        if (_dwellStart is null || _dwellMonitorId != zone.Monitor.Id)
        {
            _dwellStart = now;
            _dwellMonitorId = zone.Monitor.Id;
        }

        if (now - _dwellStart.Value < Settings.Dwell)
        {
            return;
        }

        if (Settings.BlockInFullscreen && IsFullscreen?.Invoke(zone.Monitor) == true)
        {
            return; // fullscreen block: stays hidden
        }

        _dwellStart = null;
        _leftAt = null;
        ActiveMonitor = zone.Monitor;
        SetState(BarState.Pill);
    }

    private void UpdateLeave(DateTimeOffset now, bool over)
    {
        if (over)
        {
            _leftAt = null;
            return;
        }

        _leftAt ??= now;
        if (now - _leftAt.Value >= Settings.HideDelay)
        {
            Hide();
        }
    }

    private bool IsOverBar(PixelPoint cursor)
    {
        if (ActiveMonitor is null)
        {
            return false;
        }

        if (BarBounds is { } bar && BarGeometry.HitArea(bar, ActiveMonitor, HitTolerancePx).Contains(cursor))
        {
            return true;
        }

        // mouse still rests at the edge of the active monitor
        return BarGeometry.TriggerRect(ActiveMonitor, Settings).Contains(cursor);
    }

    private void SetState(BarState state)
    {
        var old = State;
        if (old == state)
        {
            return;
        }

        State = state;
        Raise(old, state);
    }

    private void Raise(BarState old, BarState state) =>
        StateChanged?.Invoke(this, new BarStateChangedEventArgs(old, state, ActiveMonitor));
}
