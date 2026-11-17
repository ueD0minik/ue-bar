// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;

namespace UeBar.Modules.Dummy;

internal enum DummyCategory
{
    Switch,
    Dimmer,
    Thermostat,
    Media,
    Crash,
}

/// <summary>A fake device; all values in memory only. <paramref name="nameKey"/> is a resource key.</summary>
internal sealed class DummyDevice(string id, string nameKey, string icon, DummyCategory category)
{
    public static readonly (string Title, string Artist, double Duration)[] Tracks =
    [
        ("Morning Light", "The Test Band", 214),
        ("Night Bus", "Placeholder & Co.", 187),
        ("Tile Waltz", "Dummy Orchestra", 251),
    ];

    /// <summary>Short name for the choice chips, resource key of the long one for the state text.</summary>
    public static readonly (string Id, string Label, string Long)[] FanModes =
    [
        ("auto", "Auto", "Dummy_FanAuto"),
        ("low", "1", "Dummy_Fan1"),
        ("mid", "2", "Dummy_Fan2"),
        ("high", "3", "Dummy_Fan3"),
    ];

    public string Id { get; } = id;

    public string Name => DummyText.T[nameKey];

    public string Icon { get; } = icon;

    public DummyCategory Category { get; } = category;

    public bool On { get; set; }

    /// <summary>Brightness 0–100.</summary>
    public double Level { get; set; } = 60;

    /// <summary>Target temperature in °C.</summary>
    public double Target { get; set; } = 22;

    public string FanMode { get; set; } = "auto";

    public bool Playing { get; set; }

    public int TrackIndex { get; set; }

    public double Volume { get; set; } = 30;

    public string StateText => Category switch
    {
        DummyCategory.Switch => On ? DummyText.T["Dummy_On"] : DummyText.T["Dummy_Off"],
        DummyCategory.Dimmer => On ? DummyText.T.Format("Dummy_OnLevel", Level) : DummyText.T["Dummy_Off"],
        DummyCategory.Thermostat => On
            ? DummyText.T.Format("Dummy_OnThermostat", Target, FanLabel)
            : DummyText.T["Dummy_Off"],
        DummyCategory.Media => Playing ? DummyText.T.Format("Dummy_Playing", Tracks[TrackIndex].Title) : DummyText.T["Dummy_Paused"],
        DummyCategory.Crash => DummyText.T["Dummy_CrashState"],
        _ => "",
    };

    public string FanLabel => FanModes.FirstOrDefault(f => f.Id == FanMode).Long is { } key ? DummyText.T[key] : FanMode;
}
