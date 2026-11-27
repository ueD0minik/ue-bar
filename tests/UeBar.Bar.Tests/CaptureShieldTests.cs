// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using UeBar.Bar;

namespace UeBar.Bar.Tests;

/// <summary>P2-7: "hide from screen captures" – which way, every window, live switching, exceptions; hidden dumps.</summary>
public sealed class CaptureShieldTests
{
    /// <summary>Windows as the shield sees them: handle → (visible, system dialog, affinity).</summary>
    private sealed class FakeWindows : IShieldWindows
    {
        public Dictionary<IntPtr, (bool Visible, bool Dialog)> Windows { get; } = [];

        public Dictionary<IntPtr, uint> Affinity { get; } = [];

        /// <summary>Windows that refuse WDA_EXCLUDEFROMCAPTURE (an older build) – they still take WDA_MONITOR.</summary>
        public HashSet<IntPtr> RefuseExclude { get; } = [];

        public HashSet<IntPtr> RefuseAll { get; } = [];

        public int LastError { get; private set; }

        public IntPtr Add(int id, bool visible = true, bool dialog = false)
        {
            Windows[id] = (visible, dialog);
            Affinity[id] = 0;
            return id;
        }

        public IReadOnlyList<IntPtr> OwnTopLevelWindows(bool visibleOnly) =>
            [.. Windows.Where(w => !visibleOnly || w.Value.Visible).Select(w => w.Key)];

        public bool IsSystemDialog(IntPtr window) => Windows.TryGetValue(window, out var w) && w.Dialog;

        public uint GetAffinity(IntPtr window) => Affinity.GetValueOrDefault(window);

        public bool SetAffinity(IntPtr window, uint affinity)
        {
            if (RefuseAll.Contains(window) || (affinity == CaptureShieldSupport.AffinityExcludeFromCapture && RefuseExclude.Contains(window)))
            {
                LastError = 87;
                return false;
            }

            Affinity[window] = affinity;
            return true;
        }
    }

    [Fact]
    public void The_way_depends_on_the_windows_version()
    {
        Assert.Equal(CaptureShieldMode.Exclude, CaptureShieldSupport.Best(new Version(10, 0, 26300)));
        Assert.Equal(CaptureShieldMode.Exclude, CaptureShieldSupport.Best(new Version(10, 0, 19041)));   // Windows 10 2004
        Assert.Equal(CaptureShieldMode.Black, CaptureShieldSupport.Best(new Version(10, 0, 18363)));     // 1909: only black
        Assert.Equal(CaptureShieldMode.Black, CaptureShieldSupport.Best(new Version(6, 1, 7601)));       // Windows 7
        Assert.Equal(CaptureShieldMode.None, CaptureShieldSupport.Best(new Version(6, 0, 6002)));        // Vista: greyed out
        Assert.Equal((0x11u, 0x01u, 0x00u), (CaptureShieldSupport.Affinity(CaptureShieldMode.Exclude), CaptureShieldSupport.Affinity(CaptureShieldMode.Black), CaptureShieldSupport.Affinity(CaptureShieldMode.None)));
    }

    [Fact]
    public void Switching_on_covers_every_open_window_and_off_takes_it_back_without_a_restart()
    {
        var windows = new FakeWindows();
        var bar = windows.Add(1);
        var pin = windows.Add(2);
        var hiddenSettings = windows.Add(3, visible: false);
        var shield = new CaptureShield(windows, CaptureShieldMode.Exclude);

        var on = shield.SetEnabled(true);

        Assert.Equal((true, CaptureShieldMode.Exclude, 2, 0), (on.Enabled, on.Mode, on.Windows, on.Failed));
        Assert.Equal(0x11u, windows.Affinity[bar]);
        Assert.Equal(0x11u, windows.Affinity[pin]);
        Assert.Equal(0u, windows.Affinity[hiddenSettings]); // gets it when it appears

        windows.Windows[pin] = (false, false); // the pin is hidden while on – off must still reach it
        var off = shield.SetEnabled(false);

        Assert.Equal((false, 2), (off.Enabled, off.Windows));
        Assert.All(windows.Affinity.Values, a => Assert.Equal(0u, a));
        Assert.Equal(0u, shield.ExpectedAffinity);
    }

    [Fact]
    public void New_windows_get_the_setting_when_they_appear()
    {
        var windows = new FakeWindows();
        var shield = new CaptureShield(windows, CaptureShieldMode.Exclude);
        var before = windows.Add(1);
        Assert.False(shield.WindowShown(before)); // off: nothing happens
        Assert.Equal(0u, windows.Affinity[before]);

        shield.SetEnabled(true);
        var popup = windows.Add(2);    // context menu, tooltip, Win+V choice …
        var shownAgain = windows.Add(3, visible: false);

        Assert.True(shield.WindowShown(popup));
        Assert.True(shield.WindowShown(shownAgain));
        Assert.False(shield.WindowShown(popup)); // already set: nothing to do
        Assert.Equal(0x11u, windows.Affinity[popup]);
        Assert.Equal(0x11u, windows.Affinity[shownAgain]);
        Assert.Equal(0x11u, shield.ExpectedAffinity);
    }

    [Fact]
    public void Older_windows_fall_back_to_black_and_without_any_way_the_switch_stays_off()
    {
        var windows = new FakeWindows();
        var old = windows.Add(1);
        windows.RefuseExclude.Add(old);
        var shield = new CaptureShield(windows, CaptureShieldMode.Exclude);

        var on = shield.SetEnabled(true);
        Assert.Equal((1, 0, 1), (on.Windows, on.Failed, shield.BlackFallbacks));
        Assert.Equal(0x01u, windows.Affinity[old]); // WDA_MONITOR: black on captures

        var olderWindows = new FakeWindows();
        var pin = olderWindows.Add(7);
        var black = new CaptureShield(olderWindows, CaptureShieldMode.Black); // Windows before 2004
        Assert.Equal(CaptureShieldMode.Black, black.SetEnabled(true).Mode);
        Assert.Equal(0x01u, olderWindows.Affinity[pin]);

        var none = new CaptureShield(windows, CaptureShieldMode.None);
        Assert.False(none.IsAvailable);
        Assert.Equal((false, CaptureShieldMode.None, 0), (none.SetEnabled(true).Enabled, none.SetEnabled(true).Mode, none.SetEnabled(true).Windows));
        Assert.False(none.Enabled);

        var refused = new FakeWindows();
        var stubborn = refused.Add(5);
        refused.RefuseAll.Add(stubborn);
        var result = new CaptureShield(refused, CaptureShieldMode.Exclude).SetEnabled(true);
        Assert.Equal((0, 1, 87), (result.Windows, result.Failed, result.LastError));
    }

    [Fact]
    public void The_screenshot_overlay_and_windows_dialogs_stay_as_they_are()
    {
        var windows = new FakeWindows();
        var overlay = windows.Add(1);
        var saveDialog = windows.Add(2, dialog: true);
        var bar = windows.Add(3);
        var shield = new CaptureShield(windows, CaptureShieldMode.Exclude);
        shield.Exempt(overlay);

        shield.SetEnabled(true);
        Assert.False(shield.WindowShown(overlay));
        Assert.False(shield.WindowShown(saveDialog));

        Assert.Equal((0u, 0u, 0x11u), (windows.Affinity[overlay], windows.Affinity[saveDialog], windows.Affinity[bar]));

        // A handle Windows gives to a new window after the overlay closed is covered again.
        shield.Release(overlay);
        Assert.True(shield.WindowShown(overlay));

        // Exempting a window that already has it (opened while on) takes it off.
        var second = windows.Add(4);
        shield.WindowShown(second);
        shield.Exempt(second);
        Assert.Equal(0u, windows.Affinity[second]);
    }

    [Fact]
    public void The_bar_check_only_complains_about_an_affinity_nobody_wanted()
    {
        Assert.Empty(BarWindowHealth.Problems(new BarWindowFacts(true, false, 1, true, false)));
        Assert.Equal(["unexpected display affinity"], BarWindowHealth.Problems(new BarWindowFacts(true, false, 1, true, true)));
    }

    [Fact]
    public void Hang_dumps_start_without_any_window()
    {
        var info = HangDumpCommand.StartInfo(@"C:\Program Files\ue Bar\ueBar.exe", 4242, @"C:\data\logs\haenger-1.dmp");

        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.False(info.ErrorDialog);
        Assert.Equal(["--hang-dump", "4242", @"C:\data\logs\haenger-1.dmp"], info.ArgumentList);
        Assert.Equal(@"C:\Program Files\ue Bar\ueBar.exe", info.FileName);
    }

    [Fact]
    public void Only_a_complete_helper_command_writes_a_dump()
    {
        Assert.True(HangDumpCommand.TryParse(["--hang-dump", "4242", @"C:\data\haenger.dmp"], out var pid, out var path));
        Assert.Equal((4242, @"C:\data\haenger.dmp"), (pid, path));

        Assert.False(HangDumpCommand.TryParse([], out _, out _));                                          // normal start
        Assert.False(HangDumpCommand.TryParse(["--autostart"], out _, out _));
        Assert.False(HangDumpCommand.TryParse(["--hang-dump", "x", @"C:\data\haenger.dmp"], out _, out _));
        Assert.False(HangDumpCommand.TryParse(["--hang-dump", "-1", @"C:\data\haenger.dmp"], out _, out _));
        Assert.False(HangDumpCommand.TryParse(["--hang-dump", "4242", "haenger.dmp"], out _, out _));      // only full paths
    }
}
