// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Modules;
using UeBar.Core.Plugins;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>P2-1: finding, checking and loading plugins – a broken one never stops the others.</summary>
public sealed class PluginHostTests
{
    private static TileConfig Tile(string module) => new() { Id = "t1", Module = module, Type = module + ".tile" };

    [Fact]
    public async Task Bundled_plugin_that_matches_the_build_list_loads_without_asking()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        var host = box.Host();

        var plugin = Assert.Single(host.Discover());
        Assert.Equal(PluginState.Pending, plugin.State);
        Assert.True(plugin.IsVerifiedBundled);
        Assert.Equal(PluginOrigin.Bundled, plugin.Origin);

        await host.LoadAsync();

        Assert.Equal(PluginState.Loaded, plugin.State);
        Assert.Null(plugin.Problem);
        Assert.Equal("good", plugin.Module!.Id);
        Assert.Equal("1.0.0", plugin.Manifest!.Version);
        Assert.Equal("Test Author", plugin.Manifest.Author);
    }

    [Fact]
    public async Task Loaded_plugin_runs_in_the_registry_like_a_built_in_module()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        var host = await box.LoadAllAsync();
        await using var env = new TestEnv();

        host.RegisterAll(env.Registry);
        await env.Registry.StartAllAsync();

        var entry = env.Registry.Find("good")!;
        Assert.True(entry.IsLoaded);
        Assert.True(entry.IsAvailable);
        Assert.Equal(ModuleState.Connected, entry.Status.State);
        var tile = await env.Registry.CreateTileAsync(Tile("good"));
        Assert.False(tile.IsPlaceholder);
        Assert.Equal("ok", tile.StateText);
    }

    [Fact]
    public async Task The_sdk_is_shared_and_private_dependencies_come_from_the_plugin_folder()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "deps", PluginSandbox.WithDependency);
        var host = await box.LoadAllAsync();

        var module = Assert.Single(host.Plugins).Module!;

        // Same IModule type as the host's – otherwise the cast in the loader would have failed.
        Assert.IsAssignableFrom<IModule>(module);
        Assert.Same(typeof(IModule).Assembly, module.GetType().GetInterface(nameof(IModule))!.Assembly);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(module.GetType().Assembly));
        Assert.Equal("hello from the dependency (plugin:deps)", module.DisplayName);
    }

    [Theory]
    [InlineData("2.0", "Plugin_ApiNewer")]
    [InlineData("1.99", "Plugin_ApiNewer")]
    [InlineData("0.9", "Plugin_ApiOlder")]
    public async Task Plugin_for_another_api_version_is_not_loaded_and_says_why(string sdkVersion, string reasonKey)
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        box.Add(box.Bundled, "other", sdkVersion: sdkVersion);
        var host = await box.LoadAllAsync();

        var other = host.Find("other")!;
        Assert.Equal(PluginState.Incompatible, other.State);
        Assert.Null(other.Module);
        Assert.Equal(Localization.CoreText.T.Format(reasonKey, sdkVersion.Length > 3 ? sdkVersion : sdkVersion, CoreApiVersion.Text), other.Problem);
        Assert.Contains(sdkVersion, other.Problem!);
        Assert.Equal(PluginState.Loaded, host.Find("good")!.State);
    }

    [Fact]
    public void A_newer_minor_version_of_the_host_still_loads_older_plugins()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good", sdkVersion: "1.0");

        var plugin = Assert.Single(box.Host(api: UeBar.Sdk.CoreApiVersion.Version).Discover()); // the fixture is built against the current SDK

        Assert.Equal(PluginState.Pending, plugin.State);
    }

    [Fact]
    public async Task Broken_dll_gives_an_error_and_the_rest_keeps_loading()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        var broken = box.Add(box.Bundled, "broken");
        File.WriteAllText(Path.Combine(broken, "UeBar.TestPlugin.dll"), "this is not an assembly");
        var host = await box.LoadAllAsync();

        var plugin = host.Find("broken")!;
        Assert.Equal(PluginState.Error, plugin.State);
        Assert.Contains("UeBar.TestPlugin.dll", plugin.Problem!);
        Assert.Equal(PluginState.Loaded, host.Find("good")!.State);
    }

    [Fact]
    public async Task Missing_dependency_gives_an_error_with_the_name_of_what_is_missing()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        box.Add(box.Bundled, "nodep", PluginSandbox.WithDependency, withDependency: false);
        var host = await box.LoadAllAsync();

        var plugin = host.Find("nodep")!;
        Assert.Equal(PluginState.Error, plugin.State);
        Assert.Contains("UeBar.TestPlugin.Dependency", plugin.Problem!);
        Assert.Equal(PluginState.Loaded, host.Find("good")!.State);
    }

    [Fact]
    public async Task Exception_in_the_constructor_gives_an_error_with_its_message()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        box.Add(box.Bundled, "throws", PluginSandbox.Throwing);
        var host = await box.LoadAllAsync();

        var plugin = host.Find("throws")!;
        Assert.Equal(PluginState.Error, plugin.State);
        Assert.Contains("boom", plugin.Problem!);
        Assert.Equal(PluginState.Loaded, host.Find("good")!.State);
        Assert.Contains(box.Log.Entries, e => e.Level == LogLevel.Error && e.Module == "throws");
    }

    [Fact]
    public async Task Constructor_that_hangs_runs_into_the_time_limit()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "slow", PluginSandbox.Slow);
        var host = box.Host(timeout: TimeSpan.FromMilliseconds(300));
        host.Discover();

        await host.LoadAsync();

        Assert.Equal(PluginState.Error, host.Find("slow")!.State);
    }

    [Fact]
    public async Task Second_plugin_with_the_same_id_is_ignored()
    {
        using var box = new PluginSandbox();
        box.Add(box.User, "dup");
        box.Add(box.User, "dup", folder: "dup-copy");
        var host = await box.LoadAllAsync();

        Assert.Equal(2, host.Plugins.Count);
        var first = host.Plugins[0];
        var second = host.Plugins[1];
        Assert.Equal(PluginState.Loaded, first.State);
        Assert.False(first.IsDuplicate);
        Assert.Equal(PluginState.Error, second.State);
        Assert.True(second.IsDuplicate);
        Assert.Contains("dup", second.Problem!);
        Assert.Same(first, host.Find("dup"));
    }

    [Fact]
    public async Task User_plugin_cannot_replace_a_bundled_one()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "same");
        box.Add(box.User, "same", PluginSandbox.Throwing);
        var host = await box.LoadAllAsync();

        var bundled = host.Plugins.Single(p => p.Origin == PluginOrigin.Bundled);
        var user = host.Plugins.Single(p => p.Origin == PluginOrigin.Installed);
        Assert.Equal(PluginState.Loaded, bundled.State);
        Assert.Equal(PluginState.Error, user.State);
        Assert.True(user.IsDuplicate);
        Assert.Equal(Localization.CoreText.T.Format("Plugin_BundledId", "same"), user.Problem);
        Assert.True(host.IsBundledId("same"));
    }

    [Fact]
    public async Task Module_id_must_match_plugin_json()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "mine", PluginSandbox.OtherId);
        var host = await box.LoadAllAsync();

        var plugin = host.Find("mine")!;
        Assert.Equal(PluginState.Error, plugin.State);
        Assert.Contains("something-else", plugin.Problem!);
    }

    [Fact]
    public async Task Folder_without_plugin_json_or_without_the_assembly_is_an_error()
    {
        using var box = new PluginSandbox();
        Directory.CreateDirectory(Path.Combine(box.User, "empty"));
        var noDll = box.Add(box.User, "nodll");
        File.Delete(Path.Combine(noDll, "UeBar.TestPlugin.dll"));
        var host = await box.LoadAllAsync();

        Assert.Equal(PluginState.Error, host.Find("empty")!.State);
        Assert.Null(host.Find("empty")!.Manifest);
        Assert.Equal(PluginState.Error, host.Find("nodll")!.State);
        Assert.Contains("UeBar.TestPlugin.dll", host.Find("nodll")!.Problem!);
    }

    [Fact]
    public async Task Several_modules_in_one_assembly_need_entry_type()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "many", entryType: null);
        box.Add(box.Bundled, "wrong", entryType: "No.Such.Type");
        var host = await box.LoadAllAsync();

        Assert.Equal(PluginState.Error, host.Find("many")!.State);
        Assert.Contains("entryType", host.Find("many")!.Problem!);
        Assert.Equal(PluginState.Error, host.Find("wrong")!.State);
        Assert.Contains("No.Such.Type", host.Find("wrong")!.Problem!);
    }

    [Fact]
    public void Plugin_that_references_app_internals_is_refused()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.User, "internals", manifest: json => json["entryAssembly"] = "UeBar.Core.Tests.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "UeBar.Core.Tests.dll"), Path.Combine(directory, "UeBar.Core.Tests.dll"));

        var plugin = Assert.Single(box.Host().Discover());

        Assert.Equal(PluginState.Error, plugin.State);
        Assert.Equal(Localization.CoreText.T.Format("Plugin_HostReference", "UeBar.Core"), plugin.Problem);
    }

    [Fact]
    public async Task Bundled_plugins_come_first_in_the_order_of_the_build_list_then_the_users_by_name()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "alpha");
        box.Add(box.Bundled, "zeta");
        box.Add(box.User, "mid");
        box.Add(box.User, "beta");
        var list = BundledPluginList.Parse("# plugin: zeta\n# plugin: alpha\n" + string.Join("\n",
            Directory.EnumerateDirectories(box.Bundled).SelectMany(d => PluginHash.Files(d).Select(f => $"{f.Value}  {Path.GetFileName(d)}/{f.Key}"))));

        var host = await box.LoadAllAsync(list);
        await using var env = new TestEnv();
        host.RegisterAll(env.Registry);

        Assert.Equal(["zeta", "alpha", "beta", "mid"], host.Plugins.Select(p => p.Id));
        Assert.Equal(["zeta", "alpha", "beta", "mid"], env.Registry.Modules.Select(m => m.Id));
        Assert.All(host.Plugins, p => Assert.Equal(PluginState.Loaded, p.State));
    }

    [Fact]
    public async Task Real_plugin_folder_of_the_test_module_loads_with_its_language_files()
    {
        using var box = new PluginSandbox();
        var source = PluginSandbox.BuiltPlugin("Dummy");
        var target = Path.Combine(box.Bundled, "dummy");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var host = await box.LoadAllAsync();
        var plugin = Assert.Single(host.Plugins);

        Assert.Equal(PluginState.Loaded, plugin.State);
        Assert.Equal("dummy", plugin.Module!.Id);
        Assert.Equal("Test-Modul", plugin.Manifest!.DisplayName(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("Test module", plugin.Manifest.DisplayName(CultureInfo.GetCultureInfo("en-US")));
        // The module's own texts come from the satellite assembly in the plugin folder (tests run in German).
        Assert.Equal("Test-Modul", plugin.Module.DisplayName);
        Assert.NotEmpty(plugin.Module.TileTypes);
    }

    // ------------------------------------------------------------------ enable/disable, placeholders

    [Fact]
    public async Task Disabled_plugin_is_not_loaded_and_its_tiles_show_module_disabled()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "off", PluginSandbox.Throwing); // would fail if any of its code ran
        box.State.SetModuleEnabled("off", false);
        var host = await box.LoadAllAsync();
        await using var env = new TestEnv();

        host.RegisterAll(env.Registry);
        await env.Registry.StartAllAsync();

        var plugin = host.Find("off")!;
        Assert.Equal(PluginState.Disabled, plugin.State);
        Assert.Null(plugin.Module);
        var entry = env.Registry.Find("off")!;
        Assert.False(entry.IsLoaded);
        Assert.False(entry.IsEnabled);
        Assert.Equal(ModuleState.Disabled, entry.Status.State);
        Assert.Equal("Testplugin off", entry.DisplayName);
        Assert.Equal(TilePlaceholder.ModuleDisabled, (await env.Registry.CreateTileAsync(Tile("off"))).Placeholder);
        Assert.False(host.RestartNeeded);
    }

    [Fact]
    public async Task Enabling_a_plugin_that_is_not_loaded_is_saved_and_needs_a_restart()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "off");
        box.State.SetModuleEnabled("off", false);
        var host = await box.LoadAllAsync();
        var guard = new ModuleGuard(box.Log);
        await using var registry = new ModuleRegistry(guard, new Bus.ModuleBus(guard, new Bus.GrantListPermissionPolicy([]), box.Log), box.Log, box.State, box.Root);
        host.RegisterAll(registry);

        await registry.SetEnabledAsync("off", true);

        Assert.True(box.State.IsModuleEnabled("off"));
        Assert.False(registry.Find("off")!.IsLoaded);
        Assert.False(await registry.StartModuleAsync("off"));
        Assert.False(await registry.RestartModuleAsync("off"));
        Assert.True(host.RestartNeeded);

        // "Restart": a new host finds it enabled and loads it.
        var next = await box.LoadAllAsync();
        Assert.Equal(PluginState.Loaded, next.Find("off")!.State);
    }

    [Fact]
    public async Task Disabling_a_loaded_plugin_stops_it_right_away_and_it_is_not_loaded_next_time()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "good");
        var host = await box.LoadAllAsync();
        var guard = new ModuleGuard(box.Log);
        await using var registry = new ModuleRegistry(guard, new Bus.ModuleBus(guard, new Bus.GrantListPermissionPolicy([]), box.Log), box.Log, box.State, box.Root);
        host.RegisterAll(registry);
        await registry.StartAllAsync();
        Assert.True(registry.Find("good")!.IsRunning);

        await registry.SetEnabledAsync("good", false);

        Assert.False(registry.Find("good")!.IsRunning);
        Assert.Equal(TilePlaceholder.ModuleDisabled, (await registry.CreateTileAsync(Tile("good"))).Placeholder);
        Assert.False(host.RestartNeeded);

        await registry.SetEnabledAsync("good", true);
        Assert.True(registry.Find("good")!.IsRunning);

        await registry.SetEnabledAsync("good", false);
        var next = await box.LoadAllAsync();
        Assert.Equal(PluginState.Disabled, next.Find("good")!.State);
        Assert.Null(next.Find("good")!.Module);
    }

    [Fact]
    public async Task Tiles_of_missing_broken_and_refused_plugins_stay_as_placeholders()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "throws", PluginSandbox.Throwing);
        box.Add(box.User, "refused");
        var host = box.Host();
        host.Discover();
        host.Decide(host.Find("refused")!, allow: false);
        await host.LoadAsync();
        await using var env = new TestEnv();

        host.RegisterAll(env.Registry);
        await env.Registry.StartAllAsync();

        Assert.Equal(TilePlaceholder.ModuleError, (await env.Registry.CreateTileAsync(Tile("throws"))).Placeholder);
        Assert.Equal(TilePlaceholder.ModuleDisabled, (await env.Registry.CreateTileAsync(Tile("refused"))).Placeholder);
        Assert.Equal(TilePlaceholder.ModuleMissing, (await env.Registry.CreateTileAsync(Tile("gone"))).Placeholder);
        var broken = env.Registry.Find("throws")!;
        Assert.Equal(ModuleState.Error, broken.Status.State);
        Assert.Contains("boom", broken.Status.Text!);
        Assert.False(broken.IsAvailable);
        Assert.Equal(Localization.CoreText.T["Plugin_NotAllowed"], env.Registry.Find("refused")!.Status.Text);
    }

    // ------------------------------------------------------------------ trust

    [Fact]
    public async Task User_plugin_asks_first_and_the_answer_is_remembered()
    {
        using var box = new PluginSandbox();
        box.Add(box.User, "mine");
        var host = box.Host();

        var plugin = Assert.Single(host.Discover());
        Assert.Equal(PluginState.NeedsApproval, plugin.State);
        Assert.False(plugin.FilesChanged);

        await host.LoadAsync();
        Assert.Equal(PluginState.NeedsApproval, plugin.State); // nothing runs before the answer
        Assert.Null(plugin.Module);

        host.Decide(plugin, allow: true);
        await host.LoadAsync();
        Assert.Equal(PluginState.Loaded, plugin.State);

        // Next start: no question.
        var again = Assert.Single(box.Host().Discover());
        Assert.Equal(PluginState.Pending, again.State);
    }

    [Fact]
    public void Changed_files_ask_again()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.User, "mine");
        var host = box.Host();
        host.Decide(Assert.Single(host.Discover()), allow: true);

        File.AppendAllText(Path.Combine(directory, "plugin.json"), " ");
        var changed = Assert.Single(box.Host().Discover());

        Assert.Equal(PluginState.NeedsApproval, changed.State);
        Assert.True(changed.FilesChanged);
    }

    [Fact]
    public void Files_that_are_not_code_do_not_change_the_checksum()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.User, "mine");
        var before = PluginHash.Of(directory);

        File.WriteAllText(Path.Combine(directory, "notes.txt"), "something the plugin wrote");

        Assert.Equal(before, PluginHash.Of(directory));
        File.WriteAllText(Path.Combine(directory, "extra.dll"), "new code");
        Assert.NotEqual(before, PluginHash.Of(directory));
    }

    [Fact]
    public void Refusal_is_remembered_too_and_can_be_changed_later()
    {
        using var box = new PluginSandbox();
        box.Add(box.User, "mine");
        var host = box.Host();
        host.Decide(Assert.Single(host.Discover()), allow: false);

        var next = box.Host();
        var plugin = Assert.Single(next.Discover());
        Assert.Equal(PluginState.Refused, plugin.State);
        Assert.False(next.RestartNeeded);

        next.Decide(plugin, allow: true); // "Allow …" on the Plugins page
        Assert.Equal(PluginState.Pending, plugin.State);
        Assert.True(next.RestartNeeded);
        Assert.Equal(PluginState.Pending, Assert.Single(box.Host().Discover()).State);
    }

    [Fact]
    public void Bundled_plugin_with_changed_files_has_to_ask_like_any_other()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.Bundled, "good");
        var list = box.BuildList();

        File.AppendAllText(Path.Combine(directory, "plugin.json"), " ");
        var plugin = Assert.Single(box.Host(list).Discover());

        Assert.False(plugin.IsVerifiedBundled);
        Assert.Equal(PluginState.NeedsApproval, plugin.State);
        Assert.True(plugin.FilesChanged);
    }

    [Fact]
    public void Extra_assembly_in_a_bundled_plugin_folder_breaks_the_match()
    {
        using var box = new PluginSandbox();
        var directory = box.Add(box.Bundled, "good");
        var list = box.BuildList();
        Assert.True(Assert.Single(box.Host(list).Discover()).IsVerifiedBundled);

        File.WriteAllText(Path.Combine(directory, "injected.dll"), "x");

        Assert.False(Assert.Single(box.Host(list).Discover()).IsVerifiedBundled);
    }

    [Fact]
    public void Unreadable_trust_file_means_ask_again()
    {
        using var box = new PluginSandbox();
        box.Add(box.User, "mine");
        var host = box.Host();
        host.Decide(Assert.Single(host.Discover()), allow: true);

        File.WriteAllText(box.TrustFile, "{ not json");

        Assert.Equal(PluginState.NeedsApproval, Assert.Single(box.Host().Discover()).State);
    }
}
