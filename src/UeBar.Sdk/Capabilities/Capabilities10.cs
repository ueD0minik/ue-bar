// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;

namespace UeBar.Sdk;

// Optional capabilities from P2-4 (contract 1.3). Extension only (SPEC 4.8).

/// <summary>How important a notification is (<see cref="UeNotification.Priority"/>).</summary>
public enum NotificationPriority
{
    /// <summary>Follows "Do not disturb", fullscreen and a paused bar like everything else.</summary>
    Normal,

    /// <summary>
    /// Must not be missed (a timer that ran out). The bar settings decide what important notifications
    /// may do: come out while the bar is paused, during "Do not disturb", over fullscreen apps.
    /// </summary>
    Important,
}

/// <summary>
/// The module applies changed settings while it runs: the core calls
/// <see cref="IModuleSettings.ApplySettingsAsync"/> again instead of restarting the module. Without
/// this capability a change restarts the module (as before). The call can come on any thread at any
/// time while the module runs – it must be safe against the module's own work.
/// </summary>
public interface ILiveModuleSettings : IModuleSettings
{
}

/// <summary>What was dropped on the panel in edit mode (<see cref="IDropHandler"/>).</summary>
public enum DropKind
{
    /// <summary>A file (also a shortcut <c>.lnk</c> or <c>.url</c> – the path as it was dropped).</summary>
    File,

    /// <summary>A folder.</summary>
    Folder,

    /// <summary>A web address (dragged from the browser's address bar or a link).</summary>
    Link,

    /// <summary>Plain text.</summary>
    Text,
}

/// <summary>A dropped thing: <paramref name="Value"/> = full path, address or text; <paramref name="Title"/> if known (link text).</summary>
public sealed record DroppedItem(DropKind Kind, string Value, string? Title = null);

/// <summary>
/// The module can turn something dropped on the panel (edit mode) into a tile. The core asks the
/// running modules one after another; the first one that returns a tile gets it. Return null for
/// anything the module does not take. Only a description – the core adds the tile to the layout.
/// </summary>
public interface IDropHandler
{
    Task<TileConfig?> CreateTileForDropAsync(DroppedItem item, CancellationToken cancellationToken);
}

/// <summary>One entry of an <see cref="IconGridElement"/>.</summary>
public sealed class IconGridItem : ObservableModel
{
    public string Id { get; set => SetProperty(ref field, value); } = "";

    public string Label { get; set => SetProperty(ref field, value); } = "";

    /// <summary>Icon name (Segoe Fluent Icons) – shown when there is no <see cref="ImageUrl"/>.</summary>
    public string? Icon { get; set => SetProperty(ref field, value); }

    /// <summary>Image as URL or image ID of the module (<see cref="IImageSource"/>), e.g. a real file icon.</summary>
    public string? ImageUrl { get; set => SetProperty(ref field, value); }

    /// <summary>Short hint below the label or as tooltip (e.g. "Not found").</summary>
    public string? StateText { get; set => SetProperty(ref field, value); }

    public bool IsEnabled { get; set => SetProperty(ref field, value); } = true;
}

/// <summary>
/// Grid of icons with names (contract 1.3), e.g. several shortcuts in one tile. Tapping an entry
/// reports <see cref="InteractionKind.Press"/> with <see cref="TileInteraction.Text"/> = the entry's ID.
/// </summary>
public sealed class IconGridElement : TileElement
{
    /// <summary>Entries per row (the app may show fewer on narrow tiles).</summary>
    public int Columns { get; set => SetProperty(ref field, value); } = 3;

    public ObservableCollection<IconGridItem> Items { get; set => SetProperty(ref field, value); } = [];
}
