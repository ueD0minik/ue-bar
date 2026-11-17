// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Modules.Windows.Platform;
using UeBar.Sdk;
using static UeBar.Modules.Windows.WinText;

namespace UeBar.Modules.Windows.Logic;

/// <summary>IDs of the things the plugin offers (slot "main" of its tiles, status values, search).</summary>
internal static class WinIds
{
    public const string Volume = "win.volume";
    public const string Apps = "win.apps";
    public const string AppPrefix = "win.app:";
    public const string OutputPrefix = "win.output:";
    public const string Media = "win.media";
    public const string Wifi = "win.wifi";
    public const string Bluetooth = "win.bluetooth";
    public const string Devices = "win.devices";
    public const string DevicePrefix = "win.device:";
    public const string Battery = "win.battery";
    public const string Cpu = "win.cpu";
    public const string Ram = "win.ram";
    public const string Disk = "win.disk";
    public const string Brightness = "win.brightness";
    public const string Lock = "win.lock";
    public const string Sleep = "win.sleep";
    public const string ScreenOff = "win.screen_off";

    public static readonly string[] SystemValues = [Battery, Cpu, Ram, Disk];

    public static readonly string[] Actions = [Lock, Sleep, ScreenOff];

    /// <summary>Things whose values are only read by polling (while visible).</summary>
    public static bool IsPolled(string entity) =>
        entity is Cpu or Ram or Disk or Wifi or Brightness or Devices or Bluetooth || entity.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Things that need the list of Bluetooth devices.</summary>
    public static bool NeedsDevices(string entity) =>
        entity is Devices or Bluetooth || entity.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What changed – which tiles/status values need an update.</summary>
[Flags]
internal enum Area
{
    None = 0,
    Audio = 1,
    Apps = 2,
    Media = 4,
    Radio = 8,
    Devices = 16,
    Power = 32,
    System = 64,
    Brightness = 128,

    /// <summary>Language or settings changed: texts of everything.</summary>
    Texts = 256,
    All = Audio | Apps | Media | Radio | Devices | Power | System | Brightness | Texts,
}

/// <summary>Settings of the plugin (<see cref="IModuleSettings"/>).</summary>
internal sealed record WindowsSettings(double PollSeconds, bool ShowAppVolume, IReadOnlyList<string> IgnoredMediaApps, bool ShowVirtualOutputs = false)
{
    /// <summary>More outputs do not fit into the choice on the volume tile.</summary>
    public const int MaxOutputs = 4;

    public const string PollKey = "pollSeconds";
    public const string AppVolumeKey = "showAppVolume";
    public const string VirtualOutputsKey = "showVirtualOutputs";
    public const string IgnoredKey = "ignoredMediaApps";
    public const double MinPollSeconds = 2;
    public const string DefaultIgnored = "Teams";

    public static WindowsSettings Default { get; } = From(ModuleSettingValues.Empty);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(MinPollSeconds, PollSeconds));

    public static WindowsSettings From(ModuleSettingValues values) => new(
        Math.Clamp(values.GetNumber(PollKey, MinPollSeconds), MinPollSeconds, 60),
        values.GetBool(AppVolumeKey, true),
        ParseList(values.GetString(IgnoredKey) ?? DefaultIgnored),
        values.GetBool(VirtualOutputsKey, false));

    /// <summary>
    /// Outputs offered on the volume tile: real devices (virtual cables and app channels of mixer
    /// tools only if switched on), at most <see cref="MaxOutputs"/>, the active one always.
    /// </summary>
    public IReadOnlyList<AudioDevice> OfferedOutputs(AudioState audio)
    {
        var offered = audio.Outputs.Where(d => ShowVirtualOutputs || !d.IsVirtual || d.Id == audio.DefaultId).ToList();
        if (offered.Count <= MaxOutputs)
        {
            return offered;
        }

        var result = offered.Where(d => d.Id != audio.DefaultId).Take(MaxOutputs - 1).ToList();
        if (audio.Default is { } active)
        {
            result.Insert(Math.Min(offered.IndexOf(active), result.Count), active);
        }

        return result;
    }

    public static IReadOnlyList<string> ParseList(string text) =>
        [.. text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0)];

    /// <summary>Is the media app on the ignore list (part of its app ID or name, case-insensitive)?</summary>
    public bool IsIgnored(MediaSessionInfo session) =>
        IgnoredMediaApps.Any(i => session.AppId.Contains(i, StringComparison.OrdinalIgnoreCase) || session.AppName.Contains(i, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Which media session the tile and the pill show.</summary>
internal static class MediaChooser
{
    /// <summary>
    /// The user's choice while it exists; otherwise what plays (Windows' own current session first,
    /// then the one that started playing last); otherwise Windows' current one; otherwise the one that
    /// played last.
    /// </summary>
    public static MediaSessionInfo? Choose(
        IReadOnlyList<MediaSessionInfo> sessions,
        string? pinnedId,
        string? windowsCurrentId,
        IReadOnlyDictionary<string, DateTimeOffset> lastPlaying)
    {
        var candidates = sessions.Where(s => s.HasContent || s.IsPlaying).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (pinnedId is not null && candidates.FirstOrDefault(s => s.Id == pinnedId) is { } pinned)
        {
            return pinned;
        }

        DateTimeOffset Last(MediaSessionInfo s) => lastPlaying.TryGetValue(s.Id, out var t) ? t : DateTimeOffset.MinValue;
        var playing = candidates.Where(s => s.IsPlaying).ToList();
        if (playing.Count > 0)
        {
            return playing.FirstOrDefault(s => s.Id == windowsCurrentId) ?? playing.OrderByDescending(Last).First();
        }

        return candidates.FirstOrDefault(s => s.Id == windowsCurrentId) ?? candidates.OrderByDescending(Last).First();
    }
}

/// <summary>Texts for values (UI language, numbers in the Windows region).</summary>
internal static class WinFormat
{
    public static string Percent(double value) => T.Format("Win_Percent", Math.Round(value));

    public static string Gb(double value) => T.Format("Win_Gb", value >= 100 ? Math.Round(value) : Math.Round(value, 1));

    public static string Duration(TimeSpan time)
    {
        var minutes = (int)Math.Round(time.TotalMinutes);
        return minutes < 60
            ? T.Format("Win_Minutes", Math.Max(1, minutes))
            : T.Format("Win_HoursMinutes", minutes / 60, minutes % 60);
    }

    /// <summary>"87 % · charging · full in 35 min", "87 % · 2 h 10 min left", "100 % · plugged in", "No battery".</summary>
    public static string Battery(PowerState power)
    {
        if (!power.HasBattery)
        {
            return T["Win_NoBattery"];
        }

        var percent = Percent(power.Percent);
        if (power.IsCharging)
        {
            return power.UntilFull is { } full
                ? T.Format("Win_BatteryChargingFull", percent, Duration(full))
                : T.Format("Win_BatteryCharging", percent);
        }

        if (power.OnAc)
        {
            return T.Format("Win_BatteryPluggedIn", percent);
        }

        return power.Remaining is { } left
            ? T.Format("Win_BatteryRemaining", percent, Duration(left))
            : percent;
    }

    /// <summary>Second line under the big battery value.</summary>
    public static string BatteryDetail(PowerState power) =>
        !power.HasBattery ? T["Win_NoBattery"]
        : power.IsCharging ? power.UntilFull is { } full ? T.Format("Win_ChargingFullIn", Duration(full)) : T["Win_Charging"]
        : power.OnAc ? T["Win_PluggedIn"]
        : power.Remaining is { } left ? T.Format("Win_TimeLeft", Duration(left))
        : T["Win_OnBattery"];

    /// <summary>Battery icon by level (Segoe Fluent: 0–10 in steps of 10 %, charging variant).</summary>
    public static string BatteryIcon(PowerState power)
    {
        if (!power.HasBattery)
        {
            return "U+E83E"; // plug
        }

        var step = (int)Math.Clamp(Math.Round(power.Percent / 10.0), 0, 10);
        var code = (power.IsCharging || power.OnAc ? 0xEBAB : 0xEBA0) + step;
        return string.Create(CultureInfo.InvariantCulture, $"U+{code:X4}");
    }

    public static string Bars(int? bars) => bars is { } b ? T.Format("Win_SignalBars", b) : T["Win_SignalUnknown"];

    /// <summary>Wi-Fi icon by signal (bars 0–5 → Segoe Fluent Wifi1–4).</summary>
    public static string WifiIcon(RadioState radio) =>
        radio.Wifi != RadioSwitch.On || !radio.WifiConnected ? "Wifi"
        : radio.WifiBars switch
        {
            null => "Wifi",
            <= 1 => "U+E872",
            2 => "U+E873",
            3 => "U+E874",
            _ => "Wifi",
        };

    public static string Radio(RadioSwitch state) => state switch
    {
        RadioSwitch.On => T["Win_On"],
        RadioSwitch.Off => T["Win_Off"],
        RadioSwitch.Disabled => T["Win_RadioAirplane"],
        _ => T["Win_RadioMissing"],
    };

    public static string DeviceState(BluetoothDeviceInfo device) =>
        device.Battery is { } b
            ? device.IsConnected ? T.Format("Win_DeviceBatteryConnected", Percent(b)) : T.Format("Win_DeviceBatteryDisconnected", Percent(b))
            : device.IsConnected ? T["Win_Connected"] : T["Win_Disconnected"];

    public static string DeviceState(DeviceBatteryInfo device) =>
        device.Percent is { } b
            ? device.IsCharging == true ? T.Format("Win_BatteryCharging", Percent(b))
            : device.IsConnected ? T.Format("Win_DeviceBatteryConnected", Percent(b)) : T.Format("Win_DeviceBatteryDisconnected", Percent(b))
            : device.IsConnected ? T["Win_Connected"] : T["Win_Disconnected"];
}
