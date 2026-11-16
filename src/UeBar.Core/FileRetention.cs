// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Core;

/// <summary>Background cleanup (M7): keep only the newest files of a pattern.</summary>
public static class FileRetention
{
    /// <summary>
    /// Keeps the <paramref name="keep"/> newest files (by modification time) in the folder that match
    /// <paramref name="pattern"/>; deletes the rest. Returns the number of deleted files.
    /// Errors on single files (locked) are skipped.
    /// </summary>
    public static int KeepNewest(string directory, string pattern, int keep)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        var files = new DirectoryInfo(directory).GetFiles(pattern)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(Math.Max(0, keep));
        foreach (var file in files)
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}
