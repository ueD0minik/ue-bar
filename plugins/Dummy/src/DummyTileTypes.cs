// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Modules.Dummy;

/// <summary>Shared base of the test tile types: builds elements, updates them from the device, handles interaction.</summary>
internal abstract class DummyTileType(DummyModule module) : ITileType
{
    protected DummyModule Module { get; } = module;

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract string Icon { get; }

    public abstract DummyCategory Category { get; }

    public virtual IReadOnlyList<TileSize> AllowedSizes => TileSize.All;

    public virtual TileSize DefaultSize => TileSize.Small;

    public IReadOnlyList<TileSlot> Slots => [new TileSlot("main", DummyText.T["Dummy_SlotDevice"], true, Category.ToString())];

    public TileModel CreateTile(TileConfig config)
    {
        var device = Module.GetDevice(config.GetEntity("main"));
        if (device.Category != Category)
        {
            throw new InvalidOperationException($"Device '{device.Id}' does not fit the tile type '{Id}'.");
        }

        var tile = new TileModel
        {
            Title = device.Name,
            Icon = device.Icon,
        };
        Build(tile);
        tile.Handler = new DelegateInteractionHandler((t, i) => Handle(device.Id, i));
        Refresh(tile, device);
        Module.Track(device.Id, tile, this);
        return tile;
    }

    /// <summary>Create the elements.</summary>
    protected abstract void Build(TileModel tile);

    /// <summary>Transfer values from the device into the tile.</summary>
    public virtual void Refresh(TileModel tile, DummyDevice device)
    {
        tile.StateText = device.StateText;
        tile.IsActive = device.Category == DummyCategory.Media ? device.Playing : device.On;
    }

    protected abstract void Handle(string entityId, TileInteraction interaction);
}

internal sealed class SwitchTileType(DummyModule module) : DummyTileType(module)
{
    public override string Id => "dummy.switch";

    public override string DisplayName => DummyText.T["Dummy_TypeSwitch"];

    public override string Icon => "ToggleRight";

    public override DummyCategory Category => DummyCategory.Switch;

    protected override void Build(TileModel tile) =>
        tile.Elements.Add(new TapAction { Id = "toggle", Label = DummyText.T["Dummy_Toggle"] });

    protected override void Handle(string entityId, TileInteraction interaction)
    {
        if (interaction is { ElementId: "toggle", Kind: InteractionKind.Tap })
        {
            Module.Update(entityId, d => d.On = !d.On);
        }
    }
}

internal sealed class DimmerTileType(DummyModule module) : DummyTileType(module)
{
    public override string Id => "dummy.dimmer";

    public override string DisplayName => DummyText.T["Dummy_TypeDimmer"];

    public override string Icon => "Lightbulb";

    public override DummyCategory Category => DummyCategory.Dimmer;

    public override TileSize DefaultSize => TileSize.Wide;

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "toggle", Label = DummyText.T["Dummy_Toggle"] });
        tile.Elements.Add(new SliderElement { Id = "brightness", Label = DummyText.T["Dummy_Brightness"], Min = 0, Max = 100, Step = 1, Unit = "%" });
    }

    public override void Refresh(TileModel tile, DummyDevice device)
    {
        base.Refresh(tile, device);
        if (tile.FindElement<SliderElement>("brightness") is { } slider)
        {
            slider.Value = device.On ? device.Level : 0;
        }
    }

    protected override void Handle(string entityId, TileInteraction interaction)
    {
        switch (interaction)
        {
            case { ElementId: "toggle", Kind: InteractionKind.Tap }:
                Module.Update(entityId, d => d.On = !d.On);
                break;
            case { ElementId: "brightness", Kind: InteractionKind.SetValue, Number: { } value }:
                Module.Update(entityId, d =>
                {
                    d.Level = Math.Clamp(value, 0, 100);
                    d.On = d.Level > 0;
                });
                break;
        }
    }
}

internal sealed class ThermostatTileType(DummyModule module) : DummyTileType(module)
{
    public override string Id => "dummy.thermostat";

    public override string DisplayName => DummyText.T["Dummy_TypeThermostat"];

    public override string Icon => "Thermometer";

    public override DummyCategory Category => DummyCategory.Thermostat;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Wide, TileSize.Large];

    public override TileSize DefaultSize => TileSize.Large;

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "power", Label = DummyText.T["Dummy_Power"] });
        tile.Elements.Add(new StepperElement { Id = "target", Label = DummyText.T["Dummy_Target"], Min = 16, Max = 30, Step = 0.5, Unit = "°C", Decimals = 1 });
        var choice = new ChoiceElement { Id = "fan", Label = DummyText.T["Dummy_FanChoice"] };
        foreach (var (id, label, _) in DummyDevice.FanModes)
        {
            choice.Options.Add(new ChoiceOption { Id = id, Label = label });
        }

        tile.Elements.Add(choice);
    }

    public override void Refresh(TileModel tile, DummyDevice device)
    {
        base.Refresh(tile, device);
        if (tile.FindElement<StepperElement>("target") is { } stepper)
        {
            stepper.Value = device.Target;
        }

        if (tile.FindElement<ChoiceElement>("fan") is { } fan)
        {
            fan.SelectedId = device.FanMode;
        }
    }

    protected override void Handle(string entityId, TileInteraction interaction)
    {
        switch (interaction)
        {
            case { ElementId: "power", Kind: InteractionKind.Tap }:
                Module.Update(entityId, d => d.On = !d.On);
                break;
            case { ElementId: "target", Kind: InteractionKind.Increment }:
                Module.Update(entityId, d => d.Target = Math.Min(30, d.Target + 0.5));
                break;
            case { ElementId: "target", Kind: InteractionKind.Decrement }:
                Module.Update(entityId, d => d.Target = Math.Max(16, d.Target - 0.5));
                break;
            case { ElementId: "target", Kind: InteractionKind.SetValue, Number: { } value }:
                Module.Update(entityId, d => d.Target = Math.Clamp(Math.Round(value * 2) / 2, 16, 30));
                break;
            case { ElementId: "fan", Kind: InteractionKind.Select, Text: { } mode }
                when DummyDevice.FanModes.Any(f => f.Id == mode):
                Module.Update(entityId, d => d.FanMode = mode);
                break;
        }
    }
}

internal sealed class MediaTileType(DummyModule module) : DummyTileType(module)
{
    public override string Id => "dummy.media";

    public override string DisplayName => DummyText.T["Dummy_TypeMedia"];

    public override string Icon => "MusicNote";

    public override DummyCategory Category => DummyCategory.Media;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Wide, TileSize.Large];

    public override TileSize DefaultSize => TileSize.Large;

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "playpause", Label = DummyText.T["Dummy_PlayPause"] });
        tile.Elements.Add(new MediaInfoElement { Id = "info" });
        tile.Elements.Add(new ButtonRowElement
        {
            Id = "controls",
            Buttons =
            [
                new TileButton { Id = "previous", Icon = "Previous", Label = DummyText.T["Dummy_Previous"] },
                new TileButton { Id = "playpause", Icon = "Play", Label = DummyText.T["Dummy_Play"] },
                new TileButton { Id = "next", Icon = "Next", Label = DummyText.T["Dummy_Next"] },
            ],
        });
        tile.Elements.Add(new SliderElement { Id = "volume", Label = DummyText.T["Dummy_Volume"], Min = 0, Max = 100, Step = 1, Unit = "%" });
    }

    public override void Refresh(TileModel tile, DummyDevice device)
    {
        base.Refresh(tile, device);
        var track = DummyDevice.Tracks[device.TrackIndex];
        if (tile.FindElement<MediaInfoElement>("info") is { } info)
        {
            info.Title = track.Title;
            info.Artist = track.Artist;
            info.Duration = track.Duration;
            info.Position = 0;
            info.PositionUpdatedAt = DateTimeOffset.Now;
            info.IsPlaying = device.Playing;
        }

        if (tile.FindElement<ButtonRowElement>("controls")?.Buttons.FirstOrDefault(b => b.Id == "playpause") is { } button)
        {
            button.Icon = device.Playing ? "Pause" : "Play";
            button.Label = device.Playing ? DummyText.T["Dummy_Pause"] : DummyText.T["Dummy_Play"];
            button.IsActive = device.Playing;
        }

        if (tile.FindElement<SliderElement>("volume") is { } volume)
        {
            volume.Value = device.Volume;
        }
    }

    protected override void Handle(string entityId, TileInteraction interaction)
    {
        switch (interaction)
        {
            case { ElementId: "playpause", Kind: InteractionKind.Tap }:
            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "playpause" }:
                Module.Update(entityId, d => d.Playing = !d.Playing);
                break;
            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "next" }:
                Module.Update(entityId, d => d.TrackIndex = (d.TrackIndex + 1) % DummyDevice.Tracks.Length);
                break;
            case { ElementId: "controls", Kind: InteractionKind.Press, Text: "previous" }:
                Module.Update(entityId, d => d.TrackIndex = (d.TrackIndex + DummyDevice.Tracks.Length - 1) % DummyDevice.Tracks.Length);
                break;
            case { ElementId: "volume", Kind: InteractionKind.SetValue, Number: { } value }:
                Module.Update(entityId, d => d.Volume = Math.Clamp(value, 0, 100));
                break;
        }
    }
}

/// <summary>Deliberately throws an exception when tapped – test for the fault protection (SPEC 4.6).</summary>
internal sealed class CrashTileType(DummyModule module) : DummyTileType(module)
{
    public override string Id => "dummy.crash";

    public override string DisplayName => DummyText.T["Dummy_Crash"];

    public override string Icon => "Warning";

    public override DummyCategory Category => DummyCategory.Crash;

    protected override void Build(TileModel tile) =>
        tile.Elements.Add(new TapAction { Id = "crash", Label = DummyText.T["Dummy_Crash"] });

    public override void Refresh(TileModel tile, DummyDevice device)
    {
        tile.StateText = device.StateText;
        tile.IsActive = false;
        tile.AccentColor = "#FF5A5A";
    }

    protected override void Handle(string entityId, TileInteraction interaction) =>
        throw new InvalidOperationException("Crash triggered on request (test of the fault protection).");
}
