// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Bar;

namespace UeBar.Bar.Tests;

/// <summary>P2-6: the logic behind the keyboard hook, the choice popup and the privacy marks.</summary>
public sealed class ClipboardSupportTests
{
    private const int V = 0x56;

    private static KeyHookFilter WinV()
    {
        var filter = new KeyHookFilter();
        filter.Add(Hotkey.TryParse("Win+V")!);
        return filter;
    }

    [Fact]
    public void Win_v_is_swallowed_and_reported_once_with_repeat_and_key_up()
    {
        var filter = WinV();
        Assert.Equal(HookVerdict.Pass, filter.OnKey(KeyHookFilter.VkLWin, true, false, out _));
        Assert.Equal(HookVerdict.SwallowAndFire, filter.OnKey(V, true, false, out var fired));
        Assert.Equal("Win+V", fired!.Text);
        Assert.Equal(HookVerdict.Swallow, filter.OnKey(V, true, false, out fired)); // key repeat
        Assert.Null(fired);
        Assert.Equal(HookVerdict.Swallow, filter.OnKey(V, false, false, out _));    // key up
        Assert.Equal(HookVerdict.Pass, filter.OnKey(KeyHookFilter.VkLWin, false, false, out _));

        // Right Win key as well.
        filter.OnKey(KeyHookFilter.VkRWin, true, false, out _);
        Assert.Equal(HookVerdict.SwallowAndFire, filter.OnKey(V, true, false, out _));
    }

    [Fact]
    public void Everything_else_goes_through_untouched()
    {
        var filter = WinV();
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, true, false, out _));  // plain v
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, false, false, out _));

        filter.OnKey(KeyHookFilter.VkLControl, true, false, out _);
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, true, false, out _));  // Ctrl+V – pasting stays Windows'
        filter.OnKey(KeyHookFilter.VkLControl, false, false, out _);

        filter.OnKey(KeyHookFilter.VkLWin, true, false, out _);
        filter.OnKey(KeyHookFilter.VkLShift, true, false, out _);
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, true, false, out _));  // Win+Shift+V is another hotkey
        filter.OnKey(KeyHookFilter.VkLShift, false, false, out _);
        Assert.Equal(HookVerdict.Pass, filter.OnKey(0x45, true, false, out _)); // Win+E
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, true, true, out _));   // injected (other tools, our own keys)
        Assert.Equal(HookVerdict.Pass, filter.OnKey(KeyHookFilter.VkMask, true, true, out _));

        var empty = new KeyHookFilter();
        empty.OnKey(KeyHookFilter.VkLWin, true, false, out _);
        Assert.Equal(HookVerdict.Pass, empty.OnKey(V, true, false, out _));   // nothing taken over
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void Removing_the_hotkey_gives_it_back_and_reset_forgets_held_keys()
    {
        var filter = WinV();
        filter.OnKey(KeyHookFilter.VkLWin, true, false, out _);
        Assert.Equal(HotkeyModifiers.Win, filter.Modifiers);
        filter.Reset();
        Assert.Equal(HotkeyModifiers.None, filter.Modifiers);
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, true, false, out _));

        filter.OnKey(KeyHookFilter.VkLWin, true, false, out _);
        filter.Remove(Hotkey.TryParse("Win+V")!);
        Assert.Equal(HookVerdict.Pass, filter.OnKey(V, true, false, out _));
    }

    [Fact]
    public void Hook_schedule_retries_then_fails_and_refreshes_a_working_hook()
    {
        var schedule = new HookSchedule();
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.Report(false));
        Assert.False(schedule.HasFailed);
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Report(false));
        Assert.Equal(HookSchedule.Refresh, schedule.Report(false));
        Assert.True(schedule.HasFailed); // three tries – the replacement hotkey takes over

        Assert.Equal(HookSchedule.Refresh, schedule.Report(true)); // a later try works
        Assert.False(schedule.HasFailed);
        Assert.Equal(0, schedule.Failures);
        Assert.Equal(TimeSpan.FromMinutes(5), HookSchedule.Refresh);

        schedule.Reset();
        Assert.False(schedule.IsInstalled);
    }

    [Fact]
    public void Choice_keys_enter_takes_the_first_action_and_named_keys_win()
    {
        var actions = new List<(string Id, string? Shortcut)>
        {
            ("paste", null), ("plain", "Shift+Enter"), ("copy", "Ctrl+C"), ("keep", "Ctrl+M"), ("pin", "Ctrl+P"), ("delete", "Delete"),
        };
        Assert.Equal("paste", ChoiceKeys.ActionFor(actions, HotkeyModifiers.None, 0x0D));
        Assert.Equal("plain", ChoiceKeys.ActionFor(actions, HotkeyModifiers.Shift, 0x0D));
        Assert.Equal("copy", ChoiceKeys.ActionFor(actions, HotkeyModifiers.Control, 'C'));
        Assert.Equal("keep", ChoiceKeys.ActionFor(actions, HotkeyModifiers.Control, 'M'));
        Assert.Equal("pin", ChoiceKeys.ActionFor(actions, HotkeyModifiers.Control, 'P'));
        Assert.Equal("delete", ChoiceKeys.ActionFor(actions, HotkeyModifiers.None, 0x2E));
        Assert.Null(ChoiceKeys.ActionFor(actions, HotkeyModifiers.None, 'M'));   // typing searches
        Assert.Null(ChoiceKeys.ActionFor(actions, HotkeyModifiers.Control, 0x0D));
        Assert.Null(ChoiceKeys.ActionFor([], HotkeyModifiers.None, 0x0D));

        Assert.Equal("Merken (Strg + M)", ChoiceKeys.Tooltip("Merken", "Ctrl+M"));
        Assert.Equal("Löschen (Entf)", ChoiceKeys.Tooltip("Löschen", "Delete"));
        Assert.Equal("Als Text (Umschalt + Enter)", ChoiceKeys.Tooltip("Als Text", "Shift+Enter"));
        Assert.Equal("Einfügen", ChoiceKeys.Tooltip("Einfügen", null));
        Assert.Null(KeyChord.TryParse("Ctrl+M+K"));
        Assert.Null(KeyChord.TryParse("Ctrl+Nope"));
    }

    [Fact]
    public void Choice_search_needs_every_word_ignoring_case_and_accents()
    {
        string[] items = ["Größe der Bilder", "Rechnung März.pdf", "Hallo Welt", "grosse Liste"];
        Assert.Equal([0, 1, 2, 3], ChoiceSearch.Filter(items, s => s, ""));
        Assert.Equal([0, 3], ChoiceSearch.Filter(items, s => s, "GROSSE"));
        Assert.Equal([1], ChoiceSearch.Filter(items, s => s, "marz pdf"));
        Assert.Equal([2], ChoiceSearch.Filter(items, s => s, "welt  hallo"));
        Assert.Empty(ChoiceSearch.Filter(items, s => s, "nichts"));
    }

    [Fact]
    public void After_a_change_the_same_row_stays_selected_or_the_one_at_its_place()
    {
        Assert.Equal(1, ChoiceSearch.Reselect(["a", "b", "c"], "b", 0));
        Assert.Equal(2, ChoiceSearch.Reselect(["a", "c", "d"], "b", 2)); // deleted → same position
        Assert.Equal(1, ChoiceSearch.Reselect(["a", "c"], "x", 5));      // … or the last one
        Assert.Equal(0, ChoiceSearch.Reselect(["k", "a"], "a", 1, wantedId: "k"));
        Assert.Equal(-1, ChoiceSearch.Reselect([], "a", 0));
    }

    [Fact]
    public void Popup_goes_under_the_caret_above_it_without_room_and_stays_on_its_monitor()
    {
        IReadOnlyList<MonitorInfo> monitors =
        [
            new("laptop", "Laptop", new PixelRect(0, 0, 1920, 1200), new PixelRect(0, 0, 1920, 1152), true, 1.5),
            new("oled", "OLED", new PixelRect(1920, -240, 5120, 1440), new PixelRect(1920, -240, 5120, 1392), false, 1.0),
        ];
        var mouse = new PixelPoint(100, 100);

        // Under the line, with a gap of 6 DIP at 150 %.
        Assert.Equal(new PixelPoint(300, 529), ChoicePlacementRules.Place(new PixelRect(300, 500, 2, 20), mouse, 600, 500, monitors));

        // No room below → above the line.
        Assert.Equal(new PixelPoint(300, 1100 - 9 - 500), ChoicePlacementRules.Place(new PixelRect(300, 1100, 2, 20), mouse, 600, 500, monitors));

        // At the right edge of the OLED: pushed in, on that monitor (100 %).
        Assert.Equal(new PixelPoint(7040 - 600, 106), ChoicePlacementRules.Place(new PixelRect(7000, 80, 2, 20), mouse, 600, 500, monitors));

        // Unknown caret → at the mouse; centered on request.
        Assert.Equal(new PixelPoint(2506, 306), ChoicePlacementRules.Place(null, new PixelPoint(2500, 300), 600, 500, monitors));
        Assert.Equal(new PixelPoint(660, 217), ChoicePlacementRules.Place(null, mouse, 600, 500, monitors, center: true));
    }

    [Fact]
    public void Password_manager_marks_mean_nothing_is_read()
    {
        Assert.True(ClipboardPrivacy.MayRead(false, false, false, null));
        Assert.False(ClipboardPrivacy.MayRead(true, false, false, null));   // ExcludeClipboardContentFromMonitorProcessing
        Assert.False(ClipboardPrivacy.MayRead(false, true, false, null));   // Clipboard Viewer Ignore
        Assert.False(ClipboardPrivacy.MayRead(false, false, true, 0));      // CanIncludeInClipboardHistory = 0
        Assert.False(ClipboardPrivacy.MayRead(false, false, true, null));   // there, but unreadable → to be safe: no
        Assert.True(ClipboardPrivacy.MayRead(false, false, true, 1));       // explicitly allowed
        Assert.Equal("ExcludeClipboardContentFromMonitorProcessing", ClipboardPrivacy.ExcludeFormat);
        Assert.Equal("CanIncludeInClipboardHistory", ClipboardPrivacy.HistoryFormat);
    }
}
