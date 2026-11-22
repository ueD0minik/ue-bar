// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Contract 1.5 (P2-5): pins, picking a screen area, the clipboard, hotkeys of plugins, info fields and
// tiles from list entries. Everything new is optional – plugins built for an older contract run unchanged.

/// <summary>What a pin shows (<see cref="IPinService"/>).</summary>
public enum PinKind
{
    Text,
    Image,
}

/// <summary>A pin of this module as the core knows it (no content – only what it is and whether it is shown).</summary>
/// <param name="Id">ID given by the core.</param>
/// <param name="Kind">Text or image.</param>
/// <param name="Title">Short name, e.g. for the pin list in the bar.</param>
/// <param name="IsVisible">Shown right now (the user can hide pins).</param>
public sealed record PinInfo(string Id, PinKind Kind, string Title, bool IsVisible)
{
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A new text pin.</summary>
public sealed record TextPinRequest(string Text)
{
    /// <summary>Name in the pin list; null = the start of the text.</summary>
    public string? Title { get; init; }

    /// <summary>Font size in device-independent pixels; null = the user's default.</summary>
    public double? FontSize { get; init; }
}

/// <summary>A new image pin.</summary>
/// <param name="ImageData">The image as PNG (other formats Windows can decode work, PNG is stored).</param>
public sealed record ImagePinRequest(byte[] ImageData)
{
    public string? Title { get; init; }

    /// <summary>
    /// Where the image came from on the screen (e.g. a screenshot): the pin appears exactly there and
    /// in the same size. null = a free spot on the monitor under the mouse.
    /// </summary>
    public ScreenRect? ScreenBounds { get; init; }
}

/// <summary>Changes to a pin; null = leave as it is.</summary>
public sealed record PinUpdate
{
    public string? Text { get; init; }

    public string? Title { get; init; }

    public byte[]? ImageData { get; init; }

    public bool? IsVisible { get; init; }
}

/// <summary>
/// Pins (contract 1.5): small free windows the core draws on the screen – always on top, movable,
/// resizable, dimmed after a while (OLED), kept across restarts on this device only. A module creates
/// pins with text or an image without any UI code; the user moves, zooms, hides and closes them.
/// Offered on the <see cref="IModuleContext"/> (<c>context as IPinService</c>). A module sees and changes
/// only its own pins. Calls are fast and may come from any thread.
/// </summary>
public interface IPinService
{
    /// <summary>Pins of this module (also hidden ones).</summary>
    IReadOnlyList<PinInfo> GetPins();

    /// <summary>Creates a text pin and shows it; returns its ID or null (too many pins, empty text).</summary>
    string? CreateTextPin(TextPinRequest request);

    /// <summary>Creates an image pin and shows it; returns its ID or null (too many pins, not an image, too large).</summary>
    string? CreateImagePin(ImagePinRequest request);

    /// <summary>Changes a pin of this module; false = no such pin (any more) or not this module's.</summary>
    bool UpdatePin(string pinId, PinUpdate update);

    /// <summary>Closes (deletes) a pin of this module.</summary>
    bool ClosePin(string pinId);

    /// <summary>The pins of this module changed (also by the user: moved does not count, closed or hidden does). Comes through the module guard.</summary>
    event EventHandler? PinsChanged;
}

/// <summary>A rectangle on the screen in physical pixels of the virtual desktop (all monitors, the primary one starts at 0,0).</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>The common part, empty if they do not overlap.</summary>
    public ScreenRect Intersect(ScreenRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new ScreenRect(left, top, right - left, bottom - top) : default;
    }

    /// <summary>The rectangle between two points (in any order); both corners count as inside.</summary>
    public static ScreenRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);
}

/// <summary>A monitor: bounds and work area (without the taskbar) in physical pixels, scale 1.0 = 100 %.</summary>
public sealed record ScreenMonitor(string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, double Scale, bool IsPrimary);

/// <summary>What the user picks (<see cref="IScreenAreaPicker"/>).</summary>
public enum ScreenAreaMode
{
    /// <summary>Drag a rectangle (also across monitors); a click without dragging takes the window under the mouse.</summary>
    Region,

    /// <summary>Click a window.</summary>
    Window,

    /// <summary>The whole monitor under the mouse (another one with a click).</summary>
    Screen,
}

/// <summary>A button of the small bar shown at the picked area (<see cref="ScreenAreaRequest.Actions"/>).</summary>
public sealed record ScreenAreaAction(string Id, string Label, string? Icon = null)
{
    /// <summary>
    /// Only a small icon right after the previous button, the label as tooltip (contract 1.5) – e.g. the little
    /// arrow "Save as …" next to "Save".
    /// </summary>
    public bool Compact { get; init; }
}

/// <summary>How to pick a screen area.</summary>
public sealed record ScreenAreaRequest(ScreenAreaMode Mode = ScreenAreaMode.Region)
{
    /// <summary>
    /// Buttons shown at the picked area (e.g. "Pin", "Copy"); the user's choice comes back in
    /// <see cref="ScreenAreaResult.ActionId"/>. Empty = the picker closes as soon as an area is picked.
    /// </summary>
    public IReadOnlyList<ScreenAreaAction> Actions { get; init; } = [];
}

/// <summary>The picked area.</summary>
/// <param name="Bounds">In physical pixels of the virtual desktop.</param>
/// <param name="Monitors">All monitors at that moment (for sizes and scaling).</param>
public sealed record ScreenAreaResult(ScreenRect Bounds, IReadOnlyList<ScreenMonitor> Monitors)
{
    public ScreenAreaMode Mode { get; init; }

    /// <summary>The picked area as PNG, as it was on the screen when picking started (the screen is frozen while picking).</summary>
    public byte[]? Image { get; init; }

    /// <summary>The button the user chose (<see cref="ScreenAreaRequest.Actions"/>), null without buttons.</summary>
    public string? ActionId { get; init; }

    /// <summary>Title of the picked window (mode window, or a click in mode region); null otherwise.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>The monitor with the largest part of the area (the primary one if none).</summary>
    public ScreenMonitor? MainMonitor =>
        Monitors.OrderByDescending(m => m.Bounds.Intersect(Bounds).Area).ThenByDescending(m => m.IsPrimary).FirstOrDefault();
}

/// <summary>
/// The core's overlay for picking a screen area (contract 1.5): darkens all monitors, the user drags a
/// rectangle, clicks a window or a monitor; Esc cancels. Works across monitors with different scaling.
/// Offered on the <see cref="IModuleContext"/> (<c>context as IScreenAreaPicker</c>). Only one pick at a
/// time – a second call while one is open returns null.
/// </summary>
public interface IScreenAreaPicker
{
    /// <summary>The picked area, or null if the user cancelled.</summary>
    Task<ScreenAreaResult?> PickScreenAreaAsync(ScreenAreaRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Writes to the Windows clipboard (contract 1.5), offered on the <see cref="IModuleContext"/>
/// (<c>context as IClipboardAccess</c>). Reading is not offered.
/// </summary>
public interface IClipboardAccess
{
    Task<bool> SetTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>An image (PNG) – as image and as PNG, so every app can paste it.</summary>
    Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken);
}

/// <summary>What the Windows "Save as" dialog should offer (<see cref="IFileSaveDialog"/>).</summary>
/// <param name="SuggestedName">File name proposed in the dialog (with extension).</param>
public sealed record SaveFileRequest(string SuggestedName)
{
    /// <summary>Window title; null = the Windows default.</summary>
    public string? Title { get; init; }

    /// <summary>Extension with dot, e.g. ".png" – added when the user types a name without one.</summary>
    public string Extension { get; init; } = ".png";

    /// <summary>Name of the file type in the dialog, e.g. "PNG image".</summary>
    public string? TypeName { get; init; }

    /// <summary>Folder to start in when the user has not picked one before (otherwise the last one is used).</summary>
    public string? InitialFolder { get; init; }
}

/// <summary>
/// The Windows "Save as" dialog, shown by the core (contract 1.5), offered on the <see cref="IModuleContext"/>
/// (<c>context as IFileSaveDialog</c>). It starts in the folder the user picked last time for this module
/// (remembered per PC). Returns the chosen path, or null if cancelled; the module writes the file itself.
/// </summary>
public interface IFileSaveDialog
{
    Task<string?> AskSavePathAsync(SaveFileRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The module reacts to its global hotkeys (contract 1.5): every <see cref="SettingFieldKind.Hotkey"/>
/// field with a value is registered by the core while the module runs; a press calls this with the
/// field's key. Recording, conflicts ("in use") and changing the hotkey are the core's job. Return quickly
/// (the core's time limit applies) – start longer work with <see cref="IModuleContext.RunBackground"/>.
/// </summary>
public interface IHotkeyHandler
{
    Task OnHotkeyAsync(string fieldKey, CancellationToken cancellationToken);
}

/// <summary>
/// Entries of a <see cref="SettingFieldKind.List"/> can become tiles (contract 1.5): the settings page
/// offers "Also add as tile" when an entry is created, and "Add as tile" on rows the module marks
/// (<see cref="SettingListRow.CanAddTile"/>). The core puts the tile into the chosen tab.
/// </summary>
public interface ISettingListTiles
{
    /// <summary>A tile for the entry, or null if it cannot be one (yet, e.g. no target entered).</summary>
    TileConfig? CreateTileForListEntry(string fieldKey, IReadOnlyDictionary<string, string> entry);
}
