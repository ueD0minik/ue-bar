// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Abstractions;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Core.Tiles;

namespace UeBar.Core.Tests;

/// <summary>M7 in the core: developer modules, device name for modules, cleaning up dumps, settings.json detection.</summary>
public class SettingsM7CoreTests
{
    private sealed class DevModule(string id) : IModule, IDeveloperModule
    {
        public string Id { get; } = id;

        public string DisplayName => "Test-Modul";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public IModuleContext? Context { get; private set; }

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Developer_modules_are_off_by_default_in_release_but_a_stored_setting_wins()
    {
        await using var env = new TestEnv();
        env.Registry.DeveloperModulesEnabledByDefault = false; // like release
        var dev = env.Registry.Register(new DevModule("dummy"))!;
        var normal = env.Registry.Register(new TestModule("normal"))!;

        Assert.False(dev.IsEnabled);
        Assert.True(dev.IsDeveloper);
        Assert.True(normal.IsEnabled);
        Assert.False(normal.IsDeveloper);

        await using var env2 = new TestEnv();
        env2.Registry.DeveloperModulesEnabledByDefault = false;
        env2.State.SetModuleEnabled("dummy", true); // an existing user's setting
        Assert.True(env2.Registry.Register(new DevModule("dummy"))!.IsEnabled);
    }

    [Fact]
    public async Task Modules_get_the_configured_device_name_through_their_context()
    {
        await using var env = new TestEnv();
        var name = "Laptop";
        env.Registry.DeviceName = () => name;
        var module = new DevModule("m");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        var host = Assert.IsAssignableFrom<IHostInfo>(module.Context);
        Assert.Equal("Laptop", host.DeviceName);
        name = "";
        Assert.Equal(Environment.MachineName, host.DeviceName); // empty = computer name
    }

    [Fact]
    public void Only_the_three_newest_dumps_are_kept()
    {
        using var dir = new TempDirectory();
        var start = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(dir.Path, $"haenger-{i}.dmp");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, start.AddMinutes(i));
        }

        File.WriteAllText(Path.Combine(dir.Path, "uebar-20261113.log"), "bleibt");

        Assert.Equal(2, FileRetention.KeepNewest(dir.Path, "*.dmp", 3));
        Assert.Equal(["haenger-2.dmp", "haenger-3.dmp", "haenger-4.dmp"], Directory.GetFiles(dir.Path, "*.dmp").Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(dir.Path, "uebar-20261113.log")));
        Assert.Equal(0, FileRetention.KeepNewest(Path.Combine(dir.Path, "fehlt"), "*.dmp", 3));
    }

    [Fact]
    public void Settings_service_knows_whether_settings_json_existed()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "settings.json");
        var fresh = new SettingsService(path, null, new ListLogger());
        fresh.Load();
        Assert.False(fresh.FileExisted);
        Assert.Null(fresh.GetModuleEnabled("dummy"));

        fresh.SetModuleEnabled("dummy", false);
        var again = new SettingsService(path, null, new ListLogger());
        again.Load();
        Assert.True(again.FileExisted);
        Assert.False(again.GetModuleEnabled("dummy"));
    }
}
