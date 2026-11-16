// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;
using UeBar.Core.Settings;

namespace UeBar.Bar.Tests;

/// <summary>M7: save/load settings – new sections, old files stay valid.</summary>
public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uebar-m7-settings", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string File => Path.Combine(_dir, "settings.json");

    private SettingsService Load()
    {
        var service = new SettingsService(File, null, NullLogger.Instance);
        service.Load();
        return service;
    }

    [Fact]
    public void General_and_bar_settings_survive_a_restart()
    {
        var first = Load();
        first.SetSection(GeneralSettings.SectionName, new GeneralSettings { DeviceName = "Laptop", AccentColor = "#2FA463", Columns = 5, LayoutStore = "local", WelcomeDone = true });
        first.SetSection("bar", new BarSettings { OledDimMinutes = 1, OledPixelShift = false, QuickSearchHotkey = "Ctrl+Shift+F5", NotificationSeconds = 9 });

        var second = Load();
        var general = second.GetSection<GeneralSettings>(GeneralSettings.SectionName);
        var bar = second.GetSection<BarSettings>("bar");

        Assert.Equal(("Laptop", "#2FA463", 5, "local", true), (general.DeviceName, general.AccentColor, general.Columns, general.LayoutStore, general.WelcomeDone));
        Assert.Equal((1, false, "Ctrl+Shift+F5", 9), (bar.OledDimMinutes, bar.OledPixelShift, bar.QuickSearchHotkey, bar.NotificationSeconds));
        Assert.DoesNotContain("effectiveColumns", System.IO.File.ReadAllText(File), StringComparison.OrdinalIgnoreCase); // only real values
    }

    [Fact]
    public void A_settings_file_from_before_m7_keeps_working_with_defaults()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, """
            {
              "version": 1,
              "modules": { "dummy": { "enabled": true, "values": {}, "secrets": {} } },
              "bar": { "monitorMode": "underMouse", "hideDelayMs": 500, "quickSearchHotkey": "Win+Alt+Space" }
            }
            """);

        var service = Load();
        var general = service.GetSection<GeneralSettings>(GeneralSettings.SectionName);
        var bar = service.GetSection<BarSettings>("bar");

        Assert.True(service.FileExisted);
        Assert.Null(general.DeviceName);
        Assert.Equal(GeneralSettings.DefaultColumns, general.EffectiveColumns);
        Assert.False(general.WelcomeDone);
        Assert.Equal(MonitorMode.UnderMouse, bar.MonitorMode);
        Assert.Equal(500, bar.HideDelayMs);
        Assert.Equal(5, bar.OledDimMinutes); // default from SPEC 5.5
        Assert.True(bar.OledPixelShift);
        Assert.True(service.GetModuleEnabled("dummy")); // the test module stays on for an existing user
        Assert.False(WelcomeRules.ShouldShow(service.FileExisted, general.WelcomeDone, anyModuleNeedsSetup: false));
    }
}
