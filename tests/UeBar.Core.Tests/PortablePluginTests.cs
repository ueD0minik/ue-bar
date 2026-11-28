// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Plugins;

namespace UeBar.Core.Tests;

/// <summary>
/// P3-1: the portable exe also takes plugins from <c>plugins\</c> next to it – without installing, from a stick or a
/// school share that may be write-protected. They are confirmed like any added plugin and never count as bundled.
/// </summary>
public sealed class PortablePluginTests
{
    [Fact]
    public async Task Plugin_next_to_the_exe_asks_first_then_loads_where_it_is()
    {
        using var box = new PluginSandbox();
        var folder = box.Add(box.Portable, "stickplugin");
        var host = box.Host(portable: true);

        var plugin = Assert.Single(host.Discover());
        Assert.Equal((PluginOrigin.Portable, PluginState.NeedsApproval), (plugin.Origin, plugin.State));
        Assert.Equal(folder, plugin.Directory);

        host.Decide(plugin, allow: true);
        await host.LoadAsync();
        Assert.Equal(PluginState.Loaded, plugin.State);
        Assert.False(Directory.Exists(Path.Combine(box.User, "stickplugin"))); // nothing copied

        // The answer counts on this PC: the next start does not ask.
        Assert.Equal(PluginState.Pending, Assert.Single(box.Host(portable: true).Discover()).State);
    }

    [Fact]
    public void Plugin_next_to_the_exe_is_never_taken_as_bundled()
    {
        using var box = new PluginSandbox();
        box.Add(box.Portable, "lookalike");

        // Even a checksum list that names it does not make it bundled.
        var list = BundledPluginList.Parse(string.Join("\n", PluginHash.Files(Path.Combine(box.Portable, "lookalike")).Select(f => $"{f.Value.ToUpperInvariant()}  lookalike/{f.Key}")));
        var plugin = Assert.Single(box.Host(list, portable: true).Discover());
        Assert.False(plugin.IsVerifiedBundled);
        Assert.Equal(PluginState.NeedsApproval, plugin.State);
    }

    [Fact]
    public void A_bundled_id_next_to_the_exe_is_refused()
    {
        using var box = new PluginSandbox();
        box.Add(box.Bundled, "core");
        box.Add(box.Portable, "core");
        var plugins = box.Host(portable: true).Discover();
        var copy = Assert.Single(plugins, p => p.Origin == PluginOrigin.Portable);
        Assert.True(copy.IsDuplicate);
        Assert.Equal(PluginState.Error, copy.State);
    }

    [Fact]
    public void Without_the_option_the_folder_is_ignored()
    {
        using var box = new PluginSandbox();
        box.Add(box.Portable, "stickplugin");
        Assert.Empty(box.Host().Discover());
    }

    [Fact]
    public async Task A_write_protected_folder_works_and_stays_untouched()
    {
        using var box = new PluginSandbox();
        var folder = box.Add(box.Portable, "readonly");
        var files = Directory.GetFiles(box.Portable, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }

        var info = new DirectoryInfo(folder);
        info.Attributes |= FileAttributes.ReadOnly;
        try
        {
            var host = box.Host(portable: true);
            var plugin = Assert.Single(host.Discover());
            host.Decide(plugin, allow: true);
            await host.LoadAsync();
            Assert.Equal(PluginState.Loaded, plugin.State);
            Assert.Equal(files, Directory.GetFiles(box.Portable, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList());
        }
        finally
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var file in files)
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }
}
