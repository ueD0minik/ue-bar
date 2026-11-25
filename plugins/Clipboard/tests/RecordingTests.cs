// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text;
using System.Text.Json.Nodes;
using UeBar.Sdk;

namespace UeBar.Modules.Clipboard.Tests;

/// <summary>Recording, duplicates, limits, privacy, pause, storage (P2-6).</summary>
public sealed class RecordingTests
{
    [Fact]
    public async Task Records_text_with_html_and_rtf_images_and_file_lists()
    {
        await using var env = await new ClipEnv().StartAsync();
        Assert.Single(env.Watcher.Active); // watching right after the start

        await env.CopyAsync(env.Text("Hallo Welt", html: "<b>Hallo</b> Welt", rtf: @"{\rtf1 Hallo Welt}"), 1);
        await env.CopyAsync(env.Image(Png.Make(40, 30), 40, 30), 2);
        await env.CopyAsync(env.Files(@"C:\Users\demo\Dokumente\Bericht.docx", @"C:\Users\demo\Bilder"), 3);

        var entries = env.Store.Entries;
        Assert.Equal([ClipKind.Files, ClipKind.Image, ClipKind.Text], entries.Select(e => e.Kind)); // newest first
        var text = entries[2];
        Assert.True(text.HasHtml);
        Assert.True(text.HasRtf);
        Assert.Equal("Microsoft Word", text.AppName);
        Assert.Equal("WINWORD", text.AppProcess);
        Assert.Equal(20, text.ContentSize - (("<b>Hallo</b> Welt".Length + @"{\rtf1 Hallo Welt}".Length) * 2)); // "Hallo Welt" as UTF-16
        Assert.NotNull(env.Store.AppIcon("WINWORD"));

        var image = entries[1];
        Assert.Equal((40, 30), (image.ImageWidth, image.ImageHeight));
        Assert.True(image.HasThumbnail);
        Assert.NotNull(env.Store.Thumbnail(image.Id));

        var files = entries[0];
        Assert.Equal(2, files.FileCount);
        Assert.Equal("Bericht.docx, Bilder", files.Preview);

        var content = env.Store.LoadContent(text.Id)!;
        Assert.Equal("Hallo Welt", content.Text);
        Assert.Equal("<b>Hallo</b> Welt", content.Html);
        Assert.Equal(@"{\rtf1 Hallo Welt}", content.Rtf);
        Assert.Equal(Png.Make(40, 30), env.Store.LoadContent(image.Id)!.ImagePng);
        Assert.Equal([@"C:\Users\demo\Dokumente\Bericht.docx", @"C:\Users\demo\Bilder"], env.Store.LoadContent(files.Id)!.Files);
    }

    [Fact]
    public async Task The_same_content_again_moves_the_entry_up_instead_of_adding_it()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("eins"), 1);
        env.Time.Advance(TimeSpan.FromMinutes(1));
        await env.CopyAsync(env.Text("zwei"), 2);
        env.Time.Advance(TimeSpan.FromMinutes(1));
        await env.CopyAsync(env.Text("eins", app: "notepad"));

        Assert.Equal(2, env.Store.Entries.Count);
        Assert.Equal("eins", env.Store.Entries[0].Preview);
        Assert.Equal("notepad", env.Store.Entries[0].AppProcess); // the latest app counts
        Assert.Equal(env.Time.GetUtcNow(), env.Store.Entries[0].UsedAt);

        // Pictures and file lists the same way (paths without case).
        await env.CopyAsync(env.Image(Png.Make(8, 8), 8, 8), 3);
        await env.CopyAsync(env.Image(Png.Make(8, 8), 8, 8));
        await env.CopyAsync(env.Files(@"C:\a.txt"), 4);
        await env.CopyAsync(env.Files(@"c:\A.TXT"));
        Assert.Equal(4, env.Store.Entries.Count);
    }

    [Fact]
    public async Task Pasting_from_the_history_never_adds_an_entry()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("alt"), 1);
        env.Time.Advance(TimeSpan.FromMinutes(1));
        await env.CopyAsync(env.Text("neu"), 2);
        env.Time.Advance(TimeSpan.FromMinutes(1));

        // The plugin's own write comes back from the watcher: the entry only moves up …
        await env.CopyAsync(env.Text("alt", writtenBy: ClipboardModule.ModuleId));
        Assert.Equal(["alt", "neu"], env.Store.Entries.Select(e => e.Preview));

        // … and something it does not know (e.g. plain text of an image) is not added either.
        await env.CopyAsync(env.Text("nie gesehen", writtenBy: ClipboardModule.ModuleId));
        Assert.Equal(2, env.Store.Entries.Count);

        // Other modules' writes (e.g. a screenshot copied) are normal copies.
        await env.CopyAsync(env.Text("vom Screenshot", writtenBy: "screenshot"), 3);
    }

    [Fact]
    public async Task Limits_count_age_and_size_spare_the_kept_entries()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.SettingsAsync((ClipOptions.EntriesKey, "3"));
        for (var i = 1; i <= 3; i++)
        {
            await env.CopyAsync(env.Text($"Eintrag {i}"), i);
            env.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var first = env.Store.Entries.Single(e => e.Preview == "Eintrag 1");
        env.Store.SetKept(first.Id, true);
        await env.CopyAsync(env.Text("Eintrag 4"), 4); // 3 not kept + 1 kept
        await env.CopyAsync(env.Text("Eintrag 5"), 4); // "Eintrag 2" goes, the kept one stays
        Assert.DoesNotContain(env.Store.Entries, e => e.Preview == "Eintrag 2");
        Assert.Contains(env.Store.Entries, e => e.Preview == "Eintrag 1" && e.Kept);

        // Age: older than the days → gone (by the hourly check or a settings change), kept ones stay.
        env.Time.Advance(TimeSpan.FromDays(8));
        await env.SettingsAsync((ClipOptions.DaysKey, "7"));
        Assert.Equal(["Eintrag 1"], env.Store.Entries.Select(e => e.Preview));

        // Size: the oldest not kept entries go until the total fits.
        await env.SettingsAsync((ClipOptions.EntriesKey, "100"), (ClipOptions.TotalMbKey, "10"));
        for (var i = 0; i < 4; i++)
        {
            env.Time.Advance(TimeSpan.FromMinutes(1));
            await env.CopyAsync(env.Image(Png.Make(1100, 1000, seed: i + 10, noise: true), 1100, 1000)); // ≈ 3.3 MB each
        }

        Assert.True(env.Store.TotalBytes <= 10L * 1024 * 1024, $"{env.Store.TotalBytes}");
        Assert.Contains(env.Store.Entries, e => e.Kept);
        Assert.InRange(env.Store.Entries.Count(e => e.Kind == ClipKind.Image), 2, 3);
    }

    [Fact]
    public async Task Hourly_check_removes_old_entries_while_running()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("von gestern"), 1);
        env.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromHours(1));
        Assert.True(await Wait.UntilAsync(() => env.Store.Entries.Count == 0, 3000));
    }

    [Fact]
    public async Task Excluded_apps_are_never_stored()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("geheim123", app: "KeePass"));
        await env.CopyAsync(env.Text("geheim456", app: "KeePassXC"));
        await env.CopyAsync(new ClipboardChange(new ClipboardContent { Text = "geheim789" }, env.Time.GetUtcNow()) { Source = new ClipboardSourceApp("bw", "Bitwarden") });
        Assert.Empty(env.Store.Entries);

        // The list is a setting: an own app can be added, a default one removed.
        await env.SettingsAsync((ClipOptions.ExcludedKey, ModuleSettingValues.FormatList([new Dictionary<string, string> { ["id"] = "x", ["app"] = "notepad.exe" }])));
        await env.CopyAsync(env.Text("Notiz", app: "notepad"));
        await env.CopyAsync(env.Text("jetzt erlaubt", app: "KeePass"), 1);
        Assert.Equal("jetzt erlaubt", Assert.Single(env.Store.Entries).Preview);
        Assert.DoesNotContain(env.Log.Lines, l => l.Contains("geheim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pause_stores_nothing_and_a_restart_ends_it()
    {
        await using var env = await new ClipEnv().StartAsync();
        Assert.Equal(ClipboardModule.CommandPrefix, env.Module.QuickCommands[0].Prefix);
        var result = await env.Module.ExecuteQuickCommandAsync("cb", "pause", default);
        Assert.True(env.Module.IsPaused);
        Assert.Equal("Zwischenablage-Verlauf pausiert", result.Message);
        Assert.True(env.Module.TrayMenuEntries.Single().Checked);
        await env.CopyAsync(env.Text("während der Pause"));
        Assert.Empty(env.Store.Entries);

        // Tray and settings toggle it as well.
        await env.Module.RunTrayMenuEntryAsync("pause", default);
        Assert.False(env.Module.IsPaused);
        await env.Module.RunSettingsActionAsync(ClipOptions.PauseKey, default);
        Assert.True(env.Module.IsPaused);

        await env.Registry.StopModuleAsync(ClipboardModule.ModuleId);
        Assert.True(await env.Registry.StartModuleAsync(ClipboardModule.ModuleId));
        Assert.False(env.Module.IsPaused);
        await env.CopyAsync(env.Text("nach dem Neustart"), 1);
    }

    [Fact]
    public async Task Stored_files_hold_no_plain_text_and_come_back_after_a_restart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "uebar-clip-tests", Guid.NewGuid().ToString("N"), "clipboard");
        const string secret = "Sehr vertraulicher Absatz über Zürich 4711";
        try
        {
            await using (var env = await new ClipEnv(folder).StartAsync())
            {
                await env.CopyAsync(env.Text(secret, html: $"<p>{secret}</p>"), 1);
                await env.CopyAsync(env.Files(@"C:\Geheim\Vertrag-4711.pdf"), 2);
                env.Store.SetKept(env.Store.Entries[1].Id, true);
                Assert.True(env.Protector.Protected > 0);
            }

            foreach (var file in Directory.GetFiles(folder))
            {
                var bytes = File.ReadAllBytes(file);
                foreach (var needle in new[] { "vertraulicher", "Zürich", "Vertrag-4711", "Microsoft Word", "WINWORD" })
                {
                    Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)), $"{needle} in {Path.GetFileName(file)}");
                    Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(needle)), $"{needle} (UTF-16) in {Path.GetFileName(file)}");
                }
            }

            await using var again = await new ClipEnv(folder).StartAsync();
            Assert.Equal(2, again.Store.Entries.Count);
            var text = again.Store.Entries.Single(e => e.Kind == ClipKind.Text);
            Assert.True(text.Kept);
            Assert.Equal(secret, again.Store.LoadContent(text.Id)!.Text);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task Deleting_removes_the_data_at_once_and_orphans_are_cleaned_on_start()
    {
        var folder = Path.Combine(Path.GetTempPath(), "uebar-clip-tests", Guid.NewGuid().ToString("N"), "clipboard");
        try
        {
            await using (var env = await new ClipEnv(folder).StartAsync())
            {
                await env.CopyAsync(env.Image(Png.Make(20, 20), 20, 20), 1);
                await env.CopyAsync(env.Text("bleibt"), 2);
                var image = env.Store.Entries.Single(e => e.Kind == ClipKind.Image);
                Assert.True(File.Exists(Path.Combine(folder, image.Id + ".clip")));
                Assert.True(File.Exists(Path.Combine(folder, image.Id + ".thumb")));

                Assert.True(env.Store.Delete(image.Id));
                Assert.False(File.Exists(Path.Combine(folder, image.Id + ".clip")));
                Assert.False(File.Exists(Path.Combine(folder, image.Id + ".thumb")));

                // "Clear (keep ★)" and "Clear everything".
                env.Store.SetKept(env.Store.Entries[0].Id, true);
                await env.CopyAsync(env.Text("weg"), 2);
                Assert.Equal("1 Einträge gelöscht.", await env.Module.RunSettingsActionAsync(ClipOptions.ClearKey, default));
                Assert.Equal("bleibt", Assert.Single(env.Store.Entries).Preview);
                await env.Module.RunSettingsActionAsync(ClipOptions.ClearAllKey, default);
                Assert.Empty(env.Store.Entries);
                await env.CopyAsync(env.Text("zum Schluss"), 1);
            }

            File.WriteAllBytes(Path.Combine(folder, "0123456789abcdef0123456789abcdef.clip"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(folder, "0123456789abcdef0123456789abcdef.thumb"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(folder, "index.clip.tmp"), [1, 2, 3]);
            await using var again = await new ClipEnv(folder).StartAsync();
            var left = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
            var id = Assert.Single(again.Store.Entries).Id;
            Assert.Equal([id + ".clip", "index.clip"], left);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task An_unreadable_history_starts_empty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "uebar-clip-tests", Guid.NewGuid().ToString("N"), "clipboard");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "index.clip"), Encoding.UTF8.GetBytes("not encrypted"));
        try
        {
            await using var env = await new ClipEnv(folder).StartAsync();
            Assert.Empty(env.Store.Entries);
            Assert.Contains(env.Log.Lines, l => l.Contains("could not be read", StringComparison.Ordinal));
            await env.CopyAsync(env.Text("neu"), 1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task Images_off_and_too_large_images_are_left_out()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.SettingsAsync((ClipOptions.ImagesKey, "false"));
        Assert.False(Assert.Single(env.Watcher.Active).Images); // the core does not even read images
        await env.CopyAsync(new ClipboardChange(new ClipboardContent(), env.Time.GetUtcNow()) { ImageSkipped = true });
        Assert.Empty(env.Store.Entries);
        Assert.Contains(env.Log.Lines, l => l.Contains("Image left out", StringComparison.Ordinal));

        await env.SettingsAsync((ClipOptions.ImagesKey, "true"), (ClipOptions.ImageMbKey, "3"));
        var watch = Assert.Single(env.Watcher.Active);
        Assert.True(watch.Images);
        Assert.Equal(3L * 1024 * 1024, watch.MaxImageBytes);
        Assert.Equal(3, env.Watcher.Started);
    }

    [Fact]
    public async Task Logs_hold_kind_and_size_but_never_content()
    {
        await using var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("Kontonummer AT61 1904 3002 3457 3201", html: "<i>AT61 1904</i>"), 1);
        await env.CopyAsync(env.Files(@"C:\Privat\Steuer\Lohnzettel.pdf"), 2);
        await env.Module.ExecuteQuickCommandAsync("cb", "Kontonummer", default);
        await env.Module.ChooseAsync(default);

        Assert.Contains(env.Log.Lines, l => l.Contains("Stored text", StringComparison.Ordinal));
        foreach (var line in env.Log.Lines)
        {
            Assert.DoesNotContain("AT61", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Kontonummer", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Lohnzettel", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Bus_reports_only_metadata_and_shares_contents_only_when_allowed()
    {
        await using var env = new ClipEnv();
        var consumer = new ConsumerPlugin();
        env.Registry.Register(consumer);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(consumer.Id));
        env.Grant(consumer.Id, ClipboardModule.ModuleId, BusAccess.All);
        var events = new List<BusEvent>();
        using var subscription = consumer.Bus!.SubscribeEvent(ClipboardModule.ChangedEvent, e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        });
        Assert.True(subscription.Success);

        await env.CopyAsync(env.Text("Geheimnis 42"), 1);
        Assert.True(await Wait.UntilAsync(() => events.Count == 1));
        var payload = events[0].Payload!.ToJsonString();
        Assert.Contains("\"kind\":\"text\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"size\":24", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Geheimnis", payload, StringComparison.Ordinal);

        // Other plugins may put text on the clipboard – it becomes a normal entry.
        var copied = await consumer.Bus.InvokeAsync(ClipboardModule.CopyTextAction, new JsonObject { ["text"] = "vom Plugin" });
        Assert.True(copied.Success);
        Assert.Equal("vom Plugin", env.Writer.Writes[^1].Content.Text);
        Assert.Contains(env.Store.Entries, e => e.Preview == "vom Plugin" && e.AppProcess == consumer.Id);

        // Contents: not even with the bus permission, unless the user allows it.
        var denied = await consumer.Bus.InvokeAsync(ClipboardModule.RecentAction, new JsonObject { ["count"] = 5 });
        Assert.True(denied.Success);
        Assert.False(denied.Value!["shared"]!.GetValue<bool>());
        Assert.DoesNotContain("Geheimnis", denied.Value.ToJsonString(), StringComparison.Ordinal);

        await env.SettingsAsync((ClipOptions.ShareKey, "true"));
        var shared = await consumer.Bus.InvokeAsync(ClipboardModule.RecentAction, new JsonObject { ["count"] = 5 });
        Assert.Equal(["vom Plugin", "Geheimnis 42"], shared.Value!["items"]!.AsArray().Select(i => i!["text"]!.GetValue<string>()));

        // Without a permission the bus says no anyway.
        env.Grants.Clear();
        Assert.False((await consumer.Bus.InvokeAsync(ClipboardModule.RecentAction)).Success);
    }

    [Fact]
    public async Task Memory_and_time_with_100_entries_of_which_10_are_images()
    {
        await using var env = await new ClipEnv().StartAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            env.Time.Advance(TimeSpan.FromSeconds(5));
            var change = i % 10 == 0
                ? env.Image(Png.Make(1280, 720, seed: i, noise: i % 20 == 0), 1280, 720)
                : env.Text($"Absatz {i}: " + string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet. ", 20)));
            await env.CopyAsync(change, i + 1);
        }

        watch.Stop();
        Assert.Equal(100, env.Store.Entries.Count);
        Assert.Equal(10, env.Store.Entries.Count(e => e.Kind == ClipKind.Image));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        // Only the index stays in memory (previews, search text, small preview pictures) – the content is on disk.
        var held = after - before;
        Assert.True(held < 8L * 1024 * 1024, $"held {held / 1024} KB");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed}");
        Console.WriteLine($"100 entries (10 images): {held / 1024} KB held, {env.Store.TotalBytes / 1024} KB on disk, {watch.ElapsedMilliseconds} ms");
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
