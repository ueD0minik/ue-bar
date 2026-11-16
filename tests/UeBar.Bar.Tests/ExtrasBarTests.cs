// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar.Tests;

/// <summary>M6 in the bar logic: hotkey, notification queue, pill without mouse, held panel.</summary>
public class ExtrasBarTests
{
    [Theory]
    [InlineData("Win+Alt+Space", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x20)]
    [InlineData("Strg+Umschalt+K", HotkeyModifiers.Control | HotkeyModifiers.Shift, 'K')]
    [InlineData("ctrl + f12", HotkeyModifiers.Control, 0x7B)]
    [InlineData("Alt+Leertaste", HotkeyModifiers.Alt, 0x20)]
    public void Hotkeys_parse(string text, HotkeyModifiers modifiers, int key)
    {
        var hotkey = Hotkey.TryParse(text);

        Assert.NotNull(hotkey);
        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal(key, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("Space")] // without modifier
    [InlineData("Win+Alt")] // without key
    [InlineData("Win+A+B")] // two keys
    [InlineData("Win+Blub")]
    [InlineData("")]
    public void Invalid_hotkeys_are_rejected(string text) => Assert.Null(Hotkey.TryParse(text));

    [Fact]
    public void Hotkey_in_use_is_reported_without_crashing()
    {
        int? mods = null;
        var (result, hotkey, message) = HotkeyBinder.Register("Win+Alt+Space", (m, _) =>
        {
            mods = m;
            return false; // taken by another program
        });

        Assert.Equal(HotkeyRegistration.InUse, result);
        Assert.NotNull(hotkey);
        Assert.Contains("belegt", message);
        Assert.Equal((int)(HotkeyModifiers.Win | HotkeyModifiers.Alt) | HotkeyBinder.NoRepeat, mods);
        Assert.Equal(HotkeyRegistration.Invalid, HotkeyBinder.Register("Quatsch", (_, _) => true).Result);
        Assert.Equal(HotkeyRegistration.Registered, HotkeyBinder.Register("Win+Alt+Space", (_, _) => true).Result);
    }

    private static BarNotification Note(string title, double seconds = 6) =>
        new(title, "Text", null, TimeSpan.FromSeconds(seconds), DateTimeOffset.Now);

    [Fact]
    public void Notifications_are_shown_one_after_another_and_suppressed_ones_only_go_to_history()
    {
        var queue = new NotificationQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(Note("A", 6), suppress: false);
        queue.Enqueue(Note("B", 2), suppress: false);
        queue.Enqueue(Note("Vollbild"), suppress: true);

        Assert.True(queue.Advance(t0));
        Assert.Equal("A", queue.Current!.Title);
        Assert.False(queue.Advance(t0.AddSeconds(5))); // A still running, B waits
        Assert.True(queue.Advance(t0.AddSeconds(6)));
        Assert.Equal("B", queue.Current!.Title);
        Assert.True(queue.Advance(t0.AddSeconds(8)));
        Assert.Null(queue.Current); // the suppressed one never comes as a pill
        Assert.Equal(["Vollbild", "B", "A"], queue.History.Select(h => h.Title));
    }

    [Fact]
    public void History_keeps_the_last_twenty_and_can_be_cleared()
    {
        var queue = new NotificationQueue();
        for (var i = 0; i < 25; i++)
        {
            queue.Enqueue(Note($"N{i}"), suppress: true);
        }

        Assert.Equal(NotificationQueue.HistoryLimit, queue.History.Count);
        Assert.Equal("N24", queue.History[0].Title);
        queue.ClearHistory();
        Assert.Empty(queue.History);
    }

    private static readonly MonitorInfo Monitor = new("m1", "Test", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 1.0);

    private static BarStateMachine Machine(BarSettings? settings = null)
    {
        var machine = new BarStateMachine();
        machine.Configure([Monitor], settings ?? new BarSettings { HideDelayMs = 500 });
        return machine;
    }

    [Fact]
    public void Announced_pill_stays_without_mouse_then_hides_and_opens_on_hover()
    {
        var machine = Machine();
        var t0 = DateTimeOffset.UtcNow;
        var away = new PixelPoint(900, 700);

        Assert.True(machine.Announce(Monitor, t0.AddSeconds(6)));
        Assert.Equal(BarState.Pill, machine.State);
        machine.BarBounds = new PixelRect(880, 8, 160, 40);
        machine.Update(t0.AddSeconds(3), away);
        Assert.Equal(BarState.Pill, machine.State); // stays until it expires
        machine.Update(t0.AddSeconds(6.1), away);
        machine.Update(t0.AddSeconds(6.7), away);
        Assert.Equal(BarState.Hidden, machine.State); // hidden normally afterwards

        machine.Announce(Monitor, t0.AddSeconds(20));
        machine.BarBounds = new PixelRect(880, 8, 160, 40);
        machine.Update(t0.AddSeconds(8), new PixelPoint(900, 20)); // mouse on the message
        Assert.Equal(BarState.Open, machine.State);
    }

    [Fact]
    public void Paused_bar_shows_no_announcement_but_quick_search_can_hold_the_panel()
    {
        var machine = Machine(new BarSettings { Paused = true });

        Assert.False(machine.Announce(Monitor, DateTimeOffset.UtcNow.AddSeconds(5)));
        Assert.Equal(BarState.Hidden, machine.State);

        Assert.True(machine.OpenHeld(Monitor));
        machine.Update(DateTimeOffset.UtcNow, new PixelPoint(5, 900));
        Assert.Equal(BarState.Pinned, machine.State); // paused, but held
        Assert.True(machine.IsHeld);

        machine.ReleaseHeld(DateTimeOffset.UtcNow);
        Assert.Equal(BarState.Hidden, machine.State);
        Assert.False(machine.IsHeld);
    }

    /// <summary>
    /// What the window shows when it runs the actions from <see cref="BarWindowTransitions"/> –
    /// like <c>BarWindow</c> (pill/panel/nothing). Checks after every change that window and
    /// state machine agree.
    /// </summary>
    private sealed class WindowModel
    {
        public string Shows { get; private set; } = "nichts";

        public List<string> Problems { get; } = [];

        public void Attach(BarStateMachine machine) => machine.StateChanged += (_, e) =>
        {
            var action = BarWindowTransitions.For(e.OldState, e.NewState);
            Shows = action switch
            {
                BarWindowAction.ShowPill => "Pill",
                BarWindowAction.Expand or BarWindowAction.ExpandPinned or BarWindowAction.ShowPinned => "Panel",
                BarWindowAction.Hide => "nichts",
                _ => Shows, // Pin/Unpin/None do not change the shape
            };
            var expected = e.NewState switch
            {
                BarState.Pill => "Pill",
                BarState.Open or BarState.Pinned => "Panel",
                _ => "nichts",
            };
            if (Shows != expected)
            {
                Problems.Add($"{e.OldState} → {e.NewState}: window shows {Shows} ({action})");
            }
        };
    }

    [Fact]
    public void Quick_search_five_times_with_action_also_while_the_confirmation_pill_is_visible()
    {
        // M6-Fix 1: hotkey → action → confirmation pill (3 s) → hotkey while the pill is still showing.
        // Before: pill → pinned without expanding – the pill got stuck, the search was invisible.
        var machine = Machine();
        var window = new WindowModel();
        window.Attach(machine);
        var t = DateTimeOffset.UtcNow;
        var away = new PixelPoint(900, 700);
        double[] gaps = [4.5, 1.5, 0.8, 0.3, 1.2]; // seconds after the confirmation until the next hotkey

        foreach (var gap in gaps)
        {
            Assert.True(machine.OpenHeld(Monitor)); // hotkey
            Assert.Equal(BarState.Pinned, machine.State);
            Assert.Equal("Panel", window.Shows); // search field visible
            machine.Update(t, away);

            machine.ReleaseHeld(t); // action run → search closed
            Assert.True(machine.Announce(Monitor, t.AddSeconds(3))); // confirmation pill
            Assert.Equal("Pill", window.Shows);
            machine.BarBounds = new PixelRect(880, 8, 160, 40);

            for (var s = 0.1; s <= gap; s += 0.1)
            {
                machine.Update(t.AddSeconds(s), away);
            }

            t = t.AddSeconds(gap);
        }

        // At the end: search closed, pill expires → everything gone.
        Assert.True(machine.OpenHeld(Monitor));
        Assert.Equal("Panel", window.Shows);
        machine.ReleaseHeld(t);
        Assert.Equal(BarState.Hidden, machine.State); // came from the message pill → completely gone
        Assert.Empty(window.Problems);
        Assert.False(machine.IsHeld);
    }

    [Fact]
    public void Every_reachable_state_change_keeps_window_and_state_machine_in_sync()
    {
        // Random but fixed sequence of everything app/tray/mouse can trigger.
        var machine = Machine(new BarSettings { HideDelayMs = 300, DwellMs = 100 });
        var window = new WindowModel();
        window.Attach(machine);
        var random = new Random(4711);
        var t = DateTimeOffset.UtcNow;
        PixelPoint[] cursors = [new(900, 700), new(900, 0), new(900, 20), new(1500, 300)];

        for (var i = 0; i < 5000; i++)
        {
            t = t.AddMilliseconds(random.Next(16, 400));
            machine.BarBounds = machine.State switch
            {
                BarState.Pill => new PixelRect(880, 8, 160, 40),
                BarState.Open or BarState.Pinned => new PixelRect(500, 8, 900, 500),
                _ => null,
            };
            switch (random.Next(9))
            {
                case 0: machine.OpenHeld(Monitor); break;
                case 1: machine.ReleaseHeld(t); break;
                case 2: machine.Announce(Monitor, t.AddSeconds(random.Next(1, 7))); break;
                case 3: machine.Pin(); break;
                case 4: machine.Unpin(t); break;
                case 5: machine.EndAnnounce(); break;
                default: machine.Update(t, cursors[random.Next(cursors.Length)]); break;
            }
        }

        Assert.Empty(window.Problems);
    }

    [Theory]
    [InlineData(BarState.Hidden, BarState.Pill, BarWindowAction.ShowPill)]
    [InlineData(BarState.Pill, BarState.Open, BarWindowAction.Expand)]
    [InlineData(BarState.Pill, BarState.Pinned, BarWindowAction.ExpandPinned)]
    [InlineData(BarState.Open, BarState.Pinned, BarWindowAction.Pin)]
    [InlineData(BarState.Pinned, BarState.Open, BarWindowAction.Unpin)]
    [InlineData(BarState.Hidden, BarState.Pinned, BarWindowAction.ShowPinned)]
    [InlineData(BarState.Pinned, BarState.Pinned, BarWindowAction.ShowPinned)]
    [InlineData(BarState.Open, BarState.Hidden, BarWindowAction.Hide)]
    public void Window_actions_per_transition(BarState from, BarState to, BarWindowAction action) =>
        Assert.Equal(action, BarWindowTransitions.For(from, to));

    [Fact]
    public void Reset_drops_pending_and_current_notifications_but_keeps_history()
    {
        var queue = new NotificationQueue();
        queue.Enqueue(Note("A"), suppress: false);
        queue.Enqueue(Note("B"), suppress: false);
        queue.Advance(DateTimeOffset.UtcNow);

        queue.ClearPending();

        Assert.Null(queue.Current);
        Assert.False(queue.Advance(DateTimeOffset.UtcNow)); // B does not come any more
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(2, queue.History.Count);
    }

    [Fact]
    public void Releasing_a_held_panel_that_was_open_lets_it_hide_normally()
    {
        var machine = Machine();
        machine.Announce(Monitor, DateTimeOffset.UtcNow.AddSeconds(5));
        machine.BarBounds = new PixelRect(880, 8, 160, 40);
        machine.Update(DateTimeOffset.UtcNow, new PixelPoint(900, 20));
        Assert.Equal(BarState.Open, machine.State);

        machine.OpenHeld(Monitor);
        Assert.Equal(BarState.Pinned, machine.State);
        machine.ReleaseHeld(DateTimeOffset.UtcNow);

        Assert.Equal(BarState.Open, machine.State);
    }

    // ------------------------------------------------------------------ Assist display (M6-Fix 2)

    private static AssistExchange Ex(int i) => new($"Question {i}", $"Answer {i}", true);

    [Fact]
    public void Assist_shows_only_the_latest_exchange_and_keeps_ten_older_ones_behind_history()
    {
        var transcript = new AssistTranscript();
        Assert.Null(transcript.Latest);
        Assert.Empty(transcript.Older);

        for (var i = 1; i <= 15; i++)
        {
            transcript.Add(Ex(i));
        }

        Assert.Equal("Question 15", transcript.Latest!.Question);
        Assert.Equal(AssistTranscript.HistoryLimit, transcript.Older.Count);
        Assert.Equal("Question 14", transcript.Older[0].Question); // newest first
        Assert.Equal("Question 5", transcript.Older[^1].Question);

        transcript.IsHistoryExpanded = true;
        transcript.OnPanelClosed(); // panel closed → history closed
        Assert.False(transcript.IsHistoryExpanded);
        Assert.Equal(10, transcript.Older.Count); // content stays
    }

    [Fact]
    public void Assist_starts_a_new_conversation_only_when_expired_and_hints_only_if_something_was_shown()
    {
        var transcript = new AssistTranscript();
        transcript.Add(Ex(1));
        transcript.Add(Ex(2));
        transcript.IsHistoryExpanded = true;

        Assert.False(transcript.StartNewIfExpired(expired: false));
        Assert.Equal("Question 2", transcript.Latest!.Question);

        Assert.True(transcript.StartNewIfExpired(expired: true));
        Assert.Null(transcript.Latest);
        Assert.Empty(transcript.Older);
        Assert.False(transcript.IsHistoryExpanded);
        Assert.True(transcript.ShowsNewConversationHint); // "New conversation"

        transcript.Add(Ex(3)); // next question → hint gone
        Assert.False(transcript.ShowsNewConversationHint);

        var empty = new AssistTranscript();
        Assert.True(empty.StartNewIfExpired(expired: true));
        Assert.False(empty.ShowsNewConversationHint); // nothing was visible → no hint

        empty.StartNew(announce: true); // button "New": always a hint
        Assert.True(empty.ShowsNewConversationHint);
        empty.OnPanelClosed();
        Assert.False(empty.ShowsNewConversationHint);
    }
}
