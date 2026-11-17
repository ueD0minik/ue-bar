// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Icons for HA entities as the app's icon names (SPEC 4.2, Segoe Fluent Icons).
/// Order: the tile's own icon → <c>mdi:*</c> from registry/attribute → device_class →
/// domain. New icons: simply add them below in <see cref="Mdi"/> (key = mdi name without
/// "mdi:"; an entry ending in "*" matches all names starting with it).
/// </summary>
internal static class HaIcons
{
    /// <summary>mdi name → icon name of the app. Most common icons, extendable.</summary>
    private static readonly (string Mdi, string Icon)[] Mdi =
    [
        ("lightbulb*", "Lightbulb"),
        ("ceiling-light*", "Lightbulb"),
        ("desk-lamp*", "Lightbulb"),
        ("lamp*", "Lightbulb"),
        ("floor-lamp*", "Lightbulb"),
        ("led-strip*", "Lightbulb"),
        ("light*", "Lightbulb"),
        ("power*", "Power"),
        ("toggle-switch*", "Power"),
        ("light-switch*", "Power"),
        ("air-conditioner", "Snow"),
        ("snowflake*", "Snow"),
        ("thermometer*", "Thermometer"),
        ("thermostat*", "Thermometer"),
        ("radiator*", "Fire"),
        ("fire*", "Fire"),
        ("fan*", "Fan"),
        ("television*", "Tv"),
        ("tv*", "Tv"),
        ("monitor*", "Monitor"),
        ("desktop*", "Monitor"),
        ("laptop*", "Laptop"),
        ("desk", "Laptop"),
        ("speaker*", "Speaker"),
        ("cast*", "Speaker"),
        ("headphones*", "Headphones"),
        ("music*", "MusicNote"),
        ("play*", "Play"),
        ("robot-vacuum*", "Robot"),
        ("robot*", "Robot"),
        ("broom", "Broom"),
        ("shower*", "Drop"),
        ("water*", "Drop"),
        ("water-percent", "Drop"),
        ("weather-night*", "Moon"),
        ("weather-sunny*", "Sun"),
        ("weather-partly*", "PartlyCloudy"),
        ("weather-cloudy*", "Cloud"),
        ("weather-rainy", "Rain"),
        ("weather-pouring", "Rain"),
        ("weather-snowy*", "Snow"),
        ("weather-lightning*", "Storm"),
        ("weather*", "Cloud"),
        ("sleep*", "Moon"),
        ("bed*", "Moon"),
        ("moon*", "Moon"),
        ("white-balance-sunny", "Sun"),
        ("exit-run", "Run"),
        ("run*", "Run"),
        ("walk*", "Walk"),
        ("motion-sensor*", "Walk"),
        ("home*", "Home"),
        ("house*", "Home"),
        ("lock-open*", "Unlock"),
        ("lock*", "Lock"),
        ("door*", "Door"),
        ("window*", "Window"),
        ("battery*", "Battery"),
        ("flash*", "Lightning"),
        ("lightning*", "Lightning"),
        ("script*", "Lightning"),
        ("palette*", "Palette"),
        ("bell*", "Bell"),
        ("calendar*", "Calendar"),
        ("clock*", "Clock"),
        ("timer*", "Clock"),
        ("account*", "Person"),
        ("human*", "Person"),
        ("car*", "Car"),
        ("cellphone*", "Phone"),
        ("phone*", "Phone"),
        ("camera*", "Camera"),
        ("microphone*", "Mic"),
        ("wifi*", "Wifi"),
        ("leaf*", "Leaf"),
        ("gauge*", "Gauge"),
        ("eye*", "Eye"),
        ("cog*", "Settings"),
        ("star*", "Star"),
        ("heart*", "Heart"),
    ];

    /// <summary>Longest matching entry wins ("lightning*" before "light*"), exact entries first.</summary>
    private static readonly (string Mdi, string Icon)[] Ordered =
        [.. Mdi.OrderBy(e => e.Mdi.EndsWith('*') ? 1 : 0).ThenByDescending(e => e.Mdi.Length)];

    /// <summary>Icon for an entity (without an own tile icon).</summary>
    public static string For(string entityId, HaEntityState? state, HaEntityEntry? entry)
    {
        if (FromMdi(entry?.Icon) is { } fromRegistry)
        {
            return fromRegistry;
        }

        if (FromMdi(state?.Attribute<string>("icon")) is { } fromAttribute)
        {
            return fromAttribute;
        }

        var domain = entityId.Split('.', 2)[0];
        if (state is not null && FromDeviceClass(domain, state.Attribute<string>("device_class")) is { } fromClass)
        {
            return fromClass;
        }

        return ForDomain(domain);
    }

    /// <summary>mdi name (with or without "mdi:") → icon name or null.</summary>
    public static string? FromMdi(string? mdi)
    {
        if (string.IsNullOrWhiteSpace(mdi))
        {
            return null;
        }

        var name = mdi.StartsWith("mdi:", StringComparison.OrdinalIgnoreCase) ? mdi[4..] : mdi;
        foreach (var (key, icon) in Ordered)
        {
            var match = key.EndsWith('*')
                ? name.StartsWith(key[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(key, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                return icon;
            }
        }

        return null;
    }

    public static string? FromDeviceClass(string domain, string? deviceClass) => (domain, deviceClass) switch
    {
        (_, null) => null,
        (_, "temperature") => "Thermometer",
        (_, "humidity" or "moisture") => "Drop",
        (_, "battery" or "battery_charging") => "Battery",
        (_, "power" or "energy" or "current" or "voltage" or "plug") => "Lightning",
        (_, "illuminance" or "light") => "Sun",
        (_, "door" or "garage_door" or "opening") => "Door",
        (_, "window") => "Window",
        (_, "lock") => "Lock",
        (_, "motion" or "occupancy" or "presence" or "moving") => "Walk",
        (_, "connectivity") => "Wifi",
        (_, "smoke" or "gas" or "carbon_monoxide" or "problem" or "safety" or "tamper") => "Warning",
        (_, "tv") => "Tv",
        (_, "speaker" or "receiver") => "Speaker",
        (_, "outlet") => "Power",
        _ => null,
    };

    public static string ForDomain(string domain) => domain switch
    {
        "light" => "Lightbulb",
        "switch" or "input_boolean" => "Power",
        "fan" => "Fan",
        "script" or "button" or "input_button" => "Lightning",
        "scene" => "Palette",
        "automation" => "Lightning",
        "climate" or "water_heater" => "Thermometer",
        "media_player" => "Tv",
        "vacuum" => "Robot",
        "weather" => "Cloud",
        "select" or "input_select" => "Settings",
        "number" or "input_number" => "Gauge",
        "sensor" => "Info",
        "binary_sensor" => "Info",
        "lock" => "Lock",
        "cover" => "Window",
        "person" or "device_tracker" => "Person",
        "camera" => "Camera",
        "todo" => "Checkmark",
        _ => "Home",
    };

    /// <summary>Weather condition → icon (for the status line).</summary>
    public static string ForWeather(string? condition) => condition switch
    {
        "sunny" => "Sun",
        "clear-night" => "Moon",
        "partlycloudy" => "PartlyCloudy",
        "cloudy" or "fog" or "windy" or "windy-variant" => "Cloud",
        "rainy" or "pouring" => "Rain",
        "snowy" or "snowy-rainy" or "hail" => "Snow",
        "lightning" or "lightning-rainy" or "exceptional" => "Storm",
        _ => "Cloud",
    };
}
