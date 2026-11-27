// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What the UI watchdog does after one of its ticks.</summary>
public enum HangVerdict
{
    /// <summary>All fine (or still waiting below the threshold).</summary>
    None,

    /// <summary>The UI thread has not answered for longer than the threshold – report it.</summary>
    Hang,

    /// <summary>The watchdog itself did not run for a long time: sleep, hibernation or Modern Standby – start over.</summary>
    Resumed,
}

/// <summary>
/// The decision of the UI watchdog (P2-6-Fix 2), without clocks of its own: times come in as "elapsed since start".
/// A hang is only a hang if the watchdog itself kept running. When the PC sleeps (or Modern Standby pauses the app),
/// the watchdog's ticks stop too – the next tick then sees a long gap and starts over instead of reporting the whole
/// sleep as a hang. Suspend/resume messages of Windows pause it explicitly as well.
/// </summary>
public sealed class HangClock(TimeSpan threshold, TimeSpan maxTickGap)
{
    private TimeSpan? _probeSentAt;
    private TimeSpan? _lastTick;
    private bool _suspended;
    private bool _reported;

    public TimeSpan Threshold { get; } = threshold;

    /// <summary>Ticks further apart than this mean the process did not run in between.</summary>
    public TimeSpan MaxTickGap { get; } = maxTickGap;

    public bool HasProbe => _probeSentAt is not null;

    public bool IsSuspended => _suspended;

    /// <summary>The empty task for the UI thread was queued now.</summary>
    public void ProbeSent(TimeSpan now) => _probeSentAt = now;

    /// <summary>The UI thread ran it; returns how long it waited (null if there was none).</summary>
    public TimeSpan? Answered(TimeSpan now)
    {
        var waited = _probeSentAt is { } sent ? now - sent : (TimeSpan?)null;
        _probeSentAt = null;
        _reported = false;
        return waited;
    }

    /// <summary>Windows goes to sleep: nothing counts until <see cref="Resume"/>.</summary>
    public void Suspend()
    {
        _suspended = true;
        _probeSentAt = null;
        _reported = false;
    }

    /// <summary>Back from sleep: start counting from now.</summary>
    public void Resume(TimeSpan now)
    {
        _suspended = false;
        _probeSentAt = null;
        _reported = false;
        _lastTick = now;
    }

    /// <summary>One tick of the watchdog. <paramref name="waiting"/> = how long the probe has waited so far.</summary>
    public HangVerdict Tick(TimeSpan now, out TimeSpan waiting)
    {
        waiting = TimeSpan.Zero;
        var gap = _lastTick is { } last ? now - last : TimeSpan.Zero;
        _lastTick = now;
        if (_suspended)
        {
            return HangVerdict.None;
        }

        if (gap > MaxTickGap)
        {
            _probeSentAt = null; // the probe waited because nothing ran – not because the UI thread hung
            _reported = false;
            waiting = gap;
            return HangVerdict.Resumed;
        }

        if (_probeSentAt is not { } sent || _reported)
        {
            return HangVerdict.None;
        }

        waiting = now - sent;
        if (waiting < Threshold)
        {
            return HangVerdict.None;
        }

        _reported = true;
        return HangVerdict.Hang;
    }
}
