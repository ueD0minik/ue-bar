// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>
/// The folders the "Save as" dialog remembers (P2-5-Fix 1): per module (and "pins" for the pin menu), per PC –
/// a local setting, never synchronized. The dialog starts in the folder picked last time.
/// </summary>
public sealed class SaveFolders
{
    public const string Section = "saveDialogs";

    public Dictionary<string, string> LastFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the dialog starts: the last folder (if it still exists), else the given one, else the fallback.</summary>
    public string StartFolder(string owner, string? initial, string fallback)
    {
        if (LastFolders.TryGetValue(owner, out var last) && Exists(last))
        {
            return last;
        }

        return Exists(initial) ? initial! : fallback;
    }

    /// <summary>The user saved to <paramref name="path"/>: remember its folder for next time. True = changed.</summary>
    public bool Remember(string owner, string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || (LastFolders.TryGetValue(owner, out var last) && string.Equals(last, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        LastFolders[owner] = folder;
        return true;
    }

    private static bool Exists(string? folder)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
