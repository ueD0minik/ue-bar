// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What a notification asks for (P2-4): its priority and the per-notification flags of contract 1.2.</summary>
public sealed record NotificationRequest(bool Important, bool IgnoreDoNotDisturb = false, bool ShowInFullscreen = false);

/// <summary>The moment a notification arrives: what would keep it from coming out.</summary>
public sealed record AnnounceConditions(bool Paused, bool LockedOrDisplayOff, bool Fullscreen, bool DoNotDisturb);

/// <summary>Result: shown or only in the history (with the reason for the log).</summary>
public sealed record NotificationDecision(bool Show, bool OverridesPause, string? Reason);

/// <summary>
/// Decides whether a notification comes out as a pill or only goes to the history (P2-4). Normal ones
/// follow the bar: paused, fullscreen block, "Do not disturb". Important ones may pass each of these as
/// the bar settings allow. Nothing comes out while the PC is locked or the display is off.
/// </summary>
public static class NotificationGate
{
    public static NotificationDecision Decide(NotificationRequest request, AnnounceConditions now, BarSettings settings)
    {
        if (now.LockedOrDisplayOff)
        {
            return new(false, false, "locked/display off");
        }

        var overridesPause = false;
        if (now.Paused)
        {
            if (!(request.Important && settings.ImportantWhenPaused))
            {
                return new(false, false, "bar paused");
            }

            overridesPause = true;
        }

        if (now.Fullscreen && settings.BlockInFullscreen
            && !(request.Important && settings.ImportantInFullscreen) && !request.ShowInFullscreen)
        {
            return new(false, false, "fullscreen block");
        }

        if (now.DoNotDisturb && settings.RespectDoNotDisturb
            && !(request.Important && settings.ImportantDuringDoNotDisturb) && !request.IgnoreDoNotDisturb)
        {
            return new(false, false, "do not disturb");
        }

        return new(true, overridesPause, null);
    }
}
