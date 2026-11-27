// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What the bar window does for a screen capture (P2-5-Fix 1): out of sight, and back afterwards.</summary>
public interface ICaptureHideTarget
{
    /// <summary>Make the bar invisible for the capture; true = it could be seen before (then wait a frame).</summary>
    bool HideForCapture();

    /// <summary>Undo everything <see cref="HideForCapture"/> changed – always, whatever it returned.</summary>
    void RestoreAfterCapture();
}

/// <summary>
/// Runs a screen capture with the bar hidden and restores the bar in every case (P2-5-Fix 1): after an action,
/// Esc, an error or an exception. The bug in 1.4.0 before this fix: hiding set the bar's opacity to 0 even when
/// the bar was collapsed, but the restore only ran when it had been visible – so the bar stayed invisible
/// for good.
/// </summary>
public static class CaptureSafety
{
    public static async Task<T> RunWithBarHiddenAsync<T>(ICaptureHideTarget bar, Func<bool, Task<T>> capture)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(capture);
        var wasVisible = false;
        try
        {
            wasVisible = bar.HideForCapture();
            return await capture(wasVisible).ConfigureAwait(true);
        }
        finally
        {
            bar.RestoreAfterCapture();
        }
    }
}

/// <summary>
/// What the bar window really looks like to Windows (P2-5-Fix 1, checked on every switch to pill/panel).
/// <see cref="CaptureAffinity"/>: a display affinity other than the one "hide from screen captures" wants (P2-7).
/// </summary>
public readonly record struct BarWindowFacts(bool Visible, bool Cloaked, double Opacity, bool Topmost, bool CaptureAffinity);

/// <summary>
/// The cheap check "can the bar be seen?" (P2-5-Fix 1): visible, not cloaked, opacity above 0, topmost, no display
/// affinity. Every problem found is one English word for the log; the window repairs them itself.
/// </summary>
public static class BarWindowHealth
{
    public static IReadOnlyList<string> Problems(BarWindowFacts facts)
    {
        var problems = new List<string>();
        if (!facts.Visible)
        {
            problems.Add("not visible");
        }

        if (facts.Cloaked)
        {
            problems.Add("cloaked");
        }

        if (!(facts.Opacity > 0.01))
        {
            problems.Add($"opacity {facts.Opacity:0.##}");
        }

        if (!facts.Topmost)
        {
            problems.Add("not topmost");
        }

        if (facts.CaptureAffinity)
        {
            problems.Add("unexpected display affinity");
        }

        return problems;
    }
}
