// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using System.Text;
using System.Text.Json;
using UeBar.Core.Abstractions;
using UeBar.Core.Json;
using UeBar.Core.Logging;

namespace UeBar.Core.Layout;

/// <summary>
/// Layout levels combined (SPEC 9): base layout from the chosen <see cref="ILayoutStore"/>,
/// device level from <c>device.json</c>. If there is no base layout yet, <see cref="NeedsSetup"/>
/// is set – the app then shows the first start (M5b) instead of a built-in suggestion.
/// On save the base layout gets <c>updatedAt</c>/<c>updatedBy</c> (for conflicts).
/// </summary>
public sealed class LayoutManager
{
    private readonly ILayoutStore _store;
    private readonly DeviceLayoutStore _deviceStore;
    private readonly IUeLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LayoutManager(
        ILayoutStore store,
        DeviceLayoutStore deviceStore,
        IUeLogger? log = null,
        string? deviceName = null)
    {
        _store = store;
        _deviceStore = deviceStore;
        _log = log ?? NullLogger.Instance;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
    }

    /// <summary>Name of this device (default: Windows computer name; configurable from M7).</summary>
    public string DeviceName { get; }

    public LayoutDocument Base { get; private set; } = new();

    public DeviceLayout Device { get; private set; } = new();

    /// <summary>There is no base layout (yet) – neither locally nor in the remote store: offer first start.</summary>
    public bool NeedsSetup { get; private set; }

    public ComposedLayout Composed => LayoutLayers.Compose(Base, Device);

    /// <summary>(Re)load base and device layout. Broken files are not overwritten but reported.</summary>
    public async Task<ComposedLayout> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LayoutDocument? stored = null;
            try
            {
                stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                _log.Error(null, ex.Message);
            }

            Base = stored ?? new LayoutDocument();
            NeedsSetup = stored is null;

            try
            {
                Device = await _deviceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                _log.Error(null, ex.Message);
                Device = new DeviceLayout();
            }

            return Composed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Save the result of edit mode (base with backup, device in device.json).</summary>
    public async Task SaveAsync(LayoutDocument baseLayout, DeviceLayout device, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            device.DeviceName = DeviceName;
            Stamp(baseLayout);
            await _store.SaveAsync(baseLayout, cancellationToken).ConfigureAwait(false);
            await _deviceStore.SaveAsync(device, cancellationToken).ConfigureAwait(false);
            Base = LayoutLayers.Clone(baseLayout);
            Device = LayoutLayers.Clone(device);
            NeedsSetup = false;
            _log.Info(null, $"Layout saved: {baseLayout.Tabs.Count} base tabs, {device.Tabs.Count} device tabs, {device.HiddenBaseTabs.Count} hidden");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Export the base layout as readable JSON.</summary>
    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = UeBarJson.Serialize(Base);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        _log.Info(null, $"Layout exported: {Path.GetFileName(path)} ({Base.Tabs.Count} tabs, {Base.AllTiles.Count()} tiles)");
    }

    /// <summary>Read and check a file (without applying it). Throws <see cref="InvalidDataException"/> on nonsense.</summary>
    public static LayoutDocument ParseImport(string json)
    {
        LayoutDocument? layout;
        try
        {
            layout = UeBarJson.Deserialize<LayoutDocument>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(CoreText.T.Format("Layout_ImportInvalid", ex.Message), ex);
        }

        if (layout is null || layout.Tabs.Count == 0)
        {
            throw new InvalidDataException(CoreText.T["Layout_ImportNoTabs"]);
        }

        if (layout.Version > LayoutDocument.CurrentVersion)
        {
            throw new InvalidDataException(CoreText.T.Format("Layout_ImportTooNew", layout.Version, LayoutDocument.CurrentVersion));
        }

        // Repair missing/duplicate IDs so the tiles stay unique.
        var tabIds = new HashSet<string>(StringComparer.Ordinal);
        var tileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in layout.Tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.Id) || !tabIds.Add(tab.Id))
            {
                tab.Id = UniqueId("tab", tabIds);
            }

            foreach (var tile in tab.Tiles)
            {
                if (string.IsNullOrWhiteSpace(tile.Id) || !tileIds.Add(tile.Id))
                {
                    tile.Id = UniqueId("k", tileIds);
                }
            }
        }

        return layout;
    }

    /// <summary>
    /// Import: replaces the base layout (the previous file is backed up first). Unknown
    /// modules/tile types stay in the file and show placeholders (SPEC 4.5).
    /// </summary>
    public async Task ImportAsync(LayoutDocument layout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stamp(layout);
            await _store.SaveAsync(layout, cancellationToken).ConfigureAwait(false);
            Base = LayoutLayers.Clone(layout);
            NeedsSetup = false;
            _log.Info(null, $"Layout imported: {layout.Tabs.Count} tabs, {layout.AllTiles.Count()} tiles");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>First start finished: the chosen (possibly empty) layout becomes the base layout.</summary>
    public async Task ApplySetupAsync(LayoutDocument layout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stamp(layout);
            await _store.SaveAsync(layout, cancellationToken).ConfigureAwait(false);
            Base = LayoutLayers.Clone(layout);
            NeedsSetup = false;
            _log.Info(null, $"First start: layout created with {layout.Tabs.Count} tabs, {layout.AllTiles.Count()} tiles");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Stamp(LayoutDocument layout)
    {
        layout.UpdatedAt = DateTimeOffset.Now;
        layout.UpdatedBy = DeviceName;
    }

    private static string UniqueId(string prefix, HashSet<string> taken)
    {
        while (true)
        {
            var id = $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";
            if (taken.Add(id))
            {
                return id;
            }
        }
    }
}
