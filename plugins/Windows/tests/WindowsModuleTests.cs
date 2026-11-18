// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Bus;
using UeBar.Core.Modules;
using UeBar.Modules.Windows.Logic;
using UeBar.Modules.Windows.Platform;
using UeBar.Sdk;

namespace UeBar.Modules.Windows.Tests;

public sealed class VolumeTests
{
    [Fact]
    public async Task Slider_sets_the_volume_and_the_tile_follows()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.volume", WinIds.Volume);
        Assert.Equal(40, tile.FindElement<SliderElement>("volume")!.Value);
        Assert.Equal("40 % · Headphones", tile.StateText);

        await env.InteractAndSettleAsync(tile, "volume", InteractionKind.SetValue, 65);

        Assert.Equal(0.65, env.Audio.Volume, 3);
        Assert.True(await env.UntilAsync(() => tile.FindElement<SliderElement>("volume")!.Value == 65));
        Assert.Equal("65 % · Headphones", tile.StateText);
    }

    [Fact]
    public async Task Tap_mutes_and_unmutes_and_moving_the_slider_unmutes()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.volume", WinIds.Volume);

        await env.InteractAndSettleAsync(tile, "mute", InteractionKind.Tap);
        Assert.True(env.Audio.Muted);
        Assert.True(await env.UntilAsync(() => tile.StateText == "Stumm · Headphones"));
        Assert.False(tile.IsActive);
        Assert.Equal("Mute", tile.Icon);

        await env.InteractAndSettleAsync(tile, "volume", InteractionKind.SetValue, 30);
        Assert.False(env.Audio.Muted);
        Assert.True(await env.UntilAsync(() => tile.IsActive));
    }

    [Fact]
    public async Task Volume_changed_outside_shows_up_live()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.volume", WinIds.Volume);

        env.Audio.Volume = 0.9;
        env.Audio.Raise(); // e.g. volume keys of the keyboard

        Assert.True(await env.UntilAsync(() => tile.FindElement<SliderElement>("volume")!.Value == 90));
    }

    [Fact]
    public async Task Output_device_can_be_switched()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.volume", WinIds.Volume);
        var choice = tile.FindElement<ChoiceElement>("output")!;
        Assert.Equal(["Headphones", "Monitor"], choice.Options.Select(o => o.Label));
        Assert.Equal("dev-headphones", choice.SelectedId);

        await env.InteractAndSettleAsync(tile, "output", InteractionKind.Select, text: "dev-monitor");

        Assert.Equal("dev-monitor", env.Audio.DefaultId);
        Assert.True(await env.UntilAsync(() => choice.SelectedId == "dev-monitor"));
        Assert.Null(tile.Alert);
    }

    [Fact]
    public async Task Failed_switch_says_so_and_offers_the_sound_settings()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.volume", WinIds.Volume);
        env.Audio.FailSwitch = true;

        await env.InteractAndSettleAsync(tile, "output", InteractionKind.Select, text: "dev-monitor");

        Assert.Equal("dev-headphones", env.Audio.DefaultId);
        Assert.Equal("Umschalten ging nicht – Sound-Einstellungen nutzen", tile.Alert);
        Assert.Contains(env.Log.Lines, l => l.Contains("Switching the output device failed", StringComparison.Ordinal));
        Assert.Contains(tile.FindElement<ButtonRowElement>("buttons")!.Buttons, b => b.Id == "settings");

        await env.InteractAndSettleAsync(tile, "buttons", InteractionKind.Press, text: "settings");
        Assert.Contains("ms-settings:sound", env.Shell.Calls);
    }

    [Fact]
    public async Task Outputs_are_searchable_and_enter_switches_to_them()
    {
        await using var env = await new WinEnv().StartAsync();

        var found = await env.Module.SearchAsync("monitor", 10, default);
        var output = Assert.Single(found, e => e.Id.StartsWith(WinIds.OutputPrefix, StringComparison.Ordinal));
        await env.Module.ExecuteDefaultActionAsync(output.Id, default);

        Assert.Equal("dev-monitor", env.Audio.DefaultId);
    }
}

public sealed class AppVolumeTests
{
    [Fact]
    public async Task Apps_with_sound_come_and_go()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.app_mixer", WinIds.Apps);
        Assert.Empty(tile.ElementsOf<SliderElement>());
        Assert.Equal("Keine App spielt Ton", tile.StateText);

        env.Audio.SetApps(new AppAudio("spotify", "Spotify", 0.5, false, true), new AppAudio("chrome", "Google Chrome", 1, false, true), new AppAudio("discord", "Discord", 1, false, false));

        Assert.True(await env.UntilAsync(() => tile.ElementsOf<SliderElement>().Count() == 2));
        Assert.Equal(["app:chrome", "app:spotify"], tile.ElementsOf<SliderElement>().Select(s => s.Id).Order());
        Assert.Equal("2 Apps spielen Ton", tile.StateText);
        Assert.Equal(2, tile.FindElement<ButtonRowElement>("mutes")!.Buttons.Count);

        env.Audio.SetApps(new AppAudio("spotify", "Spotify", 0.5, false, true));
        Assert.True(await env.UntilAsync(() => tile.ElementsOf<SliderElement>().Count() == 1));
        Assert.Equal("Spotify", tile.StateText);

        env.Audio.SetApps();
        Assert.True(await env.UntilAsync(() => tile.ElementsOf<TileElement>().Count == 0));
    }

    [Fact]
    public async Task Slider_and_mute_button_act_on_the_app()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Audio.SetApps(new AppAudio("spotify", "Spotify", 0.5, false, true));
        var tile = await env.TileAsync("win.app_mixer", WinIds.Apps);
        Assert.True(await env.UntilAsync(() => tile.FindElement<SliderElement>("app:spotify") is not null));

        await env.InteractAndSettleAsync(tile, "app:spotify", InteractionKind.SetValue, 20);
        Assert.Equal(0.2, env.Audio.GetApps()[0].Volume, 3);

        await env.InteractAndSettleAsync(tile, "mutes", InteractionKind.Press, text: "mute:spotify");
        Assert.True(env.Audio.GetApps()[0].Muted);
        Assert.True(await env.UntilAsync(() => tile.FindElement<ButtonRowElement>("mutes")!.Buttons[0].IsActive));
    }

    [Fact]
    public async Task More_apps_than_fit_are_cut_and_the_setting_switches_it_off()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Audio.SetApps([.. Enumerable.Range(1, 5).Select(i => new AppAudio($"app{i}", $"App {i}", 1, false, true))]);
        var tile = await env.TileAsync("win.app_mixer", WinIds.Apps);
        Assert.True(await env.UntilAsync(() => tile.ElementsOf<SliderElement>().Count() == 3));

        await env.Module.ApplySettingsAsync(new ModuleSettingValues(new Dictionary<string, string> { [WindowsSettings.AppVolumeKey] = "false" }), default);

        Assert.True(await env.UntilAsync(() => !tile.ElementsOf<SliderElement>().Any()));
        Assert.Equal("In den Einstellungen ausgeschaltet", tile.StateText);
    }

    [Fact]
    public async Task Single_app_tile_mutes_with_a_tap_and_is_unavailable_without_sound()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Audio.SetApps(new AppAudio("spotify", "Spotify", 0.5, false, true));
        Assert.True(await env.UntilAsync(() => env.Module.State.Apps.Count == 1));
        var tile = await env.TileAsync("win.app_volume", WinIds.AppPrefix + "spotify", TileSize.Wide);
        Assert.Equal("Spotify", tile.Title);

        await env.InteractAndSettleAsync(tile, "mute", InteractionKind.Tap);
        Assert.True(env.Audio.GetApps()[0].Muted);

        env.Audio.SetApps();
        Assert.True(await env.UntilAsync(() => tile.IsUnavailable));
    }
}

public sealed class MediaTests
{
    [Fact]
    public async Task Playing_session_is_shown_with_cover_and_controls()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.media", WinIds.Media);
        Assert.Equal("Keine Wiedergabe", tile.StateText);

        env.Media.Set(FakeMedia.Session("Spotify", "Song A"));

        Assert.True(await env.UntilAsync(() => tile.FindElement<MediaInfoElement>("media")!.Title == "Song A"));
        var info = tile.FindElement<MediaInfoElement>("media")!;
        Assert.True(info.IsPlaying);
        Assert.StartsWith("win.media:Spotify|", info.ImageUrl, StringComparison.Ordinal);
        Assert.Equal("Spotify", tile.Title);
        Assert.Equal("Spielt", tile.StateText);
        Assert.DoesNotContain(tile.FindElement<ButtonRowElement>("controls")!.Buttons, b => b.Id == "source"); // only one source

        await env.InteractAndSettleAsync(tile, "controls", InteractionKind.Press, text: "next");
        Assert.Contains(("Spotify", MediaCommand.Next), env.Media.Commands);

        var image = await env.Module.LoadImageAsync(info.ImageUrl!, default);
        Assert.NotNull(image);
    }

    [Fact]
    public async Task Several_sources_can_be_switched_and_the_choice_sticks()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.media", WinIds.Media);
        env.Media.Set(FakeMedia.Session("Spotify", "Song A"), FakeMedia.Session("Chrome", "Video B", MediaPlayback.Paused));

        Assert.True(await env.UntilAsync(() => tile.FindElement<ButtonRowElement>("controls")!.Buttons.Any(b => b.Id == "source")));
        Assert.Equal("Song A", env.Module.CurrentNowPlaying!.Title); // the playing one wins
        Assert.Equal("Spielt · 2 Quellen", tile.StateText);
        Assert.Equal("Quelle wechseln – jetzt: Spotify", tile.FindElement<ButtonRowElement>("controls")!.Buttons.Single(b => b.Id == "source").Label);

        await env.InteractAndSettleAsync(tile, "controls", InteractionKind.Press, text: "source");

        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Video B"));
        Assert.Equal("Chrome", tile.Title);

        // Another tap wraps around; a choice from elsewhere works as well.
        await env.InteractAndSettleAsync(tile, "controls", InteractionKind.Press, text: "source");
        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Song A"));
        await env.InteractAndSettleAsync(tile, "source", InteractionKind.Select, text: "Chrome");
        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Video B"));

        // The chosen source disappears: back to automatic, no switch button.
        env.Media.Set(FakeMedia.Session("Spotify", "Song A"));
        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Song A"));
        Assert.True(await env.UntilAsync(() => tile.FindElement<ButtonRowElement>("controls")!.Buttons.All(b => b.Id != "source")));
    }

    [Fact]
    public void Only_real_outputs_are_offered_unless_virtual_ones_are_switched_on()
    {
        AudioDevice Real(string id) => new(id, id);
        AudioDevice Virtual(string id) => new(id, id, "Virtual Audio", IsVirtual: true);
        var audio = new AudioState([Virtual("cable"), Real("monitor"), Real("headphones"), Virtual("app-channel"), Real("speakers")], "headphones", 0.5, false);

        Assert.Equal(["monitor", "headphones", "speakers"], WindowsSettings.Default.OfferedOutputs(audio).Select(d => d.Id));
        var withVirtual = (WindowsSettings.Default with { ShowVirtualOutputs = true }).OfferedOutputs(audio);
        Assert.Equal(WindowsSettings.MaxOutputs, withVirtual.Count); // five, but only four fit
        Assert.Contains(withVirtual, d => d.Id == "cable");

        // A virtual device that is active right now is always offered.
        Assert.Contains(WindowsSettings.Default.OfferedOutputs(audio with { DefaultId = "cable" }), d => d.Id == "cable");

        // At most four, the active one always among them.
        var many = new AudioState([.. Enumerable.Range(1, 7).Select(i => Real("d" + i))], "d7", 0.5, false);
        var offered = WindowsSettings.Default.OfferedOutputs(many);
        Assert.Equal(WindowsSettings.MaxOutputs, offered.Count);
        Assert.Contains(offered, d => d.Id == "d7");
    }

    [Theory]
    [InlineData("ROOT", null, null, true)]
    [InlineData("SWD", null, null, true)]
    [InlineData("USB", "Elgato Virtual Audio", null, true)]
    [InlineData("HDAUDIO", "Realtek(R) Audio", "Speakers (Realtek(R) Audio)", false)]
    [InlineData(null, null, null, false)]
    public void Virtual_outputs_are_recognized(string? enumerator, string? detail, string? friendly, bool expected) =>
        Assert.Equal(expected, CoreAudioPlatform.IsVirtual(enumerator, detail, friendly));

    [Fact]
    public async Task Source_switches_to_the_one_that_starts_playing()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Media.Set(FakeMedia.Session("Spotify", "Song A"), FakeMedia.Session("Chrome", "Video B", MediaPlayback.Paused));
        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Song A"));

        env.Media.Set(FakeMedia.Session("Spotify", "Song A", MediaPlayback.Paused), FakeMedia.Session("Chrome", "Video B"));

        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Video B"));
        Assert.True(env.Module.CurrentNowPlaying!.IsPlaying);
        Assert.Equal("Chrome", env.Module.CurrentNowPlaying.SourceName);
    }

    [Fact]
    public async Task Position_updates_are_only_read_while_a_media_tile_is_visible()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Media.Set(FakeMedia.Session("Spotify", "Song A"));
        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying is not null));
        var reads = env.Module.GetDiagnostics()[^1];

        // Collapsed: position changes are only remembered.
        env.Media.RaiseTimeline();
        env.Media.RaiseTimeline();
        await env.SettleAsync();
        Assert.Equal(reads, env.Module.GetDiagnostics()[^1]);

        // The media tile becomes visible: read once, then every position change.
        var config = WinEnv.Config("win.media", WinIds.Media);
        await env.Registry.NotifyVisibilityAsync(true, [config], []);
        Assert.True(await env.UntilAsync(() => env.Module.GetDiagnostics()[^1] != reads));
        var visible = env.Module.GetDiagnostics()[^1];
        env.Media.RaiseTimeline();
        Assert.True(await env.UntilAsync(() => env.Module.GetDiagnostics()[^1] != visible));
    }

    [Fact]
    public async Task Ignored_apps_never_show_up()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Media.Set(new MediaSessionInfo("MSTeams_8wekyb3d8bbwe!MSTeams", "MSTeams_8wekyb3d8bbwe!MSTeams", "Teams", "Call", null, null, MediaPlayback.Playing));

        Assert.True(await env.UntilAsync(() => env.Module.State.Media.Count == 0 && env.Module.CurrentNowPlaying is null));

        await env.Module.ApplySettingsAsync(new ModuleSettingValues(new Dictionary<string, string> { [WindowsSettings.IgnoredKey] = "" }), default);
        Assert.True(await env.UntilAsync(() => env.Module.CurrentNowPlaying?.Title == "Call"));
    }

    [Fact]
    public void Chooser_prefers_the_users_pick_then_playing_then_windows_current()
    {
        var a = FakeMedia.Session("A", "a", MediaPlayback.Paused);
        var b = FakeMedia.Session("B", "b");
        var c = FakeMedia.Session("C", "c");
        var last = new Dictionary<string, DateTimeOffset> { ["B"] = DateTimeOffset.Now.AddMinutes(-1), ["C"] = DateTimeOffset.Now };

        Assert.Equal("A", MediaChooser.Choose([a, b, c], "A", null, last)!.Id);
        Assert.Equal("B", MediaChooser.Choose([a, b, c], null, "B", last)!.Id);
        Assert.Equal("C", MediaChooser.Choose([a, b, c], null, null, last)!.Id);
        Assert.Equal("A", MediaChooser.Choose([a], null, null, last)!.Id);
        Assert.Null(MediaChooser.Choose([], null, null, last));
    }

    [Fact]
    public void Windows_playback_ranks_before_other_devices_unless_the_user_says_otherwise()
    {
        var windows = new NowPlayingEntry("windows", new NowPlayingInfo("s", "Song", IsPlaying: true) { LastActive = DateTimeOffset.Now.AddMinutes(-5) });
        var tv = new NowPlayingEntry("homeassistant", new NowPlayingInfo("tv", "Movie", IsPlaying: true) { LastActive = DateTimeOffset.Now });
        int Priority(string id) => id == "windows" ? WindowsModule.LocalPlaybackPriority : 0;

        // Default: both play → Windows first (priority), although the TV is more recent.
        Assert.Equal("windows", NowPlayingService.Choose([tv, windows], [], Priority)!.ModuleId);

        // The user's order wins.
        Assert.Equal("homeassistant", NowPlayingService.Choose([tv, windows], ["homeassistant", "windows"], Priority)!.ModuleId);

        // What plays always beats what is paused.
        var pausedWindows = windows with { Info = windows.Info with { IsPlaying = false } };
        Assert.Equal("homeassistant", NowPlayingService.Choose([pausedWindows, tv], [], Priority)!.ModuleId);
    }

    [Fact]
    public async Task Now_playing_service_of_the_core_uses_the_priority_of_the_plugin()
    {
        await using var env = new WinEnv();
        var other = new RemotePlayer();
        env.Registry.Register(other);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(other.Id));
        var service = new NowPlayingService(env.Registry, new ListLogger());

        env.Media.Set(FakeMedia.Session("Spotify", "Song A"));
        Assert.True(await env.UntilAsync(() => service.Current?.ModuleId == WindowsModule.ModuleId));
        Assert.Equal([WindowsModule.ModuleId, other.Id], service.OrderedSources().Select(m => m.Id));

        service.SourceOrder = () => [other.Id];
        service.Refresh();
        Assert.Equal(other.Id, service.Current!.ModuleId);
        Assert.Equal([other.Id, WindowsModule.ModuleId], service.OrderedSources().Select(m => m.Id));
    }

    /// <summary>Stands in for another device's player (e.g. a TV through Home Assistant): priority 0.</summary>
    private sealed class RemotePlayer : IModule, INowPlayingSource
    {
        public string Id => "remote";

        public string DisplayName => "Remote";

        public string Icon => "Tv";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public NowPlayingInfo? CurrentNowPlaying { get; } = new("tv", "Movie", IsPlaying: true);

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public event EventHandler? NowPlayingChanged { add { } remove { } }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExecuteNowPlayingAsync(NowPlayingCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class SystemTests
{
    [Theory]
    [InlineData(true, 87, true, true, null, 35, "87 % · voll in 35 min")]
    [InlineData(true, 87, true, true, null, null, "87 % · lädt")]
    [InlineData(true, 100, false, true, null, null, "100 % · angesteckt")]
    [InlineData(true, 42, false, false, 130, null, "42 % · noch 2 h 10 min")]
    [InlineData(true, 42, false, false, 20, null, "42 % · noch 20 min")]
    [InlineData(true, 42, false, false, null, null, "42 %")]
    [InlineData(false, 0, false, true, null, null, "Kein Akku")]
    public void Battery_texts(bool hasBattery, int percent, bool charging, bool onAc, int? minutesLeft, int? minutesToFull, string expected)
    {
        var power = new PowerState(hasBattery, percent, charging, onAc,
            minutesLeft is { } l ? TimeSpan.FromMinutes(l) : null,
            minutesToFull is { } f ? TimeSpan.FromMinutes(f) : null);
        Assert.Equal(expected, WinFormat.Battery(power));
    }

    [Fact]
    public async Task Battery_tile_follows_plugging_in_and_out()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.value", WinIds.Battery, TileSize.Small);
        Assert.Equal("80 % · noch 2 h 10 min", tile.StateText);
        Assert.False(tile.IsActive);

        env.System.SetPower(new PowerState(true, 81, true, true, null, TimeSpan.FromMinutes(40)));

        Assert.True(await env.UntilAsync(() => tile.StateText == "81 % · voll in 40 min"));
        Assert.True(tile.IsActive);
        Assert.Equal("U+EBB3", tile.Icon); // charging, 80 %
    }

    [Fact]
    public async Task Polling_runs_only_while_a_polled_value_is_visible()
    {
        await using var env = await new WinEnv().StartAsync();
        var cpu = WinEnv.Config("win.value", WinIds.Cpu, TileSize.Small);
        var tile = await env.Registry.CreateTileAsync(cpu);

        // Collapsed: nothing is read, however much time passes.
        for (var i = 0; i < 5; i++)
        {
            env.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(20);
        }

        Assert.Equal(0, env.System.Samples);
        Assert.False(env.Module.IsPolling);

        // Panel open with the CPU tile: read right away, then every 2 s.
        await env.Registry.NotifyVisibilityAsync(true, [cpu], []);
        Assert.True(await env.UntilAsync(() => env.System.Samples >= 1));
        Assert.True(await env.UntilAsync(() => tile.StateText == "11 %"));
        for (var i = 0; i < 3; i++)
        {
            var before = env.System.Samples;
            env.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.True(await env.UntilAsync(() => env.System.Samples > before));
        }

        // Collapsed again: stops.
        await env.Registry.NotifyVisibilityAsync(false, [], []);
        Assert.False(env.Module.IsPolling);
        env.Time.Advance(TimeSpan.FromSeconds(2)); // the round that was already waiting may still run
        await Task.Delay(50);
        var stopped = env.System.Samples;
        for (var i = 0; i < 5; i++)
        {
            env.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(20);
        }

        Assert.Equal(stopped, env.System.Samples);
    }

    [Fact]
    public async Task Panel_open_without_polled_values_does_not_poll()
    {
        await using var env = await new WinEnv().StartAsync();
        await env.Registry.NotifyVisibilityAsync(true, [WinEnv.Config("win.volume", WinIds.Volume), WinEnv.Config("win.value", WinIds.Battery)], []);
        env.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.False(env.Module.IsPolling);
        Assert.Equal(0, env.System.Samples);
    }

    [Fact]
    public async Task Status_line_values_count_as_visible()
    {
        await using var env = await new WinEnv().StartAsync();
        var ram = await env.Registry.GetStatusValueAsync(new StatusItemConfig { Module = WindowsModule.ModuleId, Entity = WinIds.Ram });
        Assert.Equal("–", ram!.Text);

        await env.Registry.NotifyVisibilityAsync(false, [], [new StatusItemConfig { Module = WindowsModule.ModuleId, Entity = WinIds.Ram }]);

        Assert.True(await env.UntilAsync(() => ram.Text == "50 %"));
        Assert.Equal("8 GB von 16 GB belegt", ram.Tooltip);
    }

    [Fact]
    public async Task Brightness_tile_without_built_in_display_is_unavailable()
    {
        await using var env = await new WinEnv().StartAsync();
        env.System.Brightness = null;
        var config = WinEnv.Config("win.brightness", WinIds.Brightness, TileSize.Wide);
        var tile = await env.Registry.CreateTileAsync(config);
        await env.Registry.NotifyVisibilityAsync(true, [config], []);

        Assert.True(await env.UntilAsync(() => tile.IsUnavailable));
        Assert.Equal("Kein eingebauter Bildschirm aktiv", tile.StateText);
    }

    [Fact]
    public async Task Brightness_slider_sets_the_brightness()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.brightness", WinIds.Brightness, TileSize.Wide);

        await env.InteractAndSettleAsync(tile, "brightness", InteractionKind.SetValue, 35);

        Assert.Equal(35, env.System.Brightness);
        Assert.True(await env.UntilAsync(() => tile.StateText == "35 %"));
    }
}

public sealed class RadioAndDeviceTests
{
    [Fact]
    public async Task Wifi_switches_off_and_on()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.wifi", WinIds.Wifi, TileSize.Wide);
        Assert.Equal("Home network", tile.StateText);
        Assert.True(tile.IsActive);
        Assert.Equal("Signal 4/5", tile.FindElement<ValueDisplayElement>("network")!.Label);

        await env.InteractAndSettleAsync(tile, "toggle", InteractionKind.Tap);
        Assert.True(await env.UntilAsync(() => tile.StateText == "Aus"));
        Assert.False(tile.IsActive);

        await env.InteractAndSettleAsync(tile, "toggle", InteractionKind.Tap);
        Assert.True(await env.UntilAsync(() => tile.IsActive));
    }

    [Fact]
    public async Task Hidden_network_name_says_why_and_the_settings_offer_location_access()
    {
        await using var env = await new WinEnv().StartAsync();
        Assert.DoesNotContain(env.Module.SettingFields, f => f.Kind == SettingFieldKind.Button);

        env.Radio.State = env.Radio.State with { WifiNetwork = null };
        await env.Module.RefreshAsync(Area.Radio, default);
        var tile = await env.TileAsync("win.wifi", WinIds.Wifi, TileSize.Wide);

        Assert.Equal("Verbunden (Name: Standortzugriff nötig)", tile.StateText);
        var button = Assert.Single(env.Module.SettingFields, f => f.Kind == SettingFieldKind.Button);
        Assert.Equal("Standortzugriff öffnen", button.ButtonText);
        Assert.NotNull(await env.Module.RunSettingsActionAsync(button.Key, default));
        Assert.Contains("ms-settings:privacy-location", env.Shell.Calls);
    }

    [Fact]
    public async Task Refused_or_airplane_mode_says_why()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.wifi", WinIds.Wifi);
        env.Radio.Refuse = true;
        await env.InteractAndSettleAsync(tile, "toggle", InteractionKind.Tap);
        Assert.Equal("Windows hat es nicht erlaubt", tile.Alert);

        env.Radio.State = env.Radio.State with { Bluetooth = RadioSwitch.Disabled };
        await env.Module.RefreshAsync(Area.Radio, default);
        var bluetooth = await env.TileAsync("win.bluetooth", WinIds.Bluetooth);
        await env.InteractAndSettleAsync(bluetooth, "toggle", InteractionKind.Tap);
        Assert.Equal("Aus (Flugmodus)", bluetooth.Alert);
    }

    [Fact]
    public async Task Bluetooth_tile_counts_connected_devices_and_opens_the_settings()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.bluetooth", WinIds.Bluetooth, TileSize.Wide);
        Assert.Equal("An · 1 verbunden", tile.StateText);

        await env.InteractAndSettleAsync(tile, "buttons", InteractionKind.Press, text: "settings");
        Assert.Contains("ms-settings:bluetooth", env.Shell.Calls);

        await env.InteractAndSettleAsync(tile, "toggle", InteractionKind.Tap);
        Assert.True(await env.UntilAsync(() => tile.StateText == "Aus"));
    }

    [Fact]
    public async Task Device_list_shows_batteries_and_takes_batteries_of_other_plugins()
    {
        await using var env = new WinEnv();
        var vendor = new VendorPlugin();
        env.Registry.Register(vendor);
        Assert.True(await env.Registry.StartModuleAsync(vendor.Id));
        await env.StartAsync();
        var config = WinEnv.Config("win.devices", WinIds.Devices);
        var tile = await env.Registry.CreateTileAsync(config);
        await env.Registry.NotifyVisibilityAsync(true, [config], []); // the list is on screen: it is kept up to date

        // While visible the plugin re-reads every polling round – the clock moves on, so a read that
        // failed (e.g. a time limit on a busy machine) is retried.
        bool Rows(int count)
        {
            env.Time.Advance(TimeSpan.FromSeconds(2));
            return tile.ElementsOf<ValueDisplayElement>().Count() == count;
        }

        Assert.True(await env.UntilAsync(() => Rows(2)), string.Join(" | ", env.Log.Lines));
        var rows = tile.ElementsOf<ValueDisplayElement>().ToList();
        Assert.Equal(["Headset", "Earbuds"], rows.Select(r => r.Label)); // lowest battery first
        Assert.Equal(["35", "70"], rows.Select(r => r.Value));
        Assert.Equal("2 verbunden", tile.StateText);

        // The vendor plugin reports a new level: the tile follows.
        vendor.Set(20);
        Assert.True(await env.UntilAsync(() => Rows(2) && tile.ElementsOf<ValueDisplayElement>().First().Value == "20"), string.Join(" | ", env.Log.Lines));

        // The plugin's own Bluetooth batteries are offered to the others.
        var own = Assert.Single(env.Module.DeviceBatteries);
        Assert.Equal(("Earbuds", 70), (own.Name, own.Percent));
    }

    [Fact]
    public async Task Device_battery_is_read_again_while_the_list_is_visible()
    {
        await using var env = await new WinEnv().StartAsync();
        var config = WinEnv.Config("win.devices", WinIds.Devices);
        await env.Registry.NotifyVisibilityAsync(true, [config], []);
        Assert.True(await env.UntilAsync(() => env.Bluetooth.Refreshes >= 1));
    }

    /// <summary>Stands in for a vendor plugin (wireless dongle) that knows a headset's battery.</summary>
    private sealed class VendorPlugin : IModule, IDeviceBatterySource
    {
        private int _percent = 35;

        public string Id => "vendor";

        public string DisplayName => "Vendor";

        public string Icon => "Headphones";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public IReadOnlyList<DeviceBatteryInfo> DeviceBatteries => [new("headset", "Headset", Volatile.Read(ref _percent), false, true, "Headphones")];

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public event EventHandler? DeviceBatteriesChanged;

        public void Set(int percent)
        {
            Volatile.Write(ref _percent, percent);
            DeviceBatteriesChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class ActionTests
{
    [Fact]
    public async Task Lock_runs_right_away()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.action", WinIds.Lock, TileSize.Small);
        Assert.Equal("PC sperren", tile.Title);

        await env.InteractAndSettleAsync(tile, "run", InteractionKind.Tap);

        Assert.Equal(["lock"], env.Shell.Calls);
    }

    [Fact]
    public async Task Sleep_needs_a_second_tap_within_a_few_seconds()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.action", WinIds.Sleep, TileSize.Small);

        await env.InteractAndSettleAsync(tile, "run", InteractionKind.Tap);
        Assert.Empty(env.Shell.Calls);
        Assert.Equal("Zum Bestätigen nochmal tippen", tile.Alert);

        // Too late: arms again instead of sleeping.
        env.Time.Advance(TimeSpan.FromSeconds(6));
        await env.InteractAndSettleAsync(tile, "run", InteractionKind.Tap);
        Assert.Empty(env.Shell.Calls);

        await env.InteractAndSettleAsync(tile, "run", InteractionKind.Tap);
        Assert.Equal(["sleep"], env.Shell.Calls);
        Assert.Null(tile.Alert);
    }

    [Fact]
    public async Task Closing_the_panel_drops_a_pending_confirmation()
    {
        await using var env = await new WinEnv().StartAsync();
        var config = WinEnv.Config("win.action", WinIds.Sleep, TileSize.Small);
        var tile = await env.Registry.CreateTileAsync(config);
        await env.Registry.NotifyVisibilityAsync(true, [config], []);
        await env.InteractAndSettleAsync(tile, "run", InteractionKind.Tap);
        Assert.NotNull(tile.Alert);

        await env.Registry.NotifyVisibilityAsync(false, [], []);

        Assert.True(await env.UntilAsync(() => tile.Alert is null));
        await env.InteractAndSettleAsync(tile, "run", InteractionKind.Tap);
        Assert.Empty(env.Shell.Calls);
    }
}

public sealed class BusTests
{
    private static BusGrant Grant() => new() { Consumer = "consumer", Provider = WindowsModule.ModuleId, Access = BusAccess.All };

    [Fact]
    public async Task Actions_values_and_events_are_published()
    {
        await using var env = new WinEnv(Grant());
        var consumer = new Consumer();
        env.Registry.Register(consumer);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(consumer.Id));
        var bus = consumer.Bus!;

        Assert.True((await bus.InvokeAsync("win.set_volume", new JsonObject { ["volume"] = 25 })).Success);
        Assert.Equal(0.25, env.Audio.Volume, 3);

        Assert.True((await bus.InvokeAsync("win.mute")).Success);
        Assert.True(env.Audio.Muted);
        Assert.True((await bus.InvokeAsync("win.mute", new JsonObject { ["muted"] = false })).Success);
        Assert.False(env.Audio.Muted);

        Assert.True((await bus.InvokeAsync("win.lock")).Success);
        Assert.Contains("lock", env.Shell.Calls);

        var battery = bus.GetValue("win.battery");
        Assert.True(battery.Success);
        Assert.Equal(80, battery.Value!["percent"]!.GetValue<int>());
        Assert.Equal("Headphones", bus.GetValue("win.audio_device").Value!["name"]!.GetValue<string>());

        var events = new List<BusEvent>();
        using var subscription = bus.SubscribeEvent("win.media_changed", e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        });
        env.Media.Set(FakeMedia.Session("Spotify", "Song A"));
        Assert.True(await env.UntilAsync(() =>
        {
            lock (events)
            {
                return events.Any(e => e.Payload?["title"]?.GetValue<string>() == "Song A");
            }
        }));

        Assert.True((await bus.InvokeAsync("win.media_play_pause")).Success);
        Assert.Contains(("Spotify", MediaCommand.PlayPause), env.Media.Commands);
    }

    [Fact]
    public async Task Without_permission_nobody_may_use_them()
    {
        await using var env = new WinEnv();
        var consumer = new Consumer();
        env.Registry.Register(consumer);
        await env.StartAsync();
        Assert.True(await env.Registry.StartModuleAsync(consumer.Id));

        var result = await consumer.Bus!.InvokeAsync("win.set_volume", new JsonObject { ["volume"] = 99 });

        Assert.Equal(BusErrorKind.Denied, result.Error);
        Assert.Equal(0.4, env.Audio.Volume, 3);
    }

    private sealed class Consumer : IModule
    {
        public IModuleBus? Bus { get; private set; }

        public string Id => "consumer";

        public string DisplayName => "Consumer";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            Bus = context.Bus;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class SearchAndLayoutTests
{
    [Theory]
    [InlineData("Lautstärke", WinIds.Volume)]
    [InlineData("volume", WinIds.Volume)]
    [InlineData("Akku", WinIds.Battery)]
    [InlineData("WLAN", WinIds.Wifi)]
    [InlineData("wi-fi", WinIds.Wifi)]
    [InlineData("sperren", WinIds.Lock)]
    [InlineData("Earbuds", WinIds.DevicePrefix + "bt-1")]
    public async Task Things_are_found_in_both_languages(string query, string expected)
    {
        await using var env = await new WinEnv().StartAsync();
        var found = await env.Module.SearchAsync(query, 20, default);
        Assert.Contains(found, e => e.Id == expected);
    }

    [Fact]
    public async Task App_names_are_searchable_and_suggest_a_tile()
    {
        await using var env = await new WinEnv().StartAsync();
        env.Audio.SetApps(new AppAudio("spotify", "Spotify", 0.5, false, true));
        Assert.True(await env.UntilAsync(() => env.Module.State.Apps.Count == 1));

        var app = Assert.Single(await env.Module.SearchAsync("spoti", 20, default), e => e.Id.StartsWith(WinIds.AppPrefix, StringComparison.Ordinal));
        var tile = env.Module.SuggestTile(app);

        Assert.Equal("win.app_volume", tile!.Type);
        Assert.Equal(WinIds.AppPrefix + "spotify", tile.GetEntity("main"));
    }

    [Fact]
    public async Task Every_suggested_tile_can_be_created()
    {
        await using var env = await new WinEnv().StartAsync();
        foreach (var entity in await env.Module.SearchAsync("", 100, default))
        {
            var config = env.Module.SuggestTile(entity);
            Assert.NotNull(config);
            var tile = await env.Registry.CreateTileAsync(config);
            Assert.False(tile.IsPlaceholder, $"{entity.Id} → {config.Type}");
        }
    }

    [Fact]
    public async Task Suggested_layout_is_one_windows_tab_that_can_be_added_to_an_existing_layout()
    {
        await using var env = await new WinEnv().StartAsync();
        var suggestion = env.Module.GetSuggestedLayout();
        var tab = Assert.Single(suggestion.Tabs);
        Assert.Equal("Windows", tab.Name);
        Assert.Contains(tab.Tiles, t => t.Type == "win.volume");
        Assert.Contains(tab.Tiles, t => t.Type == "win.media");
        Assert.Contains(tab.Tiles, t => t.GetEntity("main") == WinIds.Battery);
        Assert.Empty(suggestion.Status);

        var existing = new LayoutDocument { Tabs = [new LayoutTab { Id = "windows", Name = "Mine", Tiles = [WinEnv.Config("win.volume", WinIds.Volume, id: "w1")] }] };
        var merged = LayoutSetup.AppendSuggestion(env.Registry, WindowsModule.ModuleId, existing)!;

        Assert.Equal(["windows", "windows-2"], merged.Tabs.Select(t => t.Id));
        Assert.Equal("Mine", merged.Tabs[0].Name); // the existing layout stays as it is
        Assert.Equal(merged.AllTiles.Count(), merged.AllTiles.Select(t => t.Id).Distinct().Count());
        Assert.Single(existing.Tabs); // not changed in place
    }

    [Fact]
    public async Task Tile_names_overridden_by_the_user_stay()
    {
        await using var env = await new WinEnv().StartAsync();
        var config = WinEnv.Config("win.volume", WinIds.Volume);
        config.Label = "Sound";
        var tile = await env.Registry.CreateTileAsync(config);

        env.Audio.SetMute(true);
        Assert.True(await env.UntilAsync(() => tile.StateText!.StartsWith("Stumm", StringComparison.Ordinal)));
        Assert.Equal("Sound", tile.Title);
    }

    [Fact]
    public async Task Language_switch_refreshes_the_texts()
    {
        await using var env = await new WinEnv().StartAsync();
        var tile = await env.TileAsync("win.value", WinIds.Battery, TileSize.Small);
        Assert.Equal("Akku", tile.Title);
        try
        {
            UeBar.Core.Localization.UiCultureSource.Shared.Set(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            Assert.True(await env.UntilAsync(() => tile.Title == "Battery"));
        }
        finally
        {
            UeBar.Core.Localization.UiCultureSource.Shared.Set(System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        }
    }
}
