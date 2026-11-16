// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Tiles;

namespace UeBar.App.Tiles;

/// <summary>
/// Keeps the tile models for the current layout. If a module changes its state (start,
/// error, restart, off), its tiles are recreated – or become placeholders.
/// In M1 only the tray menu used these models; from M2 on the bar renders them.
/// </summary>
internal sealed class TileHost
{
    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TileModel> _tiles = new(StringComparer.Ordinal);
    private LayoutDocument _layout = new();

    public TileHost(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
        _registry.ModuleStatusChanged += (_, module) => _ = RefreshSafeAsync(module.Id);

        // Language switched (R1): recreate all tiles, so element labels and placeholders follow.
        UeBar.Core.Localization.UiCultureSource.Shared.Changed += (_, _) => _ = RecreateAllSafeAsync();
    }

    private async Task RecreateAllSafeAsync()
    {
        try
        {
            await SetLayoutAsync(Layout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Tiles could not be recreated after a language switch", ex);
        }
    }

    private async Task RefreshSafeAsync(string moduleId)
    {
        try
        {
            await RefreshModuleAsync(moduleId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(moduleId, "Tiles could not be recreated", ex);
        }
    }

    /// <summary>Tile models were recreated (comes from any thread).</summary>
    public event EventHandler? Changed;

    public LayoutDocument Layout
    {
        get
        {
            lock (_lock)
            {
                return _layout;
            }
        }
    }

    /// <summary>Tiles in layout order, grouped by tab.</summary>
    public IReadOnlyList<(LayoutTab Tab, IReadOnlyList<TileModel> Tiles)> Snapshot()
    {
        lock (_lock)
        {
            return
            [
                .. _layout.Tabs.Select(tab => (tab, (IReadOnlyList<TileModel>)
                [
                    .. tab.Tiles
                        .Select(cfg => _tiles.TryGetValue(cfg.Id, out var model) ? model : null)
                        .OfType<TileModel>(),
                ])),
            ];
        }
    }

    public async Task SetLayoutAsync(LayoutDocument layout, CancellationToken cancellationToken = default)
    {
        var created = new Dictionary<string, TileModel>(StringComparer.Ordinal);
        foreach (var config in layout.AllTiles)
        {
            created[config.Id] = await _registry.CreateTileAsync(config, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            _layout = layout;
            _tiles.Clear();
            foreach (var (id, model) in created)
            {
                _tiles[id] = model;
            }
        }

        _log.Info(null, $"Layout loaded: {layout.Tabs.Count} tabs, {created.Count} tiles");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Recreate the tiles of a module (after a status change).</summary>
    public async Task RefreshModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var configs = Layout.AllTiles
            .Where(t => string.Equals(t.Module, moduleId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var config in configs)
        {
            var model = await _registry.CreateTileAsync(config, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (_layout.AllTiles.Any(t => t.Id == config.Id))
                {
                    _tiles[config.Id] = model;
                }
            }
        }

        if (configs.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
