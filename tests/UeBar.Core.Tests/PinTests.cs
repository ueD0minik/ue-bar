// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Core.Modules;
using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.Core.Tests;

/// <summary>P2-5: the pins of the core – create, change, close, keep across a restart, limits.</summary>
public sealed class PinManagerTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void Text_and_image_pins_are_created_changed_and_closed()
    {
        using var dir = new TempDirectory();
        var pins = new PinManager(dir.Path, new ListLogger());
        var changes = new List<(PinChangeKind, string)>();
        pins.Changed += (_, e) => changes.Add((e.Kind, e.Pin.Id));

        var text = pins.CreateText("Einkaufen\nMilch, Brot")!;
        var image = pins.CreateImage(Png, source: new ScreenRect(10, 20, 30, 40), owner: "screenshot")!;

        Assert.Equal(2, pins.Count);
        Assert.Equal("Einkaufen", pins.Find(text)!.Title); // first line
        var picture = pins.Find(image)!;
        Assert.Equal((1, 1, "screenshot"), (picture.PixelWidth, picture.PixelHeight, picture.Owner));
        Assert.Equal(new PinScreenArea(10, 20, 30, 40), picture.Source);
        Assert.True(File.Exists(pins.ImagePath(picture)));

        Assert.True(pins.Update(text, PinChangeKind.Options, p =>
        {
            p.Opacity = 0.05; // clamped
            p.ClickThrough = true;
            p.Locked = true;
        }));
        Assert.Equal((0.2, true, true), (pins.Find(text)!.Opacity, pins.Find(text)!.ClickThrough, pins.Find(text)!.Locked));
        Assert.True(pins.Update(text, PinChangeKind.Options, p => p.ClickThrough = false));
        Assert.False(pins.Find(text)!.ClickThrough);

        Assert.True(pins.Close(image));
        Assert.False(File.Exists(pins.ImagePath(picture)));
        Assert.False(pins.Close(image));
        Assert.Equal([(PinChangeKind.Added, text), (PinChangeKind.Added, image), (PinChangeKind.Options, text), (PinChangeKind.Options, text), (PinChangeKind.Removed, image)], changes);
    }

    [Fact]
    public void Pins_survive_a_restart_with_content_size_place_and_options()
    {
        using var dir = new TempDirectory();
        var before = new PinManager(dir.Path, new ListLogger());
        var text = before.CreateText("Vokabeln", fontSize: 20)!;
        var image = before.CreateImage(Png, title: "Folie 3")!;
        before.Update(text, PinChangeKind.Placement, p => p.Placement = new PinPlacement { Monitor = "oled", X = 120, Y = 80, Width = 300, Height = 170 });
        before.Update(image, PinChangeKind.Options, p =>
        {
            p.Zoom = 2.5;
            p.PanX = -0.25;
            p.Opacity = 0.6;
        });
        before.SetHidden(image, true);

        var after = new PinManager(dir.Path, new ListLogger());
        after.Load();

        Assert.Equal([text, image], after.Pins.Select(p => p.Id));
        var t = after.Find(text)!;
        Assert.Equal(("Vokabeln", 20.0, new PinPlacement { Monitor = "oled", X = 120, Y = 80, Width = 300, Height = 170 }), (t.Text, t.FontSize, t.Placement));
        var i = after.Find(image)!;
        Assert.Equal(("Folie 3", 2.5, -0.25, 0.6, true), (i.Title, i.Zoom, i.PanX, i.Opacity, i.Hidden));
        Assert.Equal(Png, File.ReadAllBytes(after.ImagePath(i)!));
        Assert.Contains("\"kind\": \"image\"", File.ReadAllText(Path.Combine(dir.Path, "pins.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_files_and_missing_images_are_dropped_without_failing()
    {
        using var dir = new TempDirectory();
        var first = new PinManager(dir.Path, new ListLogger());
        first.CreateText("bleibt");
        var image = first.CreateImage(Png)!;
        File.Delete(first.ImagePath(first.Find(image)!)!);
        File.WriteAllBytes(Path.Combine(dir.Path, "pin-orphan.png"), Png); // an image nobody uses

        var second = new PinManager(dir.Path, new ListLogger());
        second.Load();
        Assert.Equal("bleibt", Assert.Single(second.Pins).Text);
        Assert.False(File.Exists(Path.Combine(dir.Path, "pin-orphan.png")));

        File.WriteAllText(Path.Combine(dir.Path, "pins.json"), "{ kaputt");
        var third = new PinManager(dir.Path, new ListLogger());
        third.Load();
        Assert.Equal(0, third.Count);
        Assert.True(File.Exists(Path.Combine(dir.Path, "pins.json.broken")));
    }

    [Fact]
    public void Limits_empty_text_no_image_too_many()
    {
        using var dir = new TempDirectory();
        var pins = new PinManager(dir.Path, new ListLogger());

        Assert.Null(pins.CreateText("   "));
        Assert.Null(pins.CreateImage([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.Null(pins.CreateImage([]));
        Assert.Equal(PinManager.MaxTextLength, pins.Find(pins.CreateText(new string('x', PinManager.MaxTextLength + 50))!)!.Text!.Length);
        for (var i = pins.Count; i < PinManager.MaxPins; i++)
        {
            Assert.NotNull(pins.CreateText($"Pin {i}"));
        }

        Assert.Null(pins.CreateText("einer zu viel"));
        Assert.Empty(Directory.GetFiles(dir.Path, "pin-*")); // a refused image leaves no file
    }

    [Fact]
    public void Hide_and_show_all()
    {
        using var dir = new TempDirectory();
        var pins = new PinManager(dir.Path, new ListLogger());
        var a = pins.CreateText("a")!;
        pins.CreateText("b");

        pins.SetAllHidden(true);
        Assert.True(pins.AllHidden);

        pins.SetHidden(a, false);
        Assert.False(pins.AllHidden);
        pins.SetAllHidden(false);
        Assert.All(pins.Pins, p => Assert.False(p.Hidden));

        pins.CloseAll();
        Assert.Equal(0, pins.Count);
        Assert.True(pins.AllHidden);
    }

    [Fact]
    public void Images_are_recognized_by_their_first_bytes()
    {
        Assert.Equal("png", ImageFormat.Detect(Png));
        Assert.Equal((1, 1), ImageFormat.Size(Png));
        Assert.Equal("jpg", ImageFormat.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("gif", ImageFormat.Detect("GIF89a\x02\0\x03\0"u8));
        Assert.Equal((2, 3), ImageFormat.Size("GIF89a\x02\0\x03\0"u8));
        Assert.Null(ImageFormat.Detect("Hallo"u8));
    }

    [Fact]
    public void Titles_come_from_the_first_line()
    {
        Assert.Equal("Erste Zeile", PinManager.TitleOf(null, "\n  Erste Zeile \nzweite"));
        Assert.Equal("Eigener", PinManager.TitleOf(" Eigener ", "Text"));
        Assert.Equal(40, PinManager.TitleOf(null, new string('a', 100)).Length);
    }
}

/// <summary>P2-5: pins through the SDK, the bus and the quick command.</summary>
public sealed class PinServiceTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static async Task<(TestEnv Env, PinManager Pins, TestModule Module, TempDirectory Dir)> StartAsync(params BusGrant[] grants)
    {
        var env = new TestEnv(grants: grants);
        var dir = new TempDirectory();
        var pins = new PinManager(dir.Path, env.Log);
        env.Registry.Host.Pins = pins;
        var module = new TestModule("notes");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();
        return (env, pins, module, dir);
    }

    [Fact]
    public async Task A_module_sees_and_changes_only_its_own_pins()
    {
        var (env, pins, module, dir) = await StartAsync();
        await using var _ = env;
        using var __ = dir;
        var service = Assert.IsAssignableFrom<IPinService>(module.Context);
        var changed = 0;
        service.PinsChanged += (_, _) => Interlocked.Increment(ref changed);

        var mine = service.CreateTextPin(new TextPinRequest("Formel: a² + b²") { Title = "Mathe", FontSize = 22 })!;
        var picture = service.CreateImagePin(new ImagePinRequest(Png))!;
        var users = pins.CreateText("vom Nutzer")!;

        Assert.Equal(["Mathe", pins.Find(picture)!.Title], service.GetPins().Select(p => p.Title));
        Assert.Equal(("notes", 22.0), (pins.Find(mine)!.Owner, pins.Find(mine)!.FontSize));
        Assert.False(service.ClosePin(users));
        Assert.False(service.UpdatePin(users, new PinUpdate { Text = "gekapert" }));
        Assert.Equal("vom Nutzer", pins.Find(users)!.Text);

        Assert.True(service.UpdatePin(mine, new PinUpdate { Text = "Formel: c²", IsVisible = false }));
        Assert.Equal(("Formel: c²", true), (pins.Find(mine)!.Text, pins.Find(mine)!.Hidden));
        Assert.False(service.UpdatePin(mine, new PinUpdate { ImageData = Png })); // a text pin has no image
        Assert.True(service.UpdatePin(picture, new PinUpdate { ImageData = Png }));

        var seen = Volatile.Read(ref changed);
        pins.Update(mine, PinChangeKind.Placement, p => p.Placement = new PinPlacement { X = 1 }); // moving is no news for the module
        Assert.Equal(seen, Volatile.Read(ref changed));
        pins.Close(mine); // the user closed it
        Assert.True(await WaitAsync(() => Volatile.Read(ref changed) > seen));
        Assert.Single(service.GetPins());
    }

    [Fact]
    public async Task Without_the_host_services_a_module_gets_nothing_instead_of_an_error()
    {
        await using var env = new TestEnv();
        var module = new TestModule("plain");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        Assert.Null(((IPinService)module.Context!).CreateTextPin(new TextPinRequest("x")));
        Assert.Null(await ((IScreenAreaPicker)module.Context!).PickScreenAreaAsync(new ScreenAreaRequest(), default));
        Assert.False(await ((IClipboardAccess)module.Context!).SetTextAsync("x", default));
    }

    [Fact]
    public async Task Picker_and_clipboard_are_passed_on_and_stop_with_the_module()
    {
        await using var env = new TestEnv();
        var picker = new WaitingPicker();
        var clipboard = new RecordingClipboard();
        env.Registry.Host.ScreenAreaPicker = picker;
        env.Registry.Host.Clipboard = clipboard;
        var module = new TestModule("shots");
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();

        Assert.True(await ((IClipboardAccess)module.Context!).SetTextAsync("Hallo", default));
        Assert.Equal("Hallo", clipboard.Text);

        var pick = ((IScreenAreaPicker)module.Context!).PickScreenAreaAsync(new ScreenAreaRequest(ScreenAreaMode.Window), default);
        Assert.Equal(ScreenAreaMode.Window, (await picker.Asked.Task).Mode);
        await env.Registry.StopModuleAsync("shots"); // stopping the module ends its pick
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pick);
    }

    [Fact]
    public async Task Bus_action_creates_a_text_pin_for_the_caller_only_with_permission()
    {
        var log = new ListLogger();
        var guard = new ModuleGuard(log);
        var grants = new List<BusGrant>();
        var bus = new ModuleBus(guard, new GrantListPermissionPolicy(() => grants), log);
        using var dir = new TempDirectory();
        await using var registry = new ModuleRegistry(guard, bus, log, new Settings.InMemoryModuleStateStore(), dir.Path);
        var pins = new PinManager(Path.Combine(dir.Path, "pins"), log);
        registry.Host.Pins = pins;
        PinCommands.Register(pins, new QuickCommandService(registry, log), bus);
        var caller = new TestModule("timer");
        registry.Register(caller);
        await registry.StartAllAsync();

        var denied = await caller.Context!.Bus.InvokeAsync(PinCommands.CreateTextAction, new JsonObject { ["text"] = "Pause!" });
        Assert.Equal(BusErrorKind.Denied, denied.Error);
        Assert.Contains(bus.GetAllPublications(), p => p.Id == PinCommands.CreateTextAction && p.OwnerModuleId == PinManager.HostId);

        grants.Add(new BusGrant { Consumer = "timer", Provider = PinManager.HostId, Access = BusAccess.Actions });
        var ok = await caller.Context.Bus.InvokeAsync(PinCommands.CreateTextAction, new JsonObject { ["text"] = "Pause!", ["title"] = "Pomodoro" });

        Assert.True(ok.Success, ok.Message);
        var id = ok.Value!["id"]!.GetValue<string>();
        Assert.Equal(("Pause!", "Pomodoro", "timer"), (pins.Find(id)!.Text, pins.Find(id)!.Title, pins.Find(id)!.Owner));
        Assert.Single(((IPinService)caller.Context).GetPins()); // the caller can see and close it
        Assert.False((await caller.Context.Bus.InvokeAsync(PinCommands.CreateTextAction, new JsonObject { ["text"] = " " })).Success);
    }

    [Fact]
    public async Task Quick_command_pin_creates_a_note_and_is_a_whole_word()
    {
        await using var env = new TestEnv();
        using var dir = new TempDirectory();
        var pins = new PinManager(dir.Path, env.Log);
        var quick = new QuickCommandService(env.Registry, env.Log) { HostDisplayName = id => id == PinManager.HostId ? "Pins" : null };
        PinCommands.Register(pins, quick, env.Bus);

        var match = quick.Parse("pin Test")!;
        Assert.Equal((PinManager.HostId, "Test"), (match.ModuleId, match.Argument));
        Assert.Null(quick.Parse("pinsel kaufen")); // "pin" only as a word
        Assert.Null(quick.Parse("pin")); // needs a text

        var result = await quick.ExecuteAsync(match);

        Assert.True(result.Success);
        Assert.Equal("Angepinnt.", result.Message);
        Assert.Equal(("Test", null), (Assert.Single(pins.Pins).Text, pins.Pins[0].Owner)); // the user's
        Assert.Equal("Pins", quick.HostDisplayName!(PinManager.HostId));
    }

    [Fact]
    public async Task No_plugin_may_take_the_id_of_the_pins()
    {
        await using var env = new TestEnv();

        Assert.Null(env.Registry.Register(new TestModule("pins")));
        Assert.Contains(env.Log.Entries, e => e.Message.Contains("reserved ID 'pins'", StringComparison.Ordinal));
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class WaitingPicker : IScreenAreaPicker
    {
        public TaskCompletionSource<ScreenAreaRequest> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScreenAreaResult?> PickScreenAreaAsync(ScreenAreaRequest request, CancellationToken cancellationToken)
        {
            Asked.TrySetResult(request);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    private sealed class RecordingClipboard : IClipboardAccess
    {
        public string? Text { get; private set; }

        public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.FromResult(true);
        }

        public Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

/// <summary>Contract 1.5 (P2-5): new optional parts only.</summary>
public sealed class Contract15Tests
{
    [Fact]
    public void Version_and_screen_rectangles()
    {
        Assert.True(CoreApiVersion.Version >= new Version(1, 5)); // later contracts only extend it
        var a = new ScreenRect(0, 0, 100, 50);

        Assert.Equal(new ScreenRect(50, 10, 50, 40), a.Intersect(new ScreenRect(50, 10, 200, 200)));
        Assert.True(a.Intersect(new ScreenRect(100, 0, 10, 10)).IsEmpty); // touching is not overlapping
        Assert.Equal(5000, a.Area);
        Assert.True(a.Contains(99, 49));
        Assert.False(a.Contains(100, 0));
    }

    [Fact]
    public void New_setting_kinds_and_list_rows_keep_old_code_working()
    {
        Assert.True(Enum.IsDefined(SettingFieldKind.Hotkey));
        Assert.True(Enum.IsDefined(SettingFieldKind.Info));
        Assert.False(new SettingListRow("Titel").CanAddTile); // old rows: no button
        Assert.True((new SettingListRow("Titel") { CanAddTile = true }).CanAddTile);
    }
}
