// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.Modules.Clipboard.Tests;

/// <summary>The choice popup, pasting, Win+V, quick command, tile and loading (P2-6).</summary>
public sealed class PickerTests
{
    private static async Task<ClipEnv> WithThreeAsync()
    {
        var env = await new ClipEnv().StartAsync();
        await env.CopyAsync(env.Text("Erster Text", html: "<b>Erster</b> Text"), 1);
        env.Time.Advance(TimeSpan.FromMinutes(2));
        await env.CopyAsync(env.Image(Png.Make(30, 20), 30, 20), 2);
        env.Time.Advance(TimeSpan.FromMinutes(2));
        await env.CopyAsync(env.Files(@"C:\Daten\Liste.xlsx"), 3);
        return env;
    }

    [Fact]
    public async Task Popup_lists_kept_entries_first_with_previews_and_offers_the_keys()
    {
        await using var env = await WithThreeAsync();
        env.Store.SetKept(env.Store.Entries.Single(e => e.Kind == ClipKind.Text).Id, true);
        env.Time.Advance(TimeSpan.FromMinutes(1));
        await env.Module.ChooseAsync(default);

        var request = Assert.Single(env.Popup.Requests);
        Assert.Equal("Zwischenablage-Verlauf", request.Title);
        Assert.Equal(ChoicePlacement.Caret, request.Placement);
        Assert.Equal(["Erster Text", "Liste.xlsx", "Bild 30 × 20"], request.Items.Select(i => i.Title));
        Assert.Equal(["Gemerkt", "Zuletzt", "Zuletzt"], request.Items.Select(i => i.Group));
        Assert.True(request.Items[0].IsMarked);
        Assert.NotNull(request.Items[2].ImagePng); // picture preview
        Assert.Equal("vor 5 min · aus Microsoft Word · 1 KB", request.Items[0].Detail);
        Assert.Contains("Liste.xlsx", request.Items[1].SearchText, StringComparison.Ordinal);

        Assert.Equal(
            [("paste", null), ("plain", "Shift+Enter"), ("copy", "Ctrl+C"), ("keep", "Ctrl+M"), ("pin", "Ctrl+P"), ("delete", "Delete")],
            request.Actions.Select(a => (a.Id, a.Shortcut)));
        Assert.Equal(["keep", "delete"], request.Actions.Where(a => a.KeepOpen).Select(a => a.Id));
        Assert.Null(request.Hint);
    }

    [Fact]
    public async Task Enter_pastes_into_the_previous_window_and_moves_the_entry_up()
    {
        await using var env = await WithThreeAsync();
        env.Popup.Answer = (2, "paste"); // the oldest: the text with HTML
        env.Time.Advance(TimeSpan.FromMinutes(1));
        var choice = await env.Module.ChooseAsync(default);

        var (module, content, target) = Assert.Single(env.Paste.Pastes);
        Assert.Equal(ClipboardModule.ModuleId, module);
        Assert.Equal("Erster Text", content.Text);
        Assert.Equal("<b>Erster</b> Text", content.Html); // formatting goes along
        Assert.Equal(env.Popup.Target, target);
        Assert.Equal(choice!.ItemId, env.Store.Entries[0].Id);

        // The paste comes back from the watcher as our own write: no new entry.
        await env.CopyAsync(env.Text("Erster Text", writtenBy: ClipboardModule.ModuleId));
        Assert.Equal(3, env.Store.Entries.Count);
    }

    [Fact]
    public async Task Shift_enter_pastes_plain_text_ctrl_c_only_copies_and_ctrl_p_pins()
    {
        await using var env = await WithThreeAsync();
        env.Popup.Answer = (2, "plain");
        await env.Module.ChooseAsync(default);
        var plain = env.Paste.Pastes[^1].Content;
        Assert.Equal("Erster Text", plain.Text);
        Assert.Null(plain.Html);

        env.Popup.Answer = (1, "plain"); // files as plain text = the paths (the text moved to the top)
        await env.Module.ChooseAsync(default);
        Assert.Equal(@"C:\Daten\Liste.xlsx", env.Paste.Pastes[^1].Content.Text);

        env.Popup.Answer = (2, "copy"); // the image is last now
        await env.Module.ChooseAsync(default);
        Assert.Equal(2, env.Paste.Pastes.Count);
        Assert.NotNull(env.Writer.Writes[^1].Content.ImagePng);

        env.Popup.Answer = (0, "pin"); // the image is on top now
        await env.Module.ChooseAsync(default);
        var pin = Assert.Single(env.Pins.InfosOf(ClipboardModule.ModuleId));
        Assert.Equal(PinKind.Image, pin.Kind);

        env.Popup.Answer = (2, "pin"); // text → text pin
        await env.Module.ChooseAsync(default);
        Assert.Contains(env.Pins.InfosOf(ClipboardModule.ModuleId), p => p.Kind == PinKind.Text);
    }

    [Fact]
    public async Task Ctrl_m_keeps_and_delete_removes_while_the_popup_stays_open()
    {
        await using var env = await WithThreeAsync();
        env.Popup.Script.Add((2, "keep"));   // the text becomes kept → moves to the top group
        env.Popup.Script.Add((0, "keep"));   // and back
        env.Popup.Script.Add((0, "delete")); // the files go
        await env.Module.ChooseAsync(default);

        Assert.Equal("Gemerkt", env.Popup.Updates[0]!.Items[0].Group);
        Assert.Equal("Erster Text", env.Popup.Updates[0]!.Items[0].Title);
        Assert.Equal(env.Popup.Updates[0]!.Items[0].Id, env.Popup.Updates[0]!.SelectId);
        Assert.All(env.Popup.Updates[1]!.Items, i => Assert.Null(i.Group));
        Assert.Equal(2, env.Popup.Updates[2]!.Items.Count);
        Assert.DoesNotContain(env.Store.Entries, e => e.Kind == ClipKind.Files);
        Assert.Empty(env.Paste.Pastes); // closed with Esc
    }

    [Fact]
    public async Task Paused_history_says_so_in_the_popup_and_the_tile()
    {
        await using var env = await WithThreeAsync();
        env.Module.SetPaused(true, "test");
        await env.Module.ChooseAsync(default);
        Assert.Equal("Verlauf pausiert – neue Kopien werden nicht gespeichert.", env.Popup.Requests[0].Hint);

        var tile = env.Module.TileTypes[0].CreateTile(new TileConfig { Module = ClipboardModule.ModuleId, Type = HistoryTileType.TypeId, Size = TileSize.Wide });
        Assert.Equal("Pausiert", tile.StateText);
        Assert.Equal("Weiter", tile.FirstElement<IconGridElement>()!.Items[^1].Label);
    }

    [Fact]
    public async Task Win_v_is_taken_over_while_the_setting_is_on_and_given_back_when_off()
    {
        await using var env = await new ClipEnv().StartAsync();
        var held = Assert.Single(env.Hook.Taken);
        Assert.Equal("Win+V", held.Hotkey);
        Assert.Equal("Win+V ist übernommen und öffnet diesen Verlauf.", env.Module.HookText());

        held.Press(); // Win+V → the popup
        Assert.True(await Wait.UntilAsync(() => env.Popup.Requests.Count == 1));

        await env.SettingsAsync((ClipOptions.WinVKey, "false"));
        Assert.True(held.Disposed);
        Assert.Equal("Win+V gehört Windows. Diesen Verlauf öffnest du mit Win + Alt + V.", env.Module.HookText());

        // The replacement hotkey is a normal hotkey field – always on.
        var hotkey = env.Module.SettingFields.Single(f => f.Key == ClipOptions.HotkeyKey);
        Assert.Equal((SettingFieldKind.Hotkey, "Win+Alt+V"), (hotkey.Kind, hotkey.DefaultValue));
        await env.Module.OnHotkeyAsync(ClipOptions.HotkeyKey, default);
        Assert.True(await Wait.UntilAsync(() => env.Popup.Requests.Count == 2));

        await env.SettingsAsync((ClipOptions.WinVKey, "true"));
        Assert.Equal(2, env.Hook.Taken.Count);
        Assert.False(env.Hook.Taken[1].Disposed);

        // Stopping the plugin gives Win+V back.
        await env.Registry.StopModuleAsync(ClipboardModule.ModuleId);
        Assert.True(env.Hook.Taken[1].Disposed);
    }

    [Fact]
    public async Task A_failed_hook_points_to_the_replacement_hotkey()
    {
        await using var env = new ClipEnv();
        env.Hook.StartState = HotkeyTakeoverState.Retrying;
        await env.StartAsync();
        Assert.Equal("Win+V wird übernommen …", env.Module.HookText());
        env.Hook.Taken[0].Fail("Windows hat den Tastaturhook nicht erlaubt.");
        var info = env.Module.SettingFields.Single(f => f.Key == ClipOptions.HookInfoKey);
        Assert.Equal(SettingFieldKind.Info, info.Kind);
        Assert.Equal("Win+V ließ sich nicht übernehmen: Windows hat den Tastaturhook nicht erlaubt. Nimm Win + Alt + V.", info.Description);
    }

    [Fact]
    public async Task Without_the_core_services_the_plugin_says_why()
    {
        await using var env = new ClipEnv();
        env.Registry.Host.ClipboardWatcher = null;
        env.Registry.Host.HotkeyTakeover = null;
        await env.StartAsync();
        Assert.Empty(env.Watcher.Active);
        Assert.Empty(env.Hook.Taken);
    }

    [Fact]
    public async Task Quick_command_cb_searches_copies_pauses_and_clears()
    {
        await using var env = await WithThreeAsync();
        var hits = await env.Module.SearchAsync("cb erster", 8, default);
        var hit = Assert.Single(hits);
        Assert.Equal("Erster Text", hit.Name);
        await env.Module.ExecuteDefaultActionAsync(hit.Id, default);
        Assert.Equal("Erster Text", env.Writer.Writes[^1].Content.Text);

        var copied = await env.Module.ExecuteQuickCommandAsync("cb", "liste", default);
        Assert.Equal("Kopiert: Liste.xlsx", copied.Message);
        Assert.Equal([@"C:\Daten\Liste.xlsx"], env.Writer.Writes[^1].Content.Files);
        Assert.False((await env.Module.ExecuteQuickCommandAsync("cb", "gibtsnicht", default)).Success);

        Assert.Empty(await env.Module.SearchAsync("cb pause", 8, default)); // the command itself, no entries
        Assert.Single(await env.Module.SearchAsync("zwischenab", 8, default)); // "open the history"

        env.Store.SetKept(env.Store.Entries[^1].Id, true);
        var cleared = await env.Module.ExecuteQuickCommandAsync("cb", "leeren", default);
        Assert.Equal("Verlauf geleert: 2 entfernt, Gemerkte bleiben", cleared.Message);
        Assert.Single(env.Store.Entries);

        await env.Module.ExecuteQuickCommandAsync("cb", "", default); // opens the popup (after the quick search closed)
        Assert.True(await Wait.UntilAsync(() =>
        {
            env.Time.Advance(TimeSpan.FromMilliseconds(50)); // the delay starts on the pool – keep the clock going
            return env.Popup.Requests.Count == 1;
        }));
    }

    [Fact]
    public async Task Tile_shows_the_latest_entries_and_a_tap_copies_one()
    {
        await using var env = await WithThreeAsync();
        var type = env.Module.TileTypes[0];
        var wide = type.CreateTile(new TileConfig { Module = ClipboardModule.ModuleId, Type = HistoryTileType.TypeId, Size = TileSize.Wide });
        var grid = wide.FirstElement<IconGridElement>()!;
        Assert.Equal("3 Einträge", wide.StateText);
        Assert.Equal(["Liste.xlsx", "Bild", "Erster Text", "Alle", "Pause"], grid.Items.Select(i => i.Label));
        Assert.StartsWith(ClipboardModule.ThumbPrefix, grid.Items[1].ImageUrl, StringComparison.Ordinal);
        Assert.NotNull(await env.Module.LoadImageAsync(grid.Items[1].ImageUrl!, default));

        await wide.Handler!.HandleAsync(wide, new TileInteraction(grid.Id, InteractionKind.Press, Text: grid.Items[2].Id), default);
        Assert.Equal("Erster Text", env.Writer.Writes[^1].Content.Text);
        Assert.Equal("Erster Text", wide.FirstElement<IconGridElement>()!.Items[0].Label); // moved up

        await wide.Handler.HandleAsync(wide, new TileInteraction(grid.Id, InteractionKind.Press, Text: "pause"), default);
        Assert.True(env.Module.IsPaused);
        await wide.Handler.HandleAsync(wide, new TileInteraction(grid.Id, InteractionKind.Press, Text: "all"), default);
        Assert.True(await Wait.UntilAsync(() => env.Popup.Requests.Count == 1));

        var large = type.CreateTile(new TileConfig { Module = ClipboardModule.ModuleId, Type = HistoryTileType.TypeId, Size = TileSize.Large });
        Assert.Equal(4, large.FirstElement<IconGridElement>()!.Items.Count); // 3 entries (of up to 5) + All
        Assert.Equal("Weiter", large.FirstElement<ButtonRowElement>()!.Buttons[0].Label);
    }

    [Fact]
    public async Task Settings_page_shows_what_is_stored_and_where()
    {
        await using var env = await WithThreeAsync();
        var fields = env.Module.SettingFields;
        Assert.Equal(
            [ClipOptions.WinVKey, ClipOptions.HookInfoKey, ClipOptions.HotkeyKey, ClipOptions.EntriesKey, ClipOptions.DaysKey, ClipOptions.ImagesKey, ClipOptions.ImageMbKey,
             ClipOptions.TotalMbKey, ClipOptions.ExcludedKey, ClipOptions.ShareKey, ClipOptions.PauseKey, ClipOptions.ClearKey, ClipOptions.ClearAllKey, ClipOptions.FolderKey],
            fields.Select(f => f.Key));
        var stored = fields.Single(f => f.Key == ClipOptions.FolderKey);
        Assert.StartsWith("Gespeichert: 3 Einträge (", stored.Label, StringComparison.Ordinal);
        Assert.Contains(env.Folder, stored.Description, StringComparison.Ordinal);
        Assert.Equal("Ordner öffnen", stored.ButtonText);
        Assert.Equal("100", fields.Single(f => f.Key == ClipOptions.EntriesKey).DefaultValue);
        Assert.Equal("7", fields.Single(f => f.Key == ClipOptions.DaysKey).DefaultValue);
        var excluded = ModuleSettingValues.ParseList(fields.Single(f => f.Key == ClipOptions.ExcludedKey).DefaultValue);
        Assert.Contains(excluded, e => e["app"] == "KeePass");
        Assert.Contains(excluded, e => e["app"] == "Bitwarden");
        Assert.Contains(excluded, e => e["app"] == "1Password");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Plugin_folder_loads_like_any_bundled_plugin()
    {
        var built = Path.GetDirectoryName(typeof(ClipboardModule).Assembly.Location)!;
        var root = Path.Combine(Path.GetTempPath(), "uebar-clip-load", Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "app", "plugins");
        var target = Path.Combine(bundled, "clipboard");
        try
        {
            foreach (var relative in new[] { "plugin.json", "UeBar.Modules.Clipboard.dll", "UeBar.Modules.Clipboard.deps.json", Path.Combine("de", "UeBar.Modules.Clipboard.resources.dll") })
            {
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(Path.Combine(built, relative)))
                {
                    File.Copy(Path.Combine(built, relative), destination);
                }
            }

            var list = BundledPluginList.Parse(string.Join("\n", PluginHash.Files(target).Select(f => $"{f.Value.ToUpperInvariant()}  clipboard/{f.Key}")));
            var log = new ListLogger();
            var host = new PluginHost(
                new PluginHostOptions
                {
                    BundledDirectory = bundled,
                    BundledList = list,
                    UserDirectory = Path.Combine(root, "data", "plugins"),
                    ApiVersion = CoreApiVersion.Version,
                    LoadTimeout = TimeSpan.FromSeconds(15),
                },
                new InMemoryModuleStateStore(),
                new PluginTrustStore(Path.Combine(root, "data", "plugin-trust.json"), log),
                log);

            Assert.Equal(PluginState.Pending, Assert.Single(host.Discover()).State);
            await host.LoadAsync();
            var plugin = Assert.Single(host.Plugins);
            Assert.True(plugin.State == PluginState.Loaded, $"{plugin.State}: {plugin.Problem}");
            Assert.Equal(ClipboardModule.ModuleId, plugin.Module!.Id);
            Assert.Equal("Zwischenablage", plugin.Manifest!.DisplayName(CultureInfo.GetCultureInfo("de-DE")));
            Assert.Equal(new Version(1, 8), plugin.Manifest.SdkVersion);
            Assert.Contains("cb leeren", ((IQuickCommandProvider)plugin.Module).QuickCommands[0].Description, StringComparison.Ordinal); // own satellite assembly
            Assert.IsAssignableFrom<ITrayMenuProvider>(plugin.Module);
            Assert.IsAssignableFrom<IHotkeyHandler>(plugin.Module);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
