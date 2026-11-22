// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Reflection;
using System.Runtime.Loader;

namespace UeBar.Core.Plugins;

/// <summary>
/// Assemblies that may exist only once per process (P2-5): the C#/WinRT runtime registers a global COM wrapper
/// and its projection types must match it. A second plugin with its own copy in its own load context fails
/// ("Attempt to update previously set global instance") – e.g. the Windows and the Screenshot plugin, both
/// using Windows APIs. So the highest version any plugin brings is loaded once into the app's context and
/// shared by all plugins. Everything else stays private to its plugin.
/// </summary>
internal static class ProcessWideAssemblies
{
    public static IReadOnlyList<string> Names { get; } = ["WinRT.Runtime", "Microsoft.Windows.SDK.NET"];

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (string Path, Version Version)> Candidates = new(StringComparer.OrdinalIgnoreCase);
    private static bool _hooked;

    public static bool Contains(string? name) => name is not null && Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers the copies in a plugin folder; the highest version wins. Call for every plugin before loading one.</summary>
    public static void Offer(string directory)
    {
        foreach (var name in Names)
        {
            var path = Path.Combine(directory, name + ".dll");
            if (!File.Exists(path))
            {
                continue;
            }

            Version version;
            try
            {
                version = AssemblyName.GetAssemblyName(path).Version ?? new Version(0, 0);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                continue;
            }

            lock (Gate)
            {
                if (!Candidates.TryGetValue(name, out var known) || version > known.Version)
                {
                    Candidates[name] = (path, version);
                }

                if (!_hooked)
                {
                    // The projection itself asks the app's context for the runtime.
                    AssemblyLoadContext.Default.Resolving += (_, requested) => Contains(requested.Name) ? Load(requested.Name!, null) : null;
                    _hooked = true;
                }
            }
        }
    }

    /// <summary>The shared copy: already loaded, otherwise the best offered one (or <paramref name="fallback"/>) loaded into the app's context.</summary>
    public static Assembly? Load(string name, string? fallback)
    {
        lock (Gate)
        {
            foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return loaded;
                }
            }

            var path = Candidates.TryGetValue(name, out var best) ? best.Path : fallback;
            return path is not null && File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        }
    }
}
