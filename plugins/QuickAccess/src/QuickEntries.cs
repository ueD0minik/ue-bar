// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using static UeBar.Modules.QuickAccess.QuickText;

namespace UeBar.Modules.QuickAccess;

/// <summary>Kinds of entries in the settings (one more than tiles can carry: own commands).</summary>
internal enum EntryKind
{
    App,
    File,
    Folder,
    Url,

    /// <summary>Own command, script (.ps1, .bat) or command line – for advanced users, runs with your rights.</summary>
    Command,
}

/// <summary>An entry of this device's settings (list "entries"); found in the search and through the bus.</summary>
internal sealed record QuickEntry(string Id, string Name, EntryKind Kind, string Value, bool Admin)
{
    /// <summary>
    /// What a tile made from this entry stores: the target itself (syncs to other PCs), except for own
    /// commands – those stay a reference to this device's entry.
    /// </summary>
    public QuickTarget TileTarget(string userProfile) => Kind switch
    {
        EntryKind.App => new QuickTarget(TargetKind.App, Value, Admin),
        EntryKind.File => new QuickTarget(TargetKind.File, QuickPaths.Collapse(Value, userProfile)),
        EntryKind.Folder => new QuickTarget(TargetKind.Folder, QuickPaths.Collapse(Value, userProfile)),
        EntryKind.Url => new QuickTarget(TargetKind.Url, Value),
        _ => new QuickTarget(TargetKind.Entry, Id),
    };
}

/// <summary>The settings of the plugin: the list of entries.</summary>
internal static class QuickSettings
{
    public const string EntriesKey = "entries";
    public const string NameKey = "name";
    public const string KindKey = "kind";
    public const string FileKey = "file";
    public const string FolderKey = "folder";
    public const string TextKey = "text";
    public const string AdminKey = "admin";

    public static IReadOnlyList<SettingField> Fields =>
    [
        new(EntriesKey, T["Qa_SettingEntries"], SettingFieldKind.List, null, T["Qa_SettingEntries_Hint"])
        {
            AddText = T["Qa_SettingAdd"],
            ItemFields =
            [
                new(NameKey, T["Qa_FieldName"], SettingFieldKind.Text, null) { Placeholder = T["Qa_FieldName_Example"] },
                new(KindKey, T["Qa_FieldKind"], SettingFieldKind.Choice, "folder", null,
                [
                    new("app", T["Qa_KindApp"]),
                    new("file", T["Qa_KindFile"]),
                    new("folder", T["Qa_KindFolder"]),
                    new("url", T["Qa_KindUrl"]),
                    new("command", T["Qa_KindCommand"]),
                ]),
                new(FileKey, T["Qa_FieldFile"], SettingFieldKind.File, null, T["Qa_FieldFile_Hint"]) { ShowWhen = "kind=file" },
                new(FolderKey, T["Qa_FieldFolder"], SettingFieldKind.Folder, null) { ShowWhen = "kind=folder" },
                new(TextKey, T["Qa_FieldText"], SettingFieldKind.Text, null, T["Qa_FieldText_Hint"]) { Placeholder = "https://…", ShowWhen = "kind=app|url|command" },
                new(AdminKey, T["Qa_FieldAdmin"], SettingFieldKind.Toggle, "false", T["Qa_FieldAdmin_Hint"]) { ShowWhen = "kind=app|command" },
            ],
        },
    ];

    public static IReadOnlyList<QuickEntry> Entries(ModuleSettingValues values)
    {
        var list = new List<QuickEntry>();
        foreach (var item in values.GetList(EntriesKey))
        {
            string? Get(string key) => item.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            var kind = Get(KindKey)?.ToLowerInvariant() switch
            {
                "app" => EntryKind.App,
                "file" => EntryKind.File,
                "url" => EntryKind.Url,
                "command" => EntryKind.Command,
                _ => EntryKind.Folder,
            };
            var value = kind switch
            {
                EntryKind.File => Get(FileKey) ?? Get(TextKey),
                EntryKind.Folder => Get(FolderKey) ?? Get(TextKey),
                _ => Get(TextKey),
            };
            if (Get("id") is not { } id || value is null || (kind == EntryKind.Url && !QuickTarget.IsAllowedUrl(value)))
            {
                continue;
            }

            var name = Get(NameKey) ?? QuickNames.FromValue(kind, value);
            list.Add(new QuickEntry(id, name, kind, value, kind is EntryKind.App or EntryKind.Command && bool.TryParse(Get(AdminKey), out var admin) && admin));
        }

        return list;
    }
}

/// <summary>Names and icons derived from a target (when there is no own name).</summary>
internal static class QuickNames
{
    // Segoe Fluent Icons code points (the app understands "U+XXXX").
    public const string AppIcon = "U+E71D";
    public const string FileIcon = "U+E8A5";
    public const string FolderIcon = "U+E8B7";
    public const string UrlIcon = "U+E774";
    public const string CommandIcon = "U+E756";
    public const string GroupIcon = "U+E8F1";

    public static string FromValue(EntryKind kind, string value) => kind switch
    {
        EntryKind.File => Path.GetFileNameWithoutExtension(Trimmed(value)) is { Length: > 0 } f ? f : value,
        EntryKind.Folder => Path.GetFileName(Trimmed(value)) is { Length: > 0 } d ? d : value,
        EntryKind.Url => Host(value),
        EntryKind.Command => FirstToken(value),
        _ => value.Split('!', '\\').Last(),
    };

    public static string Icon(EntryKind kind) => kind switch
    {
        EntryKind.App => AppIcon,
        EntryKind.File => FileIcon,
        EntryKind.Folder => FolderIcon,
        EntryKind.Url => UrlIcon,
        _ => CommandIcon,
    };

    /// <summary>Short kind for tiles and search hits (the settings say "for advanced users").</summary>
    public static string KindText(EntryKind kind) => kind switch
    {
        EntryKind.App => T["Qa_KindApp"],
        EntryKind.File => T["Qa_KindFile"],
        EntryKind.Folder => T["Qa_KindFolder"],
        EntryKind.Url => T["Qa_KindUrl"],
        _ => T["Qa_KindCommandShort"],
    };

    public static EntryKind Kind(TargetKind kind) => kind switch
    {
        TargetKind.App => EntryKind.App,
        TargetKind.File => EntryKind.File,
        TargetKind.Folder => EntryKind.Folder,
        TargetKind.Url => EntryKind.Url,
        _ => EntryKind.Command,
    };

    private static string Trimmed(string path) => path.TrimEnd('\\', '/');

    private static string Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (uri.Scheme == Uri.UriSchemeMailto)
        {
            return uri.UserInfo.Length > 0 ? $"{uri.UserInfo}@{uri.Host}" : url[7..];
        }

        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
    }

    /// <summary>The program of a command line (quoted or up to the first space), without folder and extension.</summary>
    public static string FirstToken(string commandLine)
    {
        var (program, _) = SplitCommand(commandLine);
        return Path.GetFileNameWithoutExtension(program) is { Length: > 0 } name ? name : commandLine;
    }

    /// <summary><c>"C:\a b\x.exe" -y</c> → (<c>C:\a b\x.exe</c>, <c>-y</c>).</summary>
    public static (string Program, string? Arguments) SplitCommand(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0)
            {
                var rest = text[(end + 1)..].Trim();
                return (text[1..end], rest.Length == 0 ? null : rest);
            }
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, null) : (text[..space], text[(space + 1)..].Trim());
    }
}
