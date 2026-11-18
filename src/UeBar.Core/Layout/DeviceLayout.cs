// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UeBar.Sdk;
using UeBar.Core.Logging;

namespace UeBar.Core.Layout;

/// <summary>
/// Device level (SPEC 9, level 2) in <c>device.json</c>: own tabs only for this device and
/// hidden base tabs. Optionally an order of all tabs, so device tabs can also sit between
/// base tabs.
/// </summary>
public sealed class DeviceLayout
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Device name at the last save (info only; default: Windows computer name).</summary>
    public string? DeviceName { get; set; }

    /// <summary>Tabs only on this device.</summary>
    public List<LayoutTab> Tabs { get; set; } = [];

    /// <summary>IDs of base tabs that are not shown on this device.</summary>
    public List<string> HiddenBaseTabs { get; set; } = [];

    /// <summary>Order of the tab IDs on this device (unknown IDs at the end, missing ones ignored). Empty = base, then device.</summary>
    public List<string> TabOrder { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Tabs.Count == 0 && HiddenBaseTabs.Count == 0 && TabOrder.Count == 0;
}

/// <summary>Origin of a tab in the merged layout.</summary>
public enum TabOrigin
{
    /// <summary>Base layout ("On all my devices").</summary>
    Base,

    /// <summary>Only this device (device.json).</summary>
    Device,
}

/// <summary>A tab in the merged layout with its origin.</summary>
public sealed record ComposedTab(LayoutTab Tab, TabOrigin Origin, bool HiddenOnDevice);

/// <summary>Result of merging base + device.</summary>
public sealed class ComposedLayout
{
    public ComposedLayout(LayoutDocument document, IReadOnlyList<ComposedTab> allTabs)
    {
        Document = document;
        AllTabs = allTabs;
    }

    /// <summary>What is shown: base tabs (without hidden ones) + device tabs, status line from the base.</summary>
    public LayoutDocument Document { get; }

    /// <summary>All tabs including those hidden on this device (for edit mode).</summary>
    public IReadOnlyList<ComposedTab> AllTabs { get; }

    public TabOrigin? OriginOf(string tabId) => AllTabs.FirstOrDefault(t => t.Tab.Id == tabId)?.Origin;
}

public static class LayoutLayers
{
    /// <summary>
    /// Merge according to SPEC 9: base tabs minus hidden ones + device tabs, sorted by
    /// <see cref="DeviceLayout.TabOrder"/> (if set). Device tabs with an ID that already exists in the
    /// base are skipped (base wins).
    /// </summary>
    public static ComposedLayout Compose(LayoutDocument baseLayout, DeviceLayout? device)
    {
        device ??= new DeviceLayout();
        var hidden = new HashSet<string>(device.HiddenBaseTabs, StringComparer.Ordinal);
        var all = new List<ComposedTab>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in baseLayout.Tabs)
        {
            if (ids.Add(tab.Id))
            {
                all.Add(new ComposedTab(tab, TabOrigin.Base, hidden.Contains(tab.Id)));
            }
        }

        foreach (var tab in device.Tabs)
        {
            if (ids.Add(tab.Id))
            {
                all.Add(new ComposedTab(tab, TabOrigin.Device, false));
            }
        }

        if (device.TabOrder.Count > 0)
        {
            var rank = device.TabOrder.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
            all = [.. all.Select((t, i) => (t, i)).OrderBy(x => rank.TryGetValue(x.t.Tab.Id, out var r) ? r : int.MaxValue).ThenBy(x => x.i).Select(x => x.t)];
        }

        var document = new LayoutDocument
        {
            Version = baseLayout.Version,
            Tabs = [.. all.Where(t => !t.HiddenOnDevice).Select(t => t.Tab)],
            Status = baseLayout.Status,
            Extra = baseLayout.Extra,
        };
        return new ComposedLayout(document, all);
    }

    /// <summary>Is the tab visible at the location? Unknown location: all.</summary>
    public static bool IsVisible(LayoutTab tab, DeviceLocation location) => (tab.Visibility, location) switch
    {
        (_, DeviceLocation.Unknown) => true,
        (TabVisibility.HomeOnly, DeviceLocation.Away) => false,
        (TabVisibility.AwayOnly, DeviceLocation.Home) => false,
        _ => true,
    };

    /// <summary>Deep copy (through JSON – keeps unknown fields too).</summary>
    public static T Clone<T>(T value) => UeBarJson.Clone(value);
}

/// <summary>Reads/writes <c>device.json</c> atomically. Missing file: empty device layout.</summary>
public sealed class DeviceLayoutStore(string path, IUeLogger? log = null)
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly IUeLogger _log = log ?? NullLogger.Instance;

    public string FilePath => _path;

    public bool Exists => File.Exists(_path);

    public async Task<DeviceLayout> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new DeviceLayout();
        }

        var json = await LocalFileLayoutStore.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
        try
        {
            return UeBarJson.Deserialize<DeviceLayout>(json) ?? new DeviceLayout();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(CoreText.T.Format("Layout_DeviceFileInvalid", _path, ex.Message), ex);
        }
    }

    public async Task SaveAsync(DeviceLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (layout.IsEmpty && !File.Exists(_path))
        {
            return; // nothing to create
        }

        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, UeBarJson.Serialize(layout), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await LocalFileLayoutStore.ReplaceAsync(tmp, _path, cancellationToken).ConfigureAwait(false);
        _log.Debug(null, $"Device layout saved: {layout.Tabs.Count} own tabs, {layout.HiddenBaseTabs.Count} hidden");
    }
}
