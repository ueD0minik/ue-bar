// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Modules;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Modules.HomeAssistant.Tests;

/// <summary>
/// P2-1: the Home Assistant module is a plugin now. A data folder written by ue Bar 1.1.1 (module
/// built in) must work unchanged: plugin ID = former module ID, so settings, token, layout and the
/// saved secrets are found again.
/// </summary>
public sealed class PluginMigrationTests
{
    /// <summary>Stands in for DPAPI.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plainText) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

        public string Unprotect(string protectedText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText["enc:".Length..]));
    }

    private static string Configuration =>
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static string AppRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("app/ folder not found");
    }

    /// <summary>Copies the built plugin folder like the app's build does, and returns the checksum list for it.</summary>
    private static BundledPluginList StageHomeAssistantPlugin(string bundled)
    {
        var source = Path.Combine(AppRoot(), "src", "UeBar.Modules.HomeAssistant", "bin", Configuration, "net10.0");
        var target = Path.Combine(bundled, "homeassistant");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        return BundledPluginList.Parse(string.Join("\n", PluginHash.Files(target).Select(f => $"{f.Value}  homeassistant/{f.Key}")));
    }

    [Fact]
    public async Task Data_folder_of_1_1_1_works_with_the_home_assistant_plugin()
    {
        await using var server = new FakeHaServer();
        SampleStates.AddAll(server);
        var root = Path.Combine(Path.GetTempPath(), "uebar-ha-tests", "migration-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "data"));
        Directory.CreateDirectory(paths.Root);
        var protector = new FakeProtector();

        // settings.json and layout.json exactly as 1.1.1 wrote them.
        var settingsJson = $$"""
            {
              "version": 1,
              "modules": {
                "dummy": { "enabled": true, "values": {}, "secrets": {} },
                "homeassistant": {
                  "enabled": true,
                  "values": {
                    "localUrl": "{{server.BaseUrl}}",
                    "externalUrl": "",
                    "preferLocal": "true",
                    "nowPlayingPlayers": "",
                    "todoList": "todo.shopping_list"
                  },
                  "secrets": {
                    "token": "{{protector.Protect("good-token")}}",
                    "mobileAppWebhookId": "{{protector.Protect("webhook-from-1.1.1")}}",
                    "mobileAppDeviceId": "{{protector.Protect("device-from-1.1.1")}}"
                  }
                }
              },
              "busGrants": [],
              "general": { "accentColor": "#8B6CFF", "columns": 4, "welcomeDone": true }
            }
            """;
        File.WriteAllText(paths.SettingsFile, settingsJson);
        File.WriteAllText(paths.LayoutFile, UeBarJson.Serialize(SampleLayout.Create()));

        // The app's start, step by step.
        var log = new CollectingLogger();
        var settings = new SettingsService(paths.SettingsFile, protector, log);
        settings.Load();

        // The module's own secrets (notification registration) are found under the same module ID.
        // (Checked before the start: the fake server does not know this registration, so the module renews it.)
        Assert.Equal("webhook-from-1.1.1", settings.GetModuleSecret("homeassistant", "mobileAppWebhookId"));
        Assert.Equal("device-from-1.1.1", settings.GetModuleSecret("homeassistant", "mobileAppDeviceId"));
        var list = StageHomeAssistantPlugin(Path.Combine(root, "app", "plugins"));
        var plugins = new PluginHost(
            new PluginHostOptions { BundledDirectory = Path.Combine(root, "app", "plugins"), BundledList = list, UserDirectory = paths.Plugins },
            settings,
            new PluginTrustStore(paths.PluginTrustFile, log),
            log);
        var guard = new ModuleGuard(log);
        await using var registry = new ModuleRegistry(guard, new ModuleBus(guard, settings.BusPolicy, log), log, settings, paths.Root);

        var plugin = Assert.Single(plugins.Discover());
        Assert.Equal(PluginState.Pending, plugin.State); // bundled and unchanged: no question
        await plugins.LoadAsync();
        Assert.True(plugin.State == PluginState.Loaded, plugin.Problem);
        plugins.RegisterAll(registry);

        var layout = (await new LocalFileLayoutStore(paths.LayoutFile, paths.Backups, log).LoadAsync(CancellationToken.None))!;
        await registry.NotifyLayoutAsync(layout);
        await registry.StartAllAsync();

        // Same module ID, running from the plugin folder (its own load context), connected with the old token.
        var entry = registry.Find("homeassistant")!;
        Assert.True(entry.IsLoaded);
        Assert.Equal("Home Assistant", entry.DisplayName);
        Assert.NotSame(typeof(HomeAssistantModule).Assembly, entry.Module.GetType().Assembly);
        Assert.StartsWith(Path.Combine(root, "app", "plugins", "homeassistant"), entry.Module.GetType().Assembly.Location, StringComparison.OrdinalIgnoreCase);
        Assert.True(await Wait.UntilAsync(() => entry.Status.State == ModuleState.Connected, 10000), $"status: {entry.Status.State} {entry.Status.Text}");

        // The layout's tiles are real tiles of the plugin, with live states from Home Assistant.
        var tiles = new List<TileModel>();
        foreach (var config in layout.AllTiles)
        {
            tiles.Add(await registry.CreateTileAsync(config));
        }

        Assert.NotEmpty(tiles);
        Assert.All(tiles, t => Assert.False(t.IsPlaceholder, $"{t.TileId}: {t.StateText}"));
        var desk = tiles.First(t => t.TileId == "t1");
        Assert.True(await Wait.UntilAsync(() => desk.IsUnavailable == false && !string.IsNullOrEmpty(desk.StateText)));

        // Settings and token are still there, under the same keys.
        var values = settings.GetModuleSettings("homeassistant");
        Assert.Equal("todo.shopping_list", values.GetString("todoList"));
        Assert.Equal("good-token", values.GetString("token"));

        // Nothing rewrote the 1.1.1 file's content for the modules.
        var saved = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!["modules"]!;
        Assert.True(saved["homeassistant"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(server.BaseUrl.ToString(), saved["homeassistant"]!["values"]!["localUrl"]!.GetValue<string>());
        Assert.StartsWith("enc:", saved["homeassistant"]!["secrets"]!["token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(saved["dummy"]); // the test module's entry stays, although the plugin is not installed
        Assert.Null(registry.Find("dummy"));
    }

    [Fact]
    public async Task Without_the_plugin_folder_the_app_keeps_the_tiles_as_placeholders()
    {
        var root = Path.Combine(Path.GetTempPath(), "uebar-ha-tests", "missing-" + Guid.NewGuid().ToString("N"));
        var log = new CollectingLogger();
        var plugins = new PluginHost(
            new PluginHostOptions { BundledDirectory = Path.Combine(root, "plugins"), UserDirectory = Path.Combine(root, "user") },
            new InMemoryModuleStateStore(),
            new PluginTrustStore(null),
            log);
        var guard = new ModuleGuard(log);
        await using var registry = new ModuleRegistry(guard, new ModuleBus(guard, new GrantListPermissionPolicy([]), log), log, new InMemoryModuleStateStore(), root);

        Assert.Empty(plugins.Discover());
        await plugins.LoadAsync();
        plugins.RegisterAll(registry);
        await registry.StartAllAsync();

        var layout = SampleLayout.Create();
        foreach (var config in layout.AllTiles)
        {
            Assert.Equal(TilePlaceholder.ModuleMissing, (await registry.CreateTileAsync(config)).Placeholder);
        }

        Assert.Equal(SampleLayout.Create().AllTiles.Count(), layout.AllTiles.Count()); // nothing was removed from the layout
    }
}
