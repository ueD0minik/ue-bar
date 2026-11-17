// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>
/// Host-side additions to SDK types that need the core's UI texts. They are extension members so
/// the SDK itself stays free of resources: <c>status.DisplayText</c> and
/// <c>TileModel.CreatePlaceholder(…)</c> read like members of the SDK types.
/// </summary>
public static class HostExtensions
{
    extension(ModuleStatus status)
    {
        /// <summary>The module's own text, otherwise the state in the UI language.</summary>
        public string DisplayText => status.Text ?? CoreText.T["ModuleState_" + status.State];
    }

    extension(TileModel)
    {
        /// <summary>
        /// Placeholder tile for a module that is missing, off or faulted (SPEC 4.5). The tile
        /// configuration in the layout stays unchanged.
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
}
