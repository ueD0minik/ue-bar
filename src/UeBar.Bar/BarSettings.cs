// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

public enum MonitorMode
{
    /// <summary>Primary monitor.</summary>
    Primary,

    /// <summary>The monitor at whose top edge the mouse currently is.</summary>
    UnderMouse,

    /// <summary>A specific monitor (<see cref="BarSettings.MonitorId"/>).</summary>
    Specific,
}

/// <summary>Bar settings (section "bar" in settings.json).</summary>
public sealed class BarSettings
{
    public MonitorMode MonitorMode { get; set; } = MonitorMode.Primary;

    /// <summary>Stable monitor ID for <see cref="MonitorMode.Specific"/>.</summary>
    public string? MonitorId { get; set; }

    /// <summary>Width of the trigger zone in percent of the monitor width (centered).</summary>
    public double TriggerWidthPercent { get; set; } = 60;

    /// <summary>Height of the trigger zone in physical pixels.</summary>
    public int TriggerHeightPx { get; set; } = 2;

    /// <summary>Dwell time at the edge before the pill appears.</summary>
    public int DwellMs { get; set; } = 150;

    /// <summary>Delay after leaving pill/panel until it hides (0–5000).</summary>
    public int HideDelayMs { get; set; } = 1000;

    public bool BlockInFullscreen { get; set; } = true;

    public bool ReduceAnimations { get; set; }

    public bool Paused { get; set; }

    /// <summary>
    /// Render without GPU (saves ~35–50 MB RAM, takes effect after restart). Set to false if
    /// animations stutter on very large panels.
    /// </summary>
    public bool SoftwareRendering { get; set; } = true;

    /// <summary>Global hotkey for the quick search (M6).</summary>
    public string QuickSearchHotkey { get; set; } = "Win+Alt+Space";

    /// <summary>With Windows "Do not disturb": no notification pill (history only).</summary>
    public bool RespectDoNotDisturb { get; set; } = true;

    /// <summary>Important notifications (P2-4, e.g. a timer ran out) come out even while the bar is paused.</summary>
    public bool ImportantWhenPaused { get; set; } = true;

    /// <summary>Important notifications come out even during Windows "Do not disturb".</summary>
    public bool ImportantDuringDoNotDisturb { get; set; } = true;

    /// <summary>
    /// P2-5-Fix 1: the answer to something the user just did (text recognized, copied, saved) is shown by the app
    /// itself as a small message at the place of the action – also during Windows "Do not disturb" ("it is a
    /// wanted action"). Off = such answers are normal notifications.
    /// </summary>
    public bool ActionFeedbackDuringDoNotDisturb { get; set; } = true;

    /// <summary>Important notifications come out even over fullscreen apps (despite <see cref="BlockInFullscreen"/>).</summary>
    public bool ImportantInFullscreen { get; set; } = true;

    /// <summary>Display time of a notification if the module does not specify one (seconds).</summary>
    public int NotificationSeconds { get; set; } = 6;

    /// <summary>
    /// Assist: after this many minutes of pause a new conversation starts (M6-Fix 2). null = the
    /// module's timeout (the Assist module provides it, e.g. 5 min).
    /// </summary>
    public double? AssistConversationMinutes { get; set; }

    /// <summary>
    /// Now Playing with several sources (P2-2): module IDs in the preferred order. Modules not
    /// listed follow by their own priority (playback on this computer first). Empty = default.
    /// </summary>
    public List<string> NowPlayingOrder { get; set; } = [];

    /// <summary>OLED protection (SPEC 5.5): pinned and no interaction for this many minutes → dim. 0 = off.</summary>
    public int OledDimMinutes { get; set; } = 5;

    /// <summary>OLED protection: move the pinned panel by 1–2 px every few minutes.</summary>
    public bool OledPixelShift { get; set; } = true;

    /// <summary>Bar is pinned (stays visible even after a restart).</summary>
    public bool IsPinned { get; set; }

    /// <summary>Monitor on which it was pinned.</summary>
    public string? PinnedMonitorId { get; set; }

    /// <summary>Moved position per monitor ID, relative to the top-left monitor corner (physical pixels).</summary>
    public Dictionary<string, PixelPoint> PinnedPositions { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Dwell => TimeSpan.FromMilliseconds(Math.Max(0, DwellMs));

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan HideDelay => TimeSpan.FromMilliseconds(Math.Clamp(HideDelayMs, 0, 5000));

    public BarSettings Clone()
    {
        var copy = (BarSettings)MemberwiseClone();
        copy.PinnedPositions = new Dictionary<string, PixelPoint>(PinnedPositions);
        copy.NowPlayingOrder = [.. NowPlayingOrder];
        return copy;
    }
}
