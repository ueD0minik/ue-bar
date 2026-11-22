// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using UeBar.Core.Localization;
using UeBar.Sdk;

namespace UeBar.Core.Plugins;

/// <summary>
/// Own load context per plugin (SPEC 4.4). Rules:
/// <list type="bullet">
/// <item><c>UeBar.Sdk</c> always comes from the app – otherwise <c>IModule</c> of the plugin and of the app would be different types.</item>
/// <item>What the app (and the .NET runtime) already has is shared if it is new enough for the plugin – so framework types are always the app's.</item>
/// <item>The C#/WinRT runtime and projection exist once per process, shared by all plugins (<see cref="ProcessWideAssemblies"/>).</item>
/// <item>Everything else – the plugin's own dependencies and its language files – comes from the plugin folder.</item>
/// </list>
/// Not collectible: plugins are never unloaded while the app runs.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string SdkName = typeof(IModule).Assembly.GetName().Name!;

    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginId, string entryAssemblyPath)
        : base("plugin:" + pluginId, isCollectible: false)
    {
        _directory = Path.GetDirectoryName(entryAssemblyPath)!;

        // Only with a .deps.json (written for plugin projects) – it knows NuGet dependencies in subfolders.
        if (File.Exists(Path.ChangeExtension(entryAssemblyPath, ".deps.json")))
        {
            try
            {
                _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException or IOException)
            {
                _resolver = null; // fall back to looking in the folder
            }
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || string.Equals(name, SdkName, StringComparison.OrdinalIgnoreCase))
        {
            return null; // → default context
        }

        if (ProcessWideAssemblies.Contains(name))
        {
            // P2-5: once per process, shared by all plugins (C#/WinRT).
            return ProcessWideAssemblies.Load(name, _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(assemblyName));
        }

        var isSatellite = name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase);
        var host = isSatellite ? null : FromHost(name);
        if (host is not null && (assemblyName.Version is null || host.GetName().Version >= assemblyName.Version))
        {
            return host;
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : host;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            var candidate = Path.Combine(_directory, unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll");
            path = File.Exists(candidate) ? candidate : null;
        }

        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    /// <summary>The app's own copy (already loaded, or loadable by name), otherwise null.</summary>
    private static Assembly? FromHost(string name)
    {
        foreach (var loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private string? Probe(AssemblyName assemblyName)
    {
        var file = assemblyName.Name + ".dll";
        var path = string.IsNullOrEmpty(assemblyName.CultureName)
            ? Path.Combine(_directory, file)
            : Path.Combine(_directory, assemblyName.CultureName, file);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Looks at a plugin assembly without loading it: is it .NET at all, and what does it reference?</summary>
internal static class PluginInspector
{
    /// <summary>Assemblies of the app itself. A plugin that references them depends on internals without any stability promise.</summary>
    private static readonly string[] HostInternals = ["UeBar.Core", "UeBar.Bar", "ueBar"];

    /// <summary>
    /// Checks the entry assembly against the app's SDK. Throws a <see cref="PluginException"/> with the
    /// reason if it cannot work.
    /// </summary>
    public static Version Check(string assemblyPath, Version hostApi)
    {
        var fileName = Path.GetFileName(assemblyPath);
        var sdkName = typeof(IModule).Assembly.GetName().Name!;
        Version? sdkReference = null;
        string? internals = null;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                throw new PluginException(CoreText.T.Format("Plugin_NotAnAssembly", fileName));
            }

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                throw new PluginException(CoreText.T.Format("Plugin_NotAnAssembly", fileName));
            }

            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (string.Equals(name, sdkName, StringComparison.OrdinalIgnoreCase))
                {
                    sdkReference = reference.Version;
                }
                else if (HostInternals.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    internals = name;
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new PluginException(CoreText.T.Format("Plugin_NotAnAssembly", fileName), inner: ex);
        }

        if (internals is not null)
        {
            throw new PluginException(CoreText.T.Format("Plugin_HostReference", internals));
        }

        if (sdkReference is null)
        {
            throw new PluginException(CoreText.T.Format("Plugin_NoSdkReference", fileName));
        }

        // The SDK assembly version is major.minor of the contract: what the plugin was really built against.
        var built = new Version(sdkReference.Major, sdkReference.Minor);
        ThrowIfIncompatible(built, hostApi);
        return built;
    }

    /// <summary>Same major version, and the app's minor version must not be older than the plugin needs.</summary>
    public static void ThrowIfIncompatible(Version required, Version hostApi)
    {
        if (CoreApiVersion.Supports(required, hostApi))
        {
            return;
        }

        var needs = $"{required.Major}.{required.Minor}";
        var offers = $"{hostApi.Major}.{hostApi.Minor}";
        throw new PluginException(
            required.Major < hostApi.Major
                ? CoreText.T.Format("Plugin_ApiOlder", needs, offers)
                : CoreText.T.Format("Plugin_ApiNewer", needs, offers),
            incompatible: true);
    }
}
