// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using global::Windows.Devices.Radios;
using global::Windows.Networking.Connectivity;

namespace UeBar.Modules.Windows.Platform;

/// <summary>
/// Wi-Fi and Bluetooth on/off (<c>Windows.Devices.Radios</c>, like the quick settings) and the
/// connected Wi-Fi network (<c>NetworkInformation</c>). Event driven.
/// Since Windows 11 24H2 the network name needs the location permission for apps – without it the
/// tile says "Connected" without a name.
/// </summary>
internal sealed class RadioPlatform : IRadioPlatform
{
    private readonly Lock _lock = new();
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _disposed;

    private RadioPlatform()
    {
    }

    public event EventHandler? Changed;

    public static async Task<RadioPlatform> CreateAsync()
    {
        var platform = new RadioPlatform();
        if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
        {
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind == RadioKind.WiFi && platform._wifi is null)
                {
                    platform._wifi = radio;
                    radio.StateChanged += platform.OnRadioChanged;
                }
                else if (radio.Kind == RadioKind.Bluetooth && platform._bluetooth is null)
                {
                    platform._bluetooth = radio;
                    radio.StateChanged += platform.OnRadioChanged;
                }
            }
        }

        NetworkInformation.NetworkStatusChanged += platform.OnNetworkChanged;
        return platform;
    }

    public RadioState GetState()
    {
        Radio? wifi;
        Radio? bluetooth;
        lock (_lock)
        {
            wifi = _wifi;
            bluetooth = _bluetooth;
        }

        var wifiState = Map(wifi);
        string? network = null;
        int? bars = null;
        var connected = false;
        if (wifiState == RadioSwitch.On)
        {
            try
            {
                foreach (var profile in NetworkInformation.GetConnectionProfiles())
                {
                    if (!profile.IsWlanConnectionProfile || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                    {
                        continue;
                    }

                    connected = true;
                    var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                    network = string.IsNullOrWhiteSpace(ssid) ? null : ssid;
                    bars = profile.GetSignalBars() is { } b ? b : null;
                    break;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // No permission for network details – on/off still works.
            }
        }

        return new RadioState(wifiState, Map(bluetooth), network, bars, connected);
    }

    public Task<bool> SetWifiAsync(bool on, CancellationToken cancellationToken) => SetAsync(_wifi, on);

    public Task<bool> SetBluetoothAsync(bool on, CancellationToken cancellationToken) => SetAsync(_bluetooth, on);

    private static async Task<bool> SetAsync(Radio? radio, bool on) =>
        radio is not null && await radio.SetStateAsync(on ? global::Windows.Devices.Radios.RadioState.On : global::Windows.Devices.Radios.RadioState.Off) == RadioAccessStatus.Allowed;

    private static RadioSwitch Map(Radio? radio) => radio?.State switch
    {
        null => RadioSwitch.Unavailable,
        global::Windows.Devices.Radios.RadioState.On => RadioSwitch.On,
        global::Windows.Devices.Radios.RadioState.Off => RadioSwitch.Off,
        global::Windows.Devices.Radios.RadioState.Disabled => RadioSwitch.Disabled,
        _ => RadioSwitch.Unavailable,
    };

    private void OnRadioChanged(Radio sender, object args) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnNetworkChanged(object sender) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
            if (_wifi is not null)
            {
                _wifi.StateChanged -= OnRadioChanged;
            }

            if (_bluetooth is not null)
            {
                _bluetooth.StateChanged -= OnRadioChanged;
            }

            _wifi = null;
            _bluetooth = null;
        }
    }
}
