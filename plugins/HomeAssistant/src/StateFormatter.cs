// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// States in readable form in the UI language (tiles, search, status line, diagnostics) – own
/// mapping per domain. Texts come from the module's resources (keys <c>State_…</c>, R1).
/// </summary>
internal static class StateFormatter
{
    private static readonly HashSet<string> WeatherConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sunny", "clear-night", "cloudy", "partlycloudy", "fog", "rainy", "pouring", "snowy", "snowy-rainy",
        "hail", "lightning", "lightning-rainy", "windy", "windy-variant", "exceptional",
    };

    /// <summary>binary_sensor device classes with their own on/off texts (keys State_Binary_&lt;class&gt;_On/_Off).</summary>
    private static readonly HashSet<string> BinaryClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "door", "garage_door", "opening", "window", "lock", "motion", "occupancy", "presence", "moisture", "battery",
        "battery_charging", "connectivity", "plug", "power", "problem", "safety", "smoke", "gas", "carbon_monoxide",
        "heat", "cold", "light", "sound", "vibration", "moving", "running", "tamper", "update",
    };

    private static readonly HashSet<string> HvacModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "heat", "cool", "heat_cool", "auto", "dry", "fan_only",
    };

    private static string T(string key) => HaText.T[key];

    /// <summary>Text for a known state value (key <c>State_&lt;group&gt;_&lt;value&gt;</c>), otherwise the raw value.</summary>
    private static string Known(string group, string value, IReadOnlyCollection<string> known) =>
        known.Contains(value) ? T($"State_{group}_{Key(value)}") : value;

    /// <summary>Resource-key form of a state value ("clear-night" → "clear_night").</summary>
    private static string Key(string value) => value.ToLowerInvariant().Replace('-', '_');

    public static string HvacMode(string mode) => Known("Hvac", mode, HvacModes);

    public static string Format(HaEntityState? state, HaEntityEntry? entry = null)
    {
        if (state is null)
        {
            return "–";
        }

        switch (state.State)
        {
            case "unavailable":
                return T("State_Unavailable");
            case "unknown" when state.Domain is not ("scene" or "button" or "input_button"):
                return T("State_Unknown");
        }

        switch (state.Domain)
        {
            case "weather":
                var condition = WeatherCondition(state.State);
                return Temperature(state) is { } t ? $"{t} {condition}" : condition;

            case "binary_sensor":
                var deviceClass = state.Attribute<string>("device_class") ?? "";
                return BinaryClasses.Contains(deviceClass)
                    ? T($"State_Binary_{Key(deviceClass)}_{(state.State == "on" ? "On" : "Off")}")
                    : OnOff(state.State);

            case "light" or "switch" or "fan" or "input_boolean" or "automation" or "siren" or "humidifier" or "remote":
                return OnOff(state.State);

            case "media_player":
                return MediaState(state.State);

            case "script":
                return state.State == "on" ? T("State_Script_Running") : T("State_Script_Ready");

            case "scene":
                return T("State_Scene");

            case "button" or "input_button":
                return T("State_Button");

            case "vacuum":
                return VacuumState(state.State);

            case "climate":
                return HvacMode(state.State);

            case "lock":
                return Known("Lock", state.State, ["locked", "unlocked", "locking", "unlocking", "jammed", "open"]);

            case "cover":
                return Known("Cover", state.State, ["open", "closed", "opening", "closing"]);

            case "person" or "device_tracker":
                return Known("Person", state.State, ["home", "not_home"]);
        }

        if (SensorValue(state, entry) is { } value)
        {
            return value.Unit is null ? value.Text : $"{value.Text} {value.Unit}";
        }

        var measurement = state.Attribute<string>("unit_of_measurement");
        return measurement is null ? state.State : $"{state.State} {measurement}";
    }

    /// <summary>Numeric value + unit of a sensor, sensibly rounded; null if the state is not a number.</summary>
    public static (string Text, string? Unit)? SensorValue(HaEntityState state, HaEntityEntry? entry)
    {
        if (state.StateNumber is not { } number)
        {
            return null;
        }

        var decimals = Decimals(state, entry, number);
        var fixedDigits = entry?.DisplayPrecision is not null
            || state.Attribute<string>("device_class") is "temperature" or "humidity";
        return (fixedDigits ? FixedNumber(number, decimals) : Number(number, decimals), state.Attribute<string>("unit_of_measurement"));
    }

    public static string OnOff(string state) => Known("OnOff", state, ["on", "off"]);

    public static string MediaState(string state) =>
        Known("Media", state, ["playing", "paused", "idle", "buffering", "standby", "off", "on"]);

    public static string VacuumState(string state) =>
        Known("Vacuum", state, ["cleaning", "docked", "paused", "idle", "returning", "error"]);

    public static string WeatherCondition(string condition) => Known("Weather", condition, WeatherConditions);

    /// <summary>Temperature of a weather entity, e.g. "6,3 °C" / "6.3 °C".</summary>
    public static string? Temperature(HaEntityState state) =>
        state.Number("temperature") is { } t
            ? $"{Number(t, 1)} {state.Attribute<string>("temperature_unit") ?? "°C"}"
            : null;

    /// <summary>
    /// Sensible decimals: setting from HA (display_precision), otherwise temperature/humidity
    /// 1 digit, otherwise depending on the size.
    /// </summary>
    public static int Decimals(HaEntityState state, HaEntityEntry? entry, double value)
    {
        if (entry?.DisplayPrecision is { } precision)
        {
            return precision;
        }

        if (state.Attribute<string>("device_class") is "temperature" or "humidity")
        {
            return 1;
        }

        var abs = Math.Abs(value);
        return abs >= 100 ? 0 : abs >= 10 ? 1 : 2;
    }

    /// <summary>Number in the UI language's notation, trailing zeros removed (but at most <paramref name="decimals"/>).</summary>
    public static string Number(double value, int decimals)
    {
        var rounded = Math.Round(value, Math.Clamp(decimals, 0, 6), MidpointRounding.AwayFromZero);
        return rounded.ToString("0." + new string('#', Math.Clamp(decimals, 0, 6)), HaText.FormatCulture).TrimEnd(',', '.');
    }

    /// <summary>Like <see cref="Number(double, int)"/>, but always exactly that many digits (for sensors "20,0 °C").</summary>
    public static string FixedNumber(double value, int decimals) =>
        Math.Round(value, Math.Clamp(decimals, 0, 6), MidpointRounding.AwayFromZero)
            .ToString("F" + Math.Clamp(decimals, 0, 6), HaText.FormatCulture);
}
