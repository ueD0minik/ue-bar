// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO.Compression;
using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Modules.QuickAccess.Platform;
using UeBar.Sdk;

namespace UeBar.Modules.QuickAccess.Tests;

public sealed class TargetTests
{
    [Theory]
    [InlineData(@"C:\Users\demo\Documents\Brief.dotx", @"%USERPROFILE%\Documents\Brief.dotx")]
    [InlineData(@"c:\users\DEMO\Schule", @"%USERPROFILE%\Schule")]
    [InlineData(@"C:\Users\demo", "%USERPROFILE%")]
    [InlineData(@"C:\Users\demo2\x.txt", @"C:\Users\demo2\x.txt")] // only the folder itself, not a longer name
    [InlineData(@"D:\Daten\x.txt", @"D:\Daten\x.txt")]
    public void User_folder_paths_are_stored_with_the_variable(string path, string stored)
    {
        Assert.Equal(stored, QuickPaths.Collapse(path, FakeShell.Profile));
    }

    [Fact]
    public void Stored_paths_fit_another_pc()
    {
        var stored = QuickPaths.Collapse(@"C:\Users\demo\Schule\Mathe", FakeShell.Profile);
        Assert.Equal(@"C:\Users\other\Schule\Mathe", QuickPaths.Expand(stored, @"C:\Users\other"));
        Assert.Equal(@"D:\x", QuickPaths.Expand(@"D:\x", @"C:\Users\other"));
    }

    [Theory]
    [InlineData("app:Contoso.Notes_abc123!App", (int)TargetKind.App, "Contoso.Notes_abc123!App", false)]
    [InlineData("app!:Example.Writer.EXE.15", (int)TargetKind.App, "Example.Writer.EXE.15", true)]
    [InlineData(@"file:%USERPROFILE%\a.docx", (int)TargetKind.File, @"%USERPROFILE%\a.docx", false)]
    [InlineData(@"folder:D:\Schule", (int)TargetKind.Folder, @"D:\Schule", false)]
    [InlineData("url:https://example.org/a?b=c", (int)TargetKind.Url, "https://example.org/a?b=c", false)]
    [InlineData("entry:ab12cd34", (int)TargetKind.Entry, "ab12cd34", false)]
    public void Targets_survive_the_layout(string text, int kind, string value, bool admin)
    {
        var target = QuickTarget.Parse(text);
        Assert.Equal(new QuickTarget((TargetKind)kind, value, admin), target);
        Assert.Equal(text, target!.ToString());
    }

    [Theory]
    [InlineData("url:file:///C:/Windows/System32/cmd.exe")]
    [InlineData("url:javascript:alert(1)")]
    [InlineData("cmd:format c:")]
    [InlineData("folder:")]
    [InlineData("nothing")]
    public void Targets_a_layout_must_not_carry_are_refused(string text)
    {
        Assert.Null(QuickTarget.Parse(text));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "WindowsCalculator")]
    [InlineData("Microsoft.Office.WINWORD.EXE.15", "WINWORD")]
    [InlineData("MSEdge", "MSEdge")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Fabrikam\Paint\paint.exe", "paint")]
    public void App_ids_give_a_readable_name_until_the_start_menu_is_read(string id, string name)
    {
        Assert.Equal(name, QuickNames.AppName(id));
    }

    [Fact]
    public void Only_apps_may_ask_for_administrator_rights_in_a_tile()
    {
        Assert.False(QuickTarget.Parse(@"file!:C:\x.bat")!.Admin);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\x.exe"" -a -b", @"C:\Program Files\x.exe", "-a -b")]
    [InlineData(@"wt.exe -p Ubuntu", "wt.exe", "-p Ubuntu")]
    [InlineData(@"C:\Tools\backup.ps1", @"C:\Tools\backup.ps1", null)]
    public void Command_lines_are_split_into_program_and_arguments(string line, string program, string? arguments)
    {
        Assert.Equal((program, arguments), QuickNames.SplitCommand(line));
    }

    [Fact]
    public void Icons_become_valid_png_and_soft_edges_are_divided_back()
    {
        // 2×1: an opaque red pixel and a half transparent, premultiplied blue one.
        byte[] bgra = [0, 0, 255, 255, 64, 0, 0, 128];
        var png = Png.FromBgra(bgra, 2, 1);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        var idat = IndexOf(png, "IDAT"u8.ToArray());
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat - 4));
        using var zlib = new ZLibStream(new MemoryStream(png, idat + 4, length), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        Assert.Equal([0, 255, 0, 0, 255, 0, 0, 127, 128], raw.ToArray()); // filter byte, RGBA, RGBA (64*255/128 = 127)
    }

    private static int IndexOf(byte[] data, byte[] part)
    {
        for (var i = 0; i <= data.Length - part.Length; i++)
        {
            if (data.AsSpan(i, part.Length).SequenceEqual(part))
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed class QuickAccessTests
{
    [Fact]
    public async Task Every_kind_of_target_starts_through_the_shell()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Files.Add(@"C:\Users\demo\Documents\Plan.xlsx");
        env.Shell.Folders.Add(@"C:\Users\demo\Schule");
        await env.Module.AppsAsync(default);

        foreach (var target in new[]
        {
            "app:Contoso.Notes_abc123!App",
            @"file:%USERPROFILE%\Documents\Plan.xlsx",
            @"folder:%USERPROFILE%\Schule",
            "url:https://example.org/",
        })
        {
            await env.TapAsync(await env.TileAsync(QuickEnv.Item(target)));
        }

        Assert.Equal(
        [
            new LaunchRequest(@"shell:AppsFolder\Contoso.Notes_abc123!App"),
            new LaunchRequest(@"C:\Users\demo\Documents\Plan.xlsx"),
            new LaunchRequest(@"C:\Users\demo\Schule"),
            new LaunchRequest("https://example.org/"),
        ], env.Shell.Launches);
    }

    [Fact]
    public async Task App_as_administrator_asks_windows_and_a_no_is_shown_on_the_tile()
    {
        await using var env = await new QuickEnv().StartAsync();
        await env.Module.AppsAsync(default);
        var tile = await env.TileAsync(QuickEnv.Item("app!:Example.Writer.EXE.15"));
        Assert.Equal("Writer", tile.Title);
        Assert.Equal("Als Administrator", tile.StateText);

        env.Shell.DeclineAdmin = true;
        await env.TapAsync(tile);

        Assert.Equal(new LaunchRequest(@"shell:AppsFolder\Example.Writer.EXE.15", Verb: "runas"), Assert.Single(env.Shell.Launches));
        Assert.Equal("Abgebrochen", tile.Alert);
    }

    [Fact]
    public async Task An_office_template_opens_a_new_document_and_falls_back_to_opening_it()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Files.Add(@"C:\Users\demo\Vorlagen\Brief.dotx");
        var tile = await env.TileAsync(QuickEnv.Item(@"file:%USERPROFILE%\Vorlagen\Brief.dotx"));
        Assert.Equal("Brief", tile.Title);
        Assert.Equal("Vorlage – neues Dokument", tile.StateText);

        await env.TapAsync(tile);
        Assert.Equal(new LaunchRequest(@"C:\Users\demo\Vorlagen\Brief.dotx", Verb: "new"), Assert.Single(env.Shell.Launches));

        // Without an app that knows "new": open the template instead of failing.
        env.Shell.MissingVerbs.Add("new");
        await env.TapAsync(tile);
        Assert.Equal(new LaunchRequest(@"C:\Users\demo\Vorlagen\Brief.dotx"), env.Shell.Launches[^1]);
        Assert.Null(tile.Alert);
    }

    [Fact]
    public async Task Missing_targets_show_not_found_keep_the_tile_and_start_nothing()
    {
        await using var env = await new QuickEnv().StartAsync();
        await env.Module.AppsAsync(default);
        var file = await env.TileAsync(QuickEnv.Item(@"file:%USERPROFILE%\weg.docx"));
        var app = await env.TileAsync(QuickEnv.Item("app:Removed.App_x!App"));

        Assert.All([file, app], t =>
        {
            Assert.Equal("Nicht gefunden", t.StateText);
            Assert.True(t.IsUnavailable);
            Assert.Null(t.ImageUrl);
        });
        await env.TapAsync(file);
        Assert.Empty(env.Shell.Launches);

        // The file comes back (e.g. the USB stick is plugged in again): opening the panel looks again.
        env.Shell.Files.Add(@"C:\Users\demo\weg.docx");
        env.Module.OnVisibilityChanged(new ModuleVisibility(true, [], []));
        // The refresh runs on the plugin's thread and sets one property after the other: wait for all of them.
        Assert.True(await Wait.UntilAsync(() => !file.IsUnavailable && file.StateText == "Datei" && file.ImageUrl == @"icon:file:%USERPROFILE%\weg.docx"));
    }

    [Fact]
    public async Task Entries_of_the_settings_start_including_commands()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Files.Add(@"C:\Users\demo\Tools\backup.ps1");
        await env.EntriesAsync(
            QuickEnv.Entry("e1", "Backup", "command", text: @"%USERPROFILE%\Tools\backup.ps1"),
            QuickEnv.Entry("e2", "Terminal", "command", text: "wt.exe -p Ubuntu", admin: true),
            QuickEnv.Entry("e3", "Notizen", "app", text: "Notizen"),
            QuickEnv.Entry("e4", "Schlecht", "url", text: "javascript:alert(1)")); // not taken

        Assert.Equal(["e1", "e2", "e3"], env.Module.Entries.Select(e => e.Id));
        await env.Module.AppsAsync(default);
        foreach (var id in new[] { "e1", "e2", "e3" })
        {
            await env.Module.ExecuteDefaultActionAsync("entry:" + id, default);
        }

        Assert.Equal(
        [
            new LaunchRequest("powershell.exe", @"-NoProfile -File ""C:\Users\demo\Tools\backup.ps1"""),
            new LaunchRequest("wt.exe", "-p Ubuntu", "runas"),
            new LaunchRequest(@"shell:AppsFolder\Contoso.Notes_abc123!App"), // the entry names the app
        ], env.Shell.Launches);
    }

    [Fact]
    public async Task A_command_tile_only_points_to_the_entry_on_this_device()
    {
        await using var env = await new QuickEnv().StartAsync();
        await env.EntriesAsync(
            QuickEnv.Entry("c1", "Backup", "command", text: "robocopy a b"),
            QuickEnv.Entry("f1", "Mathe", "folder", folder: @"C:\Users\demo\Schule\Mathe"));

        var command = env.Module.SuggestTile(new EntityInfo("entry:c1", "Backup"))!;
        var folder = env.Module.SuggestTile(new EntityInfo("entry:f1", "Mathe"))!;

        Assert.Equal("entry:c1", command.GetEntity("main")); // the command line never goes into the synced layout
        Assert.Equal(@"folder:%USERPROFILE%\Schule\Mathe", folder.GetEntity("main")); // folders travel with the layout
        Assert.Equal("Mathe", folder.Label);

        // Another PC without the entry: "not found", nothing runs.
        await env.EntriesAsync();
        var tile = await env.TileAsync(QuickEnv.Item("entry:c1"));
        Assert.Equal("Nicht gefunden", tile.StateText);
        await env.TapAsync(tile);
        Assert.Empty(env.Shell.Launches);
    }

    [Fact]
    public async Task Renaming_an_entry_applies_live_without_a_restart()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Files.Add(@"C:\x.bat");
        await env.EntriesAsync(QuickEnv.Entry("c1", "Alt", "command", text: @"C:\x.bat"));
        var tile = await env.TileAsync(QuickEnv.Item("entry:c1"));
        Assert.Equal("Alt", tile.Title);

        await env.EntriesAsync(QuickEnv.Entry("c1", "Neu", "command", text: @"C:\x.bat"));

        Assert.Equal("Neu", tile.Title);
        Assert.DoesNotContain(env.Log.Lines, l => l.Contains("Manual restart", StringComparison.Ordinal));
        Assert.Contains(env.Log.Lines, l => l.Contains("Settings applied without a restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_finds_entries_and_start_menu_apps_and_the_collection()
    {
        await using var env = await new QuickEnv().StartAsync();
        await env.EntriesAsync(QuickEnv.Entry("f1", "Notenblätter", "folder", folder: @"D:\Noten"));

        var hits = await env.Module.SearchAsync("not", 10, default);
        Assert.Equal(["Notenblätter", "Notizen"], hits.Select(h => h.Name)); // entries before apps
        Assert.Equal("app:Contoso.Notes_abc123!App", hits[1].Id);

        var paint = Assert.Single(await env.Module.SearchAsync("paint", 10, default));
        Assert.Equal("Fabrikam Paint", paint.Name);
        Assert.Equal(QuickAccessModule.GroupEntity, Assert.Single(await env.Module.SearchAsync("Samm", 10, default)).Id);
        Assert.Equal(1, env.Shell.AppReads); // read once, not on every keystroke

        env.Time.Advance(TimeSpan.FromMinutes(11));
        await env.Module.SearchAsync("x", 10, default);
        Assert.Equal(2, env.Shell.AppReads); // after 10 minutes again (new apps installed)
    }

    [Fact]
    public async Task Quick_search_enter_starts_the_app()
    {
        await using var env = await new QuickEnv().StartAsync();
        var hit = (await env.Module.SearchAsync("Writer", 5, default))[0];

        var result = await env.Registry.InvokeAsync(QuickAccessModule.ModuleId, "Enter", async (m, ct) =>
        {
            await ((IEntitySource)m).ExecuteDefaultActionAsync(hit.Id, ct);
            return true;
        });

        Assert.True(result.Succeeded);
        Assert.Equal(@"shell:AppsFolder\Example.Writer.EXE.15", Assert.Single(env.Shell.Launches).File);
    }

    [Fact]
    public async Task Collection_tile_shows_up_to_six_entries_and_opens_the_tapped_one()
    {
        await using var env = await new QuickEnv().StartAsync();
        foreach (var subject in new[] { "Mathe", "Deutsch", "Englisch" })
        {
            env.Shell.Folders.Add($@"C:\Users\demo\Schule\{subject}");
        }

        var config = new TileConfig
        {
            Id = "schule",
            Module = QuickAccessModule.ModuleId,
            Type = GroupTileType.TypeId,
            Size = TileSize.Large,
            Label = "Schule",
            Entities = new Dictionary<string, string>
            {
                ["item1"] = @"folder:%USERPROFILE%\Schule\Mathe",
                ["item2"] = @"folder:%USERPROFILE%\Schule\Deutsch",
                ["item3"] = @"folder:%USERPROFILE%\Schule\Englisch",
                ["item5"] = @"folder:%USERPROFILE%\Schule\Latein", // missing
            },
        };
        var tile = await env.TileAsync(config);

        var grid = tile.FirstElement<IconGridElement>()!;
        Assert.Equal(["Mathe", "Deutsch", "Englisch", "Latein"], grid.Items.Select(i => i.Label));
        Assert.Equal([true, true, true, false], grid.Items.Select(i => i.IsEnabled));
        Assert.Equal("Nicht gefunden", grid.Items[3].StateText);
        Assert.Equal(@"icon:folder:%USERPROFILE%\Schule\Deutsch", grid.Items[1].ImageUrl);
        Assert.Equal("4 Einträge", tile.StateText);

        await env.Registry.InteractAsync(tile, new TileInteraction("items", InteractionKind.Press, Text: "item2"));
        Assert.Equal(@"C:\Users\demo\Schule\Deutsch", Assert.Single(env.Shell.Launches).File);

        // Its slot candidates carry the target itself (work on every PC of the layout).
        await env.EntriesAsync(QuickEnv.Entry("f1", "Schulordner", "folder", folder: @"C:\Users\demo\Schule"));
        var candidates = await env.Module.SuggestForSlotAsync(config, "item4", "Schul", 5, default);
        Assert.Equal(@"folder:%USERPROFILE%\Schule", Assert.Single(candidates).Id);
    }

    [Fact]
    public async Task Dropped_files_folders_shortcuts_and_links_become_tiles()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Folders.Add(@"D:\Projekte");
        env.Shell.Shortcuts[@"C:\Users\demo\Desktop\Projekte.lnk"] = new ShortcutInfo(@"D:\Projekte", null);
        env.Shell.Shortcuts[@"C:\Users\demo\Desktop\Spiel.lnk"] = new ShortcutInfo(@"D:\Games\game.exe", "-fullscreen");
        env.Shell.UrlShortcuts[@"C:\Users\demo\Desktop\Wiki.url"] = "https://example.org/wiki";

        async Task<TileConfig?> DropAsync(DropKind kind, string value, string? title = null) =>
            await env.Registry.CreateTileForDropAsync(new DroppedItem(kind, value, title));

        var file = await DropAsync(DropKind.File, @"C:\Users\demo\Documents\Referat.pptx");
        Assert.Equal((QuickAccessModule.ModuleId, ItemTileType.TypeId, @"file:%USERPROFILE%\Documents\Referat.pptx"), (file!.Module, file.Type, file.GetEntity("main")));
        Assert.Null(file.Label);

        Assert.Equal(@"folder:%USERPROFILE%\Schule", (await DropAsync(DropKind.Folder, @"C:\Users\demo\Schule"))!.GetEntity("main"));

        var toFolder = await DropAsync(DropKind.File, @"C:\Users\demo\Desktop\Projekte.lnk");
        Assert.Equal((@"folder:D:\Projekte", "Projekte"), (toFolder!.GetEntity("main"), toFolder.Label)); // a shortcut to a folder is the folder

        var game = await DropAsync(DropKind.File, @"C:\Users\demo\Desktop\Spiel.lnk");
        Assert.Equal((@"file:%USERPROFILE%\Desktop\Spiel.lnk", "Spiel"), (game!.GetEntity("main"), game.Label)); // keeps its arguments

        var wiki = await DropAsync(DropKind.File, @"C:\Users\demo\Desktop\Wiki.url");
        Assert.Equal(("url:https://example.org/wiki", "Wiki"), (wiki!.GetEntity("main"), wiki.Label));

        var link = await DropAsync(DropKind.Link, "https://example.org/news", "Nachrichten");
        Assert.Equal(("url:https://example.org/news", "Nachrichten"), (link!.GetEntity("main"), link.Label));

        Assert.Null(await DropAsync(DropKind.Text, "irgendein Text"));
        Assert.Null(await DropAsync(DropKind.Link, "javascript:alert(1)"));
    }

    [Fact]
    public async Task Real_icons_come_from_the_shell_and_are_kept()
    {
        await using var env = await new QuickEnv().StartAsync();
        env.Shell.Folders.Add(@"C:\Users\demo\Schule");
        var id = QuickAccessModule.IconId(QuickTarget.Parse(@"folder:%USERPROFILE%\Schule")!);

        var first = await env.Module.LoadImageAsync(id, default);
        var again = await env.Module.LoadImageAsync(id, default);

        Assert.Equal("image/png", first!.ContentType);
        Assert.Equal(QuickAccessModule.IconSize, first.Data[^1]); // asked in the right size
        Assert.Same(first.Data, again!.Data);
        Assert.Equal(1, env.Shell.IconReads);
        Assert.Null(await env.Module.LoadImageAsync("icon:url:https://example.org/", default)); // web pages keep the globe
        Assert.Null(await env.Module.LoadImageAsync("cover:x", default));
    }

    [Fact]
    public async Task Other_plugins_open_entries_only_with_permission()
    {
        await using var env = new QuickEnv();
        var consumer = new ConsumerPlugin();
        env.Registry.Register(consumer);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(consumer.Id));
        env.Shell.Folders.Add(@"D:\Noten");
        await env.EntriesAsync(QuickEnv.Entry("f1", "Noten", "folder", folder: @"D:\Noten"));
        var args = new JsonObject { ["id"] = "f1" };

        Assert.Equal(BusErrorKind.Denied, (await consumer.Bus!.InvokeAsync("quickaccess.open", args)).Error);
        Assert.Empty(env.Shell.Launches);

        env.Grants.Add(new BusGrant { Consumer = "consumer", Provider = QuickAccessModule.ModuleId, Access = BusAccess.Actions });
        Assert.True((await consumer.Bus.InvokeAsync("quickaccess.open", args)).Success);
        Assert.Equal(@"D:\Noten", Assert.Single(env.Shell.Launches).File);
        Assert.False((await consumer.Bus.InvokeAsync("quickaccess.open", new JsonObject { ["id"] = "nope" })).Success);
    }
}
