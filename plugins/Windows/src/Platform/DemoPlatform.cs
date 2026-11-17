// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Modules.Windows.Platform;

/// <summary>
/// Demo data with neutral names for screenshots and trying things out (environment variable
/// <c>UEBAR_WINDOWS_DEMO=1</c>, developers only). Everything lives in memory; nothing touches Windows.
/// </summary>
internal static class DemoPlatform
{
    public const string Variable = "UEBAR_WINDOWS_DEMO";

    public static bool IsOn => Environment.GetEnvironmentVariable(Variable) is "1" or "true";

    public static WindowsPlatform Create() => new(new Audio(), new Media(), new Radio(), new Bluetooth(), new SystemValues(), new Shell());

    private sealed class Audio : IAudioPlatform
    {
        private readonly List<AudioDevice> _outputs = [new("demo-headphones", "Headphones"), new("demo-monitor", "Monitor"), new("demo-speakers", "Speakers")];
        private readonly List<AppAudio> _apps = [new("music", "Music", 0.7, false, true), new("browser", "Browser", 0.45, false, true)];
        private string _default = "demo-headphones";
        private double _volume = 0.35;
        private bool _muted;

        public event EventHandler? Changed;

        public event EventHandler? AppsChanged;

        public AudioState GetState() => new([.. _outputs], _default, _volume, _muted);

        public IReadOnlyList<AppAudio> GetApps() => [.. _apps];

        public void SetVolume(double volume)
        {
            _volume = volume;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetMute(bool muted)
        {
            _muted = muted;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetDefaultOutput(string deviceId)
        {
            _default = deviceId;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetAppVolume(string key, double volume) => Change(key, a => a with { Volume = volume });

        public void SetAppMute(string key, bool muted) => Change(key, a => a with { Muted = muted });

        private void Change(string key, Func<AppAudio, AppAudio> change)
        {
            var i = _apps.FindIndex(a => a.Key == key);
            if (i >= 0)
            {
                _apps[i] = change(_apps[i]);
                AppsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Media : IMediaPlatform
    {
        private bool _playing = true;

        public event EventHandler? Changed;

        public event EventHandler? TimelineChanged
        {
            add { }
            remove { }
        }

        public string? CurrentSessionId => "music";

        public Task<IReadOnlyList<MediaSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaSessionInfo>>(
            [
                new("music", "Music.exe", "Music", "Morning Light", "Demo Artist", "Demo Album", _playing ? MediaPlayback.Playing : MediaPlayback.Paused,
                    72, 214, DateTimeOffset.Now),
                new("browser", "Browser.exe", "Browser", "Nature documentary", "Demo Channel", null, MediaPlayback.Paused, 600, 2400, DateTimeOffset.Now),
            ]);

        public Task SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken)
        {
            if (command == MediaCommand.PlayPause)
            {
                _playing = !_playing;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadThumbnailAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public void Dispose()
        {
        }
    }

    private sealed class Radio : IRadioPlatform
    {
        private RadioState _state = new(RadioSwitch.On, RadioSwitch.On, "Home Wi-Fi", 4, true);

        public event EventHandler? Changed;

        public RadioState GetState() => _state;

        public Task<bool> SetWifiAsync(bool on, CancellationToken cancellationToken)
        {
            _state = _state with { Wifi = on ? RadioSwitch.On : RadioSwitch.Off, WifiConnected = on };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }

        public Task<bool> SetBluetoothAsync(bool on, CancellationToken cancellationToken)
        {
            _state = _state with { Bluetooth = on ? RadioSwitch.On : RadioSwitch.Off };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Bluetooth : IBluetoothPlatform
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<BluetoothDeviceInfo> GetDevices() =>
        [
            new("demo-earbuds", "Earbuds", true, 80, DeviceIcons.Headphones),
            new("demo-mouse", "Mouse", true, 55, DeviceIcons.Mouse),
            new("demo-keyboard", "Keyboard", false, null, DeviceIcons.Keyboard),
        ];

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class SystemValues : ISystemPlatform
    {
        private int _brightness = 70;

        public event EventHandler? PowerChanged
        {
            add { }
            remove { }
        }

        public PowerState GetPower() => new(true, 76, false, false, TimeSpan.FromMinutes(185));

        public Task<SystemSample> SampleAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SystemSample(12, 41, 6.5, 16, "C:", 212, 476));

        public int? GetBrightness() => _brightness;

        public void SetBrightness(int percent) => _brightness = percent;

        public void Dispose()
        {
        }
    }

    private sealed class Shell : IShellPlatform
    {
        public void Lock()
        {
        }

        public SleepResult Sleep() => SleepResult.Sleeping;

        public void ScreenOff()
        {
        }

        public void OpenSettings(string uri)
        {
        }
    }
}
