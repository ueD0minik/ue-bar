// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.IO;
using UeBar.App.Settings;
using UeBar.Core;
using UeBar.Core.Logging;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;

namespace UeBar.App.Plugins;

/// <summary>
/// Where this app finds its plugins (SPEC 4.4): the bundled ones next to the app
/// (<c>plugins\</c>) – or, for the portable single exe, embedded in the exe and unpacked once per
/// version – and the user's in <c>%APPDATA%\ueBar\plugins</c>.
/// </summary>
internal static class PluginStartup
{
    /// <summary>Name of the embedded ZIP in the portable exe (see build/BundledPlugins.targets).</summary>
    private const string EmbeddedPlugins = "UeBar.BundledPlugins.zip";

    public static PluginHost CreateHost(AppPaths paths, SettingsService settings, IUeLogger log)
    {
        var (directory, list) = LocateBundled(log, out var portable);
        var options = new PluginHostOptions
        {
            BundledDirectory = directory,
            BundledList = list,
            UserDirectory = paths.Plugins,

            // P3-1: the portable exe also takes plugins from plugins\ next to it (unpacked ZIPs, e.g. on a stick).
            PortableDirectory = portable,

            // P2-5-Fix 1: checksums of unchanged files (path, size, times) from the last start.
            HashCache = new PluginHashCache(Path.Combine(paths.Root, "plugin-hashes.json"), log),
        };
        return new PluginHost(options, settings, new PluginTrustStore(paths.PluginTrustFile, log), log);
    }

    private static (string? Directory, BundledPluginList List) LocateBundled(IUeLogger log, out string? portable)
    {
        portable = null;
        var assembly = typeof(PluginStartup).Assembly;
        if (assembly.GetManifestResourceInfo(EmbeddedPlugins) is not null)
        {
            portable = PortablePluginFolder(Environment.ProcessPath);
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ueBar", "builtin-plugins");
                return BundledPluginArchive.Unpack(() => assembly.GetManifestResourceStream(EmbeddedPlugins)!, root, AppInfo.Version, log);
            }
            catch (Exception ex)
            {
                // The app still starts – just without its bundled plugins (their tiles show "module missing").
                log.Error(null, "Bundled plugins could not be unpacked", ex);
                return (null, BundledPluginList.Empty);
            }
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "plugins");
        return (directory, BundledPluginList.Load(Path.Combine(directory, BundledPluginList.FileName)));
    }

    /// <summary>
    /// <c>plugins\</c> next to the portable exe (P3-1). Only read – a write-protected stick or a school share works. Null when
    /// the folder is not there.
    /// </summary>
    internal static string? PortablePluginFolder(string? exePath)
    {
        if (Path.GetDirectoryName(exePath) is not { Length: > 0 } folder)
        {
            return null;
        }

        var plugins = Path.Combine(folder, "plugins");
        return Directory.Exists(plugins) ? plugins : null;
    }
}

/// <summary>Restart of the app (a plugin change needs one): start a second process that waits for this one to end.</summary>
internal static class AppRestart
{
    private const string Argument = "--after-exit=";

    /// <summary>Starts the successor. The caller then ends this process.</summary>
    public static bool StartSuccessor(IUeLogger log)
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe)
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(exe, Argument + Environment.ProcessId) { UseShellExecute = false })?.Dispose();
            log.Info(null, "Restart requested – successor started");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(null, "Restart failed", ex);
            return false;
        }
    }

    /// <summary>In the successor: wait until the previous process has ended (at most 15 s).</summary>
    public static async Task WaitForPredecessorAsync(string[] args, IUeLogger log)
    {
        var value = args.FirstOrDefault(a => a.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));
        if (value is null || !int.TryParse(value[Argument.Length..], out var pid))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(pid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await previous.WaitForExitAsync(timeout.Token);
            log.Info(null, $"Restart: previous process {pid} has ended");
        }
        catch (ArgumentException)
        {
            // already gone
        }
        catch (OperationCanceledException)
        {
            log.Warn(null, $"Restart: previous process {pid} is still running after 15 s");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Warn(null, $"Restart: could not wait for process {pid}: {ex.Message}");
        }
    }
}
