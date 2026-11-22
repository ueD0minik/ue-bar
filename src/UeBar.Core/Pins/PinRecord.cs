// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Core.Pins;

/// <summary>
/// A pin as it is kept in <c>%APPDATA%\ueBar\pins\pins.json</c> (P2-5). Images live next to it as files;
/// nothing of this goes into the synchronized layout or to a server.
/// </summary>
public sealed class PinRecord
{
    public string Id { get; set; } = "";

    public PinKind Kind { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Text of a text pin.</summary>
    public string? Text { get; set; }

    /// <summary>Font size of a text pin (device-independent pixels); 0 = default.</summary>
    public double FontSize { get; set; }

    /// <summary>File name of the image (in the pins folder).</summary>
    public string? ImageFile { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    /// <summary>Module that created it; null = the user (quick note, bar).</summary>
    public string? Owner { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Position locked: neither moving nor resizing.</summary>
    public bool Locked { get; set; }

    /// <summary>Clicks go through the pin to the window below.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Opacity chosen by the user (0.2–1); dimming comes on top.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Zoom of an image pin (1 = fit).</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>Shift of the zoomed image as a share of the pin size (0 = centered).</summary>
    public double PanX { get; set; }

    public double PanY { get; set; }

    /// <summary>Where it is on the screen; null = not placed yet (the app places it when showing).</summary>
    public PinPlacement? Placement { get; set; }

    /// <summary>Where the content came from on the screen (screenshot) – the first placement covers it exactly.</summary>
    public PinScreenArea? Source { get; set; }

    public PinRecord Clone()
    {
        var copy = (PinRecord)MemberwiseClone();
        copy.Placement = Placement is null ? null : Placement with { };
        copy.Source = Source is null ? null : Source with { };
        return copy;
    }

    public PinInfo ToInfo() => new(Id, Kind, Title, !Hidden) { CreatedAt = CreatedAt };
}

/// <summary>
/// Position per monitor: the offset from the monitor's top left corner in physical pixels (stays right if
/// monitors are rearranged) and the size in device-independent pixels (stays right if the scaling changes).
/// </summary>
public sealed record PinPlacement
{
    /// <summary>Stable monitor ID (device path) – a missing monitor means the primary one.</summary>
    public string? Monitor { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }
}

/// <summary>A rectangle in physical pixels of the virtual desktop (as <see cref="ScreenRect"/>, serializable).</summary>
public sealed record PinScreenArea(int X, int Y, int Width, int Height)
{
    public ScreenRect ToRect() => new(X, Y, Width, Height);

    public static PinScreenArea From(ScreenRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}

/// <summary>What changed (<see cref="PinManager.Changed"/>).</summary>
public enum PinChangeKind
{
    Added,
    Removed,

    /// <summary>Text, image or title.</summary>
    Content,

    /// <summary>Shown/hidden.</summary>
    Visibility,

    /// <summary>Position or size.</summary>
    Placement,

    /// <summary>Opacity, lock, click-through, zoom, font size.</summary>
    Options,
}

public sealed class PinChangedEventArgs(PinChangeKind kind, PinRecord pin) : EventArgs
{
    public PinChangeKind Kind { get; } = kind;

    /// <summary>Copy of the pin after the change (before removal for <see cref="PinChangeKind.Removed"/>).</summary>
    public PinRecord Pin { get; } = pin;
}
