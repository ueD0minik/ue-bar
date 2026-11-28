// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Settings;

namespace UeBar.Bar.Tests;

/// <summary>
/// P3-1 without WPF: the app's own dialogs (buttons, Enter, Esc), the Markdown of answers, "Send marked text to …"
/// (copy, restore the clipboard) and the new default "hide from screen captures".
/// </summary>
public sealed class ContentActionsBarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uebar-p31-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ dialogs

    [Fact]
    public void Dialog_buttons_default_and_keys()
    {
        Assert.Equal([DialogAnswer.Confirmed], DialogRules.Buttons(DialogButtons.Ok));
        Assert.Equal([DialogAnswer.Confirmed, DialogAnswer.Declined], DialogRules.Buttons(DialogButtons.YesNo));

        Assert.Equal(DialogAnswer.Confirmed, DialogRules.Default(DialogButtons.YesNo, safeDefault: false));
        Assert.Equal(DialogAnswer.Declined, DialogRules.Default(DialogButtons.OkCancel, safeDefault: true));
        Assert.Equal(DialogAnswer.Confirmed, DialogRules.Default(DialogButtons.Ok, safeDefault: true)); // only one button

        // Enter takes the focused button (after Tab the other one), Esc and the X always decline.
        Assert.Equal(DialogAnswer.Declined, DialogRules.OnKey(DialogKey.Enter, DialogAnswer.Declined, DialogButtons.YesNo));
        Assert.Equal(DialogAnswer.Confirmed, DialogRules.OnKey(DialogKey.Enter, DialogAnswer.Confirmed, DialogButtons.YesNo));
        Assert.Equal(DialogAnswer.Declined, DialogRules.OnKey(DialogKey.Escape, DialogAnswer.Confirmed, DialogButtons.YesNo));
        Assert.Equal(DialogAnswer.Confirmed, DialogRules.OnKey(DialogKey.Escape, DialogAnswer.Confirmed, DialogButtons.Ok));
        Assert.Null(DialogRules.OnKey(DialogKey.Other, DialogAnswer.Confirmed, DialogButtons.YesNo)); // Tab moves the focus
        Assert.Equal(DialogAnswer.Declined, DialogRules.OnClose(DialogButtons.OkCancel));
        Assert.NotEqual(DialogRules.Glyph(DialogKind.Error), DialogRules.Glyph(DialogKind.Info));
    }

    // ------------------------------------------------------------------ markdown

    [Fact]
    public void Markdown_blocks_of_a_typical_answer()
    {
        var blocks = MarkdownLite.Parse("""
            # Titel
            Ein **fetter** und *kursiver* Satz mit `code`.
            Zweite Zeile.

            - eins
              - eins a
            2. zwei
            - [x] erledigt

            > Zitat

            ```csharp
            var x = 1;
            ```
            ---
            | A | B |
            |---|---|
            | 1 | 2 |
            """);

        Assert.Equal(
            [MdBlockKind.Heading, MdBlockKind.Paragraph, MdBlockKind.ListItem, MdBlockKind.ListItem, MdBlockKind.ListItem, MdBlockKind.ListItem, MdBlockKind.Quote, MdBlockKind.Code, MdBlockKind.Rule, MdBlockKind.Table],
            blocks.Select(b => b.Kind));
        Assert.Equal(1, blocks[0].Level);
        Assert.Contains(blocks[1].Inlines, i => i.Text == "fetter" && i.Style == MdStyle.Bold);
        Assert.Contains(blocks[1].Inlines, i => i.Text == "kursiver" && i.Style == MdStyle.Italic);
        Assert.Contains(blocks[1].Inlines, i => i.Text == "code" && i.Style == MdStyle.Code);
        Assert.Contains("\n", string.Concat(blocks[1].Inlines.Select(i => i.Text)), StringComparison.Ordinal);
        Assert.Equal((0, 1), (blocks[2].Level, blocks[3].Level));
        Assert.Equal(2, blocks[4].Number);
        Assert.True(blocks[5].Checked);
        Assert.Equal(("csharp", "var x = 1;"), (blocks[7].Language, blocks[7].Code));
        Assert.Equal(2, blocks[9].Rows!.Count);
    }

    [Fact]
    public void Markdown_that_is_still_coming_in_and_odd_marks()
    {
        // An open code block (streaming) is a code block up to the end.
        var open = MarkdownLite.Parse("Text\n```\nhalf");
        Assert.Equal(MdBlockKind.Code, open[^1].Kind);
        Assert.Equal("half", open[^1].Code);

        // Underscores in words, lone stars, escapes and links.
        var inline = MarkdownLite.Inline(@"file_name_x 3 * 4 \*kein\* [Seite](https://example.org) [x](javascript:alert)");
        var text = string.Concat(inline.Select(i => i.Text));
        Assert.Contains("file_name_x", text, StringComparison.Ordinal);
        Assert.Contains("3 * 4", text, StringComparison.Ordinal);
        Assert.Contains("*kein*", text, StringComparison.Ordinal);
        Assert.Contains(inline, i => i.Link == "https://example.org" && i.Text == "Seite");
        Assert.DoesNotContain(inline, i => i.Link is { } l && l.StartsWith("javascript", StringComparison.Ordinal)); // only web and mail links
        Assert.Empty(MarkdownLite.Parse(null));
        Assert.Equal("Titel\n• a\nx = 1", MarkdownLite.PlainText("# Titel\n- a\n```\nx = 1\n```"));
    }

    // ------------------------------------------------------------------ marked text

    [Fact]
    public async Task Marked_text_is_copied_and_the_old_clipboard_comes_back()
    {
        var io = new FakeSelection { Clipboard = "alt", Marked = "markiert", HeldSteps = 3 };
        var result = await SelectionCopier.CopyAsync(io, NoWait, default);

        Assert.Equal((SelectionCopyOutcome.Text, "markiert", true, true), (result.Outcome, result.Text, result.Restored, result.WaitedForKeys));
        Assert.Equal("alt", io.Clipboard); // as before
        Assert.Equal(1, io.Copies);
        Assert.True(io.CopySentAfterKeysReleased);
    }

    [Fact]
    public void Every_clipboard_step_stays_on_the_calling_thread_even_after_real_waits()
    {
        // The clipboard only works on the STA UI thread: found in the real test, a wait must not move on to the pool.
        var io = new FakeSelection { Clipboard = "alt", Marked = "markiert", HeldSteps = 2 };
        var threads = new HashSet<int>();
        io.Touched = () => threads.Add(Environment.CurrentManagedThreadId);
        SingleThread.Run(() => SelectionCopier.CopyAsync(io, (d, ct) => Task.Delay(1, ct), default));
        Assert.Single(threads);
    }

    [Fact]
    public async Task Nothing_marked_leaves_the_clipboard_alone()
    {
        var io = new FakeSelection { Clipboard = "alt", Marked = null };
        var result = await SelectionCopier.CopyAsync(io, NoWait, default);
        Assert.Equal((SelectionCopyOutcome.NoSelection, false), (result.Outcome, result.Restored));
        Assert.Equal("alt", io.Clipboard);
        Assert.Equal(0, io.Restores);

        var failed = await SelectionCopier.CopyAsync(new FakeSelection { CanSend = false }, NoWait, default);
        Assert.Equal(SelectionCopyOutcome.Failed, failed.Outcome);
    }

    [Fact]
    public async Task Only_spaces_count_as_nothing_but_the_clipboard_is_restored()
    {
        var io = new FakeSelection { Clipboard = "alt", Marked = "   " };
        var result = await SelectionCopier.CopyAsync(io, NoWait, default);
        Assert.Equal((SelectionCopyOutcome.NoSelection, true), (result.Outcome, result.Restored));
        Assert.Equal("alt", io.Clipboard);
    }

    // ------------------------------------------------------------------ defaults

    [Fact]
    public void Hiding_from_captures_is_on_for_new_and_once_for_existing_installations()
    {
        Assert.True(new GeneralSettings().HideFromCapture);
        Assert.Equal("Win+Alt+C", new GeneralSettings().SelectionHotkey);

        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """{ "version": 2, "general": { "hideFromCapture": false, "columns": 5 } }""");
        var settings = new SettingsService(file, null, NullLogger.Instance);
        settings.Load();
        SettingsMigration.Run(settings, NullLogger.Instance);

        var reloaded = new SettingsService(file, null, NullLogger.Instance);
        reloaded.Load();
        var general = reloaded.GetSection<GeneralSettings>(GeneralSettings.SectionName);
        Assert.True(general.HideFromCapture);
        Assert.Equal(5, general.Columns);
        Assert.Equal(SettingsMigration.CurrentVersion, reloaded.Current.Version);

        // Afterwards the user's choice counts.
        general.HideFromCapture = false;
        reloaded.SetSection(GeneralSettings.SectionName, general);
        SettingsMigration.Run(reloaded, NullLogger.Instance);
        Assert.False(reloaded.GetSection<GeneralSettings>(GeneralSettings.SectionName).HideFromCapture);
    }

    private static Task NoWait(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class FakeSelection : ISelectionIo
    {
        public string? Clipboard { get; set; }

        public string? Marked { get; set; }

        public int HeldSteps { get; set; }

        public bool CanSend { get; set; } = true;

        public Action? Touched { get; set; }

        public int Copies { get; private set; }

        public int Restores { get; private set; }

        public bool CopySentAfterKeysReleased { get; private set; }

        public uint Sequence { get; private set; } = 10;

        public bool ModifiersDown() => HeldSteps-- > 0;

        public object? Snapshot() => Touch(Clipboard);

        private T Touch<T>(T value)
        {
            Touched?.Invoke();
            return value;
        }

        public bool Restore(object? snapshot)
        {
            Touched?.Invoke();
            Restores++;
            Clipboard = (string?)snapshot;
            Sequence++;
            return true;
        }

        public bool SendCopy()
        {
            Touched?.Invoke();
            if (!CanSend)
            {
                return false;
            }

            Copies++;
            CopySentAfterKeysReleased = HeldSteps <= 0;
            if (Marked is not null)
            {
                Clipboard = Marked;
                Sequence++;
            }

            return true;
        }

        public string? ReadText() => Touch(Clipboard);
    }
}

/// <summary>Runs async code on one thread with its own message loop – like the WPF UI thread.</summary>
internal sealed class SingleThread : SynchronizationContext
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public static void Run(Func<Task> work)
    {
        var context = new SingleThread();
        var previous = Current;
        SetSynchronizationContext(context);
        try
        {
            var task = work();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
}
