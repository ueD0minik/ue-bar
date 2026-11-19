// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Bar;

/// <summary>
/// Turns what was dropped on the panel (P2-4) into <see cref="DroppedItem"/>s for the plugins: paths
/// become files or folders, a web address becomes a link, anything else plain text.
/// </summary>
public static class DropClassifier
{
    public static IReadOnlyList<DroppedItem> FromPaths(IEnumerable<string> paths, Func<string, bool>? isFolder = null)
    {
        isFolder ??= Directory.Exists;
        return [.. paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new DroppedItem(isFolder(p) ? DropKind.Folder : DropKind.File, p))];
    }

    /// <summary>Text from a browser or another app: the first line that is an http(s) address is a link.</summary>
    public static DroppedItem? FromText(string? text, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var first = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return Uri.TryCreate(first, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new DroppedItem(DropKind.Link, uri.AbsoluteUri, string.IsNullOrWhiteSpace(title) ? null : title.Trim())
            : new DroppedItem(DropKind.Text, text.Trim());
    }
}
