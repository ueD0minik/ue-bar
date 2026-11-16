// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Core.Json;
using UeBar.Core.Tiles;

namespace UeBar.Core.Layout;

/// <summary>
/// Edit mode without UI (SPEC 7): works on copies of the base and device layout,
/// every change is a step with undo. "Cancel" = discard the editor, "Done" =
/// save <see cref="Result"/>. Order of the tiles in the tab = order in the grid
/// (the GridPacker does the arrangement).
/// </summary>
public sealed class LayoutEditor
{
    public const int MaxUndo = 100;

    private readonly Func<TileConfig, IReadOnlyList<TileSize>?>? _allowedSizes;
    private readonly Stack<(string Base, string Device)> _undo = new();
    private LayoutDocument _base;
    private DeviceLayout _device;

    /// <param name="allowedSizes">Allowed sizes per tile (from the tile type); null = all allowed.</param>
    public LayoutEditor(LayoutDocument baseLayout, DeviceLayout? device, Func<TileConfig, IReadOnlyList<TileSize>?>? allowedSizes = null)
    {
        _base = LayoutLayers.Clone(baseLayout);
        _device = LayoutLayers.Clone(device ?? new DeviceLayout());
        _allowedSizes = allowedSizes;
    }

    /// <summary>After every change and after undo.</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Number of steps since the start (undo counts back).</summary>
    public int StepCount => _undo.Count;

    /// <summary>Merged view of the working state (incl. hidden base tabs).</summary>
    public ComposedLayout Composed => LayoutLayers.Compose(_base, _device);

    /// <summary>Alle Tabs in Anzeigereihenfolge.</summary>
    public IReadOnlyList<ComposedTab> Tabs => Composed.AllTabs;

    /// <summary>Result to save (copies).</summary>
    public (LayoutDocument Base, DeviceLayout Device) Result => (LayoutLayers.Clone(_base), LayoutLayers.Clone(_device));

    public LayoutTab? FindTab(string tabId) =>
        _base.Tabs.FirstOrDefault(t => t.Id == tabId) ?? _device.Tabs.FirstOrDefault(t => t.Id == tabId);

    public TabOrigin? OriginOf(string tabId) =>
        _base.Tabs.Any(t => t.Id == tabId) ? TabOrigin.Base : _device.Tabs.Any(t => t.Id == tabId) ? TabOrigin.Device : null;

    public (LayoutTab Tab, TileConfig Tile)? FindTile(string tileId)
    {
        foreach (var tab in _base.Tabs.Concat(_device.Tabs))
        {
            if (tab.Tiles.FirstOrDefault(t => t.Id == tileId) is { } tile)
            {
                return (tab, tile);
            }
        }

        return null;
    }

    /// <summary>Allowed sizes for a tile (unknown type → all).</summary>
    public IReadOnlyList<TileSize> AllowedSizes(TileConfig tile) =>
        _allowedSizes?.Invoke(tile) is { Count: > 0 } sizes ? sizes : TileSize.All;

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var (b, d) = _undo.Pop();
        _base = UeBarJson.Deserialize<LayoutDocument>(b)!;
        _device = UeBarJson.Deserialize<DeviceLayout>(d)!;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Tabs

    /// <summary>Create a new tab (default: at the end, in the base layout). Returns the new ID.</summary>
    public string AddTab(string name, string? icon = null, TabOrigin origin = TabOrigin.Base, int? index = null)
    {
        var tab = new LayoutTab { Id = NewId("tab"), Name = Clean(name) ?? CoreText.T["Layout_NewTab"], Icon = icon };
        Change(() =>
        {
            var order = DisplayOrder();
            (origin == TabOrigin.Base ? _base.Tabs : _device.Tabs).Add(tab);
            order.Insert(Math.Clamp(index ?? order.Count, 0, order.Count), tab.Id);
            ApplyOrder(order);
            return true;
        });
        return tab.Id;
    }

    public bool RenameTab(string tabId, string name) =>
        Change(() => FindTab(tabId) is { } tab && Clean(name) is { } clean && tab.Name != clean && Set(() => tab.Name = clean));

    public bool SetTabIcon(string tabId, string? icon) =>
        Change(() => FindTab(tabId) is { } tab && tab.Icon != icon && Set(() => tab.Icon = icon));

    public bool SetTabVisibility(string tabId, TabVisibility visibility) =>
        Change(() => FindTab(tabId) is { } tab && tab.Visibility != visibility && Set(() => tab.Visibility = visibility));

    /// <summary>"On all my devices" (base) ↔ "Only on this device". Position stays.</summary>
    public bool SetTabOrigin(string tabId, TabOrigin origin) => Change(() =>
    {
        var current = OriginOf(tabId);
        if (current is null || current == origin)
        {
            return false;
        }

        var order = DisplayOrder();
        var from = current == TabOrigin.Base ? _base.Tabs : _device.Tabs;
        var to = origin == TabOrigin.Base ? _base.Tabs : _device.Tabs;
        var tab = from.First(t => t.Id == tabId);
        from.Remove(tab);
        to.Add(tab);
        _device.HiddenBaseTabs.Remove(tabId);
        ApplyOrder(order);
        return true;
    });

    /// <summary>Hide/show a base tab only on this device.</summary>
    public bool SetTabHiddenOnDevice(string tabId, bool hidden) => Change(() =>
    {
        if (OriginOf(tabId) != TabOrigin.Base || _device.HiddenBaseTabs.Contains(tabId) == hidden)
        {
            return false;
        }

        if (hidden)
        {
            _device.HiddenBaseTabs.Add(tabId);
        }
        else
        {
            _device.HiddenBaseTabs.Remove(tabId);
        }

        return true;
    });

    /// <summary>Move a tab to a new position in the display order.</summary>
    public bool MoveTab(string tabId, int newIndex) => Change(() =>
    {
        var order = DisplayOrder();
        var old = order.IndexOf(tabId);
        if (old < 0)
        {
            return false;
        }

        newIndex = Math.Clamp(newIndex, 0, order.Count - 1);
        if (old == newIndex)
        {
            return false;
        }

        order.RemoveAt(old);
        order.Insert(newIndex, tabId);
        ApplyOrder(order);
        return true;
    });

    public bool RemoveTab(string tabId) => Change(() =>
    {
        var order = DisplayOrder();
        var removed = _base.Tabs.RemoveAll(t => t.Id == tabId) + _device.Tabs.RemoveAll(t => t.Id == tabId);
        if (removed == 0)
        {
            return false;
        }

        _device.HiddenBaseTabs.Remove(tabId);
        order.Remove(tabId);
        ApplyOrder(order);
        return true;
    });

    // ------------------------------------------------------------------ Tiles

    /// <summary>Insert a tile into a tab (default: at the end). A missing/duplicate ID is reassigned. Returns the ID.</summary>
    public string? AddTile(string tabId, TileConfig tile, int? index = null)
    {
        var copy = LayoutLayers.Clone(tile);
        if (string.IsNullOrWhiteSpace(copy.Id) || FindTile(copy.Id) is not null)
        {
            copy.Id = NewId("k");
        }

        var ok = Change(() =>
        {
            if (FindTab(tabId) is not { } tab)
            {
                return false;
            }

            tab.Tiles.Insert(Math.Clamp(index ?? tab.Tiles.Count, 0, tab.Tiles.Count), copy);
            return true;
        });
        return ok ? copy.Id : null;
    }

    /// <summary>
    /// Move a tile – within the same tab (new position) or into another one. <paramref name="index"/>
    /// refers to the target tab's list without the tile; null = at the end.
    /// </summary>
    public bool MoveTile(string tileId, string targetTabId, int? index = null) => Change(() =>
    {
        if (FindTile(tileId) is not { } found || FindTab(targetTabId) is not { } target)
        {
            return false;
        }

        var oldIndex = found.Tab.Tiles.IndexOf(found.Tile);
        found.Tab.Tiles.RemoveAt(oldIndex);
        var newIndex = Math.Clamp(index ?? target.Tiles.Count, 0, target.Tiles.Count);
        target.Tiles.Insert(newIndex, found.Tile);
        return !(ReferenceEquals(found.Tab, target) && oldIndex == newIndex);
    });

    /// <summary>Change size – only to a size the tile type allows.</summary>
    public bool ResizeTile(string tileId, TileSize size) => Change(() =>
        FindTile(tileId) is { } found
        && found.Tile.Size != size
        && AllowedSizes(found.Tile).Contains(size)
        && Set(() => found.Tile.Size = size));

    /// <summary>Override the name (empty = name from the module).</summary>
    public bool SetTileLabel(string tileId, string? label) => Change(() =>
        FindTile(tileId) is { } found && found.Tile.Label != Clean(label) && Set(() => found.Tile.Label = Clean(label)));

    /// <summary>Override the icon (null = icon from the module).</summary>
    public bool SetTileIcon(string tileId, string? icon) => Change(() =>
        FindTile(tileId) is { } found && found.Tile.Icon != Clean(icon) && Set(() => found.Tile.Icon = Clean(icon)));

    /// <summary>Replace type/slots/size of a tile (ID and position stay).</summary>
    public bool ReplaceTile(string tileId, TileConfig replacement) => Change(() =>
    {
        if (FindTile(tileId) is not { } found)
        {
            return false;
        }

        var copy = LayoutLayers.Clone(replacement);
        copy.Id = tileId;
        found.Tab.Tiles[found.Tab.Tiles.IndexOf(found.Tile)] = copy;
        return true;
    });

    public bool RemoveTile(string tileId) => Change(() =>
        FindTile(tileId) is { } found && found.Tab.Tiles.Remove(found.Tile));

    // ------------------------------------------------------------------ Status line (M6)

    /// <summary>At most this many values in the status line (SPEC 10).</summary>
    public const int MaxStatusItems = 5;

    /// <summary>Status values of the base layout in display order.</summary>
    public IReadOnlyList<StatusItemConfig> StatusItems => [.. _base.Status];

    /// <summary>Add a value (no duplicates, at most <see cref="MaxStatusItems"/>).</summary>
    public bool AddStatus(string moduleId, string entity) => Change(() =>
    {
        if (_base.Status.Count >= MaxStatusItems
            || _base.Status.Any(s => string.Equals(s.Module, moduleId, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Entity, entity, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _base.Status.Add(new StatusItemConfig { Module = moduleId, Entity = entity });
        return true;
    });

    public bool RemoveStatus(int index) => Change(() =>
    {
        if (index < 0 || index >= _base.Status.Count)
        {
            return false;
        }

        _base.Status.RemoveAt(index);
        return true;
    });

    public bool MoveStatus(int index, int newIndex) => Change(() =>
    {
        if (index < 0 || index >= _base.Status.Count || newIndex < 0 || newIndex >= _base.Status.Count || index == newIndex)
        {
            return false;
        }

        var item = _base.Status[index];
        _base.Status.RemoveAt(index);
        _base.Status.Insert(newIndex, item);
        return true;
    });

    // ------------------------------------------------------------------ internal

    /// <summary>Run a step; it only lands on the undo stack if it changed something.</summary>
    private bool Change(Func<bool> action)
    {
        var snapshot = (UeBarJson.Serialize(_base), UeBarJson.Serialize(_device));
        bool changed;
        try
        {
            changed = action();
        }
        catch
        {
            Restore(snapshot);
            throw;
        }

        if (!changed)
        {
            Restore(snapshot); // undo half-done changes
            return false;
        }

        _undo.Push(snapshot);
        if (_undo.Count > MaxUndo)
        {
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep)
            {
                _undo.Push(s);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Restore((string Base, string Device) snapshot)
    {
        _base = UeBarJson.Deserialize<LayoutDocument>(snapshot.Base)!;
        _device = UeBarJson.Deserialize<DeviceLayout>(snapshot.Device)!;
    }

    private static bool Set(Action action)
    {
        action();
        return true;
    }

    private List<string> DisplayOrder() => [.. Composed.AllTabs.Select(t => t.Tab.Id)];

    /// <summary>
    /// Take over the order: base and device tabs each in this order; the
    /// overall order is only saved if there are device tabs (otherwise device.json stays empty).
    /// </summary>
    private void ApplyOrder(List<string> order)
    {
        int Rank(LayoutTab t) => order.IndexOf(t.Id) is var i and >= 0 ? i : int.MaxValue;
        _base.Tabs = [.. _base.Tabs.OrderBy(Rank)];
        _device.Tabs = [.. _device.Tabs.OrderBy(Rank)];
        _device.TabOrder = _device.Tabs.Count > 0 ? [.. order.Where(id => FindTab(id) is not null)] : [];
    }

    private string NewId(string prefix)
    {
        while (true)
        {
            var id = $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";
            if (FindTab(id) is null && FindTile(id) is null)
            {
                return id;
            }
        }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
