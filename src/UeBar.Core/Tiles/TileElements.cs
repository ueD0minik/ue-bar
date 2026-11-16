// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace UeBar.Core.Tiles;

/// <summary>
/// Base of all tile elements (SPEC 4.2). Observable and fully JSON-serializable;
/// the field "kind" distinguishes the type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TapAction), "tap")]
[JsonDerivedType(typeof(SliderElement), "slider")]
[JsonDerivedType(typeof(StepperElement), "stepper")]
[JsonDerivedType(typeof(ButtonRowElement), "buttonRow")]
[JsonDerivedType(typeof(ChoiceElement), "choice")]
[JsonDerivedType(typeof(ValueDisplayElement), "valueDisplay")]
[JsonDerivedType(typeof(MediaInfoElement), "mediaInfo")]
public abstract class TileElement : ObservableModel
{
    /// <summary>ID within the tile, so interactions can be assigned.</summary>
    public string Id { get; set => SetProperty(ref field, value); } = "";

    /// <summary>Greyed out / not operable.</summary>
    public bool IsEnabled { get; set => SetProperty(ref field, value); } = true;
}

/// <summary>Tap the whole tile = toggle/run.</summary>
public sealed class TapAction : TileElement
{
    /// <summary>Optional label, e.g. for accessibility ("Toggle").</summary>
    public string? Label { get; set => SetProperty(ref field, value); }
}

/// <summary>Slider (brightness, volume, temperature).</summary>
public sealed class SliderElement : TileElement
{
    public string? Label { get; set => SetProperty(ref field, value); }

    public double Min { get; set => SetProperty(ref field, value); }

    public double Max { get; set => SetProperty(ref field, value); } = 100;

    public double Step { get; set => SetProperty(ref field, value); } = 1;

    public double Value { get; set => SetProperty(ref field, value); }

    public string? Unit { get; set => SetProperty(ref field, value); }
}

/// <summary>−/+ with value (target temperature).</summary>
public sealed class StepperElement : TileElement
{
    public string? Label { get; set => SetProperty(ref field, value); }

    public double Min { get; set => SetProperty(ref field, value); }

    public double Max { get; set => SetProperty(ref field, value); } = 100;

    public double Step { get; set => SetProperty(ref field, value); } = 1;

    public double Value { get; set => SetProperty(ref field, value); }

    public string? Unit { get; set => SetProperty(ref field, value); }

    /// <summary>Number of decimal places for display.</summary>
    public int Decimals { get; set => SetProperty(ref field, value); }
}

/// <summary>A button in a <see cref="ButtonRowElement"/>.</summary>
public sealed class TileButton : ObservableModel
{
    public string Id { get; set => SetProperty(ref field, value); } = "";

    /// <summary>Icon name (Segoe Fluent Icons), e.g. "Play".</summary>
    public string? Icon { get; set => SetProperty(ref field, value); }

    public string? Label { get; set => SetProperty(ref field, value); }

    public bool IsActive { get; set => SetProperty(ref field, value); }

    public bool IsEnabled { get; set => SetProperty(ref field, value); } = true;
}

/// <summary>Button row (play/pause/next/previous, start/pause/dock).</summary>
public sealed class ButtonRowElement : TileElement
{
    public ObservableCollection<TileButton> Buttons { get; set => SetProperty(ref field, value); } = [];
}

/// <summary>An option in <see cref="ChoiceElement"/>.</summary>
public sealed class ChoiceOption : ObservableModel
{
    public string Id { get; set => SetProperty(ref field, value); } = "";

    public string Label { get; set => SetProperty(ref field, value); } = "";
}

/// <summary>Choice (fan speed, input).</summary>
public sealed class ChoiceElement : TileElement
{
    public string? Label { get; set => SetProperty(ref field, value); }

    public ObservableCollection<ChoiceOption> Options { get; set => SetProperty(ref field, value); } = [];

    public string? SelectedId { get; set => SetProperty(ref field, value); }
}

/// <summary>Large value + unit (sensor).</summary>
public sealed class ValueDisplayElement : TileElement
{
    public string? Label { get; set => SetProperty(ref field, value); }

    public string Value { get; set => SetProperty(ref field, value); } = "";

    public string? Unit { get; set => SetProperty(ref field, value); }
}

/// <summary>Media info: cover, title, artist, progress.</summary>
public sealed class MediaInfoElement : TileElement
{
    public string? Title { get; set => SetProperty(ref field, value); }

    public string? Artist { get; set => SetProperty(ref field, value); }

    public string? Album { get; set => SetProperty(ref field, value); }

    /// <summary>Cover as URL or image ID (no WPF type).</summary>
    public string? ImageUrl { get; set => SetProperty(ref field, value); }

    /// <summary>Position in seconds.</summary>
    public double? Position { get; set => SetProperty(ref field, value); }

    /// <summary>Duration in seconds.</summary>
    public double? Duration { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// Time at which <see cref="Position"/> was measured – so the display can count up
    /// by itself without the module needing a timer.
    /// </summary>
    public DateTimeOffset? PositionUpdatedAt { get; set => SetProperty(ref field, value); }

    public bool IsPlaying { get; set => SetProperty(ref field, value); }
}
