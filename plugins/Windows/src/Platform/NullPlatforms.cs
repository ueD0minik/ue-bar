// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.Windows.Platform;

// Stand-ins when a Windows API is not available (old Windows, service not running, no access):
// the rest of the plugin keeps working, the affected tiles say "not available".

internal sealed class NullAudioPlatform : IAudioPlatform
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler? AppsChanged
    {
        add { }
        remove { }
    }

    public AudioState GetState() => AudioState.None;

    public IReadOnlyList<AppAudio> GetApps() => [];

    public void SetVolume(double volume)
    {
    }

    public void SetMute(bool muted)
    {
    }

    public void SetDefaultOutput(string deviceId) => throw new AudioSwitchException("No audio");

    public void SetAppVolume(string key, double volume)
    {
    }

    public void SetAppMute(string key, bool muted)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class NullMediaPlatform : IMediaPlatform
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler? TimelineChanged
    {
        add { }
        remove { }
    }

    public string? CurrentSessionId => null;

    public Task<IReadOnlyList<MediaSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaSessionInfo>>([]);

    public Task SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]?> LoadThumbnailAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public void Dispose()
    {
    }
}

internal sealed class NullRadioPlatform : IRadioPlatform
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public RadioState GetState() => RadioState.None;

    public Task<bool> SetWifiAsync(bool on, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> SetBluetoothAsync(bool on, CancellationToken cancellationToken) => Task.FromResult(false);

    public void Dispose()
    {
    }
}

internal sealed class NullBluetoothPlatform : IBluetoothPlatform
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<BluetoothDeviceInfo> GetDevices() => [];

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}

internal sealed class NullSystemPlatform : ISystemPlatform
{
    public event EventHandler? PowerChanged
    {
        add { }
        remove { }
    }

    public PowerState GetPower() => PowerState.NoBattery;

    public Task<SystemSample> SampleAsync(CancellationToken cancellationToken) => Task.FromResult(new SystemSample(0, 0, 0, 0, "C:", 0, 0));

    public int? GetBrightness() => null;

    public void SetBrightness(int percent)
    {
    }

    public void Dispose()
    {
    }
}
