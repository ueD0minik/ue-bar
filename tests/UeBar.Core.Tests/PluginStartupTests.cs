// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Pins;
using UeBar.Core.Plugins;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>
/// P2-5-Fix 1: the start took 15–20 s – every start hashed all plugin files again, among them 50 MB of WinRT
/// projection. Now unchanged files keep their checksum, and the projection comes once in plugins\.shared.
/// </summary>
public sealed class PluginHashCacheTests
{
    [Fact]
    public void An_unchanged_file_is_not_read_again_and_a_changed_one_always_is()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "a.dll");
        File.WriteAllBytes(file, [1, 2, 3]);
        var cacheFile = Path.Combine(dir.Path, "cache.json");

        var first = new PluginHashCache(cacheFile);
        first.Load();
        var hash = first.HashOf(file);
        first.Save();
        Assert.Equal((0, 1), first.Stats);

        var second = new PluginHashCache(cacheFile); // the next start
        second.Load();
        Assert.Equal(hash, second.HashOf(file));
        Assert.Equal((1, 0), second.Stats);

        // Same size, other content, written later: hashed again – the cache never hides a change.
        File.WriteAllBytes(file, [9, 9, 9]);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));
        var changed = second.HashOf(file);
        Assert.NotEqual(hash, changed);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([9, 9, 9])), changed);

        // Other size with the old time stamp put back: hashed again as well.
        var time = File.GetLastWriteTimeUtc(file);
        File.WriteAllBytes(file, [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(file, time);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([9, 9, 9, 9])), second.HashOf(file));
        Assert.Equal((1, 2), second.Stats);
    }

    [Fact]
    public void A_broken_cache_file_only_costs_time()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "a.dll");
        File.WriteAllBytes(file, [1, 2, 3]);
        var cacheFile = Path.Combine(dir.Path, "cache.json");
        File.WriteAllText(cacheFile, "{ kaputt");

        var cache = new PluginHashCache(cacheFile);
        cache.Load();

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([1, 2, 3])), cache.HashOf(file));
        Assert.Equal((0, 1), cache.Stats);
    }

    [Fact]
    public void Plugin_folders_use_the_cache_and_a_changed_bundled_plugin_still_needs_approval()
    {
        using var sandbox = new PluginSandbox();
        var directory = sandbox.Add(sandbox.Bundled, "alpha");
        var list = sandbox.BuildList();
        var cacheFile = Path.Combine(sandbox.Root, "data", "plugin-hashes.json");

        PluginHost Host() => new(
            new PluginHostOptions { BundledDirectory = sandbox.Bundled, BundledList = list, UserDirectory = sandbox.User, HashCache = new PluginHashCache(cacheFile, sandbox.Log) },
            sandbox.State,
            new PluginTrustStore(sandbox.TrustFile, sandbox.Log),
            sandbox.Log);

        Assert.Equal(PluginState.Pending, Assert.Single(Host().Discover()).State);
        Assert.True(File.Exists(cacheFile));
        Assert.Equal(PluginState.Pending, Assert.Single(Host().Discover()).State); // second start: from the cache

        // Someone swaps the plugin's dependency (other content, same name): it is no longer the bundled one.
        var dependency = Path.Combine(directory, "UeBar.TestPlugin.Dependency.dll");
        File.Copy(Path.Combine(directory, "UeBar.TestPlugin.dll"), dependency, overwrite: true);
        Assert.Equal(PluginState.NeedsApproval, Assert.Single(Host().Discover()).State);
    }
}

/// <summary>
/// P2-5-Fix 2: a slow start on a test PC coincided with the virus scanner's cloud check. Discovering now opens
/// no DLL of an unchanged plugin at all – the check of the entry assembly is remembered like its checksum.
/// </summary>
public sealed class PluginEntryCheckCacheTests
{
    [Fact]
    public void A_note_holds_only_while_the_file_is_unchanged_and_survives_a_restart()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "a.dll");
        File.WriteAllBytes(file, [1, 2, 3]);
        var cacheFile = Path.Combine(dir.Path, "cache.json");

        var first = new PluginHashCache(cacheFile);
        Assert.Null(first.NoteOf(file, "sdk"));
        first.SetNote(file, "sdk", "1.5");
        first.HashOf(file); // the checksum of the same file keeps the note
        first.Save();

        var second = new PluginHashCache(cacheFile);
        second.Load();
        Assert.Equal("1.5", second.NoteOf(file, "sdk"));

        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.Null(second.NoteOf(file, "sdk")); // changed → check again
        Assert.Null(PluginHashCache.None.NoteOf(file, "sdk"));
    }

    [Fact]
    public void An_unchanged_plugin_is_not_checked_again_but_a_replaced_entry_assembly_is()
    {
        using var sandbox = new PluginSandbox();
        var directory = sandbox.Add(sandbox.User, "beta");
        var cacheFile = Path.Combine(sandbox.Root, "data", "plugin-hashes.json");

        PluginHost Host() => new(
            new PluginHostOptions { UserDirectory = sandbox.User, HashCache = new PluginHashCache(cacheFile, sandbox.Log) },
            sandbox.State,
            new PluginTrustStore(sandbox.TrustFile, sandbox.Log),
            sandbox.Log);

        var plugin = Assert.Single(Host().Discover());
        Assert.Equal(PluginState.NeedsApproval, plugin.State);
        Assert.Equal(PluginState.NeedsApproval, Assert.Single(Host().Discover()).State); // second start: check from the cache
        Assert.Contains(sandbox.Log.Entries, e => e.Message.Contains("beta: entry assembly unchanged, check from the cache", StringComparison.Ordinal));

        // Another file under the same name: not an assembly any more – found, because the file changed.
        File.WriteAllBytes(Path.Combine(directory, "UeBar.TestPlugin.dll"), [0x4D, 0x5A, 1, 2, 3]);
        var broken = Assert.Single(Host().Discover());
        Assert.Equal(PluginState.Error, broken.State);
    }

    [Fact]
    public void Loading_waits_long_enough_for_a_virus_scanner() =>
        Assert.True(new PluginHostOptions { UserDirectory = "x" }.LoadTimeout >= TimeSpan.FromSeconds(45));
}

public sealed class SharedPluginAssembliesTests
{
    [Fact]
    public void The_shared_folder_is_no_plugin_and_is_checked_against_the_bundled_list()
    {
        using var sandbox = new PluginSandbox();
        sandbox.Add(sandbox.Bundled, "alpha");
        var shared = Path.Combine(sandbox.Bundled, PluginHash.SharedFolder);
        Directory.CreateDirectory(shared);
        File.WriteAllBytes(Path.Combine(shared, "WinRT.Runtime.dll"), [1, 2, 3, 4]);
        var list = sandbox.BuildList();

        Assert.Equal(["alpha"], list.Ids); // ".shared" is not a module
        Assert.NotNull(list.HashOf(PluginHash.SharedFolder, "WinRT.Runtime.dll"));
        var host = sandbox.Host(list);
        Assert.Equal(["alpha"], host.Discover().Select(p => p.Id));
        Assert.DoesNotContain(sandbox.Log.Entries, e => e.Message.Contains("Shared plugin assemblies", StringComparison.Ordinal));

        File.WriteAllBytes(Path.Combine(shared, "WinRT.Runtime.dll"), [4, 3, 2, 1]); // not the file that came with ue Bar
        sandbox.Host(list).Discover();
        Assert.Contains(sandbox.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Shared plugin assemblies not used", StringComparison.Ordinal));
    }
}

public sealed class PinFilesTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void Closing_deletes_the_pin_with_its_image_and_the_storage_use_follows()
    {
        using var dir = new TempDirectory();
        var pins = new PinManager(dir.Path, new ListLogger());
        var a = pins.CreateImage(Png)!;
        pins.CreateImage(Png);
        pins.CreateText("Notiz");

        var (count, bytes) = pins.StorageUse();
        Assert.Equal(3, count);
        Assert.True(bytes > 2 * Png.Length); // two images + pins.json
        Assert.Equal(2, Directory.GetFiles(dir.Path, "pin-*").Length);

        pins.Close(a);
        Assert.Single(Directory.GetFiles(dir.Path, "pin-*"));
        pins.CloseAll();
        Assert.Empty(Directory.GetFiles(dir.Path, "pin-*"));
        Assert.Equal(0, pins.StorageUse().Pins);
    }

    [Fact]
    public void At_the_start_files_without_an_entry_are_deleted()
    {
        using var dir = new TempDirectory();
        var first = new PinManager(dir.Path, new ListLogger());
        var kept = first.CreateImage(Png)!;
        File.WriteAllBytes(Path.Combine(dir.Path, "pin-0123456789.png"), Png); // orphaned image
        File.WriteAllText(Path.Combine(dir.Path, "pins.json.tmp"), "{}"); // left over from an interrupted save

        var log = new ListLogger();
        var second = new PinManager(dir.Path, log);
        second.Load();

        Assert.Equal(kept, Assert.Single(second.Pins).Id);
        Assert.Equal([Path.GetFileName(second.ImagePath(second.Find(kept)!)!)], Directory.GetFiles(dir.Path, "pin-*").Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(dir.Path, "pins.json.tmp")));
        Assert.Contains(log.Entries, e => e.Message.Contains("2 orphaned file(s)", StringComparison.Ordinal));
    }
}
