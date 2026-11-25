// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// Which clipboard content the core may read at all (P2-6). Password managers mark what they copy with special
/// formats; then nothing is read – not even briefly, and no plugin hears of it.
/// </summary>
public static class ClipboardPrivacy
{
    /// <summary>"Do not look at this" (Windows, documented for clipboard monitors).</summary>
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>DWORD: 0 = do not put this into a clipboard history.</summary>
    public const string HistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>Older convention (e.g. KeePass): clipboard viewers should ignore this.</summary>
    public const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    /// <summary>
    /// True if the content may be read. <paramref name="canIncludeInHistory"/> = value of the DWORD format, null if it
    /// is not there (or unreadable – then it counts as "no", to be safe).
    /// </summary>
    public static bool MayRead(bool hasExcludeFormat, bool hasViewerIgnoreFormat, bool hasHistoryFormat, uint? canIncludeInHistory) =>
        !hasExcludeFormat && !hasViewerIgnoreFormat && (!hasHistoryFormat || canIncludeInHistory is { } v && v != 0);
}

/// <summary>Coalescing of clipboard notifications (P2-6): apps often write in several steps.</summary>
public static class ClipboardTiming
{
    /// <summary>Wait this long after the last notification before reading.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(120);
}
