// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using UeBar.Core.Bus;
using UeBar.Core.Localization;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Modules.Windows.Platform;
using UeBar.Sdk;

namespace UeBar.Modules.Windows.Tests;

/// <summary>Tests check German texts – pin the culture (like the other test projects).</summary>
internal static class TestCulture
{
#pragma warning disable CA2255 // module initializer in a test assembly is intended
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void PinGerman()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = german;
        CultureInfo.DefaultThreadCurrentUICulture = german;
        CultureInfo.CurrentCulture = german;
        CultureInfo.CurrentUICulture = german;
        UiCultureSource.Shared.Set(german);
    }
}

internal sealed class ListLogger : IUeLogger
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    public void Log(LogLevel level, string? moduleId, string message, Exception? exception = null)
    {
        lock (_lock)
        {
            _lines.Add($"[{level}] [{moduleId}] {message} {exception?.Message}");
        }
    }
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The worker changed a tile's elements while the condition looked at them – look again.
            }

            await Task.Delay(10);
        }

        return condition();
    }
}

// ====================================================================== fakes

internal sealed class FakeAudio : IAudioPlatform
{
    public List<AudioDevice> Outputs { get; } =
    [
        new("dev-headphones", "Headphones", "USB Audio"),
        new("dev-monitor", "Monitor", "Display Audio"),
    ];

    public string? DefaultId { get; set; } = "dev-headphones";

    public double Volume { get; set; } = 0.4;

    public bool Muted { get; set; }

    public List<AppAudio> Apps { get; } = [];

    public bool FailSwitch { get; set; }

    public event EventHandler? Changed;

    public event EventHandler? AppsChanged;

    public AudioState GetState() => new([.. Outputs], DefaultId, Volume, Muted);

    public IReadOnlyList<AppAudio> GetApps()
    {
        lock (Apps)
        {
            return [.. Apps];
        }
    }

    public void SetVolume(double volume)
    {
        Volume = volume;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetMute(bool muted)
    {
        Muted = muted;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDefaultOutput(string deviceId)
    {
        if (FailSwitch)
        {
            throw new AudioSwitchException("IPolicyConfig refused (test)");
        }

        DefaultId = deviceId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAppVolume(string key, double volume) => ChangeApp(key, a => a with { Volume = volume });

    public void SetAppMute(string key, bool muted) => ChangeApp(key, a => a with { Muted = muted });

    public void SetApps(params AppAudio[] apps)
    {
        lock (Apps)
        {
            Apps.Clear();
            Apps.AddRange(apps);
        }

        AppsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeApp(string key, Func<AppAudio, AppAudio> change)
    {
        lock (Apps)
        {
            var i = Apps.FindIndex(a => a.Key == key);
            if (i >= 0)
            {
                Apps[i] = change(Apps[i]);
            }
        }

        AppsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }
}

internal sealed class FakeMedia : IMediaPlatform
{
    private readonly Lock _lock = new();
    private List<MediaSessionInfo> _sessions = [];

    public string? CurrentSessionId { get; set; }

    public List<(string Session, MediaCommand Command)> Commands { get; } = [];

    public event EventHandler? Changed;

    public event EventHandler? TimelineChanged;

    public void RaiseTimeline() => TimelineChanged?.Invoke(this, EventArgs.Empty);

    public void Set(params MediaSessionInfo[] sessions)
    {
        lock (_lock)
        {
            _sessions = [.. sessions];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<MediaSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MediaSessionInfo>>([.. _sessions]);
        }
    }

    public Task SendAsync(string sessionId, MediaCommand command, CancellationToken cancellationToken)
    {
        lock (Commands)
        {
            Commands.Add((sessionId, command));
        }

        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadThumbnailAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<byte[]?>([1, 2, 3]);

    public void Dispose()
    {
    }

    public static MediaSessionInfo Session(string app, string title, MediaPlayback playback = MediaPlayback.Playing, string? artist = "Artist") =>
        new(app, app + ".exe", app, title, artist, null, playback, 10, 200, DateTimeOffset.Now, ThumbnailKey: title.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture));
}

internal sealed class FakeRadio : IRadioPlatform
{
    public RadioState State { get; set; } = new(RadioSwitch.On, RadioSwitch.On, "Home network", 4, true);

    public bool Refuse { get; set; }

    public event EventHandler? Changed;

    public RadioState GetState() => State;

    public Task<bool> SetWifiAsync(bool on, CancellationToken cancellationToken)
    {
        if (Refuse)
        {
            return Task.FromResult(false);
        }

        State = State with { Wifi = on ? RadioSwitch.On : RadioSwitch.Off, WifiConnected = on, WifiNetwork = on ? "Home network" : null };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    public Task<bool> SetBluetoothAsync(bool on, CancellationToken cancellationToken)
    {
        State = State with { Bluetooth = on ? RadioSwitch.On : RadioSwitch.Off };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeBluetooth : IBluetoothPlatform
{
    public List<BluetoothDeviceInfo> Devices { get; } =
    [
        new("bt-1", "Earbuds", true, 70, "Headphones"),
        new("bt-2", "Mouse", false, null, "U+E962"),
    ];

    public int Refreshes { get; private set; }

    public event EventHandler? Changed;

    public IReadOnlyList<BluetoothDeviceInfo> GetDevices() => [.. Devices];

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Refreshes++;
        return Task.CompletedTask;
    }

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }
}

internal sealed class FakeSystem : ISystemPlatform
{
    private int _samples;

    public PowerState Power { get; set; } = new(true, 80, false, false, TimeSpan.FromMinutes(130));

    public int? Brightness { get; set; } = 60;

    public int Samples => Volatile.Read(ref _samples);

    public event EventHandler? PowerChanged;

    public PowerState GetPower() => Power;

    public void SetPower(PowerState power)
    {
        Power = power;
        PowerChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<SystemSample> SampleAsync(CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _samples);
        return Task.FromResult(new SystemSample(10 + n, 50, 8, 16, "C:", 120, 476));
    }

    public int? GetBrightness() => Brightness;

    public void SetBrightness(int percent) => Brightness = percent;

    public void Dispose()
    {
    }
}

internal sealed class FakeShell : IShellPlatform
{
    public List<string> Calls { get; } = [];

    public SleepResult SleepResult { get; set; } = SleepResult.Sleeping;

    public void Lock() => Add("lock");

    public SleepResult Sleep()
    {
        Add("sleep");
        return SleepResult;
    }

    public void ScreenOff() => Add("screen-off");

    public void OpenSettings(string uri) => Add(uri);

    private void Add(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }
    }
}

// ====================================================================== environment

/// <summary>
/// The plugin inside the real core (registry, guard, bus) with fake Windows APIs and a fake clock
/// for polling and the sleep confirmation.
/// </summary>
internal sealed class WinEnv : IAsyncDisposable
{
    public WinEnv(params BusGrant[] grants)
    {
        Guard = new ModuleGuard(Log);
        Policy = new GrantListPermissionPolicy(grants);
        Bus = new ModuleBus(Guard, Policy, Log);
        State = new InMemoryModuleStateStore();
        DataDirectory = Path.Combine(Path.GetTempPath(), "uebar-win-tests", Guid.NewGuid().ToString("N"));
        Registry = new ModuleRegistry(Guard, Bus, Log, State, DataDirectory);
        Module = new WindowsModule(_ => Task.FromResult(new WindowsPlatform(Audio, Media, Radio, Bluetooth, System, Shell)), Time) { Coalesce = TimeSpan.Zero };
        Registry.Register(Module);
    }

    public ListLogger Log { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    public FakeAudio Audio { get; } = new();

    public FakeMedia Media { get; } = new();

    public FakeRadio Radio { get; } = new();

    public FakeBluetooth Bluetooth { get; } = new();

    public FakeSystem System { get; } = new();

    public FakeShell Shell { get; } = new();

    public ModuleGuard Guard { get; }

    public GrantListPermissionPolicy Policy { get; }

    public ModuleBus Bus { get; }

    public InMemoryModuleStateStore State { get; }

    public ModuleRegistry Registry { get; }

    public WindowsModule Module { get; }

    public string DataDirectory { get; }

    public async Task<WinEnv> StartAsync()
    {
        Assert.True(await Registry.StartModuleAsync(WindowsModule.ModuleId));
        return this;
    }

    public static TileConfig Config(string type, string entity, TileSize? size = null, string? id = null) => new()
    {
        Id = id ?? "t-" + Guid.NewGuid().ToString("N")[..6],
        Module = WindowsModule.ModuleId,
        Type = type,
        Size = size ?? TileSize.Large,
        Entities = new Dictionary<string, string> { ["main"] = entity },
    };

    public Task<TileModel> TileAsync(string type, string entity, TileSize? size = null) => Registry.CreateTileAsync(Config(type, entity, size));

    public Task InteractAsync(TileModel tile, string element, InteractionKind kind, double? number = null, string? text = null) =>
        Registry.InteractAsync(tile, new TileInteraction(element, kind, number, text));

    /// <summary>
    /// Waits until the condition holds and the update worker is idle (it may still be filling the
    /// tile when the first property changes).
    /// </summary>
    public async Task<bool> UntilAsync(Func<bool> condition)
    {
        var met = await Wait.UntilAsync(condition);
        await SettleAsync();
        return met && condition();
    }

    /// <summary>The worker has nothing left to do (twice in a row, a few ms apart).</summary>
    public async Task SettleAsync()
    {
        for (var calm = 0; calm < 2;)
        {
            await Task.Delay(5);
            calm = Module.IsIdle ? calm + 1 : 0;
        }
    }

    public async Task InteractAndSettleAsync(TileModel tile, string element, InteractionKind kind, double? number = null, string? text = null)
    {
        await InteractAsync(tile, element, kind, number, text);
        await SettleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
