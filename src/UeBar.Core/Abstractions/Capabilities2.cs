// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;

namespace UeBar.Core.Abstractions;

// More optional capabilities (from M3). Extension only, nothing in the contract broken (SPEC 4.8).

/// <summary>The parts of the layout that belong to a module.</summary>
public sealed record ModuleLayoutInfo(IReadOnlyList<TileConfig> Tiles, IReadOnlyList<StatusItemConfig> Status)
{
    public static ModuleLayoutInfo Empty { get; } = new([], []);

    /// <summary>All referenced "things" (slots of all tiles + status values).</summary>
    public IReadOnlySet<string> ReferencedEntities =>
        new HashSet<string>(
            Tiles.SelectMany(t => t.Entities.Values).Concat(Status.Select(s => s.Entity)).Where(e => !string.IsNullOrWhiteSpace(e)),
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The module wants to know which of its tiles/status values are in the layout (e.g. to keep only
/// those things live). Called before every start and on every layout change.
/// </summary>
public interface ILayoutAware
{
    void OnLayoutChanged(ModuleLayoutInfo layout);
}

/// <summary>Result line of a settings test ("Test connection").</summary>
public sealed record SettingsTestLine(string Title, bool Success, string Detail);

/// <summary>Module can check entered (not yet saved) settings.</summary>
public interface IModuleSettingsTest
{
    Task<IReadOnlyList<SettingsTestLine>> TestSettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken);
}

/// <summary>Short diagnostic lines for tray/settings. Must be fast (memory reads only).</summary>
public interface IModuleDiagnostics
{
    IReadOnlyList<string> GetDiagnostics();
}

/// <summary>Raw image data from a module (e.g. cover art) – the app decodes and caches it.</summary>
public sealed record ModuleImage(byte[] Data, string? ContentType = null);

/// <summary>
/// Module provides images for the image IDs it sets in <c>ImageUrl</c> of tiles/elements
/// (from M4). Credentials stay in the module; the app only knows the ID. Returns null if there is
/// no image (any more).
/// </summary>
public interface IImageSource
{
    Task<ModuleImage?> LoadImageAsync(string imageId, CancellationToken cancellationToken);
}
