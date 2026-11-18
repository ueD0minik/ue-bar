// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.Windows.Platform;

// Small interfaces around the Windows APIs, so the module logic is testable without a real Windows
// (fakes in the tests). Events may come from any thread; the module only marks the area as changed
// and reads the new state on its own update worker.

/// <summary>An audio output (speakers, headphones, monitor …).</summary>
/// <param name="Name">Short name ("Headphones", "Monitor").</param>
/// <param name="Detail">Interface/driver name ("Realtek(R) Audio") or null.</param>
/// <param name="IsVirtual">Software device (virtual cable, app channel of a mixer tool) – not offered by default.</param>
internal sealed record AudioDevice(string Id, string Name, string? Detail = null, bool IsVirtual = false);

/// <summary>Master volume of the default output.</summary>
/// <param name="Volume">0–1.</param>
internal sealed record AudioState(IReadOnlyList<AudioDevice> Outputs, string? DefaultId, double Volume, bool Muted)
{
    public static AudioState None { get; } = new([], null, 0, false);

    public AudioDevice? Default => Outputs.FirstOrDefault(d => d.Id == DefaultId);
}

/// <summary>Sound of one app (all its audio sessions together).</summary>
/// <param name="Key">Process name in lower case ("spotify").</param>
/// <param name="IsPlaying">The app plays sound right now (session active).</param>
internal sealed record AppAudio(string Key, string Name, double Volume, bool Muted, bool IsPlaying);

/// <summary>Switching the default output failed (no official API, SPEC decision P2-2).</summary>
internal sealed class AudioSwitchException(string message, Exception? inner = null) : Exception(message, inner);

internal interface IAudioPlatform : IDisposable
{
    AudioState GetState();

    /// <summary>All apps with an audio session on the default output (playing or not).</summary>
    IReadOnlyList<AppAudio> GetApps();

    void SetVolume(double volume);

    void SetMute(bool muted);

    /// <summary>Makes the device the default output (all roles). Throws <see cref="AudioSwitchException"/>.</summary>
    void SetDefaultOutput(string deviceId);

    void SetAppVolume(string key, double volume);

    void SetAppMute(string key, bool muted);

    /// <summary>Devices, default device, volume or mute changed.</summary>
    event EventHandler? Changed;

    /// <summary>App sessions came, went or changed.</summary>
    event EventHandler? AppsChanged;
}

internal enum MediaPlayback
{
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

internal enum MediaCommand
{
    PlayPause,
    Next,
    Previous,
}

/// <summary>A media session Windows knows (the same list as the volume flyout's media controls).</summary>
/// <param name="Id">Stable for as long as the session lives (app ID, numbered if an app has several).</param>
/// <param name="AppId">App user model ID ("Spotify.exe", "MSEdge", "…!App").</param>
/// <param name="ThumbnailKey">Changes when the cover changes (null = no cover).</param>
internal sealed record MediaSessionInfo(
    string Id,
    string AppId,
    string AppName,
    string? Title,
    string? Artist,
    string? Album,
    MediaPlayback Playback,
    double? Position = null,
    double? Duration = null,
    DateTimeOffset? PositionUpdatedAt = null,
    bool CanPrevious = true,
    bool CanNext = true,
    bool CanPlayPause = true,
    string? ThumbnailKey = null)
{
    public bool IsPlaying => Playback == MediaPlayback.Playing;

    public bool HasContent => !string.IsNullOrWhiteSpace(Title);
}

internal interface IMediaPlatform : IDisposable
{
    Task<IReadOnlyList<MediaSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken);

    /// <summary>The session Windows itself considers current (null = none).</summary>
    string? CurrentSessionId { get; }

    Task SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken);

    Task<byte[]?> LoadThumbnailAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Sessions, title, artist or play state changed.</summary>
    event EventHandler? Changed;

    /// <summary>Only the position changed (players report it often) – the module reads it only while a media tile is visible.</summary>
    event EventHandler? TimelineChanged;
}

internal enum RadioSwitch
{
    /// <summary>No such radio (or no access).</summary>
    Unavailable,
    Off,
    On,

    /// <summary>Switched off by hardware/airplane mode – cannot be switched on from here.</summary>
    Disabled,
}

/// <param name="WifiBars">0–5, null = unknown.</param>
internal sealed record RadioState(RadioSwitch Wifi, RadioSwitch Bluetooth, string? WifiNetwork = null, int? WifiBars = null, bool WifiConnected = false)
{
    /// <summary>Connected, but Windows does not tell the network name (since 11 24H2 apps need the location permission).</summary>
    public bool NetworkNameHidden => WifiConnected && string.IsNullOrWhiteSpace(WifiNetwork);

    public static RadioState None { get; } = new(RadioSwitch.Unavailable, RadioSwitch.Unavailable);
}

internal interface IRadioPlatform : IDisposable
{
    RadioState GetState();

    Task<bool> SetWifiAsync(bool on, CancellationToken cancellationToken);

    Task<bool> SetBluetoothAsync(bool on, CancellationToken cancellationToken);

    event EventHandler? Changed;
}

/// <summary>A paired Bluetooth device.</summary>
/// <param name="Battery">0–100 as Windows knows it (Settings → Bluetooth &amp; devices), null = unknown.</param>
/// <param name="Icon">Icon name (Segoe Fluent Icons or "U+E7xx").</param>
internal sealed record BluetoothDeviceInfo(string Id, string Name, bool IsConnected, int? Battery, string Icon);

internal interface IBluetoothPlatform : IDisposable
{
    IReadOnlyList<BluetoothDeviceInfo> GetDevices();

    /// <summary>
    /// Reads devices, connection state and battery levels (no events – called while a device tile is
    /// visible; the platform decides how often it really reads).
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    event EventHandler? Changed;
}

/// <param name="Remaining">Time until empty (discharging), null = unknown.</param>
/// <param name="UntilFull">Time until full (charging), null = unknown.</param>
internal sealed record PowerState(bool HasBattery, int Percent, bool IsCharging, bool OnAc, TimeSpan? Remaining = null, TimeSpan? UntilFull = null)
{
    public static PowerState NoBattery { get; } = new(false, 0, false, true);
}

internal sealed record SystemSample(
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    string DiskName,
    double DiskFreeGb,
    double DiskTotalGb);

internal interface ISystemPlatform : IDisposable
{
    PowerState GetPower();

    event EventHandler? PowerChanged;

    /// <summary>CPU since the last sample (waits briefly for a baseline if the last one is old), memory, disk.</summary>
    Task<SystemSample> SampleAsync(CancellationToken cancellationToken);

    /// <summary>Brightness of the built-in display 0–100, null = there is none (or it is off).</summary>
    int? GetBrightness();

    void SetBrightness(int percent);
}

internal enum SleepResult
{
    Sleeping,

    /// <summary>No classic standby (Modern Standby): the screen was switched off instead.</summary>
    ScreenOff,
    Failed,
}

internal interface IShellPlatform
{
    void Lock();

    SleepResult Sleep();

    void ScreenOff();

    /// <summary>Opens a page of the Windows settings ("ms-settings:sound").</summary>
    void OpenSettings(string uri);
}

/// <summary>Everything the module talks to.</summary>
internal sealed record WindowsPlatform(
    IAudioPlatform Audio,
    IMediaPlatform Media,
    IRadioPlatform Radio,
    IBluetoothPlatform Bluetooth,
    ISystemPlatform System,
    IShellPlatform Shell) : IDisposable
{
    public void Dispose()
    {
        foreach (var part in new IDisposable[] { Audio, Media, Radio, Bluetooth, System })
        {
            try
            {
                part.Dispose();
            }
            catch (Exception)
            {
                // Disposing must never break stopping the module.
            }
        }
    }
}
