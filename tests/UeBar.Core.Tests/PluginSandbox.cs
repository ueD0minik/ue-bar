// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;

namespace UeBar.Core.Tests;

/// <summary>
/// Plugin folders in a temporary directory, built from the fixture assembly
/// <c>tests/TestPlugins/UeBar.TestPlugin</c> (several module classes, picked with "entryType").
/// The fixture modules take their ID from the folder name.
/// </summary>
internal sealed class PluginSandbox : IDisposable
{
    public const string Good = "UeBar.TestPlugin.GoodModule";
    public const string Throwing = "UeBar.TestPlugin.ThrowingModule";
    public const string Slow = "UeBar.TestPlugin.SlowModule";
    public const string OtherId = "UeBar.TestPlugin.OtherIdModule";
    public const string WithDependency = "UeBar.TestPlugin.DependencyModule";

    private readonly TempDirectory _temp = new();

    public PluginSandbox()
    {
        Bundled = Path.Combine(_temp.Path, "app", "plugins");
        User = Path.Combine(_temp.Path, "data", "plugins");
        TrustFile = Path.Combine(_temp.Path, "data", "plugin-trust.json");
        Directory.CreateDirectory(Bundled);
        Directory.CreateDirectory(User);
    }

    public string Root => _temp.Path;

    public string Bundled { get; }

    public string User { get; }

    public string TrustFile { get; }

    public InMemoryModuleStateStore State { get; } = new();

    public ListLogger Log { get; } = new();

    /// <summary>Build configuration of this test run ("Debug"/"Release") – the fixtures are built with the same.</summary>
    public static string Configuration { get; } =
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    public static string FixtureDirectory { get; } =
        Path.Combine(SourceTree.AppRoot, "tests", "TestPlugins", "UeBar.TestPlugin", "bin", Configuration, "net10.0");

    /// <summary>Output folder of a plugin project in <c>src/</c> (a real plugin folder with plugin.json).</summary>
    /// <summary>Build output of an official plugin, by its folder name under <c>plugins/</c> (e.g. "Dummy").</summary>
    public static string BuiltPlugin(string folder) => Path.Combine(SourceTree.Plugins, folder, "src", "bin", Configuration, "net10.0");

    /// <summary>Creates <c>&lt;root&gt;\&lt;folder&gt;</c> with the fixture assembly and a plugin.json.</summary>
    public string Add(
        string root,
        string id,
        string? entryType = Good,
        string sdkVersion = "1.0",
        bool withDependency = true,
        string? folder = null,
        string version = "1.0.0",
        Action<JsonObject>? manifest = null)
    {
        var directory = Path.Combine(root, folder ?? id);
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(FixtureDirectory, "UeBar.TestPlugin.dll"), Path.Combine(directory, "UeBar.TestPlugin.dll"), overwrite: true);
        if (withDependency)
        {
            File.Copy(Path.Combine(FixtureDirectory, "UeBar.TestPlugin.Dependency.dll"), Path.Combine(directory, "UeBar.TestPlugin.Dependency.dll"), overwrite: true);
        }

        var json = new JsonObject
        {
            ["id"] = id,
            ["name"] = new JsonObject { ["en"] = "Fixture " + id, ["de"] = "Testplugin " + id },
            ["description"] = "A test plugin",
            ["version"] = version,
            ["author"] = "Test Author",
            ["license"] = "GPL-3.0-or-later",
            ["sdkVersion"] = sdkVersion,
            ["entryAssembly"] = "UeBar.TestPlugin.dll",
        };
        if (entryType is not null)
        {
            json["entryType"] = entryType;
        }

        manifest?.Invoke(json);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), json.ToJsonString());
        return directory;
    }

    /// <summary>Checksum list like the build writes it, for the current content of the bundled folder.</summary>
    public BundledPluginList BuildList()
    {
        var lines = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(Bundled))
        {
            foreach (var (file, hash) in PluginHash.Files(directory))
            {
                lines.Add($"{hash.ToUpperInvariant()}  {Path.GetFileName(directory)}/{file}");
            }
        }

        return BundledPluginList.Parse(string.Join("\n", lines));
    }

    /// <summary>A host like the app creates it. <paramref name="list"/> null = the bundled folder as it is now counts as genuine.</summary>
    public PluginHost Host(BundledPluginList? list = null, Version? api = null, TimeSpan? timeout = null) => new(
        new PluginHostOptions
        {
            BundledDirectory = Bundled,
            BundledList = list ?? BuildList(),
            UserDirectory = User,
            ApiVersion = api ?? UeBar.Sdk.CoreApiVersion.Version,
            LoadTimeout = timeout ?? TimeSpan.FromSeconds(15),
        },
        State,
        new PluginTrustStore(TrustFile, Log),
        Log);

    /// <summary>Discover, allow everything that asks, load.</summary>
    public async Task<PluginHost> LoadAllAsync(BundledPluginList? list = null)
    {
        var host = Host(list);
        foreach (var plugin in host.Discover().Where(p => p.State == PluginState.NeedsApproval).ToList())
        {
            host.Decide(plugin, allow: true);
        }

        await host.LoadAsync();
        return host;
    }

    public void Dispose() => _temp.Dispose();
}
