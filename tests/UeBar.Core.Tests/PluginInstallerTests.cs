// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UeBar.Core.Plugins;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>P2-1: plugin.json, contract version, adding/removing plugins, the bundled list and the portable archive.</summary>
public sealed class PluginInstallerTests
{
    private const string Manifest = """
        {
          // comments and trailing commas are fine
          "id": "hello",
          "name": { "en": "Hello", "de": "Hallo" },
          "description": "One text for all languages",
          "version": "1.2.3",
          "author": "Someone",
          "license": "MIT",
          "homepage": "https://example.org",
          "sdkVersion": "1.0",
          "entryAssembly": "Hello.dll",
        }
        """;

    [Fact]
    public void Manifest_is_read_with_texts_per_language()
    {
        var manifest = PluginManifest.Parse(Manifest);

        Assert.Equal("hello", manifest.Id);
        Assert.Equal("Hallo", manifest.DisplayName(CultureInfo.GetCultureInfo("de-AT")));
        Assert.Equal("Hello", manifest.DisplayName(CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("Hello", manifest.DisplayName(CultureInfo.GetCultureInfo("fr-FR"))); // falls back to English
        Assert.Equal("One text for all languages", manifest.Description(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal(new Version(1, 0), manifest.SdkVersion);
        Assert.Equal("Hello.dll", manifest.EntryAssembly);
        Assert.Null(manifest.EntryType);
    }

    [Theory]
    [InlineData("\"id\": \"hello\"", "\"id\": \"Hello World\"", "id")]
    [InlineData("\"id\": \"hello\"", "\"id\": \"../evil\"", "id")]
    [InlineData("\"id\": \"hello\",", "", "id")]
    [InlineData("\"version\": \"1.2.3\",", "", "version")]
    [InlineData("\"sdkVersion\": \"1.0\"", "\"sdkVersion\": \"one\"", "sdkVersion")]
    [InlineData("\"entryAssembly\": \"Hello.dll\"", "\"entryAssembly\": \"..\\\\other\\\\Hello.dll\"", "entryAssembly")]
    [InlineData("\"entryAssembly\": \"Hello.dll\"", "\"entryAssembly\": \"Hello.exe\"", "entryAssembly")]
    [InlineData("\"name\": { \"en\": \"Hello\", \"de\": \"Hallo\" },", "", "name")]
    public void Manifest_with_a_bad_field_names_the_field(string find, string replace, string field)
    {
        var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(Manifest.Replace(find, replace, StringComparison.Ordinal)));

        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Manifest_that_is_not_json_says_so()
    {
        Assert.Throws<PluginException>(() => PluginManifest.Parse("id: hello"));
        Assert.Throws<PluginException>(() => PluginManifest.Parse("[]"));
    }

    [Theory]
    [InlineData("1.0", "1.0", true)]
    [InlineData("1.0", "1.4", true)] // host is newer: fine
    [InlineData("1.4", "1.0", false)] // plugin needs more than the host has
    [InlineData("2.0", "1.9", false)]
    [InlineData("1.0", "2.0", false)]
    public void Api_version_rule_same_major_and_minor_not_newer_than_the_host(string plugin, string host, bool loads)
    {
        Assert.True(CoreApiVersion.TryParse(plugin, out var required));
        Assert.True(CoreApiVersion.TryParse(host, out var offered));

        Assert.Equal(loads, CoreApiVersion.Supports(required, offered));
    }

    [Fact]
    public void Api_version_parses_major_minor_and_ignores_a_patch()
    {
        Assert.True(CoreApiVersion.TryParse("1", out var one));
        Assert.Equal(new Version(1, 0), one);
        Assert.True(CoreApiVersion.TryParse(" 1.2.3 ", out var patched));
        Assert.Equal(new Version(1, 2), patched);
        Assert.False(CoreApiVersion.TryParse("", out _));
        Assert.False(CoreApiVersion.TryParse("v1", out _));
        Assert.False(CoreApiVersion.TryParse("1.x", out _));
        Assert.Equal(CoreApiVersion.Text, $"{CoreApiVersion.Major}.{CoreApiVersion.Minor}");
        Assert.Equal(CoreApiVersion.Major, CoreApiVersion.Current);
    }

    [Fact]
    public void Sdk_assembly_and_package_version_carry_the_api_version()
    {
        // The loader compares the plugin's reference to UeBar.Sdk with this – they must not drift apart.
        var assembly = typeof(IModule).Assembly.GetName().Version!;
        Assert.Equal(CoreApiVersion.Major, assembly.Major);
        Assert.Equal(CoreApiVersion.Minor, assembly.Minor);

        var project = XDocument.Load(Path.Combine(SourceTree.Src, "UeBar.Sdk", "UeBar.Sdk.csproj"));
        var package = project.Descendants("Version").First().Value;
        Assert.StartsWith(CoreApiVersion.Text + ".", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_plugins_carry_the_app_version_and_the_current_api_version()
    {
        var props = XDocument.Load(Path.Combine(SourceTree.AppRoot, "Directory.Build.props"));
        var appVersion = props.Descendants("Version").First().Value;
        Assert.NotEmpty(SourceTree.PluginSources);
        foreach (var folder in SourceTree.PluginSources)
        {
            var manifest = PluginManifest.Load(folder);
            Assert.Equal(appVersion, manifest.Version);
            Assert.Equal(CoreApiVersion.Version, manifest.SdkVersion);
        }
    }

    [Fact]
    public void Plugin_projects_reference_only_the_sdk_and_the_app_references_no_plugin()
    {
        var reference = new Regex(@"<ProjectReference\s+Include=""([^""]+)""");
        foreach (var folder in SourceTree.PluginSources)
        {
            var project = Assert.Single(Directory.GetFiles(folder, "*.csproj"));
            var references = reference.Matches(File.ReadAllText(project)).Select(m => Path.GetFileName(m.Groups[1].Value));
            Assert.Equal(["UeBar.Sdk.csproj"], references);
        }

        var app = reference.Matches(File.ReadAllText(Path.Combine(SourceTree.Src, "UeBar.App", "UeBar.App.csproj"))).Select(m => Path.GetFileName(m.Groups[1].Value)).ToList();
        Assert.DoesNotContain(app, r => r.Contains("Modules", StringComparison.Ordinal));
        Assert.Empty(reference.Matches(File.ReadAllText(Path.Combine(SourceTree.Src, "UeBar.Sdk", "UeBar.Sdk.csproj"))));
    }

    // ------------------------------------------------------------------ add / remove

    private static string Zip(PluginSandbox box, string pluginFolder, bool wrapInFolder = false)
    {
        var zip = Path.Combine(box.Root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(pluginFolder, zip, CompressionLevel.Fastest, includeBaseDirectory: wrapInFolder);
        return zip;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zip_is_checked_installed_and_loaded_on_the_next_start(bool wrapInFolder)
    {
        using var box = new PluginSandbox();
        var source = box.Add(Path.Combine(box.Root, "downloads"), "hello", version: "2.0.0");
        var host = box.Host();
        host.Discover();

        PluginInfo added;
        using (var package = host.Installer.Open(Zip(box, source, wrapInFolder)))
        {
            Assert.Equal("hello", package.Manifest.Id);
            Assert.Equal("2.0.0", package.Manifest.Version);
            Assert.False(host.IsChanged(package));
            added = host.Install(package); // the app calls this after the user said "Allow"
            Assert.Equal(package.Hash, added.Hash);
        }

        Assert.Equal(Path.Combine(box.User, "hello"), added.Directory);
        Assert.True(File.Exists(Path.Combine(box.User, "hello", "plugin.json")));
        Assert.Equal(PluginState.Pending, added.State); // allowed, loaded on the next start
        Assert.True(host.RestartNeeded);
        Assert.Contains(added, host.Plugins);

        var next = box.Host();
        var plugin = Assert.Single(next.Discover());
        Assert.Equal(PluginState.Pending, plugin.State); // no second question
        await next.LoadAsync();
        Assert.Equal(PluginState.Loaded, plugin.State);
    }

    [Fact]
    public void Folder_can_be_added_too()
    {
        using var box = new PluginSandbox();
        var source = box.Add(Path.Combine(box.Root, "downloads"), "hello");
        var host = box.Host();
        host.Discover();

        using var package = host.Installer.Open(source);
        host.Install(package);

        Assert.True(File.Exists(Path.Combine(box.User, "hello", "UeBar.TestPlugin.dll")));
    }

    [Fact]
    public void Package_problems_are_reported_before_anything_is_copied()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "taken");
        var host = box.Host();
        host.Discover();
        var downloads = Path.Combine(box.Root, "downloads");

        var notAPlugin = Path.Combine(downloads, "nothing");
        Directory.CreateDirectory(notAPlugin);
        File.WriteAllText(Path.Combine(notAPlugin, "readme.txt"), "hi");
        Assert.Equal(Localization.CoreText.T["Plugin_PackageNoManifest"], Assert.Throws<PluginException>(() => host.Installer.Open(Zip(box, notAPlugin))).Message);

        var bundledId = box.Add(downloads, "taken");
        Assert.Equal(Localization.CoreText.T.Format("Plugin_BundledId", "taken"), Assert.Throws<PluginException>(() => host.Installer.Open(bundledId)).Message);

        var tooNew = box.Add(downloads, "future", sdkVersion: "3.1");
        var incompatible = Assert.Throws<PluginException>(() => host.Installer.Open(tooNew));
        Assert.True(incompatible.Incompatible);
        Assert.Contains("3.1", incompatible.Message);

        var broken = box.Add(downloads, "broken");
        File.WriteAllText(Path.Combine(broken, "UeBar.TestPlugin.dll"), "nope");
        Assert.Throws<PluginException>(() => host.Installer.Open(broken));

        Assert.Throws<PluginException>(() => host.Installer.Open(Path.Combine(downloads, "does-not-exist.zip")));
        var notAZip = Path.Combine(downloads, "text.zip");
        File.WriteAllText(notAZip, "plain text");
        Assert.Throws<PluginException>(() => host.Installer.Open(notAZip));

        Assert.Empty(Directory.EnumerateFileSystemEntries(box.User));
    }

    [Fact]
    public void Zip_with_paths_outside_the_folder_is_refused()
    {
        using var box = new PluginSandbox();
        var zip = Path.Combine(box.Root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../../outside.txt").Open());
            writer.Write("escaped");
        }

        var ex = Assert.Throws<PluginException>(() => box.Host().Installer.Open(zip));

        Assert.Equal(Localization.CoreText.T["Plugin_PackageUnsafe"], ex.Message);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "outside.txt")));
    }

    [Fact]
    public void Removing_a_plugin_that_is_not_loaded_deletes_it_and_forgets_the_permission()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.User, "mine");
        var host = box.Host();
        var plugin = Assert.Single(host.Discover());
        host.Decide(plugin, allow: true);

        Assert.True(host.Uninstall(plugin));

        Assert.False(Directory.Exists(directory));
        Assert.Empty(host.Plugins);
        box.Add(box.User, "mine");
        Assert.Equal(PluginState.NeedsApproval, Assert.Single(box.Host().Discover()).State); // asks again
    }

    [Fact]
    public async Task Removing_a_loaded_plugin_happens_on_the_next_start()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.User, "mine");
        var host = await box.LoadAllAsync();
        var plugin = Assert.Single(host.Plugins);
        Assert.Equal(PluginState.Loaded, plugin.State);

        Assert.False(host.Uninstall(plugin)); // files are in use

        Assert.True(plugin.RemovalPending);
        Assert.True(host.RestartNeeded);
        Assert.True(Directory.Exists(directory));
        Assert.True(PluginInstaller.IsMarkedForRemoval(directory));

        // A new start in this same process cannot delete the still loaded assembly; the folder is at
        // least no longer offered as a plugin.
        Assert.Empty(box.Host().Discover());
    }

    [Fact]
    public void Marked_folder_is_deleted_at_the_start()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.User, "mine");
        File.WriteAllText(Path.Combine(directory, PluginHash.RemoveMarker), "");

        Assert.Empty(box.Host().Discover());

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Update_of_a_loaded_plugin_waits_for_the_next_start()
    {
        using var box = new PluginSandbox();
        box.Add(box.User, "mine", version: "1.0.0");
        var host = await box.LoadAllAsync();
        var newer = box.Add(Path.Combine(box.Root, "downloads"), "mine", version: "1.1.0");

        using (var package = host.Installer.Open(newer))
        {
            Assert.True(host.IsChanged(package)); // the dialog says "files changed"
            var info = host.Install(package);
            Assert.Equal("1.1.0", info.UpdatePendingVersion);
            Assert.Equal("1.0.0", info.Manifest!.Version);
        }

        Assert.True(host.RestartNeeded);
        Assert.True(host.Installer.IsStaged("mine"));
        Assert.Equal("1.0.0", PluginManifest.Load(Path.Combine(box.User, "mine")).Version);

        // The old assembly is still loaded in this test process, so the swap itself is tested separately below.
    }

    [Fact]
    public void Staged_update_takes_the_place_of_the_old_folder_at_the_start()
    {
        using var box = new PluginSandbox();
        box.Add(box.User, "mine", version: "1.0.0");
        box.Add(Path.Combine(box.User, ".staged"), "mine", version: "1.1.0");

        var plugin = Assert.Single(box.Host().Discover());

        Assert.Equal("1.1.0", plugin.Manifest!.Version);
        Assert.False(Directory.Exists(Path.Combine(box.User, ".staged")));
    }

    // ------------------------------------------------------------------ bundled list, portable archive

    [Fact]
    public void Bundled_list_reads_the_sha256sum_format_and_keeps_the_declared_order()
    {
        var list = BundledPluginList.Parse("""
            # plugin: homeassistant
            # plugin: dummy
            AAAA  dummy/Dummy.dll
            BBBB  dummy/plugin.json
            CCCC  dummy/Dummy.pdb
            DDDD  homeassistant\de\Ha.resources.dll
            EEEE *homeassistant/plugin.json
            """);

        Assert.Equal(["homeassistant", "dummy"], list.Ids);
        Assert.True(list.Contains("Dummy"));
        Assert.True(list.Matches("dummy", new Dictionary<string, string> { ["dummy.dll"] = "aaaa", ["plugin.json"] = "bbbb" })); // .pdb does not count
        Assert.False(list.Matches("dummy", new Dictionary<string, string> { ["dummy.dll"] = "ffff", ["plugin.json"] = "bbbb" }));
        Assert.False(list.Matches("dummy", new Dictionary<string, string> { ["dummy.dll"] = "aaaa" }));
        Assert.True(list.Matches("homeassistant", new Dictionary<string, string> { ["de/ha.resources.dll"] = "dddd", ["plugin.json"] = "eeee" }));
        Assert.False(list.Matches("unknown", new Dictionary<string, string>()));
        Assert.False(BundledPluginList.Empty.Contains("dummy"));
    }

    [Fact]
    public async Task Portable_archive_is_unpacked_once_per_version_and_loads()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        File.WriteAllLines(Path.Combine(box.Bundled, BundledPluginList.FileName),
            Directory.EnumerateDirectories(box.Bundled).SelectMany(d => PluginHash.Files(d).Select(f => $"{f.Value}  {Path.GetFileName(d)}/{f.Key}")));
        var zip = Path.Combine(box.Root, "bundled.zip");
        ZipFile.CreateFromDirectory(box.Bundled, zip);
        var bytes = File.ReadAllBytes(zip);
        var root = Path.Combine(box.Root, "local", "builtin-plugins");
        var old = Path.Combine(root, "0.9.0-abc");
        Directory.CreateDirectory(old);

        var (directory, list) = BundledPluginArchive.Unpack(() => new MemoryStream(bytes), root, "1.2.0", box.Log);

        Assert.StartsWith(Path.Combine(root, "1.2.0-"), directory, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directory, "good", "plugin.json")));
        Assert.False(Directory.Exists(old)); // older versions are cleaned up
        Assert.Equal(["good"], list.Ids);

        // Second start: nothing is unpacked again.
        var marker = File.GetLastWriteTimeUtc(Path.Combine(directory, "good", "plugin.json"));
        var (again, _) = BundledPluginArchive.Unpack(() => new MemoryStream(bytes), root, "1.2.0", box.Log);
        Assert.Equal(directory, again);
        Assert.Equal(marker, File.GetLastWriteTimeUtc(Path.Combine(directory, "good", "plugin.json")));

        var host = new PluginHost(new PluginHostOptions { BundledDirectory = directory, BundledList = list, UserDirectory = box.User }, box.State, new PluginTrustStore(null), box.Log);
        var plugin = Assert.Single(host.Discover());
        Assert.True(plugin.IsVerifiedBundled);
        await host.LoadAsync();
        Assert.Equal(PluginState.Loaded, plugin.State);

        // An unpacked file changed afterwards no longer matches the list from the archive.
        File.AppendAllText(Path.Combine(directory, "good", "plugin.json"), " ");
        Assert.False(Assert.Single(host.Discover()).IsVerifiedBundled);
    }
}
