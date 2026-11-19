// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.QuickAccess.Platform;

/// <summary>An app of the Start menu: <paramref name="Id"/> = the shell's app ID (for Store apps the AUMID).</summary>
internal sealed record AppInfo(string Id, string Name);

/// <summary>What to start: like <c>ShellExecute</c> – file/address, arguments, verb (null = default, "runas", "new").</summary>
internal sealed record LaunchRequest(string File, string? Arguments = null, string? Verb = null);

/// <summary>Result of a start; <paramref name="Cancelled"/> = the user said no to the administrator prompt.</summary>
internal sealed record LaunchResult(bool Success, string? Error = null, bool Cancelled = false, bool NoSuchVerb = false)
{
    public static LaunchResult Ok { get; } = new(true);
}

/// <summary>Content of a shortcut (.lnk).</summary>
internal sealed record ShortcutInfo(string? TargetPath, string? Arguments);

/// <summary>
/// The Windows shell as far as the plugin needs it. Real: <see cref="WindowsShell"/>; tests use a fake.
/// All methods may be called on any thread (the plugin calls them on the thread pool).
/// </summary>
internal interface IQuickShell
{
    /// <summary>The apps of the Start menu (classic and Store), as the shell's "Apps" folder lists them.</summary>
    Task<IReadOnlyList<AppInfo>> GetAppsAsync(CancellationToken cancellationToken);

    LaunchResult Launch(LaunchRequest request);

    bool FileExists(string path);

    bool FolderExists(string path);

    ShortcutInfo? ReadShortcut(string path);

    /// <summary>Address of an Internet shortcut (.url), or null.</summary>
    string? ReadUrlShortcut(string path);

    /// <summary>The real icon as PNG (<paramref name="parsingName"/> = path or <c>shell:AppsFolder\…</c>), or null.</summary>
    byte[]? GetIconPng(string parsingName, int size);
}
