// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Modules.Windows.Logic;
using UeBar.Modules.Windows.Platform;
using UeBar.Sdk;
using static UeBar.Modules.Windows.WinText;

namespace UeBar.Modules.Windows.Tiles;

/// <summary>
/// Shared base of the Windows tile types: builds the elements from the SDK building blocks (no WPF),
/// refreshes them from <see cref="WinState"/> and handles interactions.
/// </summary>
internal abstract class WinTileType(WindowsModule module) : ITileType
{
    protected WindowsModule Module { get; } = module;

    public abstract string Id { get; }

    protected abstract string NameKey { get; }

    public string DisplayName => T[NameKey];

    public abstract string Icon { get; }

    /// <summary>Areas whose changes this tile shows.</summary>
    public abstract Area Areas { get; }

    public virtual IReadOnlyList<TileSize> AllowedSizes => TileSize.All;

    public virtual TileSize DefaultSize => TileSize.Small;

    /// <summary>Thing used when the slot is empty (null = slot required).</summary>
    protected virtual string? DefaultEntity => null;

    protected virtual string SlotFilter => Id;

    public IReadOnlyList<TileSlot> Slots => [new TileSlot("main", T["Win_SlotMain"], DefaultEntity is null, SlotFilter)];

    /// <summary>Does the thing fit this tile type?</summary>
    public abstract bool Accepts(string entity);

    public TileModel CreateTile(TileConfig config)
    {
        var entity = config.GetEntity("main") ?? DefaultEntity
            ?? throw new InvalidOperationException($"Tile type '{Id}' needs a thing in slot \"main\".");
        if (!Accepts(entity))
        {
            throw new InvalidOperationException($"'{entity}' does not fit the tile type '{Id}'.");
        }

        var tile = new TileModel { Title = DisplayName, Icon = Icon };
        Build(tile, entity);
        tile.Handler = new DelegateInteractionHandler((t, i, ct) => HandleAsync(t, entity, i, ct));
        Module.Track(tile, this, config, entity);
        Refresh(tile, config, entity, Module.State);
        return tile;
    }

    protected abstract void Build(TileModel tile, string entity);

    public void Refresh(TileModel tile, TileConfig config, string entity, WinState state)
    {
        Update(tile, entity, state, out var title, out var icon);
        if (string.IsNullOrWhiteSpace(config.Label))
        {
            tile.Title = title;
        }

        if (string.IsNullOrWhiteSpace(config.Icon))
        {
            tile.Icon = icon;
        }
    }

    /// <summary>Values into the elements; title/icon only through the out parameters (the user may have overridden them).</summary>
    protected abstract void Update(TileModel tile, string entity, WinState state, out string title, out string icon);

    protected abstract Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken);

    protected static void SetButtons(ButtonRowElement row, IReadOnlyList<TileButton> wanted)
    {
        // Update in place where the IDs match (no flicker), otherwise rebuild.
        if (row.Buttons.Select(b => b.Id).SequenceEqual(wanted.Select(b => b.Id)))
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                row.Buttons[i].Icon = wanted[i].Icon;
                row.Buttons[i].Label = wanted[i].Label;
                row.Buttons[i].IsActive = wanted[i].IsActive;
                row.Buttons[i].IsEnabled = wanted[i].IsEnabled;
            }

            return;
        }

        row.Buttons = [.. wanted];
    }

    protected static void SetOptions(ChoiceElement choice, IReadOnlyList<ChoiceOption> wanted, string? selected)
    {
        if (!choice.Options.Select(o => (o.Id, o.Label)).SequenceEqual(wanted.Select(o => (o.Id, o.Label))))
        {
            choice.Options = [.. wanted];
        }

        choice.SelectedId = selected;
    }

    protected static TileButton Button(string id, string icon, string label, bool active = false, bool enabled = true) =>
        new() { Id = id, Icon = icon, Label = label, IsActive = active, IsEnabled = enabled };
}

// ====================================================================== volume

/// <summary>Master volume, mute, output device (headphones ↔ monitor ↔ speakers), sound settings.</summary>
internal sealed class VolumeTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.volume";

    protected override string NameKey => "Win_Volume";

    public override string Icon => "Volume";

    public override Area Areas => Area.Audio;

    public override TileSize DefaultSize => TileSize.Large;

    protected override string? DefaultEntity => WinIds.Volume;

    public override bool Accepts(string entity) => entity == WinIds.Volume;

    public static string StateText(WinState s) =>
        s.Audio.Outputs.Count == 0 ? T["Win_NoAudio"]
        : s.Audio.Muted ? T.Format("Win_VolumeMutedOn", s.Audio.Default?.Name ?? "")
        : T.Format("Win_VolumeOn", WinFormat.Percent(s.Audio.Volume * 100), s.Audio.Default?.Name ?? "");

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "mute", Label = T["Win_Mute"] });
        tile.Elements.Add(new SliderElement { Id = "volume", Min = 0, Max = 100, Step = 1, Unit = "%" });
        tile.Elements.Add(new ChoiceElement { Id = "output", Label = T["Win_Output"] });
        tile.Elements.Add(new ButtonRowElement { Id = "buttons" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        title = T["Win_Volume"];
        icon = s.Audio.Muted ? "Mute" : "Volume";
        tile.StateText = StateText(s);
        tile.IsActive = !s.Audio.Muted && s.Audio.Outputs.Count > 0;
        tile.IsUnavailable = s.Audio.Outputs.Count == 0;
        if (tile.FindElement<SliderElement>("volume") is { } slider)
        {
            slider.Value = Math.Round(s.Audio.Volume * 100);
            slider.IsEnabled = s.Audio.Outputs.Count > 0;
        }

        if (tile.FindElement<ChoiceElement>("output") is { } choice)
        {
            choice.Label = T["Win_Output"];
            SetOptions(choice, [.. s.Settings.OfferedOutputs(s.Audio).Select(o => new ChoiceOption { Id = o.Id, Label = o.Name })], s.Audio.DefaultId);
        }

        if (tile.FindElement<ButtonRowElement>("buttons") is { } row)
        {
            SetButtons(row,
            [
                Button("mute", s.Audio.Muted ? "Mute" : "Volume", s.Audio.Muted ? T["Win_Unmute"] : T["Win_Mute"], s.Audio.Muted),
                Button("settings", "Settings", T["Win_SoundSettings"]),
            ]);
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        var audio = Module.Platform.Audio;
        var s = Module.State;
        switch (interaction)
        {
            case { ElementId: "volume", Kind: InteractionKind.SetValue, Number: { } value }:
                if (s.Audio.Muted && value > 0)
                {
                    audio.SetMute(false);
                }

                audio.SetVolume(value / 100);
                break;
            case { ElementId: "mute", Kind: InteractionKind.Tap }:
            case { ElementId: "buttons", Kind: InteractionKind.Press, Text: "mute" }:
                audio.SetMute(!s.Audio.Muted);
                break;
            case { ElementId: "buttons", Kind: InteractionKind.Press, Text: "settings" }:
                Module.Platform.Shell.OpenSettings("ms-settings:sound");
                break;
            case { ElementId: "output", Kind: InteractionKind.Select, Text: { } deviceId }:
                tile.Alert = null;
                try
                {
                    audio.SetDefaultOutput(deviceId);
                }
                catch (AudioSwitchException ex)
                {
                    Module.Log?.Warn($"Switching the output device failed: {ex.Message}");
                    tile.Alert = T["Win_OutputSwitchFailed"];
                }

                break;
        }

        Module.Signal(Area.Audio | Area.Apps);
        return Task.CompletedTask;
    }
}

// ====================================================================== app volume

/// <summary>Apps that play sound right now, each with a slider and a mute button (2x2).</summary>
internal sealed class AppMixerTileType(WindowsModule module) : WinTileType(module)
{
    /// <summary>More do not fit into a 2x2 tile.</summary>
    public const int MaxApps = 3;

    public override string Id => "win.app_mixer";

    protected override string NameKey => "Win_AppVolume";

    public override string Icon => "U+E995";

    public override Area Areas => Area.Apps | Area.Audio;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Wide, TileSize.Large];

    public override TileSize DefaultSize => TileSize.Large;

    protected override string? DefaultEntity => WinIds.Apps;

    public override bool Accepts(string entity) => entity == WinIds.Apps;

    public static string StateText(WinState s) =>
        !s.Settings.ShowAppVolume ? T["Win_AppVolumeOff"]
        : s.PlayingApps.Count switch
        {
            0 => T["Win_NoAppPlaying"],
            1 => s.PlayingApps[0].Name,
            var n => T.Format("Win_AppsPlaying", n),
        };

    protected override void Build(TileModel tile, string entity)
    {
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        title = T["Win_AppVolume"];
        icon = Icon;
        tile.StateText = StateText(s);
        var apps = s.PlayingApps.Take(MaxApps).ToList();
        tile.IsActive = apps.Count > 0;

        // Sliders "app:<key>" in order, then one row of mute buttons "mute:<key>".
        var wanted = apps.Select(a => "app:" + a.Key).ToList();
        var current = tile.Elements.OfType<SliderElement>().Select(e => e.Id).ToList();
        if (!current.SequenceEqual(wanted))
        {
            tile.Elements.Clear();
            foreach (var app in apps)
            {
                tile.Elements.Add(new SliderElement { Id = "app:" + app.Key, Min = 0, Max = 100, Step = 1, Unit = "%" });
            }

            if (apps.Count > 0)
            {
                tile.Elements.Add(new ButtonRowElement { Id = "mutes" });
            }
        }

        foreach (var app in apps)
        {
            if (tile.FindElement<SliderElement>("app:" + app.Key) is { } slider)
            {
                slider.Label = app.Name + " ";
                slider.Value = Math.Round(app.Volume * 100);
                slider.IsEnabled = !app.Muted;
            }
        }

        if (tile.FindElement<ButtonRowElement>("mutes") is { } row)
        {
            SetButtons(row, [.. apps.Select(a => Button("mute:" + a.Key, a.Muted ? "Mute" : "Volume", T.Format(a.Muted ? "Win_AppUnmute" : "Win_AppMute", a.Name), a.Muted))]);
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        var audio = Module.Platform.Audio;
        switch (interaction)
        {
            case { Kind: InteractionKind.SetValue, Number: { } value } when interaction.ElementId.StartsWith("app:", StringComparison.Ordinal):
                audio.SetAppVolume(interaction.ElementId[4..], value / 100);
                break;
            case { ElementId: "mutes", Kind: InteractionKind.Press, Text: { } button } when button.StartsWith("mute:", StringComparison.Ordinal):
                var key = button[5..];
                var app = Module.State.Apps.FirstOrDefault(a => a.Key == key);
                audio.SetAppMute(key, !(app?.Muted ?? false));
                break;
        }

        Module.Signal(Area.Apps);
        return Task.CompletedTask;
    }
}

/// <summary>One app: tap = mute, slider = volume.</summary>
internal sealed class AppVolumeTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.app_volume";

    protected override string NameKey => "Win_OneApp";

    public override string Icon => "Volume";

    public override Area Areas => Area.Apps | Area.Texts;

    public override TileSize DefaultSize => TileSize.Wide;

    public override bool Accepts(string entity) => entity.StartsWith(WinIds.AppPrefix, StringComparison.Ordinal);

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "mute", Label = T["Win_Mute"] });
        tile.Elements.Add(new SliderElement { Id = "volume", Min = 0, Max = 100, Step = 1, Unit = "%" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        var key = entity[WinIds.AppPrefix.Length..];
        var app = s.Apps.FirstOrDefault(a => a.Key == key);
        title = app?.Name ?? key;
        icon = app?.Muted == true ? "Mute" : "Volume";
        tile.IsUnavailable = app is null;
        tile.StateText = app is null ? T["Win_AppNoSound"] : app.Muted ? T["Win_Muted"] : WinFormat.Percent(app.Volume * 100);
        tile.IsActive = app is { Muted: false, IsPlaying: true };
        if (tile.FindElement<SliderElement>("volume") is { } slider)
        {
            slider.Value = Math.Round((app?.Volume ?? 0) * 100);
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        var key = entity[WinIds.AppPrefix.Length..];
        var app = Module.State.Apps.FirstOrDefault(a => a.Key == key);
        if (app is null)
        {
            return Task.CompletedTask;
        }

        var audio = Module.Platform.Audio;
        switch (interaction)
        {
            case { ElementId: "volume", Kind: InteractionKind.SetValue, Number: { } value }:
                audio.SetAppVolume(key, value / 100);
                break;
            case { ElementId: "mute", Kind: InteractionKind.Tap }:
                audio.SetAppMute(key, !app.Muted);
                break;
        }

        Module.Signal(Area.Apps);
        return Task.CompletedTask;
    }
}

// ====================================================================== media

/// <summary>Native media of all apps: cover, title, artist, progress, controls, source choice.</summary>
internal sealed class MediaTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.media";

    protected override string NameKey => "Win_Media";

    public override string Icon => "MusicNote";

    public override Area Areas => Area.Media;

    public override TileSize DefaultSize => TileSize.Large;

    protected override string? DefaultEntity => WinIds.Media;

    public override bool Accepts(string entity) => entity == WinIds.Media;

    /// <summary>Sources with something to show (the tile offers to switch between them).</summary>
    public static IReadOnlyList<MediaSessionInfo> Sources(WinState s) => [.. s.Media.Where(x => x.HasContent)];

    /// <summary>"Playing", "Paused · 2 sources" – the app name is the title already.</summary>
    public static string StateText(WinState s)
    {
        if (s.SelectedMedia is not { } m)
        {
            return T["Win_NoMedia"];
        }

        var state = m.IsPlaying ? T["Win_MediaPlaying"] : T["Win_MediaPaused"];
        var count = Sources(s).Count;
        return count > 1 ? T.Format("Win_MediaSources", state, count) : state;
    }

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "playpause", Label = T["Win_PlayPause"] });
        tile.Elements.Add(new MediaInfoElement { Id = "media" });
        tile.Elements.Add(new ButtonRowElement { Id = "controls" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        var m = s.SelectedMedia;
        title = m?.AppName ?? T["Win_Media"];
        icon = "MusicNote";
        tile.StateText = StateText(s);
        tile.IsActive = m?.IsPlaying == true;
        if (tile.FindElement<MediaInfoElement>("media") is { } info)
        {
            info.Title = m?.Title ?? T["Win_NoMedia"];
            info.Artist = m?.Artist;
            info.Album = m?.Album;
            info.ImageUrl = m is null ? null : WindowsModule.ImageId(m);
            info.Duration = m?.Duration;
            info.Position = m?.Position;
            info.PositionUpdatedAt = m?.PositionUpdatedAt;
            info.IsPlaying = m?.IsPlaying == true;
        }

        if (tile.FindElement<ButtonRowElement>("controls") is { } row)
        {
            List<TileButton> buttons =
            [
                Button("previous", "Previous", T["Win_Previous"], enabled: m?.CanPrevious == true),
                Button("playpause", m?.IsPlaying == true ? "Pause" : "Play", T["Win_PlayPause"], enabled: m is not null),
                Button("next", "Next", T["Win_Next"], enabled: m?.CanNext == true),
            ];

            // Several apps have something: one more button switches the source (a choice would not fit into 2x2).
            if (Sources(s).Count > 1)
            {
                buttons.Add(Button("source", "U+E8AB", T.Format("Win_MediaSource", m?.AppName ?? "")));
            }

            SetButtons(row, buttons);
        }
    }

    /// <summary>The source after the current one (wraps around).</summary>
    public static string? NextSource(WinState s)
    {
        var sources = Sources(s);
        if (sources.Count < 2)
        {
            return null;
        }

        var index = sources.ToList().FindIndex(x => x.Id == s.SelectedMedia?.Id);
        return sources[(index + 1) % sources.Count].Id;
    }

    protected override async Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction is { ElementId: "source", Kind: InteractionKind.Select, Text: { } id })
        {
            Module.PinMedia(id); // a choice (e.g. from another device's UI)
            return;
        }

        if (interaction is { ElementId: "controls", Kind: InteractionKind.Press, Text: "source" })
        {
            if (NextSource(Module.State) is { } next)
            {
                Module.PinMedia(next);
            }

            return;
        }

        if (Module.State.SelectedMedia is not { } session)
        {
            return;
        }

        var command = interaction switch
        {
            { ElementId: "playpause", Kind: InteractionKind.Tap } => MediaCommand.PlayPause,
            { ElementId: "controls", Text: "playpause" } => MediaCommand.PlayPause,
            { ElementId: "controls", Text: "next" } => MediaCommand.Next,
            { ElementId: "controls", Text: "previous" } => MediaCommand.Previous,
            _ => (MediaCommand?)null,
        };
        if (command is { } c)
        {
            await Module.SendMediaAsync(session.Id, c, cancellationToken).ConfigureAwait(false);
        }
    }
}

// ====================================================================== radios

/// <summary>Wi-Fi on/off with network and signal.</summary>
internal sealed class WifiTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.wifi";

    protected override string NameKey => "Win_Wifi";

    public override string Icon => "Wifi";

    public override Area Areas => Area.Radio;

    protected override string? DefaultEntity => WinIds.Wifi;

    public override bool Accepts(string entity) => entity == WinIds.Wifi;

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "toggle", Label = T["Win_Toggle"] });
        tile.Elements.Add(new ValueDisplayElement { Id = "network" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        var r = s.Radio;
        title = T["Win_Wifi"];
        icon = WinFormat.WifiIcon(r);
        tile.IsActive = r.Wifi == RadioSwitch.On;
        tile.IsUnavailable = r.Wifi == RadioSwitch.Unavailable;
        tile.StateText = r.Wifi != RadioSwitch.On ? WinFormat.Radio(r.Wifi)
            : !r.WifiConnected ? T["Win_NotConnected"]
            : r.WifiNetwork ?? T["Win_Connected"];
        if (tile.FindElement<ValueDisplayElement>("network") is { } value)
        {
            value.Value = r.Wifi == RadioSwitch.On && r.WifiConnected ? r.WifiNetwork ?? T["Win_Connected"] : WinFormat.Radio(r.Wifi);
            value.Label = r.WifiConnected ? WinFormat.Bars(r.WifiBars) : null;
        }
    }

    protected override async Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction is not { ElementId: "toggle", Kind: InteractionKind.Tap })
        {
            return;
        }

        var state = Module.State.Radio.Wifi;
        tile.Alert = null;
        if (state == RadioSwitch.Disabled)
        {
            tile.Alert = T["Win_RadioAirplane"];
            return;
        }

        if (!await Module.Platform.Radio.SetWifiAsync(state != RadioSwitch.On, cancellationToken).ConfigureAwait(false))
        {
            tile.Alert = T["Win_RadioFailed"];
        }

        Module.Signal(Area.Radio);
    }
}

/// <summary>Bluetooth on/off and a button to the Bluetooth settings (connecting devices is not part of it).</summary>
internal sealed class BluetoothTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.bluetooth";

    protected override string NameKey => "Win_Bluetooth";

    public override string Icon => DeviceIcons.Bluetooth;

    public override Area Areas => Area.Radio | Area.Devices;

    protected override string? DefaultEntity => WinIds.Bluetooth;

    public override bool Accepts(string entity) => entity == WinIds.Bluetooth;

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "toggle", Label = T["Win_Toggle"] });
        tile.Elements.Add(new ButtonRowElement { Id = "buttons" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        title = T["Win_Bluetooth"];
        icon = DeviceIcons.Bluetooth;
        var state = s.Radio.Bluetooth;
        var connected = s.Bluetooth.Count(d => d.IsConnected);
        tile.IsActive = state == RadioSwitch.On;
        tile.IsUnavailable = state == RadioSwitch.Unavailable;
        tile.StateText = state == RadioSwitch.On && connected > 0 ? T.Format("Win_BluetoothConnected", connected) : WinFormat.Radio(state);
        if (tile.FindElement<ButtonRowElement>("buttons") is { } row)
        {
            SetButtons(row, [Button("settings", "Settings", T["Win_BluetoothSettings"])]);
        }
    }

    protected override async Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction is { ElementId: "buttons", Kind: InteractionKind.Press, Text: "settings" })
        {
            Module.Platform.Shell.OpenSettings("ms-settings:bluetooth");
            return;
        }

        if (interaction is not { ElementId: "toggle", Kind: InteractionKind.Tap })
        {
            return;
        }

        var state = Module.State.Radio.Bluetooth;
        tile.Alert = null;
        if (state == RadioSwitch.Disabled)
        {
            tile.Alert = T["Win_RadioAirplane"];
            return;
        }

        if (!await Module.Platform.Radio.SetBluetoothAsync(state != RadioSwitch.On, cancellationToken).ConfigureAwait(false))
        {
            tile.Alert = T["Win_RadioFailed"];
        }

        Module.Signal(Area.Radio);
    }
}

// ====================================================================== devices & batteries

/// <summary>An entry of the device list: own Bluetooth devices and batteries from other plugins.</summary>
internal sealed record DeviceRow(string Id, string Name, string Icon, int? Battery, bool IsConnected, string StateText);

/// <summary>Connected devices with their battery level (2x2 list).</summary>
internal sealed class DevicesTileType(WindowsModule module) : WinTileType(module)
{
    /// <summary>More do not fit into a 2x2 tile.</summary>
    public const int MaxRows = 2;

    public override string Id => "win.devices";

    protected override string NameKey => "Win_Devices";

    public override string Icon => DeviceIcons.Bluetooth;

    public override Area Areas => Area.Devices;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide, TileSize.Large];

    public override TileSize DefaultSize => TileSize.Large;

    protected override string? DefaultEntity => WinIds.Devices;

    public override bool Accepts(string entity) => entity == WinIds.Devices;

    /// <summary>
    /// Own Bluetooth devices plus batteries reported by other plugins (e.g. a wireless dongle the
    /// vendor software knows). Connected first, then the lowest battery first.
    /// </summary>
    public static IReadOnlyList<DeviceRow> Rows(WinState s) =>
    [
        .. s.Bluetooth.Select(d => new DeviceRow(d.Id, d.Name, d.Icon, d.Battery, d.IsConnected, WinFormat.DeviceState(d)))
            .Concat(s.ExternalBatteries.Select(b => new DeviceRow($"{b.Source}/{b.DeviceId}", b.Name, b.Icon ?? DeviceIcons.Bluetooth, b.Percent, b.IsConnected, WinFormat.DeviceState(b))))
            .OrderByDescending(r => r.IsConnected)
            .ThenBy(r => r.Battery ?? 101)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    public static string StateText(WinState s)
    {
        var connected = Rows(s).Count(r => r.IsConnected);
        return connected == 0 ? T["Win_NoDevicesConnected"] : T.Format("Win_DevicesConnected", connected);
    }

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "settings", Label = T["Win_BluetoothSettings"] });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        title = T["Win_Devices"];
        icon = DeviceIcons.Bluetooth;
        var rows = Rows(s).Where(r => r.IsConnected).Take(MaxRows).ToList();
        tile.StateText = StateText(s);
        tile.IsActive = rows.Count > 0;
        var wanted = rows.Select(r => "device:" + r.Id).ToList();
        var current = tile.Elements.OfType<ValueDisplayElement>().Select(e => e.Id).ToList();
        if (!current.SequenceEqual(wanted))
        {
            foreach (var old in tile.Elements.OfType<ValueDisplayElement>().ToList())
            {
                tile.Elements.Remove(old);
            }

            foreach (var row in rows)
            {
                tile.Elements.Add(new ValueDisplayElement { Id = "device:" + row.Id });
            }
        }

        foreach (var row in rows)
        {
            if (tile.FindElement<ValueDisplayElement>("device:" + row.Id) is { } value)
            {
                value.Value = row.Battery is { } b ? b.ToString(System.Globalization.CultureInfo.CurrentCulture) : "–";
                value.Unit = row.Battery is null ? null : "%";
                value.Label = row.Name;
            }
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction.Kind == InteractionKind.Tap)
        {
            Module.Platform.Shell.OpenSettings("ms-settings:bluetooth");
        }

        return Task.CompletedTask;
    }
}

/// <summary>One Bluetooth device: name, battery, connected/disconnected.</summary>
internal sealed class DeviceTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.device";

    protected override string NameKey => "Win_OneDevice";

    public override string Icon => DeviceIcons.Bluetooth;

    public override Area Areas => Area.Devices;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide];

    public override bool Accepts(string entity) => entity.StartsWith(WinIds.DevicePrefix, StringComparison.Ordinal);

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "settings", Label = T["Win_BluetoothSettings"] });
        tile.Elements.Add(new ValueDisplayElement { Id = "battery", Unit = "%" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        var id = entity[WinIds.DevicePrefix.Length..];
        var device = s.Bluetooth.FirstOrDefault(d => d.Id == id);
        title = device?.Name ?? T["Win_OneDevice"];
        icon = device?.Icon ?? DeviceIcons.Bluetooth;
        tile.IsUnavailable = device is null;
        tile.IsActive = device?.IsConnected == true;
        tile.StateText = device is null ? T["Win_DeviceGone"] : WinFormat.DeviceState(device);
        if (tile.FindElement<ValueDisplayElement>("battery") is { } value)
        {
            value.Value = device?.Battery is { } b ? b.ToString(System.Globalization.CultureInfo.CurrentCulture) : "–";
            value.Unit = device?.Battery is null ? null : "%";
            value.Label = device is null ? null : device.IsConnected ? T["Win_Connected"] : T["Win_Disconnected"];
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction.Kind == InteractionKind.Tap)
        {
            Module.Platform.Shell.OpenSettings("ms-settings:bluetooth");
        }

        return Task.CompletedTask;
    }
}

// ====================================================================== system values

/// <summary>Battery, CPU, memory, disk as a big value.</summary>
internal sealed class SystemValueTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.value";

    protected override string NameKey => "Win_SystemValue";

    public override string Icon => "Gauge";

    public override Area Areas => Area.Power | Area.System;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide];

    public override bool Accepts(string entity) => WinIds.SystemValues.Contains(entity);

    protected override void Build(TileModel tile, string entity)
    {
        if (entity is WinIds.Battery or WinIds.Disk)
        {
            tile.Elements.Add(new TapAction { Id = "settings", Label = T["Win_OpenSettings"] });
        }

        tile.Elements.Add(new ValueDisplayElement { Id = "value" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        var value = tile.FindElement<ValueDisplayElement>("value");
        var sample = s.Sample;
        switch (entity)
        {
            case WinIds.Battery:
                title = T["Win_Battery"];
                icon = WinFormat.BatteryIcon(s.Power);
                tile.StateText = WinFormat.Battery(s.Power);
                tile.IsActive = s.Power.IsCharging;
                tile.IsUnavailable = !s.Power.HasBattery;
                Set(value, s.Power.HasBattery ? s.Power.Percent.ToString(System.Globalization.CultureInfo.CurrentCulture) : "–", "%", WinFormat.BatteryDetail(s.Power));
                break;
            case WinIds.Cpu:
                title = T["Win_Cpu"];
                icon = "U+E950";
                tile.StateText = sample is null ? "–" : WinFormat.Percent(sample.CpuPercent);
                Set(value, sample is null ? "–" : Math.Round(sample.CpuPercent).ToString(System.Globalization.CultureInfo.CurrentCulture), "%", null);
                break;
            case WinIds.Ram:
                title = T["Win_Ram"];
                icon = "U+E964";
                tile.StateText = sample is null ? "–" : WinFormat.Percent(sample.MemoryPercent);
                Set(value, sample is null ? "–" : Math.Round(sample.MemoryPercent).ToString(System.Globalization.CultureInfo.CurrentCulture), "%",
                    sample is null ? null : T.Format("Win_RamDetail", WinFormat.Gb(sample.MemoryUsedGb), WinFormat.Gb(sample.MemoryTotalGb)));
                break;
            default:
                title = T["Win_Disk"];
                icon = "U+EDA2";
                tile.StateText = sample is null ? "–" : T.Format("Win_DiskFree", WinFormat.Gb(sample.DiskFreeGb));
                Set(value, sample is null ? "–" : Math.Round(sample.DiskFreeGb).ToString(System.Globalization.CultureInfo.CurrentCulture), "GB",
                    sample is null ? null : T.Format("Win_DiskDetail", sample.DiskName, WinFormat.Gb(sample.DiskTotalGb)));
                break;
        }

        static void Set(ValueDisplayElement? element, string text, string? unit, string? label)
        {
            if (element is not null)
            {
                element.Value = text;
                element.Unit = unit;
                element.Label = label;
            }
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction.Kind == InteractionKind.Tap)
        {
            Module.Platform.Shell.OpenSettings(entity == WinIds.Battery ? "ms-settings:batterysaver" : "ms-settings:storagesense");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Brightness of the built-in laptop display (only if there is one).</summary>
internal sealed class BrightnessTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.brightness";

    protected override string NameKey => "Win_Brightness";

    public override string Icon => "Sun";

    public override Area Areas => Area.Brightness;

    public override TileSize DefaultSize => TileSize.Wide;

    protected override string? DefaultEntity => WinIds.Brightness;

    public override bool Accepts(string entity) => entity == WinIds.Brightness;

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new SliderElement { Id = "brightness", Min = 0, Max = 100, Step = 1, Unit = "%" });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        title = T["Win_Brightness"];
        icon = "Sun";
        var missing = s.BrightnessRead && s.Brightness is null;
        tile.IsUnavailable = missing;
        tile.IsActive = s.Brightness is not null;
        tile.StateText = missing ? T["Win_NoInternalDisplay"] : s.Brightness is { } b ? WinFormat.Percent(b) : "–";
        if (tile.FindElement<SliderElement>("brightness") is { } slider)
        {
            slider.Value = s.Brightness ?? 0;
            slider.IsEnabled = !missing;
        }
    }

    protected override Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction is { ElementId: "brightness", Kind: InteractionKind.SetValue, Number: { } value })
        {
            Module.Platform.System.SetBrightness((int)Math.Round(value));
            Module.Signal(Area.Brightness);
        }

        return Task.CompletedTask;
    }
}

// ====================================================================== actions

/// <summary>Lock the PC, sleep (with a short confirmation: tap twice), screen off.</summary>
internal sealed class ActionTileType(WindowsModule module) : WinTileType(module)
{
    public override string Id => "win.action";

    protected override string NameKey => "Win_Action";

    public override string Icon => "Lock";

    public override Area Areas => Area.Texts;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide];

    public override bool Accepts(string entity) => WinIds.Actions.Contains(entity);

    protected override void Build(TileModel tile, string entity)
    {
        tile.Elements.Add(new TapAction { Id = "run", Label = T["Win_Run"] });
    }

    protected override void Update(TileModel tile, string entity, WinState s, out string title, out string icon)
    {
        (title, icon, tile.StateText) = entity switch
        {
            WinIds.Lock => (T["Win_Lock"], "Lock", T["Win_LockHint"]),
            WinIds.Sleep => (T["Win_Sleep"], "Moon", T["Win_SleepHint"]),
            _ => (T["Win_ScreenOff"], "Monitor", T["Win_ScreenOffHint"]),
        };
        if (entity == WinIds.Sleep && !Module.IsSleepArmed)
        {
            tile.Alert = null;
        }
    }

    protected override async Task HandleAsync(TileModel tile, string entity, TileInteraction interaction, CancellationToken cancellationToken)
    {
        if (interaction.Kind != InteractionKind.Tap)
        {
            return;
        }

        var shell = Module.Platform.Shell;
        switch (entity)
        {
            case WinIds.Lock:
                shell.Lock();
                break;
            case WinIds.Sleep:
                if (!Module.ConfirmSleep())
                {
                    tile.Alert = T["Win_SleepConfirm"];
                    return;
                }

                tile.Alert = null;
                var result = shell.Sleep();
                Module.Log?.Info($"Sleep requested: {result}");
                if (result == SleepResult.Failed)
                {
                    tile.Alert = T["Win_SleepFailed"];
                }

                break;
            default:
                // Short pause: the click itself would wake the screen right away.
                await Task.Delay(TimeSpan.FromMilliseconds(600), Module.Time, cancellationToken).ConfigureAwait(false);
                shell.ScreenOff();
                break;
        }
    }
}
