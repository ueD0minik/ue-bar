// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;
using UeBar.Core.Tiles;

namespace UeBar.Modules.HomeAssistant.Tiles;

/// <summary>
/// ha.light (SPEC 7.1): tap = toggle, slider brightness (if dimmable), with 2x2 additionally
/// color temperature (if color_temp is supported). Capabilities only from supported_color_modes and
/// min/max_color_temp_kelvin – nothing guessed.
/// </summary>
internal sealed class LightTile : HaTile
{
    private static readonly HashSet<string> DimmableModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "brightness", "color_temp", "hs", "xy", "rgb", "rgbw", "rgbww", "white",
    };

    private readonly TapAction _tap = new() { Id = "toggle", Label = HaText.T["Element_Toggle"] };
    private readonly SliderElement _brightness = new() { Id = "brightness", Label = HaText.T["Element_Brightness"], Min = 0, Max = 100, Step = 1, Unit = "%" };
    private readonly SliderElement _colorTemp = new() { Id = "color_temp", Label = HaText.T["Element_ColorTemp"], Step = 50, Unit = " K" };

    public LightTile(HaTileHost host, TileConfig config)
        : base(host, config, "Lightbulb")
    {
    }

    protected override void RenderCore(HaEntityState main)
    {
        var modes = main.Strings("supported_color_modes");
        var dimmable = modes.Any(DimmableModes.Contains);
        var minK = main.Number("min_color_temp_kelvin");
        var maxK = main.Number("max_color_temp_kelvin");
        var hasColorTemp = IsLarge && modes.Contains("color_temp", StringComparer.OrdinalIgnoreCase) && minK is not null && maxK > minK;

        SetElements(_tap, dimmable ? _brightness : null, hasColorTemp ? _colorTemp : null);

        var on = main.State == "on";
        var percent = Percent(main);
        Model.IsActive = on;
        Model.StateText = on && dimmable && percent is { } p ? HaText.T.Format("State_Light_OnPercent", p) : StateFormatter.OnOff(main.State);
        _brightness.Value = on ? percent ?? 100 : 0;

        if (hasColorTemp)
        {
            _colorTemp.Min = minK!.Value;
            _colorTemp.Max = maxK!.Value;
            _colorTemp.Value = Math.Clamp(main.Number("color_temp_kelvin") ?? _colorTemp.Value, minK.Value, maxK.Value);
            _colorTemp.IsEnabled = on;
        }
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        var id = MainEntity!;
        var current = State(id);
        if (current is null)
        {
            return null;
        }

        switch (interaction)
        {
            case { ElementId: "toggle", Kind: InteractionKind.Tap }:
                return new HaAction("light", "toggle", id) { Optimistic = ExpectState(current, Flip(current.State)) };

            case { ElementId: "brightness", Kind: InteractionKind.SetValue, Number: { } value }:
                var pct = Math.Clamp(Math.Round(value), 0, 100);
                if (pct <= 0)
                {
                    return new HaAction("light", "turn_off", id) { Optimistic = ExpectState(current, "off") };
                }

                return new HaAction("light", "turn_on", id, new() { ["brightness_pct"] = Json(pct) })
                {
                    Optimistic = new HaOptimistic(id, current.With("on", ("brightness", Json(Math.Round(pct * 255 / 100)))),
                        s => s.State == "on" && Percent(s) is { } p && Math.Abs(p - pct) <= 2),
                };

            case { ElementId: "color_temp", Kind: InteractionKind.SetValue, Number: { } kelvin }:
                var k = Math.Round(kelvin);
                return new HaAction("light", "turn_on", id, new() { ["color_temp_kelvin"] = Json(k) })
                {
                    Optimistic = new HaOptimistic(id, current.With("on", ("color_temp_kelvin", Json(k)), ("color_mode", Json("color_temp"))),
                        s => s.State == "on" && s.Number("color_temp_kelvin") is { } real && Math.Abs(real - k) <= Math.Max(30, k * 0.02)),
                };
        }

        return null;
    }

    /// <summary>Brightness 0–255 → percent (rounded as in HA).</summary>
    private static double? Percent(HaEntityState state) =>
        state.Number("brightness") is { } b ? Math.Round(b * 100 / 255) : null;
}

/// <summary>ha.toggle: switch, input_boolean, fan – tap = homeassistant.toggle.</summary>
internal sealed class ToggleTile : HaTile
{
    private readonly TapAction _tap = new() { Id = "toggle", Label = HaText.T["Element_Toggle"] };

    public ToggleTile(HaTileHost host, TileConfig config)
        : base(host, config, "Power")
    {
        SetElements(_tap);
    }

    protected override void RenderCore(HaEntityState main)
    {
        Model.IsActive = main.State == "on";
        Model.StateText = StateFormatter.Format(main, Host.Entry(main.EntityId));
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (interaction is not { ElementId: "toggle", Kind: InteractionKind.Tap } || State(MainEntity) is not { } current)
        {
            return null;
        }

        return new HaAction("homeassistant", "toggle", current.EntityId) { Optimistic = ExpectState(current, Flip(current.State)) };
    }
}

/// <summary>
/// ha.script: script (script.turn_on, non-blocking; "Running" while on), scene
/// (scene.turn_on), button (button.press). Short confirmation "Done".
/// </summary>
internal sealed class ScriptTile : HaTile
{
    private readonly TapAction _tap = new() { Id = "run", Label = HaText.T["Element_Run"] };

    public ScriptTile(HaTileHost host, TileConfig config)
        : base(host, config, "Lightning")
    {
        SetElements(_tap);
    }

    /// <summary>Scenes/buttons have the time of the last run as their state – "unknown" = never, not "unavailable".</summary>
    protected override bool IsUnavailable(HaEntityState main) =>
        main.IsUnavailable || (main.IsUnknown && main.Domain == "script");

    protected override void RenderCore(HaEntityState main)
    {
        var running = main.Domain == "script" && main.State == "on";
        Model.IsActive = running;
        Model.StateText = main.Domain switch
        {
            "script" => running ? HaText.T["State_Script_Running"] : HaText.T["State_Script_Ready"],
            "scene" => HaText.T["State_Scene"],
            _ => HaText.T["State_Button"],
        };
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (interaction is not { ElementId: "run", Kind: InteractionKind.Tap } || State(MainEntity) is not { } current)
        {
            return null;
        }

        var id = current.EntityId;
        switch (current.Domain)
        {
            case "script":
                var lastTriggered = current.Text("last_triggered");
                return new HaAction("script", "turn_on", id)
                {
                    // Short scripts are often finished before the state arrives: then last_triggered counts.
                    Optimistic = new HaOptimistic(id, current.With("on"), s => s.State == "on" || s.Text("last_triggered") != lastTriggered),
                    SuccessFlash = HaText.T["Tile_Done"],
                };

            case "scene":
                return new HaAction("scene", "turn_on", id)
                {
                    Optimistic = ChangedFrom(current),
                    SuccessFlash = HaText.T["Tile_Activated"],
                };

            case "button" or "input_button":
                return new HaAction(current.Domain, "press", id)
                {
                    Optimistic = ChangedFrom(current),
                    SuccessFlash = HaText.T["Tile_Done"],
                };
        }

        return null;
    }

    /// <summary>Scene/button: confirmed as soon as the timestamp state changes.</summary>
    private static HaOptimistic ChangedFrom(HaEntityState current)
    {
        var before = current.State;
        return new HaOptimistic(current.EntityId, current, s => s.State != before);
    }
}

/// <summary>ha.select: select/input_select – choice from the entity's options.</summary>
internal sealed class SelectTile : HaTile
{
    private readonly ChoiceElement _choice = new() { Id = "option" };

    public SelectTile(HaTileHost host, TileConfig config)
        : base(host, config, "Settings")
    {
        SetElements(_choice);
    }

    protected override void RenderCore(HaEntityState main)
    {
        SetOptions(_choice, [.. main.Strings("options").Select(o => (o, o))]);
        _choice.SelectedId = main.State;
        Model.StateText = main.State;
        Model.IsActive = false;
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (interaction is not { ElementId: "option", Kind: InteractionKind.Select, Text: { } option }
            || State(MainEntity) is not { } current
            || !current.Strings("options").Contains(option))
        {
            return null;
        }

        return new HaAction(current.Domain == "input_select" ? "input_select" : "select", "select_option", current.EntityId,
            new() { ["option"] = Json(option) })
        {
            Optimistic = ExpectState(current, option),
        };
    }
}

/// <summary>ha.sensor: sensor/binary_sensor – value + unit, sensibly rounded.</summary>
internal sealed class SensorTile : HaTile
{
    private readonly ValueDisplayElement _value = new() { Id = "value" };

    public SensorTile(HaTileHost host, TileConfig config)
        : base(host, config, "Info")
    {
        SetElements(_value);
    }

    protected override void RenderCore(HaEntityState main)
    {
        var entry = Host.Entry(main.EntityId);
        if (StateFormatter.SensorValue(main, entry) is { } value)
        {
            _value.Value = value.Text;
            _value.Unit = value.Unit;
        }
        else
        {
            _value.Value = StateFormatter.Format(main, entry);
            _value.Unit = null;
        }

        Model.StateText = StateFormatter.Format(main, entry);
        Model.IsActive = main.Domain == "binary_sensor" && main.State == "on";
    }

    protected override HaAction? Map(TileInteraction interaction) => null;
}

/// <summary>
/// Fallback for unknown domains (SPEC 7.1): display; tap = homeassistant.toggle only for
/// switchable domains.
/// </summary>
internal sealed class GenericTile : HaTile
{
    private static readonly HashSet<string> Switchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "switch", "light", "fan", "input_boolean", "automation", "siren", "humidifier", "remote",
    };

    private readonly TapAction _tap = new() { Id = "toggle", Label = HaText.T["Element_Toggle"] };

    public GenericTile(HaTileHost host, TileConfig config)
        : base(host, config, "Home")
    {
    }

    public static bool IsSwitchable(string domain) => Switchable.Contains(domain);

    protected override void RenderCore(HaEntityState main)
    {
        SetElements(IsSwitchable(main.Domain) ? _tap : null);
        Model.StateText = StateFormatter.Format(main, Host.Entry(main.EntityId));
        Model.IsActive = main.State == "on";
    }

    protected override HaAction? Map(TileInteraction interaction)
    {
        if (interaction is not { ElementId: "toggle", Kind: InteractionKind.Tap }
            || State(MainEntity) is not { } current
            || !IsSwitchable(current.Domain))
        {
            return null;
        }

        return new HaAction("homeassistant", "toggle", current.EntityId) { Optimistic = ExpectState(current, Flip(current.State)) };
    }
}
