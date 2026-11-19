// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.QuickAccess;

/// <summary>What a quick access entry opens.</summary>
internal enum TargetKind
{
    /// <summary>App from the Start menu (classic or Store) by its app ID.</summary>
    App,

    /// <summary>File with its default app; Office templates open a new document.</summary>
    File,

    /// <summary>Folder in Explorer.</summary>
    Folder,

    /// <summary>Web page (http, https, mailto) in the default browser.</summary>
    Url,

    /// <summary>
    /// An entry of this device's settings (by its ID). Own commands are only ever reached like this: a
    /// tile in the synced layout cannot carry a command line of its own.
    /// </summary>
    Entry,
}

/// <summary>
/// The target of a tile, as stored in the layout (slot "main"): <c>kind:value</c>, an exclamation mark
/// after the kind = as administrator (<c>app!:…</c>). Paths below the user folder are stored as
/// <c>%USERPROFILE%\…</c>, so a synced layout also fits another PC.
/// </summary>
internal sealed record QuickTarget(TargetKind Kind, string Value, bool Admin = false)
{
    private static readonly (TargetKind Kind, string Prefix)[] Prefixes =
    [
        (TargetKind.App, "app"),
        (TargetKind.File, "file"),
        (TargetKind.Folder, "folder"),
        (TargetKind.Url, "url"),
        (TargetKind.Entry, "entry"),
    ];

    /// <summary>Office templates: opening means "new document from it".</summary>
    public static readonly IReadOnlySet<string> TemplateExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dotx", ".dotm", ".dot", ".xltx", ".xltm", ".xlt", ".potx", ".potm", ".pot",
    };

    /// <summary>Web addresses a tile may open – no <c>file:</c>, <c>javascript:</c> or app protocols.</summary>
    public static bool IsAllowedUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    public bool IsTemplate => Kind == TargetKind.File && TemplateExtensions.Contains(Path.GetExtension(Value));

    public override string ToString() =>
        $"{Prefixes.First(p => p.Kind == Kind).Prefix}{(Admin ? "!" : "")}:{Value}";

    public static QuickTarget? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var head = text[..colon];
        var admin = head.EndsWith('!');
        if (admin)
        {
            head = head[..^1];
        }

        var value = text[(colon + 1)..].Trim();
        if (value.Length == 0)
        {
            return null;
        }

        foreach (var (kind, prefix) in Prefixes)
        {
            if (string.Equals(head, prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (kind == TargetKind.Url && !IsAllowedUrl(value))
                {
                    return null;
                }

                // Only apps (and entries, which carry their own flag) can ask for administrator rights.
                return new QuickTarget(kind, value, admin && kind == TargetKind.App);
            }
        }

        return null;
    }
}

/// <summary>Paths that fit every PC of the same person: the user folder as <c>%USERPROFILE%</c>.</summary>
internal static class QuickPaths
{
    public const string Token = "%USERPROFILE%";

    /// <summary><c>C:\Users\me\Documents\x.docx</c> → <c>%USERPROFILE%\Documents\x.docx</c> (other paths unchanged).</summary>
    public static string Collapse(string path, string userProfile)
    {
        var profile = userProfile.TrimEnd('\\', '/');
        if (profile.Length == 0)
        {
            return path;
        }

        if (string.Equals(path, profile, StringComparison.OrdinalIgnoreCase))
        {
            return Token;
        }

        return path.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(profile + "/", StringComparison.OrdinalIgnoreCase)
            ? Token + path[profile.Length..]
            : path;
    }

    /// <summary>The other way round, with this PC's user folder; further variables (%APPDATA% …) as Windows knows them.</summary>
    public static string Expand(string path, string userProfile)
    {
        var expanded = path.StartsWith(Token, StringComparison.OrdinalIgnoreCase)
            ? userProfile.TrimEnd('\\', '/') + path[Token.Length..]
            : path;
        return Environment.ExpandEnvironmentVariables(expanded);
    }
}
