// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Bar.Tests;

/// <summary>P2-4: important notifications, the paused bar, settings migration, what was dropped.</summary>
public sealed class ImportantNotificationTests : IDisposable
{
    private static readonly MonitorInfo Monitor = new("m1", "Test", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 1.0);
    private static readonly NotificationRequest Normal = new(false);
    private static readonly NotificationRequest Important = new(true);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uebar-p24", Guid.NewGuid().ToString("N"));

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

    private static AnnounceConditions When(bool paused = false, bool locked = false, bool fullscreen = false, bool dnd = false) => new(paused, locked, fullscreen, dnd);

    [Fact]
    public void Normal_notifications_follow_pause_fullscreen_and_do_not_disturb()
    {
        var settings = new BarSettings();
        Assert.True(NotificationGate.Decide(Normal, When(), settings).Show);
        Assert.Equal("bar paused", NotificationGate.Decide(Normal, When(paused: true), settings).Reason);
        Assert.Equal("fullscreen block", NotificationGate.Decide(Normal, When(fullscreen: true), settings).Reason);
        Assert.Equal("do not disturb", NotificationGate.Decide(Normal, When(dnd: true), settings).Reason);

        // Without the fullscreen block / "Do not disturb" respected, they come out.
        Assert.True(NotificationGate.Decide(Normal, When(fullscreen: true, dnd: true), new BarSettings { BlockInFullscreen = false, RespectDoNotDisturb = false }).Show);
    }

    [Fact]
    public void Important_ones_pass_as_far_as_the_bar_settings_allow()
    {
        var all = new BarSettings();
        var paused = NotificationGate.Decide(Important, When(paused: true, fullscreen: true, dnd: true), all);
        Assert.True(paused.Show);
        Assert.True(paused.OverridesPause);
        Assert.False(NotificationGate.Decide(Important, When(), all).OverridesPause);

        Assert.Equal("bar paused", NotificationGate.Decide(Important, When(paused: true), new BarSettings { ImportantWhenPaused = false }).Reason);
        Assert.Equal("fullscreen block", NotificationGate.Decide(Important, When(fullscreen: true), new BarSettings { ImportantInFullscreen = false }).Reason);
        Assert.Equal("do not disturb", NotificationGate.Decide(Important, When(dnd: true), new BarSettings { ImportantDuringDoNotDisturb = false }).Reason);

        // Never while the PC is locked or the display is off.
        Assert.Equal("locked/display off", NotificationGate.Decide(Important, When(locked: true), all).Reason);
    }

    [Fact]
    public void Flags_of_contract_1_2_still_count()
    {
        var settings = new BarSettings();
        Assert.True(NotificationGate.Decide(new NotificationRequest(false, IgnoreDoNotDisturb: true), When(dnd: true), settings).Show);
        Assert.True(NotificationGate.Decide(new NotificationRequest(false, ShowInFullscreen: true), When(fullscreen: true), settings).Show);
        Assert.False(NotificationGate.Decide(new NotificationRequest(false, true, true), When(paused: true), settings).Show); // they never passed a pause
    }

    [Fact]
    public void Paused_bar_lets_an_important_pill_out_and_hides_again_afterwards()
    {
        var machine = new BarStateMachine();
        machine.Configure([Monitor], new BarSettings { Paused = true, HideDelayMs = 500 });
        var t0 = DateTimeOffset.UtcNow;
        var away = new PixelPoint(900, 700);

        Assert.False(machine.Announce(Monitor, t0.AddSeconds(5))); // normal: no
        Assert.True(machine.Announce(Monitor, t0.AddSeconds(5), overridePause: true));
        Assert.Equal(BarState.Pill, machine.State);
        Assert.True(machine.IsOverridingPause);

        machine.BarBounds = new PixelRect(880, 8, 160, 40);
        machine.Update(t0.AddSeconds(2), away);
        Assert.Equal(BarState.Pill, machine.State); // the paused bar does not swallow it
        machine.Update(t0.AddSeconds(3), new PixelPoint(900, 20));
        Assert.Equal(BarState.Open, machine.State); // can be opened (buttons like "+5 min")

        machine.Update(t0.AddSeconds(4), away);
        machine.Update(t0.AddSeconds(4.6), away);
        Assert.Equal(BarState.Hidden, machine.State);
        Assert.False(machine.IsOverridingPause); // paused again
        machine.Update(t0.AddSeconds(5), new PixelPoint(960, 0));
        Assert.Equal(BarState.Hidden, machine.State);
    }

    [Fact]
    public void Ending_an_important_pill_on_a_paused_bar_hides_it_at_once()
    {
        var machine = new BarStateMachine();
        machine.Configure([Monitor], new BarSettings { Paused = true });
        machine.Announce(Monitor, DateTimeOffset.UtcNow.AddSeconds(60), overridePause: true);

        machine.EndAnnounce(); // "×" or the next one in the queue

        Assert.Equal(BarState.Hidden, machine.State);
        Assert.False(machine.IsOverridingPause);
    }

    [Fact]
    public void Switched_off_timer_switches_of_1_3_1_move_into_the_bar_settings_once()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """
            { "version": 1,
              "modules": { "timer": { "enabled": true, "values": { "ignoreDoNotDisturb": "false", "showInFullscreen": "true", "volume": "40" } } },
              "bar": { "notificationSeconds": 9 } }
            """);
        var settings = new SettingsService(file, null, NullLogger.Instance);
        settings.Load();

        SettingsMigration.Run(settings, NullLogger.Instance);

        var reloaded = new SettingsService(file, null, NullLogger.Instance);
        reloaded.Load();
        var bar = reloaded.GetSection<BarSettings>("bar");
        Assert.False(bar.ImportantDuringDoNotDisturb);
        Assert.True(bar.ImportantInFullscreen);
        Assert.True(bar.ImportantWhenPaused);
        Assert.Equal(9, bar.NotificationSeconds); // the rest stays
        var timer = reloaded.GetModuleSettings("timer");
        Assert.Null(timer.GetString("ignoreDoNotDisturb"));
        Assert.Equal("40", timer.GetString("volume"));
        Assert.Equal(SettingsMigration.CurrentVersion, reloaded.Current.Version);

        // Only once: a later change in the bar settings is not overwritten.
        reloaded.SetSection("bar", new BarSettings { ImportantDuringDoNotDisturb = true });
        SettingsMigration.Run(reloaded, NullLogger.Instance);
        Assert.True(reloaded.GetSection<BarSettings>("bar").ImportantDuringDoNotDisturb);
    }

    [Fact]
    public void Dropped_paths_and_text_are_told_apart()
    {
        var items = DropClassifier.FromPaths([@"C:\a\b.docx", @"C:\a\Ordner", ""], p => p.EndsWith("Ordner", StringComparison.Ordinal));
        Assert.Equal([new DroppedItem(DropKind.File, @"C:\a\b.docx"), new DroppedItem(DropKind.Folder, @"C:\a\Ordner")], items);

        Assert.Equal(new DroppedItem(DropKind.Link, "https://example.org/x", "Titel"), DropClassifier.FromText("https://example.org/x\r\nTitel", "Titel"));
        Assert.Equal(DropKind.Text, DropClassifier.FromText("nur ein Satz")!.Kind);
        Assert.Equal(DropKind.Text, DropClassifier.FromText("ftp://example.org/x")!.Kind);
        Assert.Null(DropClassifier.FromText("  "));
    }
}
