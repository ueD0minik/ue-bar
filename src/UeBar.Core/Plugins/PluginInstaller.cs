// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO.Compression;
using System.Security.Cryptography;
using UeBar.Core.Localization;
using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.Core.Plugins;

/// <summary>A plugin that was opened from a ZIP or a folder and checked, ready to be installed.</summary>
public sealed class PluginPackage : IDisposable
{
    private readonly string? _temporary;

    internal PluginPackage(PluginManifest manifest, string directory, string hash, string? temporary)
    {
        Manifest = manifest;
        Directory = directory;
        Hash = hash;
        _temporary = temporary;
    }

    public PluginManifest Manifest { get; }

    /// <summary>Folder with <c>plugin.json</c> (for a ZIP: a temporary folder).</summary>
    public string Directory { get; }

    /// <summary>Checksum of the code files – the same the installed plugin will have.</summary>
    public string Hash { get; }

    public void Dispose()
    {
        if (_temporary is not null)
        {
            PluginInstaller.TryDelete(_temporary);
        }
    }
}

/// <summary>
/// Adds and removes the user's plugins in <c>%APPDATA%\ueBar\plugins</c>. A loaded plugin cannot be
/// replaced or deleted (its files are in use), so those changes wait for the next start:
/// an update is parked in <c>.staged\&lt;id&gt;</c>, a removal is a marker file in the folder.
/// </summary>
public sealed class PluginInstaller
{
    private const string StagedFolder = ".staged";
    private const long MaxBytes = 500L * 1024 * 1024;
    private const int MaxEntries = 5000;

    private readonly string _userDirectory;
    private readonly Version _api;
    private readonly Func<string, bool> _isBundledId;
    private readonly IUeLogger _log;

    public PluginInstaller(string userDirectory, Func<string, bool>? isBundledId = null, Version? apiVersion = null, IUeLogger? log = null)
    {
        _userDirectory = userDirectory;
        _isBundledId = isBundledId ?? (_ => false);
        _api = apiVersion ?? CoreApiVersion.Version;
        _log = log ?? NullLogger.Instance;
    }

    public string UserDirectory => _userDirectory;

    /// <summary>
    /// Opens a plugin ZIP or folder and checks it: <c>plugin.json</c>, the assembly, the contract
    /// version, no clash with a bundled plugin. Throws a <see cref="PluginException"/> with the reason.
    /// No plugin code runs.
    /// </summary>
    public PluginPackage Open(string path)
    {
        string? temporary = null;
        try
        {
            string root;
            if (System.IO.Directory.Exists(path))
            {
                root = path;
            }
            else if (File.Exists(path))
            {
                temporary = Path.Combine(Path.GetTempPath(), "ueBar-plugin-" + Guid.NewGuid().ToString("N"));
                Extract(path, temporary);
                root = temporary;
            }
            else
            {
                throw new PluginException(CoreText.T.Format("Plugin_PackageUnreadable", path));
            }

            var directory = FindManifestFolder(root) ?? throw new PluginException(CoreText.T["Plugin_PackageNoManifest"]);
            var manifest = PluginManifest.Load(directory);
            if (_isBundledId(manifest.Id))
            {
                throw new PluginException(CoreText.T.Format("Plugin_BundledId", manifest.Id));
            }

            var entry = Path.Combine(directory, manifest.EntryAssembly);
            if (!File.Exists(entry))
            {
                throw new PluginException(CoreText.T.Format("Plugin_EntryMissing", manifest.EntryAssembly));
            }

            PluginInspector.ThrowIfIncompatible(manifest.SdkVersion, _api);
            PluginInspector.Check(entry, _api);
            var package = new PluginPackage(manifest, directory, PluginHash.Of(directory), temporary);
            temporary = null;
            return package;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new PluginException(CoreText.T.Format("Plugin_PackageUnreadable", ex.Message), inner: ex);
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// Copies the plugin into the user's plugin folder. Returns the folder it will be loaded from.
    /// <paramref name="replacesLoaded"/>: a plugin with this ID is loaded right now – then the new
    /// files wait in <c>.staged</c> and take its place on the next start.
    /// </summary>
    public string Install(PluginPackage package, bool replacesLoaded)
    {
        ArgumentNullException.ThrowIfNull(package);
        var id = package.Manifest.Id;
        var target = Path.Combine(_userDirectory, id);
        try
        {
            if (replacesLoaded)
            {
                var staged = Path.Combine(_userDirectory, StagedFolder, id);
                TryDelete(staged);
                CopyFolder(package.Directory, staged);
                _log.Info(null, $"Plugin '{id}' {package.Manifest.Version} staged – replaces the loaded one on the next start");
            }
            else
            {
                if (System.IO.Directory.Exists(target))
                {
                    System.IO.Directory.Delete(target, recursive: true);
                }

                CopyFolder(package.Directory, target);
                _log.Info(null, $"Plugin '{id}' {package.Manifest.Version} installed to {target}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(CoreText.T.Format("Plugin_InstallFailed", ex.Message), inner: ex);
        }

        return target;
    }

    /// <summary>
    /// Removes an installed plugin. Returns true if the folder is gone now, false if it is in use and
    /// will be deleted on the next start.
    /// </summary>
    public bool Remove(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.Origin != PluginOrigin.Installed)
        {
            throw new InvalidOperationException("Only installed plugins can be removed.");
        }

        TryDelete(Path.Combine(_userDirectory, StagedFolder, plugin.Id));
        if (plugin.State != PluginState.Loaded)
        {
            try
            {
                System.IO.Directory.Delete(plugin.Directory, recursive: true);
                _log.Info(null, $"Plugin '{plugin.Id}' removed ({plugin.Directory})");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(null, $"Plugin '{plugin.Id}' could not be deleted right now ({ex.Message}) – on the next start");
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(plugin.Directory, PluginHash.RemoveMarker), DateTimeOffset.UtcNow.ToString("O"));
            _log.Info(null, $"Plugin '{plugin.Id}' is removed on the next start");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(CoreText.T.Format("Plugin_InstallFailed", ex.Message), inner: ex);
        }

        return false;
    }

    /// <summary>Is this plugin folder marked for removal on the next start?</summary>
    public static bool IsMarkedForRemoval(string directory) => File.Exists(Path.Combine(directory, PluginHash.RemoveMarker));

    /// <summary>Is an update for this ID waiting for the next start?</summary>
    public bool IsStaged(string id) => System.IO.Directory.Exists(Path.Combine(_userDirectory, StagedFolder, id));

    /// <summary>Start of the app, before anything is loaded: delete marked folders, move staged updates into place.</summary>
    public static void ApplyPending(string userDirectory, IUeLogger log)
    {
        try
        {
            if (!System.IO.Directory.Exists(userDirectory))
            {
                return;
            }

            foreach (var directory in System.IO.Directory.EnumerateDirectories(userDirectory).Where(IsMarkedForRemoval).ToList())
            {
                if (TryDeleteMarked(directory))
                {
                    log.Info(null, $"Plugin folder removed: {directory}");
                }
                else
                {
                    log.Warn(null, $"Plugin folder could not be removed (next start): {directory}");
                }
            }

            var stagedRoot = Path.Combine(userDirectory, StagedFolder);
            if (!System.IO.Directory.Exists(stagedRoot))
            {
                return;
            }

            foreach (var staged in System.IO.Directory.EnumerateDirectories(stagedRoot).ToList())
            {
                var target = Path.Combine(userDirectory, Path.GetFileName(staged));
                try
                {
                    if (System.IO.Directory.Exists(target))
                    {
                        System.IO.Directory.Delete(target, recursive: true);
                    }

                    System.IO.Directory.Move(staged, target);
                    log.Info(null, $"Plugin update applied: {target}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn(null, $"Plugin update could not be applied (next start): {target} – {ex.Message}");
                }
            }

            if (!System.IO.Directory.EnumerateFileSystemEntries(stagedRoot).Any())
            {
                System.IO.Directory.Delete(stagedRoot);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn(null, $"Pending plugin changes could not be applied: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a folder marked for removal – the marker goes last, so a folder that could only be
    /// deleted partly (a file still in use) stays marked and is tried again on the next start.
    /// </summary>
    private static bool TryDeleteMarked(string directory)
    {
        try
        {
            var marker = Path.Combine(directory, PluginHash.RemoveMarker);
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(file, marker, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }

            System.IO.Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The folder with plugin.json: the root itself, or its only subfolder (ZIPs often wrap everything in one folder).</summary>
    private static string? FindManifestFolder(string root)
    {
        if (File.Exists(Path.Combine(root, PluginManifest.FileName)))
        {
            return root;
        }

        var folders = System.IO.Directory.GetDirectories(root);
        return folders.Length == 1 && System.IO.Directory.GetFiles(root).Length == 0 && File.Exists(Path.Combine(folders[0], PluginManifest.FileName))
            ? folders[0]
            : null;
    }

    /// <summary>Unpacks a ZIP – never outside the target folder, with limits for size and number of files.</summary>
    internal static void Extract(string zipFile, string target)
    {
        using var stream = File.OpenRead(zipFile);
        Extract(stream, target);
    }

    internal static void Extract(Stream zip, string target)
    {
        var root = Path.GetFullPath(target);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxEntries || archive.Entries.Sum(e => e.Length) > MaxBytes)
        {
            throw new PluginException(CoreText.T["Plugin_PackageTooBig"]);
        }

        System.IO.Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginException(CoreText.T["Plugin_PackageUnsafe"]);
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                System.IO.Directory.CreateDirectory(path);
                continue;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static void CopyFolder(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (string.Equals(Path.GetFileName(file), PluginHash.RemoveMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

/// <summary>
/// Portable single exe: the bundled plugins travel inside the exe as a ZIP and are unpacked once
/// per version to <c>%LOCALAPPDATA%\ueBar\builtin-plugins\&lt;version&gt;-&lt;checksum&gt;</c>. The checksum
/// list is read from the ZIP in the exe (not from the unpacked folder), so unpacked files that were
/// changed afterwards are noticed.
/// </summary>
public static class BundledPluginArchive
{
    private const string CompleteMarker = ".complete";

    /// <summary>Returns the folder with the unpacked plugins and their checksum list.</summary>
    public static (string Directory, BundledPluginList List) Unpack(Func<Stream> open, string root, string version, IUeLogger? log = null)
    {
        log ??= NullLogger.Instance;
        string checksum;
        BundledPluginList list;
        using (var stream = open())
        {
            checksum = Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.GetEntry(BundledPluginList.FileName);
            if (entry is null)
            {
                list = BundledPluginList.Empty;
            }
            else
            {
                using var reader = new StreamReader(entry.Open());
                list = BundledPluginList.Parse(reader.ReadToEnd());
            }
        }

        var name = $"{version}-{checksum}";
        var directory = Path.Combine(root, name);
        if (!File.Exists(Path.Combine(directory, CompleteMarker)))
        {
            PluginInstaller.TryDelete(directory);
            using var stream = open();
            PluginInstaller.Extract(stream, directory);
            File.WriteAllText(Path.Combine(directory, CompleteMarker), DateTimeOffset.UtcNow.ToString("O"));
            log.Info(null, $"Bundled plugins unpacked to {directory}");
        }

        // Older versions' folders: best effort (another running version may still use its own).
        try
        {
            foreach (var other in System.IO.Directory.EnumerateDirectories(root).Where(d => !string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                // Marker first: a folder that can only be deleted partly is unpacked again when needed.
                File.Delete(Path.Combine(other, CompleteMarker));
                PluginInstaller.TryDelete(other);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not important
        }

        return (directory, list);
    }
}
