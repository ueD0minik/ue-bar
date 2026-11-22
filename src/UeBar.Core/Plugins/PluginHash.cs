// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Security.Cryptography;
using System.Text;

namespace UeBar.Core.Plugins;

/// <summary>
/// Checksums of a plugin folder. Only the files that decide which code runs count: assemblies
/// (<c>.dll</c>, <c>.exe</c>), <c>plugin.json</c> and <c>*.deps.json</c>. Paths are relative with '/'.
/// </summary>
public static class PluginHash
{
    /// <summary>Marker file: the folder is removed on the next start.</summary>
    public const string RemoveMarker = ".uebar-remove";

    public static bool IsCodeFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, PluginManifest.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Folder next to the bundled plugins with assemblies they share (P2-5-Fix 1): the WinRT projection comes with
    /// ue Bar once instead of once per plugin. Starts with a dot, so it is never taken for a plugin. Its files
    /// are listed in <c>bundled.sha256</c> like a plugin's.
    /// </summary>
    public const string SharedFolder = ".shared";

    /// <summary>SHA-256 (lower-case hex) of every code file in the folder, by relative path.</summary>
    public static SortedDictionary<string, string> Files(string directory) => Files(directory, PluginHashCache.None);

    /// <summary>Same, with known checksums of unchanged files taken from <paramref name="cache"/> (P2-5-Fix 1).</summary>
    public static SortedDictionary<string, string> Files(string directory, PluginHashCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(directory);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Normalize(Path.GetRelativePath(root, file));
            if (IsCodeFile(relative))
            {
                result[relative] = cache.HashOf(file);
            }
        }

        return result;
    }

    /// <summary>One checksum for the whole plugin: changes as soon as a code file changes, appears or disappears.</summary>
    public static string Combine(IReadOnlyDictionary<string, string> files)
    {
        var text = new StringBuilder();
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            text.Append(hash).Append("  ").Append(path).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static string Of(string directory) => Combine(Files(directory));

    /// <summary>Relative path with '/', lower case (Windows file names are case-insensitive).</summary>
    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}

/// <summary>
/// The plugins that come with ue Bar, with the checksum of every file – written when ue Bar is built
/// (<c>plugins\bundled.sha256</c>, format like <c>sha256sum</c>: "&lt;hash&gt;  &lt;id&gt;/&lt;file&gt;"). A plugin whose
/// files match the list loads without asking.
/// </summary>
public sealed class BundledPluginList
{
    public const string FileName = "bundled.sha256";

    private readonly Dictionary<string, Dictionary<string, string>> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public static BundledPluginList Empty { get; } = new();

    /// <summary>Plugin IDs in the order of the list (= order of the modules).</summary>
    public IReadOnlyList<string> Ids => _order;

    public bool Contains(string id) => _plugins.ContainsKey(id);

    public static BundledPluginList Parse(string text)
    {
        var list = new BundledPluginList();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("# plugin:", StringComparison.OrdinalIgnoreCase))
            {
                // Order of the plugins as listed in the app project
                var listed = line["# plugin:".Length..].Trim().ToLowerInvariant();
                if (listed.Length > 0 && !list._order.Contains(listed, StringComparer.OrdinalIgnoreCase))
                {
                    list._order.Add(listed);
                }

                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            var hash = line[..space].ToLowerInvariant();
            var path = PluginHash.Normalize(line[space..].Trim().TrimStart('*'));
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == path.Length - 1)
            {
                continue;
            }

            var id = path[..slash];
            if (!list._plugins.TryGetValue(id, out var files))
            {
                files = new Dictionary<string, string>(StringComparer.Ordinal);
                list._plugins[id] = files;
                if (!id.StartsWith('.') && !list._order.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    list._order.Add(id);
                }
            }

            files[path[(slash + 1)..]] = hash;
        }

        return list;
    }

    public static BundledPluginList Load(string file)
    {
        try
        {
            return File.Exists(file) ? Parse(File.ReadAllText(file)) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>The checksum listed for one file of a folder (e.g. <c>.shared/winrt.runtime.dll</c>), or null.</summary>
    public string? HashOf(string id, string relativeFile) =>
        _plugins.TryGetValue(id, out var files) && files.TryGetValue(PluginHash.Normalize(relativeFile), out var hash) ? hash : null;

    /// <summary>
    /// Do the code files of the folder match the list exactly – nothing changed, nothing added,
    /// nothing missing?
    /// </summary>
    public bool Matches(string id, IReadOnlyDictionary<string, string> files)
    {
        if (!_plugins.TryGetValue(id, out var listed))
        {
            return false;
        }

        var expected = listed.Where(f => PluginHash.IsCodeFile(f.Key)).ToList();
        return expected.Count > 0
            && expected.Count == files.Count
            && expected.All(f => files.TryGetValue(f.Key, out var hash) && string.Equals(hash, f.Value, StringComparison.OrdinalIgnoreCase));
    }
}
