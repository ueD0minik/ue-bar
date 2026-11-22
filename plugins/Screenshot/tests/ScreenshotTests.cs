// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Sdk;

namespace UeBar.Modules.Screenshot.Tests;

public sealed class ScreenshotTests
{
    [Fact]
    public async Task Asking_offers_the_bar_and_pin_puts_the_image_exactly_over_the_area()
    {
        await using var env = await new ShotEnv().StartAsync();

        var done = await env.Module.CaptureAsync(null, null, default);

        Assert.Equal(ShotAction.Pin, done);
        var request = Assert.Single(env.Picker.Requests);
        Assert.Equal(ScreenAreaMode.Region, request.Mode);
        Assert.Equal(["pin", "copy", "save", "saveas", "ocr"], request.Actions.Select(a => a.Id));
        Assert.Equal(["saveas"], request.Actions.Where(a => a.Compact).Select(a => a.Id)); // the small arrow at "Save"
        var pin = Assert.Single(env.Pins.Pins);
        Assert.Equal((PinKind.Image, ScreenshotModule.ModuleId), (pin.Kind, pin.Owner));
        Assert.Equal(new UeBar.Core.Pins.PinScreenArea(1800, 200, 400, 300), pin.Source); // where it was on the screen
    }

    [Fact]
    public async Task A_default_action_skips_the_bar_and_copy_puts_the_png_on_the_clipboard()
    {
        await using var env = await new ShotEnv().StartAsync();
        await env.SettingsAsync((ShotOptions.AfterKey, "copy"), (ShotOptions.ModeKey, "window"));

        var done = await env.Module.CaptureAsync(null, null, default);

        Assert.Equal(ShotAction.Copy, done);
        var request = Assert.Single(env.Picker.Requests);
        Assert.Empty(request.Actions);
        Assert.Equal(ScreenAreaMode.Window, request.Mode);
        Assert.Same(Png.Tiny, Assert.Single(env.Clipboard.Images));
        Assert.Empty(env.Pins.Pins);
    }

    [Fact]
    public async Task Save_writes_a_dated_file_into_the_folder_and_never_overwrites()
    {
        await using var env = await new ShotEnv().StartAsync();
        var folder = Path.Combine(env.DataDirectory, "shots");
        await env.SettingsAsync((ShotOptions.AfterKey, "save"), (ShotOptions.FolderKey, folder));

        await env.Module.CaptureAsync(null, null, default);
        await env.Module.CaptureAsync(null, null, default);

        Assert.Equal(
            ["Screenshot 2026-11-20 14-03-05 (2).png", "Screenshot 2026-11-20 14-03-05.png"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(Png.Tiny, File.ReadAllBytes(Path.Combine(folder, "Screenshot 2026-11-20 14-03-05.png")));
        Assert.Contains(env.Notifications, n => n.Title == "Screenshot gespeichert");
    }

    [Fact]
    public void File_names_and_the_default_folder()
    {
        Assert.Equal("Screenshot 2026-01-02 03-04-05.png", ShotFiles.FileName(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        Assert.EndsWith(Path.Combine("Screenshots", "ue Bar"), ShotFiles.DefaultFolder, StringComparison.Ordinal);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ShotFiles.DefaultFolder, StringComparison.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\x\a.png", @"C:\x\a (2).png" };
        Assert.Equal(@"C:\x\a (3).png", ShotFiles.UniquePath(@"C:\x", "a.png", taken.Contains));
        Assert.Equal(ShotFiles.DefaultFolder, ShotOptions.From(ModuleSettingValues.Empty).Folder); // empty setting = default folder
    }

    [Fact]
    public async Task Cancelled_picking_does_nothing_and_tells_nobody()
    {
        await using var env = await new ShotEnv().StartAsync();
        var consumer = new ConsumerPlugin();
        env.Registry.Register(consumer);
        await env.Registry.StartModuleAsync(consumer.Id);
        env.Grant(consumer.Id, ScreenshotModule.ModuleId, BusAccess.Events);
        consumer.Bus!.SubscribeEvent(ScreenshotModule.TakenEvent, e => consumer.Events.Add(e));
        env.Picker.Result = null;

        Assert.Null(await env.Module.CaptureAsync(null, null, default));

        Assert.Empty(env.Pins.Pins);
        Assert.Empty(env.Clipboard.Images);
        Assert.Empty(consumer.Events);
    }

    [Fact]
    public async Task Taken_event_carries_only_metadata_and_counts_the_monitors_of_the_area()
    {
        await using var env = await new ShotEnv().StartAsync();
        var consumer = new ConsumerPlugin();
        env.Registry.Register(consumer);
        await env.Registry.StartModuleAsync(consumer.Id);
        env.Grant(consumer.Id, ScreenshotModule.ModuleId, BusAccess.Events);
        consumer.Bus!.SubscribeEvent(ScreenshotModule.TakenEvent, e => consumer.Events.Add(e));
        env.Picker.Choose = "copy";

        await env.Module.CaptureAsync(null, null, default);

        Assert.True(await Wait.UntilAsync(() => consumer.Events.Count == 1));
        var payload = (JsonObject)consumer.Events[0].Payload!;
        Assert.Equal(["mode", "action", "width", "height", "monitors"], payload.Select(p => p.Key));
        Assert.Equal(("region", "copy", 400, 300, 2), (payload["mode"]!.GetValue<string>(), payload["action"]!.GetValue<string>(), payload["width"]!.GetValue<int>(), payload["height"]!.GetValue<int>(), payload["monitors"]!.GetValue<int>()));
    }

    [Fact]
    public void The_main_monitor_of_an_area_across_two_monitors_with_different_scaling()
    {
        // 120 px on the laptop (150 %), 280 px on the OLED (100 %): the OLED holds most of it.
        var across = FakePicker.TwoMonitorResult(new ScreenRect(1800, 200, 400, 300));
        Assert.Equal("oled", across.MainMonitor!.DeviceName);
        Assert.Equal(new ScreenRect(1920, 200, 280, 300), across.Bounds.Intersect(FakePicker.Monitors[1].Bounds));

        // Above the laptop (y < 0) lies only the OLED – the desktop has negative coordinates.
        var top = FakePicker.TwoMonitorResult(new ScreenRect(2000, -200, 100, 50));
        Assert.Equal("oled", top.MainMonitor!.DeviceName);
        Assert.Equal("laptop", FakePicker.TwoMonitorResult(new ScreenRect(-50, -50, 10, 10)).MainMonitor!.DeviceName); // nowhere: the primary one
        Assert.Equal(new ScreenRect(5, 5, 6, 6), ScreenRect.FromPoints(10, 10, 5, 5));
    }

    [Fact]
    public async Task Recognized_text_is_copied_and_can_be_pinned_from_the_notification()
    {
        await using var env = await new ShotEnv().StartAsync();
        env.Picker.Choose = "ocr";

        Assert.Equal(ShotAction.Ocr, await env.Module.CaptureAsync(null, null, default));

        Assert.Equal("Hallo Welt\nZeile zwei", Assert.Single(env.Clipboard.Texts));
        Assert.Null(env.Ocr.AskedLanguage); // automatic: the Windows languages
        var note = env.Notifications.Last();
        Assert.Equal("21 Zeichen erkannt und kopiert", note.Title);
        Assert.True(note.IsActionFeedback); // shown by the app at the place of the action, also during "Do not disturb"
        Assert.Equal("Hallo Welt · Zeile zwei", note.Message);
        Assert.Equal(["pin"], note.Actions!.Select(a => a.Id));

        await env.Module.HandleNotificationActionAsync(note.Id!, "pin", default);

        var pin = Assert.Single(env.Pins.Pins);
        Assert.Equal((PinKind.Text, "Hallo Welt\nZeile zwei"), (pin.Kind, pin.Text));
    }

    [Fact]
    public async Task Recognized_text_can_become_a_text_pin_right_away()
    {
        await using var env = await new ShotEnv().StartAsync();
        await env.SettingsAsync((ShotOptions.AfterKey, "ocr"), (ShotOptions.OcrResultKey, "pin"), (ShotOptions.OcrLanguageKey, "de-DE"));

        await env.Module.CaptureAsync(null, null, default);

        Assert.Equal("de-DE", env.Ocr.AskedLanguage);
        Assert.Empty(env.Clipboard.Texts);
        Assert.Equal("Hallo Welt\nZeile zwei", Assert.Single(env.Pins.Pins).Text);
        Assert.Equal(["copy"], env.Notifications.Last().Actions!.Select(a => a.Id));
    }

    [Fact]
    public async Task Missing_recognition_language_explains_how_to_install_one()
    {
        await using var env = await new ShotEnv().StartAsync();
        env.Ocr.Outcome = new OcrOutcome("", null, OcrProblem.NoLanguage);
        env.Picker.Choose = "ocr";

        await env.Module.CaptureAsync(null, null, default);

        var note = env.Notifications.Last();
        Assert.Equal("Texterkennung nicht möglich", note.Title);
        Assert.Contains("Optische Zeichenerkennung", note.Message, StringComparison.Ordinal);
        Assert.Empty(env.Clipboard.Texts);
        Assert.Empty(env.Pins.Pins);

        await env.SettingsAsync((ShotOptions.OcrLanguageKey, "ja-JP"));
        await env.Module.CaptureAsync(null, ShotAction.Ocr, default);
        Assert.Contains("„ja-JP“", env.Notifications.Last().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_text_found_says_so()
    {
        await using var env = await new ShotEnv().StartAsync();
        env.Ocr.Outcome = new OcrOutcome("   ", "de-DE");

        await env.Module.CaptureAsync(null, ShotAction.Ocr, default);

        Assert.Equal("Kein Text gefunden", env.Notifications.Last().Title);
        Assert.Empty(env.Clipboard.Texts);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("fenster", ScreenAreaMode.Window, null)]
    [InlineData("bildschirm text", ScreenAreaMode.Screen, "ocr")]
    [InlineData("pin window", ScreenAreaMode.Window, "pin")]
    public async Task Quick_command_shot_takes_mode_and_action_in_any_order(string argument, ScreenAreaMode? mode, string? actionId)
    {
        var action = ShotOptions.ParseAction(actionId);
        await using var env = await new ShotEnv().StartAsync();
        env.Picker.Choose = "copy";

        var result = await env.Module.ExecuteQuickCommandAsync("shot", argument, default);

        Assert.True(result.Success);
        Assert.True(await Wait.UntilAsync(() =>
        {
            env.Time.Advance(TimeSpan.FromMilliseconds(50)); // the short wait until the quick search is closed
            return env.Picker.Requests.Count == 1;
        }));
        var request = env.Picker.Requests[0];
        Assert.Equal(mode ?? ScreenAreaMode.Region, request.Mode);
        Assert.Equal(action is null ? 5 : 0, request.Actions.Count); // a given action needs no bar
        Assert.True(await Wait.UntilAsync(() => action == ShotAction.Pin ? env.Pins.Count == 1 : action == ShotAction.Ocr ? env.Clipboard.Texts.Count == 1 : env.Clipboard.Images.Count == 1));
        var command = Assert.Single(env.Module.QuickCommands);
        Assert.Equal(("shot", true), (command.Prefix, command.ArgumentOptional));
    }

    [Fact]
    public async Task Hotkey_field_defaults_to_win_alt_s_and_a_press_captures()
    {
        await using var env = await new ShotEnv().StartAsync();

        var field = env.Module.SettingFields.Single(f => f.Kind == SettingFieldKind.Hotkey);
        Assert.Equal((ShotOptions.HotkeyKey, "Win+Alt+S"), (field.Key, field.DefaultValue));
        await env.Module.OnHotkeyAsync("other", default);
        await Task.Delay(50);
        Assert.Empty(env.Picker.Requests);

        await env.Module.OnHotkeyAsync(ShotOptions.HotkeyKey, default);

        Assert.True(await Wait.UntilAsync(() => env.Pins.Count == 1));
    }

    [Fact]
    public async Task Only_one_capture_at_a_time()
    {
        await using var env = await new ShotEnv().StartAsync();
        var gate = new TaskCompletionSource();
        env.Registry.Host.ScreenAreaPicker = new GatedPicker(gate.Task);

        Assert.True(env.Module.StartCapture(null, ShotAction.Copy, "test"));
        Assert.False(env.Module.StartCapture(null, ShotAction.Copy, "test"));

        gate.SetResult();
        Assert.True(await Wait.UntilAsync(() => env.Module.StartCapture(null, ShotAction.Copy, "test again")));
    }

    [Fact]
    public async Task Bus_action_capture_needs_a_permission_and_starts_a_capture()
    {
        await using var env = await new ShotEnv().StartAsync();
        var consumer = new ConsumerPlugin();
        env.Registry.Register(consumer);
        await env.Registry.StartModuleAsync(consumer.Id);
        env.Picker.Choose = "copy";

        var denied = await consumer.Bus!.InvokeAsync(ScreenshotModule.CaptureAction, new JsonObject { ["mode"] = "screen", ["action"] = "pin" });
        Assert.False(denied.Success);

        env.Grant(consumer.Id, ScreenshotModule.ModuleId, BusAccess.Actions);
        var ok = await consumer.Bus.InvokeAsync(ScreenshotModule.CaptureAction, new JsonObject { ["mode"] = "screen", ["action"] = "pin" });

        Assert.True(ok.Success, ok.Message);
        Assert.True(ok.Value!["started"]!.GetValue<bool>());
        Assert.True(await Wait.UntilAsync(() => env.Pins.Count == 1));
        Assert.Equal(ScreenAreaMode.Screen, env.Picker.Requests.Last().Mode);
    }

    [Fact]
    public async Task Tile_and_search_start_captures_and_show_the_hotkey()
    {
        await using var env = await new ShotEnv().StartAsync();
        var hits = await env.Module.SearchAsync("bildschirmfoto", 10, default);
        Assert.Equal("capture", Assert.Single(hits).Id);
        var config = env.Module.SuggestTile(hits[0])!;
        Assert.Equal((CaptureTileType.TypeId, TileSize.Small), (config.Type, config.Size));
        config.Id = "t1";
        config.Size = TileSize.Wide;

        var tile = await env.Registry.CreateTileAsync(config);
        Assert.Equal("Win + Alt + S", tile.StateText);
        await env.Registry.InteractAsync(tile, new TileInteraction("modes", InteractionKind.Press, Text: "window"));

        Assert.True(await Wait.UntilAsync(() => env.Picker.Requests.Count == 1));
        Assert.Equal(ScreenAreaMode.Window, env.Picker.Requests[0].Mode);

        await env.SettingsAsync((ShotOptions.HotkeyKey, ""));
        Assert.Equal("Kein Tastenkürzel", tile.StateText);
        Assert.Contains((await env.Module.SearchAsync("text", 10, default)), h => h.Id == "capture.text");
    }

    [Theory]
    [InlineData(300, 120, 10000, 3.0)]
    [InlineData(1200, 800, 10000, 2.0)]
    [InlineData(3000, 2000, 10000, 1.0)]
    [InlineData(3000, 2000, 2600, 2600 / 3000.0)]
    [InlineData(1500, 300, 2600, 2600 / 1500.0)]
    public void Small_images_are_enlarged_for_recognition_within_the_engine_limit(int width, int height, int max, double expected) =>
        Assert.Equal(expected, OcrScale.Factor(width, height, max), 6);

    private sealed class GatedPicker(Task gate) : IScreenAreaPicker
    {
        public async Task<ScreenAreaResult?> PickScreenAreaAsync(ScreenAreaRequest request, CancellationToken cancellationToken)
        {
            await gate;
            return null;
        }
    }
}
