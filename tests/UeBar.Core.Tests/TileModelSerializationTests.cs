// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Json;
using UeBar.Core.Tiles;

namespace UeBar.Core.Tests;

/// <summary>SPEC 4.2: TileModel and elements must be fully transferable as JSON.</summary>
public class TileModelSerializationTests
{
    private static TileModel CreateFullTile() => new()
    {
        TileId = "t1",
        ModuleId = "dummy",
        TypeId = "dummy.everything",
        Title = "Alles",
        Icon = "Lightbulb",
        StateText = "An · 60 %",
        IsActive = true,
        AccentColor = "#3E91FF",
        ImageUrl = "img://cover/1",
        Handler = new DelegateInteractionHandler((_, _) => { }),
        Elements =
        [
            new TapAction { Id = "tap", Label = "Umschalten" },
            new SliderElement { Id = "s", Label = "Helligkeit", Min = 0, Max = 100, Step = 5, Value = 60, Unit = "%" },
            new StepperElement { Id = "st", Min = 16, Max = 30, Step = 0.5, Value = 22.5, Unit = "°C", Decimals = 1 },
            new ButtonRowElement
            {
                Id = "b",
                Buttons =
                [
                    new TileButton { Id = "prev", Icon = "Previous" },
                    new TileButton { Id = "play", Icon = "Pause", IsActive = true, Label = "Pause" },
                ],
            },
            new ChoiceElement
            {
                Id = "c",
                Label = "Lüfter",
                SelectedId = "mid",
                Options = [new ChoiceOption { Id = "low", Label = "Stufe 1" }, new ChoiceOption { Id = "mid", Label = "Stufe 2" }],
            },
            new ValueDisplayElement { Id = "v", Value = "21,4", Unit = "°C", Label = "Raum", IsEnabled = false },
            new MediaInfoElement
            {
                Id = "m",
                Title = "Lied",
                Artist = "Band",
                Album = "Album",
                ImageUrl = "https://example.invalid/cover.jpg",
                Position = 42.5,
                Duration = 200,
                PositionUpdatedAt = DateTimeOffset.Parse("2026-11-05T12:00:00+01:00"),
                IsPlaying = true,
            },
        ],
    };

    [Fact]
    public void Tile_model_round_trips_completely()
    {
        var json = UeBarJson.Serialize(CreateFullTile());

        var back = UeBarJson.Deserialize<TileModel>(json)!;
        var again = UeBarJson.Serialize(back);

        Assert.Equal(json, again);
        Assert.Equal(7, back.Elements.Count);
        Assert.IsType<TapAction>(back.Elements[0]);
        Assert.Equal(60, back.FindElement<SliderElement>("s")!.Value);
        Assert.Equal(22.5, back.FindElement<StepperElement>("st")!.Value);
        Assert.True(back.FindElement<ButtonRowElement>("b")!.Buttons[1].IsActive);
        Assert.Equal("mid", back.FindElement<ChoiceElement>("c")!.SelectedId);
        Assert.False(back.FindElement<ValueDisplayElement>("v")!.IsEnabled);
        Assert.Equal(42.5, back.FindElement<MediaInfoElement>("m")!.Position);
        Assert.Equal("#3E91FF", back.AccentColor);
        Assert.True(back.IsActive);
    }

    [Fact]
    public void Json_uses_kind_discriminator_and_contains_no_handler()
    {
        var node = JsonNode.Parse(UeBarJson.Serialize(CreateFullTile()))!;

        var kinds = node["elements"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>());
        Assert.Equal(["tap", "slider", "stepper", "buttonRow", "choice", "valueDisplay", "mediaInfo"], kinds);
        Assert.Null(node["handler"]);
        Assert.Null(node["isPlaceholder"]);
        Assert.Equal("none", node["placeholder"]!.GetValue<string>());
    }

    [Fact]
    public void Unavailable_and_alert_round_trip()
    {
        var tile = new TileModel { Title = "Desk", StateText = "Nicht verfügbar", IsUnavailable = true, Alert = "Keine Rückmeldung" };

        var node = JsonNode.Parse(UeBarJson.Serialize(tile))!;
        var back = UeBarJson.Deserialize<TileModel>(node.ToJsonString())!;

        Assert.True(node["isUnavailable"]!.GetValue<bool>());
        Assert.Equal("Keine Rückmeldung", node["alert"]!.GetValue<string>());
        Assert.True(back.IsUnavailable);
        Assert.Equal("Keine Rückmeldung", back.Alert);
    }

    [Fact]
    public void Placeholder_round_trips()
    {
        var tile = TileModel.CreatePlaceholder(new Layout.TileConfig { Id = "x", Module = "fehlt", Type = "fehlt.typ" }, TilePlaceholder.ModuleMissing);

        var back = UeBarJson.Deserialize<TileModel>(UeBarJson.Serialize(tile))!;

        Assert.Equal(TilePlaceholder.ModuleMissing, back.Placeholder);
        Assert.True(back.IsPlaceholder);
    }

    [Fact]
    public void Interaction_round_trips()
    {
        var interaction = new TileInteraction("s", InteractionKind.SetValue, 33.3);

        var back = UeBarJson.Deserialize<TileInteraction>(UeBarJson.Serialize(interaction));

        Assert.Equal(interaction, back);
    }

    [Fact]
    public void Elements_raise_property_changed()
    {
        var slider = new SliderElement();
        var changed = new List<string?>();
        slider.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        slider.Value = 10;
        slider.Value = 10; // equal → no event

        Assert.Equal([nameof(SliderElement.Value)], changed);
    }

    [Theory]
    [InlineData("1x1", 1, 1)]
    [InlineData("2x1", 2, 1)]
    [InlineData("2X2", 2, 2)]
    public void Tile_size_parses(string text, int columns, int rows)
    {
        Assert.Equal(new TileSize(columns, rows), TileSize.Parse(text));
    }
}
