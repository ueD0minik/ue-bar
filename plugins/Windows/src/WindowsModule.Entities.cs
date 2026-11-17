// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Modules.Windows.Logic;
using UeBar.Modules.Windows.Platform;
using UeBar.Sdk;
using static UeBar.Modules.Windows.WinText;

namespace UeBar.Modules.Windows;

public sealed partial class WindowsModule
{
    private IModuleBus? _bus;

    // ------------------------------------------------------------------ search (IEntitySource)

    /// <summary>All things right now: fixed ones + outputs, apps with sound, Bluetooth devices.</summary>
    internal IReadOnlyList<(EntityInfo Info, string[] Words)> AllEntities()
    {
        var s = State;
        var list = new List<(EntityInfo, string[])>();

        void Add(string id, string nameKey, string icon, string? state, string category, params string[] extra) =>
            list.Add((new EntityInfo(id, T[nameKey], icon, T["Win_AreaName"], state, category), [.. BothLanguages(nameKey), .. extra]));

        Add(WinIds.Volume, "Win_Volume", s.Audio.Muted ? "Mute" : "Volume", Tiles.VolumeTileType.StateText(s), "audio", "sound", "ton", "audio");
        foreach (var output in s.Audio.Outputs)
        {
            list.Add((new EntityInfo(WinIds.OutputPrefix + output.Id, output.Name, "Speaker", T["Win_OutputArea"],
                output.Id == s.Audio.DefaultId ? T["Win_OutputActive"] : T["Win_OutputSwitch"], "output"), [output.Name, output.Detail ?? "", .. BothLanguages("Win_Output")]));
        }

        if (s.Settings.ShowAppVolume)
        {
            Add(WinIds.Apps, "Win_AppVolume", "U+E995", Tiles.AppMixerTileType.StateText(s), "apps", "mixer");
            foreach (var app in s.Apps)
            {
                list.Add((new EntityInfo(WinIds.AppPrefix + app.Key, app.Name, app.Muted ? "Mute" : "Volume", T["Win_AppVolume"],
                    app.Muted ? T["Win_Muted"] : WinFormat.Percent(app.Volume * 100), "app"), [app.Name, app.Key]));
            }
        }

        Add(WinIds.Media, "Win_Media", "MusicNote", Tiles.MediaTileType.StateText(s), "media", "spotify", "youtube", "music", "musik");
        Add(WinIds.Wifi, "Win_Wifi", "Wifi", WinFormat.Radio(s.Radio.Wifi), "radio", "wlan", "wifi", "wi-fi");
        Add(WinIds.Bluetooth, "Win_Bluetooth", DeviceIcons.Bluetooth, WinFormat.Radio(s.Radio.Bluetooth), "radio");
        Add(WinIds.Devices, "Win_Devices", DeviceIcons.Bluetooth, Tiles.DevicesTileType.StateText(s), "devices", "akku", "battery");
        foreach (var device in s.Bluetooth)
        {
            list.Add((new EntityInfo(WinIds.DevicePrefix + device.Id, device.Name, device.Icon, T["Win_Devices"], WinFormat.DeviceState(device), "device"), [device.Name]));
        }

        if (s.Power.HasBattery)
        {
            Add(WinIds.Battery, "Win_Battery", WinFormat.BatteryIcon(s.Power), WinFormat.Battery(s.Power), "system");
        }

        Add(WinIds.Cpu, "Win_Cpu", "U+E950", s.Sample is { } c ? WinFormat.Percent(c.CpuPercent) : null, "system", "processor", "prozessor");
        Add(WinIds.Ram, "Win_Ram", "U+E964", s.Sample is { } r ? WinFormat.Percent(r.MemoryPercent) : null, "system", "ram", "memory");
        Add(WinIds.Disk, "Win_Disk", "U+EDA2", s.Sample is { } d ? T.Format("Win_DiskFree", WinFormat.Gb(d.DiskFreeGb)) : null, "system", "disk", "ssd");
        if (s.Brightness is not null || !s.BrightnessRead)
        {
            Add(WinIds.Brightness, "Win_Brightness", "Sun", s.Brightness is { } b ? WinFormat.Percent(b) : null, "display", "display", "bildschirm");
        }

        Add(WinIds.Lock, "Win_Lock", "Lock", null, "action", "sperren");
        Add(WinIds.Sleep, "Win_Sleep", "Moon", null, "action", "standby");
        Add(WinIds.ScreenOff, "Win_ScreenOff", "Monitor", null, "action", "display");
        return list;
    }

    public async Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        // Bluetooth devices are only read on request – searching is such a request.
        await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(Area.Devices, cancellationToken).ConfigureAwait(false);
        var q = query?.Trim() ?? "";
        return
        [
            .. AllEntities()
                .Where(e => q.Length == 0
                    || e.Info.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || e.Words.Any(w => w.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .Take(maxResults)
                .Select(e => e.Info),
        ];
    }

    public async Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        var s = State;
        var platform = Platform;
        switch (entityId)
        {
            case WinIds.Volume:
                platform.Audio.SetMute(!s.Audio.Muted);
                Signal(Area.Audio);
                break;
            case WinIds.Media when s.SelectedMedia is { } session:
                await SendMediaAsync(session.Id, MediaCommand.PlayPause, cancellationToken).ConfigureAwait(false);
                break;
            case WinIds.Wifi:
                await platform.Radio.SetWifiAsync(s.Radio.Wifi != RadioSwitch.On, cancellationToken).ConfigureAwait(false);
                break;
            case WinIds.Bluetooth:
                await platform.Radio.SetBluetoothAsync(s.Radio.Bluetooth != RadioSwitch.On, cancellationToken).ConfigureAwait(false);
                break;
            case WinIds.Lock:
                platform.Shell.Lock();
                break;
            case WinIds.Sleep:
                platform.Shell.Sleep();
                break;
            case WinIds.ScreenOff:
                await Task.Delay(TimeSpan.FromMilliseconds(600), _time, cancellationToken).ConfigureAwait(false);
                platform.Shell.ScreenOff();
                break;
            case WinIds.Devices:
                platform.Shell.OpenSettings("ms-settings:bluetooth");
                break;
            case WinIds.Battery:
                platform.Shell.OpenSettings("ms-settings:batterysaver");
                break;
            case WinIds.Disk:
                platform.Shell.OpenSettings("ms-settings:storagesense");
                break;
            default:
                if (entityId.StartsWith(WinIds.OutputPrefix, StringComparison.Ordinal))
                {
                    platform.Audio.SetDefaultOutput(entityId[WinIds.OutputPrefix.Length..]);
                    Signal(Area.Audio | Area.Apps);
                }
                else if (entityId.StartsWith(WinIds.AppPrefix, StringComparison.Ordinal) && s.Apps.FirstOrDefault(a => WinIds.AppPrefix + a.Key == entityId) is { } app)
                {
                    platform.Audio.SetAppMute(app.Key, !app.Muted);
                    Signal(Area.Apps);
                }
                else if (entityId.StartsWith(WinIds.DevicePrefix, StringComparison.Ordinal))
                {
                    platform.Shell.OpenSettings("ms-settings:bluetooth");
                }

                break;
        }
    }

    public TileConfig? SuggestTile(EntityInfo entity)
    {
        var (type, size) = entity.Id switch
        {
            WinIds.Volume => ("win.volume", TileSize.Large),
            WinIds.Apps => ("win.app_mixer", TileSize.Large),
            WinIds.Media => ("win.media", TileSize.Large),
            WinIds.Wifi => ("win.wifi", TileSize.Small),
            WinIds.Bluetooth => ("win.bluetooth", TileSize.Small),
            WinIds.Devices => ("win.devices", TileSize.Large),
            WinIds.Brightness => ("win.brightness", TileSize.Wide),
            _ when WinIds.SystemValues.Contains(entity.Id) => ("win.value", TileSize.Small),
            _ when WinIds.Actions.Contains(entity.Id) => ("win.action", TileSize.Small),
            _ when entity.Id.StartsWith(WinIds.AppPrefix, StringComparison.Ordinal) => ("win.app_volume", TileSize.Wide),
            _ when entity.Id.StartsWith(WinIds.DevicePrefix, StringComparison.Ordinal) => ("win.device", TileSize.Small),
            _ when entity.Id.StartsWith(WinIds.OutputPrefix, StringComparison.Ordinal) => ("win.volume", TileSize.Large),
            _ => (null, TileSize.Small),
        };
        if (type is null)
        {
            return null;
        }

        var main = entity.Id.StartsWith(WinIds.OutputPrefix, StringComparison.Ordinal) ? WinIds.Volume : entity.Id;
        return new TileConfig
        {
            Id = $"{ModuleId}-{Guid.NewGuid():N}"[..16],
            Module = ModuleId,
            Type = type,
            Size = size,
            Entities = new Dictionary<string, string> { ["main"] = main },
        };
    }

    // ------------------------------------------------------------------ suggested layout (first start / "Add suggestion")

    public LayoutDocument GetSuggestedLayout()
    {
        var s = State;
        var tiles = new List<TileConfig>
        {
            Tile("w1", "win.volume", TileSize.Large, WinIds.Volume),
            Tile("w2", "win.media", TileSize.Large, WinIds.Media),
            Tile("w3", "win.wifi", TileSize.Small, WinIds.Wifi),
            Tile("w4", "win.bluetooth", TileSize.Small, WinIds.Bluetooth),
        };
        if (s.Power.HasBattery)
        {
            tiles.Add(Tile("w5", "win.value", TileSize.Small, WinIds.Battery));
        }

        tiles.Add(Tile("w6", "win.value", TileSize.Small, WinIds.Cpu));
        tiles.Add(Tile("w7", "win.value", TileSize.Small, WinIds.Ram));
        tiles.Add(Tile("w8", "win.value", TileSize.Small, WinIds.Disk));
        tiles.Add(Tile("w9", "win.action", TileSize.Small, WinIds.Lock));
        return new LayoutDocument
        {
            Tabs = [new LayoutTab { Id = "windows", Name = T["Win_TabName"], Icon = "Laptop", Tiles = tiles }],
        };

        static TileConfig Tile(string id, string type, TileSize size, string entity) => new()
        {
            Id = id,
            Module = ModuleId,
            Type = type,
            Size = size,
            Entities = new Dictionary<string, string> { ["main"] = entity },
        };
    }

    // ------------------------------------------------------------------ status line

    public IReadOnlyList<StatusValueInfo> AvailableStatusValues =>
    [
        new(WinIds.Battery, T["Win_Battery"], "Battery"),
        new(WinIds.Cpu, T["Win_Cpu"], "U+E950"),
        new(WinIds.Ram, T["Win_Ram"], "U+E964"),
        new(WinIds.Disk, T["Win_Disk"], "U+EDA2"),
        new(WinIds.Volume, T["Win_Volume"], "Volume"),
        new(WinIds.Media, T["Win_Media"], "MusicNote"),
    ];

    public StatusValue? GetStatusValue(string key)
    {
        if (AvailableStatusValues.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } info)
        {
            return null;
        }

        StatusValue value;
        lock (_lock)
        {
            if (!_status.TryGetValue(info.Key, out value!))
            {
                value = new StatusValue { Key = info.Key, Icon = info.Icon };
                _status[info.Key] = value;
            }
        }

        Apply(value, State);
        return value;
    }

    private void UpdateStatusValues(WinState state)
    {
        List<StatusValue> values;
        lock (_lock)
        {
            values = [.. _status.Values];
        }

        foreach (var value in values)
        {
            Apply(value, state);
        }
    }

    private static void Apply(StatusValue value, WinState s)
    {
        switch (value.Key)
        {
            case WinIds.Battery:
                value.Icon = WinFormat.BatteryIcon(s.Power);
                value.Text = s.Power.HasBattery ? WinFormat.Percent(s.Power.Percent) : T["Win_NoBattery"];
                value.Tooltip = WinFormat.Battery(s.Power);
                break;
            case WinIds.Cpu:
                value.Text = s.Sample is { } c ? WinFormat.Percent(c.CpuPercent) : "–";
                value.Tooltip = T["Win_Cpu"];
                break;
            case WinIds.Ram:
                value.Text = s.Sample is { } r ? WinFormat.Percent(r.MemoryPercent) : "–";
                value.Tooltip = s.Sample is { } r2 ? T.Format("Win_RamDetail", WinFormat.Gb(r2.MemoryUsedGb), WinFormat.Gb(r2.MemoryTotalGb)) : T["Win_Ram"];
                break;
            case WinIds.Disk:
                value.Text = s.Sample is { } d ? WinFormat.Gb(d.DiskFreeGb) : "–";
                value.Tooltip = s.Sample is { } d2 ? T.Format("Win_DiskDetail", d2.DiskName, WinFormat.Gb(d2.DiskTotalGb)) : T["Win_Disk"];
                break;
            case WinIds.Volume:
                value.Icon = s.Audio.Muted ? "Mute" : "Volume";
                value.Text = s.Audio.Muted ? T["Win_Muted"] : WinFormat.Percent(s.Audio.Volume * 100);
                value.Tooltip = s.Audio.Default?.Name;
                break;
            case WinIds.Media:
                value.Icon = s.SelectedMedia?.IsPlaying == true ? "Play" : "MusicNote";
                value.Text = s.SelectedMedia?.Title ?? T["Win_NoMedia"];
                value.Tooltip = s.SelectedMedia is { } m ? $"{m.AppName}: {m.Artist}" : null;
                break;
        }
    }

    // ------------------------------------------------------------------ module bus (SPEC 4.7) – published, no permissions requested

    private void PublishOnBus(IModuleBus bus)
    {
        _bus = bus;
        bus.PublishAction("win.set_volume", (call, _) =>
        {
            var volume = Number(call.Arguments?["volume"]) ?? throw new ArgumentException("\"volume\" (0–100) is missing");
            Platform.Audio.SetVolume(Math.Clamp(volume, 0, 100) / 100);
            Signal(Area.Audio);
            return Task.FromResult<JsonNode?>(null);
        }, "Set the master volume: { \"volume\": 0–100 }");
        bus.PublishAction("win.mute", (call, _) =>
        {
            var muted = call.Arguments?["muted"] is JsonValue m && m.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? m.GetValueKind() == System.Text.Json.JsonValueKind.True
                : !State.Audio.Muted;
            Platform.Audio.SetMute(muted);
            Signal(Area.Audio);
            return Task.FromResult<JsonNode?>(JsonValue.Create(muted));
        }, "Mute: { \"muted\": true/false } – without arguments: toggle");
        bus.PublishAction("win.media_play_pause", async (_, ct) =>
        {
            await ExecuteNowPlayingAsync(NowPlayingCommand.PlayPause, ct).ConfigureAwait(false);
            return null;
        }, "Play/pause the current media session");
        bus.PublishAction("win.lock", (_, _) =>
        {
            Platform.Shell.Lock();
            return Task.FromResult<JsonNode?>(null);
        }, "Lock the PC");
        bus.PublishValue("win.battery", BatteryValue(State.Power), "Battery: { percent, charging, onAc } or null without battery");
        bus.PublishValue("win.cpu", null, "CPU use in percent (updated while it is shown)");
        bus.PublishValue("win.audio_device", null, "Default output: { id, name }");
        bus.PublishEvent("win.media_changed", "Media changed: { app, title, artist, playing }");
    }

    private void UpdateBus(Area areas)
    {
        if (_bus is not { } bus)
        {
            return;
        }

        var s = State;
        if (areas.HasFlag(Area.Power))
        {
            bus.SetValue("win.battery", BatteryValue(s.Power));
        }

        if (areas.HasFlag(Area.System) && s.Sample is { } sample)
        {
            bus.SetValue("win.cpu", JsonValue.Create(Math.Round(sample.CpuPercent, 1)));
        }

        if (areas.HasFlag(Area.Audio) && s.Audio.Default is { } device && Interlocked.Exchange(ref _lastAudioDevice, device.Id) != device.Id)
        {
            bus.SetValue("win.audio_device", new JsonObject { ["id"] = device.Id, ["name"] = device.Name });
        }
    }

    /// <summary>A number from JSON, no matter whether it was written as int or double.</summary>
    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private void RaiseBusEvent(string id, JsonNode payload) => _bus?.RaiseEvent(id, payload);

    private static JsonNode? BatteryValue(PowerState power) =>
        power.HasBattery ? new JsonObject { ["percent"] = power.Percent, ["charging"] = power.IsCharging, ["onAc"] = power.OnAc } : null;
}
