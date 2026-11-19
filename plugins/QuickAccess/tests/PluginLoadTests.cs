// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Modules.QuickAccess.Tests;

/// <summary>The real plugin folder loads through the normal plugin loader – no special treatment in the core.</summary>
public sealed class PluginLoadTests
{
    private static string BuiltPlugin => Path.GetDirectoryName(typeof(QuickAccessModule).Assembly.Location)!;

    [Fact]
    public async Task Plugin_folder_loads_like_any_bundled_plugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "uebar-qa-load", Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "app", "plugins");
        var target = Path.Combine(bundled, "quickaccess");
        try
        {
            foreach (var file in Directory.EnumerateFiles(BuiltPlugin, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(BuiltPlugin, file);
                // Only what belongs to the plugin (the test output also holds the test assemblies and the core).
                if (!IsPluginFile(relative))
                {
                    continue;
                }

                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }

            var list = BundledPluginList.Parse(string.Join("\n", PluginHash.Files(target).Select(f => $"{f.Value.ToUpperInvariant()}  quickaccess/{f.Key}")));
            var log = new ListLogger();
            var host = new PluginHost(
                new PluginHostOptions
                {
                    BundledDirectory = bundled,
                    BundledList = list,
                    UserDirectory = Path.Combine(root, "data", "plugins"),
                    ApiVersion = CoreApiVersion.Version,
                    LoadTimeout = TimeSpan.FromSeconds(15),
                },
                new InMemoryModuleStateStore(),
                new PluginTrustStore(Path.Combine(root, "data", "plugin-trust.json"), log),
                log);

            var found = Assert.Single(host.Discover());
            Assert.Equal(PluginState.Pending, found.State); // bundled + matching checksum: loads without asking
            Assert.True(found.IsVerifiedBundled);
            await host.LoadAsync();

            var plugin = Assert.Single(host.Plugins);
            Assert.True(plugin.State == PluginState.Loaded, $"{plugin.State}: {plugin.Problem}");
            Assert.Equal(QuickAccessModule.ModuleId, plugin.Module!.Id);
            Assert.Equal("Schnellzugriff", plugin.Manifest!.DisplayName(CultureInfo.GetCultureInfo("de-DE")));
            Assert.Equal(CoreApiVersion.Version, plugin.Manifest.SdkVersion);
            Assert.Contains(plugin.Module.TileTypes, t => t.Id == "quickaccess.group");
            Assert.Equal("Sammlung", plugin.Module.TileTypes.First(t => t.Id == "quickaccess.group").DisplayName); // texts from its own satellite assembly
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // the load context keeps the assemblies open – the temp folder is cleaned up later
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly string[] PluginFiles = ["UeBar.Modules.QuickAccess.dll", "UeBar.Modules.QuickAccess.deps.json", "plugin.json"];

    private static bool IsPluginFile(string relative) =>
        PluginFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)
        || relative.Equals(Path.Combine("de", "UeBar.Modules.QuickAccess.resources.dll"), StringComparison.OrdinalIgnoreCase);
}
