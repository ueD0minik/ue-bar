// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>What <see cref="SelectionCopier"/> needs from Windows – the app gives the real thing, tests a fake.</summary>
public interface ISelectionIo
{
    /// <summary>The clipboard's change counter (GetClipboardSequenceNumber).</summary>
    uint Sequence { get; }

    /// <summary>Are keys of the hotkey (Win, Alt, Shift, Ctrl) still held? Ctrl+C would arrive as Win+Alt+Ctrl+C.</summary>
    bool ModifiersDown();

    /// <summary>Everything on the clipboard right now (null = empty or not readable).</summary>
    object? Snapshot();

    /// <summary>Puts <paramref name="snapshot"/> back (null = empty the clipboard), marked so Windows' history does not count it again.</summary>
    bool Restore(object? snapshot);

    /// <summary>Sends Ctrl+C to the window in front.</summary>
    bool SendCopy();

    /// <summary>The text on the clipboard now (null = none).</summary>
    string? ReadText();
}

public enum SelectionCopyOutcome
{
    Text,

    /// <summary>Nothing was marked (the app did not copy anything, or no text).</summary>
    NoSelection,

    /// <summary>Ctrl+C could not be sent.</summary>
    Failed,
}

/// <summary>The marked text and whether the clipboard got its old content back.</summary>
public sealed record SelectionCopyResult(SelectionCopyOutcome Outcome, string? Text, bool Restored, bool WaitedForKeys);

/// <summary>
/// "Send marked text to …" (P3-1): copies the marking of the window in front with Ctrl+C and gives the clipboard its old
/// content back right after – the user's clipboard looks as before. Waits until the keys of the hotkey are let go
/// (at most 1.5 s), then until the app has copied (at most 0.8 s). The clipboard history of ue Bar ignores the
/// moment (the app pauses it), Windows' history does not count the restored content again. Stays on the caller's thread
/// (the clipboard needs the STA UI thread), so every await resumes there.
/// </summary>
public static class SelectionCopier
{
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(15);

    public const int KeyWaitSteps = 100;    // 1.5 s

    public const int CopyWaitSteps = 55;    // ~0.8 s

    public static async Task<SelectionCopyResult> CopyAsync(ISelectionIo io, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(delay);

        var waited = false;
        for (var i = 0; i < KeyWaitSteps && io.ModifiersDown(); i++)
        {
            waited = true;
            await delay(Step, cancellationToken).ConfigureAwait(true);
        }

        var before = io.Sequence;
        var saved = io.Snapshot();
        if (!io.SendCopy())
        {
            return new SelectionCopyResult(SelectionCopyOutcome.Failed, null, Restored: false, waited);
        }

        var changed = false;
        for (var i = 0; i < CopyWaitSteps; i++)
        {
            if (io.Sequence != before)
            {
                changed = true;
                break;
            }

            await delay(Step, cancellationToken).ConfigureAwait(true);
        }

        if (!changed)
        {
            // The app copied nothing (no marking): the clipboard was not touched – nothing to put back.
            return new SelectionCopyResult(SelectionCopyOutcome.NoSelection, null, Restored: false, waited);
        }

        // Some apps write several formats one after the other – a moment for the rest.
        await delay(Step * 2, cancellationToken).ConfigureAwait(true);
        var text = io.ReadText();
        var restored = io.Restore(saved);
        return string.IsNullOrWhiteSpace(text)
            ? new SelectionCopyResult(SelectionCopyOutcome.NoSelection, null, restored, waited)
            : new SelectionCopyResult(SelectionCopyOutcome.Text, text, restored, waited);
    }
}
