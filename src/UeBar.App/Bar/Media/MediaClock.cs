// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Threading;

namespace UeBar.App.Bar.Media;

/// <summary>
/// Per-second ticking for media progress displays. Only runs while the panel is visible
/// <b>and</b> at least one displayed media tile is playing – otherwise there is no timer (M4).
/// Use on the UI thread only.
/// </summary>
internal static class MediaClock
{
    private static readonly HashSet<object> Playing = [];
    private static DispatcherTimer? _timer;
    private static bool _panelVisible;

    /// <summary>Once per second (only while the timer runs) and when the panel becomes visible.</summary>
    public static event EventHandler? Tick;

    /// <summary>For tests/diagnostics.</summary>
    public static bool IsRunning => _timer?.IsEnabled == true;

    public static bool PanelVisible
    {
        get => _panelVisible;
        set
        {
            if (_panelVisible == value)
            {
                return;
            }

            _panelVisible = value;
            Update();
            if (value)
            {
                Tick?.Invoke(null, EventArgs.Empty); // show the current state right away
            }
        }
    }

    /// <summary>A display reports whether it currently shows something playing.</summary>
    public static void SetPlaying(object owner, bool playing)
    {
        var changed = playing ? Playing.Add(owner) : Playing.Remove(owner);
        if (changed)
        {
            Update();
        }
    }

    private static void Update()
    {
        var run = _panelVisible && Playing.Count > 0;
        if (run && _timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick?.Invoke(null, EventArgs.Empty);
            _timer.Start();
        }
        else if (!run && _timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }
    }
}
