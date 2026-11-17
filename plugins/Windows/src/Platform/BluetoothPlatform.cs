// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Enumeration;
using global::Windows.Devices.Enumeration.Pnp;

namespace UeBar.Modules.Windows.Platform;

/// <summary>
/// Paired Bluetooth devices (classic + LE) with connection state and the battery level Windows shows
/// under Settings → Bluetooth &amp; devices. Windows keeps that level as a device property
/// (<see cref="BatteryKey"/>) on a device node of the same container (e.g. the hands-free profile of a
/// headset); it changes without an event.
/// Read on request only (while a device tile is visible, at most every <see cref="Interval"/>): permanent
/// device watchers cost several MB of memory for something nobody looks at while the bar is collapsed.
/// </summary>
internal sealed class BluetoothPlatform : IBluetoothPlatform
{
    internal const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string AepConnected = "System.Devices.Aep.IsConnected";
    private const string AepContainer = "System.Devices.Aep.ContainerId";
    private const string CodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";

    /// <summary>Not more often than this (the module asks on every polling round while visible).</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private IReadOnlyList<BluetoothDeviceInfo> _devices = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public event EventHandler? Changed;

    public IReadOnlyList<BluetoothDeviceInfo> GetDevices()
    {
        lock (_lock)
        {
            return _devices;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (DateTimeOffset.Now - _readAt < Interval)
            {
                return;
            }

            _readAt = DateTimeOffset.Now;
        }

        var endpoints = new List<Entry>();
        foreach (var selector in new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) })
        {
            DeviceInformationCollection found;
            try
            {
                found = await DeviceInformation.FindAllAsync(selector, [AepConnected, AepContainer, CodMajor, CodMinor], DeviceInformationKind.AssociationEndpoint);
            }
            catch (ArgumentException)
            {
                found = await DeviceInformation.FindAllAsync(selector, [AepConnected, AepContainer], DeviceInformationKind.AssociationEndpoint);
            }

            endpoints.AddRange(found.Select(info => new Entry(
                info.Id,
                info.Name,
                Get<bool>(info.Properties, AepConnected),
                Get<Guid>(info.Properties, AepContainer),
                IconFor(info.Properties, info.Name))));
        }

        var batteries = await ReadBatteriesAsync().ConfigureAwait(false);
        IReadOnlyList<BluetoothDeviceInfo> devices =
        [
            .. endpoints
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .GroupBy(e => e.Container == Guid.Empty ? (object)e.Id : e.Container)
                .Select(g =>
                {
                    // Classic and LE endpoint of the same device: one entry, connected if one of them is.
                    var first = g.OrderByDescending(e => e.Connected).First();
                    int? battery = first.Container != Guid.Empty && batteries.TryGetValue(first.Container, out var b) ? b : null;
                    return new BluetoothDeviceInfo(first.Id, first.Name, g.Any(e => e.Connected), battery, first.Icon);
                })
                .OrderByDescending(d => d.IsConnected)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
        ];

        bool changed;
        lock (_lock)
        {
            changed = !devices.SequenceEqual(_devices);
            _devices = devices;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Battery level per device container.</summary>
    private static async Task<Dictionary<Guid, int>> ReadBatteriesAsync()
    {
        var batteries = new Dictionary<Guid, int>();
        PnpObjectCollection objects;
        try
        {
            objects = await PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryKey, ContainerKey], $"\"{BatteryKey}\":<>[]");
        }
        catch (ArgumentException)
        {
            // Filter not understood: read all devices and filter here (slower, rare).
            objects = await PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryKey, ContainerKey]);
        }

        foreach (var item in objects)
        {
            if (item.Properties.TryGetValue(BatteryKey, out var level) && level is not null
                && item.Properties.TryGetValue(ContainerKey, out var container) && container is Guid id && id != Guid.Empty)
            {
                var percent = Convert.ToInt32(level, System.Globalization.CultureInfo.InvariantCulture);
                if (percent is >= 0 and <= 100)
                {
                    batteries[id] = percent;
                }
            }
        }

        return batteries;
    }

    private static T? Get<T>(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>Icon from the Bluetooth class of device (major/minor), otherwise from the name.</summary>
    internal static string IconFor(IReadOnlyDictionary<string, object> properties, string? name)
    {
        var major = properties.TryGetValue(CodMajor, out var mj) && mj is not null ? Convert.ToInt32(mj, System.Globalization.CultureInfo.InvariantCulture) : -1;
        var minor = properties.TryGetValue(CodMinor, out var mn) && mn is not null ? Convert.ToInt32(mn, System.Globalization.CultureInfo.InvariantCulture) : -1;
        return DeviceIcons.For(major, minor, name);
    }

    public void Dispose()
    {
    }

    private sealed record Entry(string Id, string Name, bool Connected, Guid Container, string Icon);
}

/// <summary>Icons for devices (Segoe Fluent Icons code points).</summary>
internal static class DeviceIcons
{
    public const string Bluetooth = "U+E702";
    public const string Headphones = "Headphones";
    public const string Mouse = "U+E962";
    public const string Keyboard = "U+E765";
    public const string Gamepad = "U+E7FC";
    public const string Phone = "Phone";
    public const string Speaker = "Speaker";

    /// <param name="major">Bluetooth major device class (-1 = unknown).</param>
    /// <param name="minor">Minor device class (-1 = unknown).</param>
    public static string For(int major, int minor, string? name)
    {
        switch (major)
        {
            case 2:
                return Phone;
            case 4:
                return Headphones;
            case 5 when (minor & 0x10) != 0:
                return Keyboard;
            case 5 when (minor & 0x20) != 0:
                return Mouse;
            case 5 when (minor & 0x0F) is 1 or 2:
                return Gamepad;
        }

        var n = name ?? "";
        bool Has(string word) => n.Contains(word, StringComparison.OrdinalIgnoreCase);
        return Has("buds") || Has("headset") || Has("headphone") || Has("kopfh") || Has("airpods") || Has("wh-") || Has("wf-") ? Headphones
            : Has("mouse") || Has("maus") || Has("mx ") ? Mouse
            : Has("keyboard") || Has("tastatur") || Has("keys") ? Keyboard
            : Has("controller") || Has("gamepad") ? Gamepad
            : Has("speaker") || Has("lautsprecher") || Has("boom") ? Speaker
            : Bluetooth;
    }
}
