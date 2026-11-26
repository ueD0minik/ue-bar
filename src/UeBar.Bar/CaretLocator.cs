// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>Where the position of the text cursor came from (P2-6-Fix 1).</summary>
public enum CaretSource
{
    /// <summary>The old Windows interface (<c>GetGUIThreadInfo</c>) – Notepad, Office, Photoshop …</summary>
    SystemCaret,

    /// <summary>UI Automation (text pattern / accessibility) – Chromium, Electron, WinUI/XAML, WebView2.</summary>
    UiAutomation,

    /// <summary>Nothing found – the popup goes to the mouse.</summary>
    Mouse,
}

/// <summary>
/// Finds the text cursor when the choice popup opens (P2-6-Fix 1): first the old interface (it stays decisive where it
/// works), then UI Automation on its own thread with a hard time limit (150 ms), then the mouse. Only when opening,
/// never in the background; a slow app can never hold the popup up, and while an earlier lookup still hangs no second
/// one is started.
/// </summary>
public sealed class CaretLocator
{
    public static readonly TimeSpan AutomationLimit = TimeSpan.FromMilliseconds(150);

    private int _running;

    /// <summary>A lookup that took too long is still running (its thread ends by itself).</summary>
    public bool IsBusy => Volatile.Read(ref _running) != 0;

    /// <summary>Why the last lookup fell back to the mouse (for the log; never content).</summary>
    public string? LastProblem { get; private set; }

    /// <summary>
    /// The caret rectangle and where it came from. <paramref name="automation"/> runs on its own thread and is given up
    /// after <paramref name="limit"/> (default <see cref="AutomationLimit"/>). Implausible rectangles count as nothing.
    /// </summary>
    public (PixelRect? Caret, CaretSource Source) Locate(Func<PixelRect?> systemCaret, Func<PixelRect?> automation, PixelRect? window, TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(systemCaret);
        ArgumentNullException.ThrowIfNull(automation);
        if (Plausible(Safe(systemCaret), window) is { } legacy)
        {
            return (legacy, CaretSource.SystemCaret);
        }

        LastProblem = null;
        var raw = RunLimited(automation, limit ?? AutomationLimit);
        if (Plausible(raw, window) is { } found)
        {
            return (found, CaretSource.UiAutomation);
        }

        LastProblem ??= raw is { } r ? $"implausible {r.X},{r.Y} {r.Width}×{r.Height}" : IsBusy ? "still running or too slow" : "nothing found";
        return (null, CaretSource.Mouse);
    }

    /// <summary>Runs the lookup on its own thread; null after the time limit or while an earlier one still runs.</summary>
    public PixelRect? RunLimited(Func<PixelRect?> lookup, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null; // the previous one still hangs – do not pile up threads
        }

        PixelRect? result = null;
        using var done = new ManualResetEventSlim();
        var signal = done; // used by the thread only until it was set; disposing afterwards is fine
        var thread = new Thread(() =>
        {
            try
            {
                result = lookup();
            }
            catch (Exception ex)
            {
                // An app's accessibility can throw anything – it only means "not found".
                result = null;
                LastProblem = ex.GetType().Name;
            }
            finally
            {
                Volatile.Write(ref _running, 0);
                try
                {
                    signal.Set();
                }
                catch (ObjectDisposedException)
                {
                    // the caller gave up already
                }
            }
        })
        {
            IsBackground = true,
            Name = "ueBar caret lookup",
        };
        thread.Start();
        return done.Wait(limit) ? result : null;
    }

    /// <summary>
    /// A rectangle is a caret if it is on the window (a little around it is fine) and has a sensible height; a caret of
    /// zero width is normal.
    /// </summary>
    public static PixelRect? Plausible(PixelRect? caret, PixelRect? window)
    {
        if (caret is not { } c || c.Height is < 4 or > 400 || c.Width is < 0 or > 2000)
        {
            return null;
        }

        if (window is { } w && !w.Inflate(40, 40).Contains(new PixelPoint(c.X, c.Y + (c.Height / 2))))
        {
            return null;
        }

        return c with { Width = Math.Max(1, c.Width) };
    }

    private static PixelRect? Safe(Func<PixelRect?> lookup)
    {
        try
        {
            return lookup();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
