// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using UeBar.Core.Layout;

namespace UeBar.Core.Tiles;

/// <summary>Why a tile only shows a placeholder (SPEC 4.4 / 4.5 / 4.6).</summary>
public enum TilePlaceholder
{
    None,

    /// <summary>Module is not registered/installed.</summary>
    ModuleMissing,

    /// <summary>Module is switched off in the settings.</summary>
    ModuleDisabled,

    /// <summary>Module is faulted or creating the tile failed.</summary>
    ModuleError,

    /// <summary>Module does not know the tile type.</summary>
    UnknownType,
}

/// <summary>
/// Content of a tile as data (SPEC 4.2): title, icon, state, accent, image, elements.
/// Rendered by the core; fully JSON-serializable.
/// </summary>
public sealed class TileModel : ObservableModel
{
    /// <summary>ID of the tile in the layout (set by the core).</summary>
    public string TileId { get; set => SetProperty(ref field, value); } = "";

    /// <summary>Module ID (set by the core).</summary>
    public string ModuleId { get; set => SetProperty(ref field, value); } = "";

    /// <summary>Tile type ID (set by the core).</summary>
    public string TypeId { get; set => SetProperty(ref field, value); } = "";

    public string Title { get; set => SetProperty(ref field, value); } = "";

    /// <summary>Icon name (Segoe Fluent Icons), e.g. "Lightbulb".</summary>
    public string? Icon { get; set => SetProperty(ref field, value); }

    public string? StateText { get; set => SetProperty(ref field, value); }

    /// <summary>On = accent color.</summary>
    public bool IsActive { get; set => SetProperty(ref field, value); }

    /// <summary>Optional own accent color as hex string (#RRGGBB), otherwise the global one.</summary>
    public string? AccentColor { get; set => SetProperty(ref field, value); }

    /// <summary>Optional image as URL or image ID.</summary>
    public string? ImageUrl { get; set => SetProperty(ref field, value); }

    /// <summary>Set if the tile is only a placeholder.</summary>
    public TilePlaceholder Placeholder { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// The thing is currently unreachable (e.g. device off, connection lost): the tile is
    /// greyed out and not operable. The reason is in <see cref="StateText"/>. (from M4)
    /// </summary>
    public bool IsUnavailable { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// Short, subtle hint on the tile (e.g. "No response"). While set, the tile shows
    /// it instead of the state (red). The module sets it back to null. (from M4)
    /// </summary>
    public string? Alert { get; set => SetProperty(ref field, value); }

    public ObservableCollection<TileElement> Elements { get; set => SetProperty(ref field, value); } = [];

    /// <summary>Receiver for interactions – not part of the JSON.</summary>
    [JsonIgnore]
    public ITileInteractionHandler? Handler { get; set; }

    [JsonIgnore]
    public bool IsPlaceholder => Placeholder != TilePlaceholder.None;

    public T? FindElement<T>(string id)
        where T : TileElement =>
        Elements.OfType<T>().FirstOrDefault(e => e.Id == id);

    public T? FirstElement<T>()
        where T : TileElement =>
        Elements.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Placeholder tile. The tile configuration in the layout stays unchanged.
    /// </summary>
    public static TileModel CreatePlaceholder(TileConfig config, TilePlaceholder reason, string? detail = null)
    {
        var text = reason switch
        {
            TilePlaceholder.ModuleMissing => CoreText.T.Format("Placeholder_ModuleMissing", config.Module),
            TilePlaceholder.ModuleDisabled => CoreText.T["Placeholder_ModuleDisabled"],
            TilePlaceholder.ModuleError => CoreText.T["Placeholder_ModuleError"],
            TilePlaceholder.UnknownType => CoreText.T.Format("Placeholder_UnknownType", config.Type),
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            text = text is null ? detail : $"{text}: {detail}";
        }

        return new TileModel
        {
            TileId = config.Id,
            ModuleId = config.Module,
            TypeId = config.Type,
            Title = string.IsNullOrWhiteSpace(config.Label) ? config.Type : config.Label!,
            Icon = reason == TilePlaceholder.ModuleError ? "Error" : "Puzzle",
            StateText = text,
            Placeholder = reason,
        };
    }
}
