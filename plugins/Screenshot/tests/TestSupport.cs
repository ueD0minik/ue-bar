// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using UeBar.Core.Bus;
using UeBar.Core.Localization;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Pins;
using UeBar.Core.Settings;
using UeBar.Sdk;

// One shared UI language per test run.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UeBar.Modules.Screenshot.Tests;

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
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}

/// <summary>Stands in for the core's picker: records the request and answers with a prepared result.</summary>
internal sealed class FakePicker : IScreenAreaPicker
{
    public List<ScreenAreaRequest> Requests { get; } = [];

    /// <summary>The answer; its action is replaced by <see cref="Choose"/> when buttons were offered.</summary>
    public ScreenAreaResult? Result { get; set; } = TwoMonitorResult(new ScreenRect(1800, 200, 400, 300));

    public string? Choose { get; set; } = "pin";

    public Task<ScreenAreaResult?> PickScreenAreaAsync(ScreenAreaRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Result is null ? null : Result with { Mode = request.Mode, ActionId = request.Actions.Count > 0 ? Choose : null });
    }

    /// <summary>Laptop (150 %, primary) left of a 4K-wide OLED (100 %), as at Dominik's desk.</summary>
    public static IReadOnlyList<ScreenMonitor> Monitors { get; } =
    [
        new("laptop", new ScreenRect(0, 0, 1920, 1200), new ScreenRect(0, 0, 1920, 1152), 1.5, true),
        new("oled", new ScreenRect(1920, -240, 5120, 1440), new ScreenRect(1920, -240, 5120, 1392), 1.0, false),
    ];

    public static ScreenAreaResult TwoMonitorResult(ScreenRect bounds) => new(bounds, Monitors) { Image = Png.Tiny };
}

internal sealed class FakeClipboard : IClipboardAccess
{
    public List<string> Texts { get; } = [];

    public List<byte[]> Images { get; } = [];

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        Texts.Add(text);
        return Task.FromResult(true);
    }

    public Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken)
    {
        Images.Add(png);
        return Task.FromResult(true);
    }
}

/// <summary>Stands in for the core's "Save as" dialog: answers with a prepared path (null = cancelled).</summary>
internal sealed class FakeSaveDialog
{
    public List<(string Owner, SaveFileRequest Request)> Asked { get; } = [];

    public string? Answer { get; set; }

    public Task<string?> AskAsync(string owner, SaveFileRequest request, CancellationToken cancellationToken)
    {
        Asked.Add((owner, request));
        return Task.FromResult(Answer);
    }
}

internal sealed class FakeOcr : IShotOcr
{
    public OcrOutcome Outcome { get; set; } = new("Hallo Welt\nZeile zwei", "de-DE");

    public string? AskedLanguage { get; private set; }

    public IReadOnlyList<(string Tag, string Name)> Languages { get; set; } = [("de-DE", "Deutsch (Deutschland)")];

    public Task<OcrOutcome> RecognizeAsync(byte[] image, string? language, CancellationToken cancellationToken)
    {
        AskedLanguage = language;
        return Task.FromResult(Outcome);
    }
}

/// <summary>A 1×1 PNG (enough for everything that does not decode it).</summary>
internal static class Png
{
    public static byte[] Tiny { get; } = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}

/// <summary>Another plugin that may use the screenshot over the bus.</summary>
internal sealed class ConsumerPlugin : IModule
{
    public IModuleBus? Bus { get; private set; }

    public List<BusEvent> Events { get; } = [];

    public string Id => "consumer";

    public string DisplayName => "Consumer";

    public string Icon => "Puzzle";

    public ModuleStatus Status => ModuleStatus.Connected;

    public IReadOnlyList<ITileType> TileTypes => [];

    public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        Bus = context.Bus;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The screenshot plugin inside the real core (registry, guard, bus, pins) with fake picker, clipboard and OCR.</summary>
internal sealed class ShotEnv : IAsyncDisposable
{
    private readonly List<UeNotification> _notifications = [];

    public ShotEnv()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 14, 3, 5, TimeSpan.Zero));
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Guard = new ModuleGuard(Log);
        Bus = new ModuleBus(Guard, new GrantListPermissionPolicy(() => Grants), Log);
        DataDirectory = Path.Combine(Path.GetTempPath(), "uebar-shot-tests", Guid.NewGuid().ToString("N"));
        Registry = new ModuleRegistry(Guard, Bus, Log, State, DataDirectory);
        Pins = new PinManager(Path.Combine(DataDirectory, "pins"), Log, Time);
        Registry.Host.Pins = Pins;
        Registry.Host.ScreenAreaPicker = Picker;
        Registry.Host.Clipboard = Clipboard;
        Registry.Host.SaveDialog = SaveDialog.AskAsync;
        Module = new ScreenshotModule(Ocr, Time);
        Module.NotificationReceived += (_, n) =>
        {
            lock (_notifications)
            {
                _notifications.Add(n);
            }
        };
        Registry.Register(Module);
    }

    public ListLogger Log { get; } = new();

    public FakeTimeProvider Time { get; }

    public List<BusGrant> Grants { get; } = [];

    public ModuleGuard Guard { get; }

    public ModuleBus Bus { get; }

    public InMemoryModuleStateStore State { get; } = new();

    public ModuleRegistry Registry { get; }

    public PinManager Pins { get; }

    public FakePicker Picker { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakeOcr Ocr { get; } = new();

    public FakeSaveDialog SaveDialog { get; } = new();

    public ScreenshotModule Module { get; }

    public string DataDirectory { get; }

    public IReadOnlyList<UeNotification> Notifications
    {
        get
        {
            lock (_notifications)
            {
                return [.. _notifications];
            }
        }
    }

    public async Task<ShotEnv> StartAsync()
    {
        Assert.True(await Registry.StartModuleAsync(ScreenshotModule.ModuleId));
        return this;
    }

    public void Grant(string consumer, string provider, BusAccess access) =>
        Grants.Add(new BusGrant { Consumer = consumer, Provider = provider, Access = access });

    public async Task SettingsAsync(params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
        {
            State.SetValue(ScreenshotModule.ModuleId, key, value);
        }

        await Module.ApplySettingsAsync(State.GetModuleSettings(ScreenshotModule.ModuleId), default);
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
