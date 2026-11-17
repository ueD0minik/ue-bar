// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;

namespace UeBar.Modules.HomeAssistant.Tiles;

/// <summary>
/// ha.climate_ir (composite, SPEC 7.1): on/off through the switch <c>power</c>,
/// target temperature through the number entity <c>target</c> (limits/step from min/max/step),
/// fan through the select entity <c>fan</c> (options from options).
/// </summary>
internal sealed class ClimateIrTile : HaTile
{
    private readonly TapAction _tap = new() { Id = "power", Label = HaText.T["Element_Power"] };
    private readonly StepperElement _target = new() { Id = "target", Label = HaText.T["Slot_Target"] };
    private readonly ChoiceElement _fan = new() { Id = "fan", Label = HaText.T["Slot_Fan"] };

    public ClimateIrTile(HaTileHost host, TileConfig config)
        : base(host, config, "Snow")
    {
    }

    protected override string? MainEntity => Config.GetEntity("power") ?? Config.Entities.Values.FirstOrDefault();

    private string? TargetId => Config.GetEntity("target");

    private string? FanId => Config.GetEntity("fan");

    protected override string PickIcon(string entityId, HaEntityState? state, HaEntityEntry? entry) => "Snow";

    protected override void RenderCore(HaEntityState main)
    {
        var target = State(TargetId);
        var fan = State(FanId);
        SetElements(_tap, TargetId is null ? null : _target, FanId is null ? null : _fan);

        var on = main.State == "on";
        Model.IsActive = on;

        var parts = new List<string> { StateFormatter.OnOff(on ? "on" : "off") };
        if (target is not null && !target.IsUnavailable && target.StateNumber is { } value)
        {
            var step = target.Number("step") ?? 1;
            _target.Min = target.Number("min") ?? _target.Min;
            _target.Max = target.Number("max") ?? _target.Max;
            _target.Step = step > 0 ? step : 1;
            _target.Decimals = Decimals(_target.Step);
            _target.Unit = target.Attribute<string>("unit_of_measurement") is { } unit ? " " + unit : null;
            _target.Value = value;
            if (on)
            {
                parts.Add(StateFormatter.FixedNumber(value, _target.Decimals) + (_target.Unit ?? ""));
            }
        }
        else
        {
            _target.IsEnabled = false;
        }

        if (fan is not null && !fan.IsUnavailable && fan.Strings("options") is { Count: > 0 } options)
        {
            SetOptions(_fan, [.. options.Select(o => (o, o))]);
            _fan.SelectedId = fan.State;
            if (on)
            {
                parts.Add(fan.State);
            }
        }
        else
        {
            _fan.IsEnabled = false;
        }

        Model.StateText = string.Join(" · ", parts);
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        switch (interaction)
        {
            case { ElementId: "power", Kind: InteractionKind.Tap } when State(MainEntity) is { } power:
                return new HaAction(power.Domain == "input_boolean" ? "input_boolean" : "switch", "toggle", power.EntityId)
                {
                    Optimistic = ExpectState(power, Flip(power.State)),
                };

            case { ElementId: "target" } when State(TargetId) is { StateNumber: { } current } target:
                var step = target.Number("step") is { } s && s > 0 ? s : 1;
                var min = target.Number("min") ?? double.MinValue;
                var max = target.Number("max") ?? double.MaxValue;
                double? wanted = interaction.Kind switch
                {
                    InteractionKind.Increment => current + step,
                    InteractionKind.Decrement => current - step,
                    InteractionKind.SetValue => interaction.Number,
                    _ => null,
                };
                if (wanted is not { } raw)
                {
                    return null;
                }

                var value = Math.Clamp(Math.Round(raw / step) * step, min, max);
                if (Math.Abs(value - current) < step / 2)
                {
                    return null; // already at the limit
                }

                return new HaAction(target.Domain == "input_number" ? "input_number" : "number", "set_value", target.EntityId,
                    new() { ["value"] = Json(value) })
                {
                    Optimistic = new HaOptimistic(target.EntityId, target.With(Invariant(value)),
                        st => st.StateNumber is { } n && Math.Abs(n - value) < step / 2),
                };

            case { ElementId: "fan", Kind: InteractionKind.Select, Text: { } option } when State(FanId) is { } fan
                && fan.Strings("options").Contains(option):
                return new HaAction(fan.Domain == "input_select" ? "input_select" : "select", "select_option", fan.EntityId,
                    new() { ["option"] = Json(option) })
                {
                    Optimistic = ExpectState(fan, option),
                };
        }

        return null;
    }

    private static int Decimals(double step) =>
        step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
}

/// <summary>ha.climate: mode (hvac_modes) + target temperature (temperature, min/max_temp, target_temp_step).</summary>
internal sealed class ClimateTile : HaTile
{
    private readonly ChoiceElement _mode = new() { Id = "mode", Label = HaText.T["Element_Mode"] };
    private readonly StepperElement _target = new() { Id = "target", Label = HaText.T["Slot_Target"] };

    public ClimateTile(HaTileHost host, TileConfig config)
        : base(host, config, "Thermometer")
    {
    }

    protected override void RenderCore(HaEntityState main)
    {
        var modes = main.Strings("hvac_modes");
        var hasTarget = main.Number("temperature") is not null;
        SetElements(hasTarget ? _target : null, modes.Count > 0 ? _mode : null);

        SetOptions(_mode, [.. modes.Select(m => (m, StateFormatter.HvacMode(m)))]);
        _mode.SelectedId = main.State;

        var unit = main.Attribute<string>("temperature_unit") ?? "°C";
        var step = Step(main);
        _target.Step = step;
        _target.Decimals = step >= 1 ? 0 : 1;
        _target.Min = main.Number("min_temp") ?? 7;
        _target.Max = main.Number("max_temp") ?? 35;
        _target.Unit = " " + unit;
        if (main.Number("temperature") is { } t)
        {
            _target.Value = t;
        }

        Model.IsActive = main.State != "off";
        var text = StateFormatter.HvacMode(main.State);
        if (main.State != "off" && main.Number("temperature") is { } target)
        {
            text += $" · {StateFormatter.FixedNumber(target, _target.Decimals)} {unit}";
        }

        if (main.Number("current_temperature") is { } current)
        {
            text += " " + HaText.T.Format("Climate_Current", StateFormatter.FixedNumber(current, 1), unit);
        }

        Model.StateText = text;
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (State(MainEntity) is not { } current)
        {
            return null;
        }

        switch (interaction)
        {
            case { ElementId: "mode", Kind: InteractionKind.Select, Text: { } mode } when current.Strings("hvac_modes").Contains(mode):
                return new HaAction("climate", "set_hvac_mode", current.EntityId, new() { ["hvac_mode"] = Json(mode) })
                {
                    Optimistic = ExpectState(current, mode),
                };

            case { ElementId: "target" } when current.Number("temperature") is { } temperature:
                var step = Step(current);
                double? wanted = interaction.Kind switch
                {
                    InteractionKind.Increment => temperature + step,
                    InteractionKind.Decrement => temperature - step,
                    InteractionKind.SetValue => interaction.Number,
                    _ => null,
                };
                if (wanted is not { } raw)
                {
                    return null;
                }

                var value = Math.Clamp(Math.Round(raw / step) * step, current.Number("min_temp") ?? double.MinValue, current.Number("max_temp") ?? double.MaxValue);
                return new HaAction("climate", "set_temperature", current.EntityId, new() { ["temperature"] = Json(value) })
                {
                    Optimistic = new HaOptimistic(current.EntityId, current.With(null, ("temperature", Json(value))),
                        s => s.Number("temperature") is { } t && Math.Abs(t - value) < step / 2),
                };
        }

        return null;
    }

    /// <summary>target_temp_step, otherwise like the HA frontend: 0.5 for °C, 1 otherwise.</summary>
    private static double Step(HaEntityState state) =>
        state.Number("target_temp_step") is { } s && s > 0 ? s
            : (state.Attribute<string>("temperature_unit") ?? "°C") == "°C" ? 0.5 : 1;
}

/// <summary>
/// ha.media: MediaInfo (cover, title, artist, progress), button row only with supported
/// buttons (supported_features), volume slider (if VOLUME_SET).
/// </summary>
internal sealed class MediaTile : HaTile
{
    private readonly MediaInfoElement _info = new() { Id = "info" };
    private readonly ButtonRowElement _controls = new() { Id = "controls" };
    private readonly SliderElement _volume = new() { Id = "volume", Label = HaText.T["Element_Volume"], Min = 0, Max = 100, Step = 1, Unit = "%" };
    private readonly TileButton _power = new() { Id = "power", Icon = "Power", Label = HaText.T["Element_Power"] };
    private readonly TileButton _previous = new() { Id = "previous", Icon = "Previous", Label = HaText.T["Element_Previous"] };
    private readonly TileButton _playPause = new() { Id = "playpause", Icon = "Play", Label = HaText.T["Element_Play"] };
    private readonly TileButton _next = new() { Id = "next", Icon = "Next", Label = HaText.T["Element_Next"] };
    private readonly TileButton _mute = new() { Id = "mute", Icon = "Volume", Label = HaText.T["Element_Mute"] };

    public MediaTile(HaTileHost host, TileConfig config)
        : base(host, config, "Tv")
    {
    }

    /// <summary>Off/standby is not "unavailable" – then it can be switched on.</summary>
    protected override bool IsUnavailable(HaEntityState main) => main.IsUnavailable || main.IsUnknown;

    protected override void RenderCore(HaEntityState main)
    {
        var f = main.Features;
        var on = main.State is not ("off" or "standby");
        var canPlay = (f & (MediaFeatures.Pause | MediaFeatures.Play)) != 0;

        var buttons = new List<TileButton>();
        if ((f & (MediaFeatures.TurnOn | MediaFeatures.TurnOff)) != 0)
        {
            buttons.Add(_power);
        }

        if ((f & MediaFeatures.PreviousTrack) != 0)
        {
            buttons.Add(_previous);
        }

        if (canPlay)
        {
            buttons.Add(_playPause);
        }

        if ((f & MediaFeatures.NextTrack) != 0)
        {
            buttons.Add(_next);
        }

        if ((f & MediaFeatures.VolumeMute) != 0)
        {
            buttons.Add(_mute);
        }

        SetButtons(_controls, buttons);
        SetElements(_info, buttons.Count > 0 ? _controls : null, (f & MediaFeatures.VolumeSet) != 0 ? _volume : null);

        var playing = main.State == "playing";
        _info.IsPlaying = playing;
        _info.Title = main.Attribute<string>("media_title") ?? main.Attribute<string>("app_name");
        _info.Artist = main.Attribute<string>("media_artist") ?? main.Attribute<string>("media_series_title") ?? main.Attribute<string>("media_album_name");
        _info.Album = main.Attribute<string>("media_album_name");
        _info.Duration = main.Number("media_duration") is { } d && d > 0 ? d : null;
        _info.Position = main.Number("media_position");
        _info.PositionUpdatedAt = DateTimeOffset.TryParse(main.Text("media_position_updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
        _info.ImageUrl = HaImages.IdFor(main);

        _playPause.Icon = playing ? "Pause" : "Play";
        _playPause.Label = playing ? HaText.T["Element_Pause"] : HaText.T["Element_Play"];
        _playPause.IsActive = playing;
        _playPause.IsEnabled = on;
        _previous.IsEnabled = on;
        _next.IsEnabled = on;
        _power.IsActive = on;
        _power.Label = on ? HaText.T["Element_TurnOff"] : HaText.T["Element_TurnOn"];
        var muted = main.Attribute<bool>("is_volume_muted");
        _mute.Icon = muted ? "Mute" : "Volume";
        _mute.Label = muted ? HaText.T["Element_Unmute"] : HaText.T["Element_Mute"];
        _mute.IsActive = muted;
        _mute.IsEnabled = on;

        _volume.Value = Math.Round((main.Number("volume_level") ?? 0) * 100);
        _volume.IsEnabled = on && main.Number("volume_level") is not null;

        Model.IsActive = playing || main.State == "on";
        var state = StateFormatter.MediaState(main.State);
        Model.StateText = main.State == "on" && Source(main) is { } source ? $"{state} · {source}" : state;
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (State(MainEntity) is not { } current)
        {
            return null;
        }

        var id = current.EntityId;
        var f = current.Features;
        switch (interaction)
        {
            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "playpause" }:
                var playing = current.State == "playing";
                var service = (f & MediaFeatures.Play) != 0 && (f & MediaFeatures.Pause) != 0 ? "media_play_pause"
                    : playing ? "media_pause" : "media_play";
                return new HaAction("media_player", service, id) { Optimistic = ExpectState(current, playing ? "paused" : "playing") };

            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "next" }:
                return new HaAction("media_player", "media_next_track", id);

            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "previous" }:
                return new HaAction("media_player", "media_previous_track", id);

            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "power" }:
                var isOff = current.State is "off" or "standby";
                if (isOff && (f & MediaFeatures.TurnOn) != 0)
                {
                    return new HaAction("media_player", "turn_on", id)
                    {
                        Optimistic = new HaOptimistic(id, current.With("on"), s => s.State is not ("off" or "standby")),
                    };
                }

                return !isOff && (f & MediaFeatures.TurnOff) != 0
                    ? new HaAction("media_player", "turn_off", id) { Optimistic = ExpectState(current, "off") }
                    : null;

            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "mute" }:
                var muted = !current.Attribute<bool>("is_volume_muted");
                return new HaAction("media_player", "volume_mute", id, new() { ["is_volume_muted"] = Json(muted) })
                {
                    Optimistic = new HaOptimistic(id, current.With(null, ("is_volume_muted", Json(muted))), s => s.Attribute<bool>("is_volume_muted") == muted),
                };

            case { ElementId: "volume", Kind: InteractionKind.SetValue, Number: { } value }:
                var level = Math.Round(Math.Clamp(value, 0, 100)) / 100;
                return new HaAction("media_player", "volume_set", id, new() { ["volume_level"] = Json(level) })
                {
                    Optimistic = new HaOptimistic(id, current.With(null, ("volume_level", Json(level))),
                        s => s.Number("volume_level") is { } v && Math.Abs(v - level) <= 0.015),
                };
        }

        return null;
    }

    private static string? Source(HaEntityState state) =>
        state.Attribute<string>("source") is { } source
            ? source.StartsWith("hdmi", StringComparison.OrdinalIgnoreCase) ? source.ToUpperInvariant().Replace("HDMI", "HDMI ", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal) : source
            : null;
}

/// <summary>ha.vacuum: start/pause/return to dock (only supported ones), battery from slot "battery" or the attribute battery_level.</summary>
internal sealed class VacuumTile : HaTile
{
    private readonly ButtonRowElement _controls = new() { Id = "controls" };
    private readonly ValueDisplayElement _battery = new() { Id = "battery", Label = HaText.T["Element_Battery"], Unit = "%" };
    private readonly TileButton _start = new() { Id = "start", Icon = "Play", Label = HaText.T["Element_Start"] };
    private readonly TileButton _pause = new() { Id = "pause", Icon = "Pause", Label = HaText.T["Element_Pause"] };
    private readonly TileButton _home = new() { Id = "return", Icon = "Home", Label = HaText.T["Element_Dock"] };

    public VacuumTile(HaTileHost host, TileConfig config)
        : base(host, config, "Robot")
    {
    }

    private string? BatteryId => Config.GetEntity("battery");

    protected override void RenderCore(HaEntityState main)
    {
        var f = main.Features;
        var buttons = new List<TileButton>();
        if ((f & VacuumFeatures.Start) != 0)
        {
            buttons.Add(_start);
        }

        if ((f & VacuumFeatures.Pause) != 0)
        {
            buttons.Add(_pause);
        }

        if ((f & VacuumFeatures.ReturnHome) != 0)
        {
            buttons.Add(_home);
        }

        SetButtons(_controls, buttons);

        var battery = Battery(main);
        SetElements(buttons.Count > 0 ? _controls : null, battery is null ? null : _battery);

        _start.IsActive = main.State == "cleaning";
        _pause.IsActive = main.State == "paused";
        _home.IsActive = main.State is "docked" or "returning";
        _battery.Value = battery is { } b ? StateFormatter.Number(b, 0) : "–";

        Model.IsActive = main.State is "cleaning" or "returning";
        var text = StateFormatter.VacuumState(main.State);
        Model.StateText = battery is { } level ? $"{text} · {StateFormatter.Number(level, 0)} %" : text;
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (interaction is not { ElementId: "controls", Kind: InteractionKind.Press, Text: { } button } || State(MainEntity) is not { } current)
        {
            return null;
        }

        var before = current.State;
        var (service, expected) = button switch
        {
            "start" => ("start", "cleaning"),
            "pause" => ("pause", "paused"),
            "return" => ("return_to_base", "returning"),
            _ => (null, null),
        };

        // Confirmed as soon as the state changes (a robot vacuum e.g. jumps quickly from "returning" to "docked").
        return service is null
            ? null
            : new HaAction("vacuum", service, current.EntityId)
            {
                Optimistic = new HaOptimistic(current.EntityId, current.With(expected), s => s.State == expected || s.State != before),
            };
    }

    private double? Battery(HaEntityState main) =>
        State(BatteryId) is { StateNumber: { } fromSensor } ? fromSensor : main.Number("battery_level");
}

/// <summary>media_player supported_features (HA MediaPlayerEntityFeature).</summary>
internal static class MediaFeatures
{
    public const long Pause = 1;
    public const long Seek = 2;
    public const long VolumeSet = 4;
    public const long VolumeMute = 8;
    public const long PreviousTrack = 16;
    public const long NextTrack = 32;
    public const long TurnOn = 128;
    public const long TurnOff = 256;
    public const long PlayMedia = 512;
    public const long VolumeStep = 1024;
    public const long SelectSource = 2048;
    public const long Stop = 4096;
    public const long Play = 16384;
}

/// <summary>vacuum supported_features (HA VacuumEntityFeature).</summary>
internal static class VacuumFeatures
{
    public const long Pause = 4;
    public const long Stop = 8;
    public const long ReturnHome = 16;
    public const long FanSpeed = 32;
    public const long Battery = 64;
    public const long Locate = 512;
    public const long State = 4096;
    public const long Start = 8192;
}
