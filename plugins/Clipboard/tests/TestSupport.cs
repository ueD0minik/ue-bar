// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
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

namespace UeBar.Modules.Clipboard.Tests;

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

/// <summary>Stands in for the app's clipboard watcher: tests raise changes as the app would.</summary>
internal sealed class FakeWatcher : IHostClipboardWatcher
{
    private readonly Lock _lock = new();
    private readonly List<(string Module, ClipboardWatchOptions Options, Action<ClipboardChange> Changed)> _watches = [];

    public int Started { get; private set; }

    public IReadOnlyList<ClipboardWatchOptions> Active
    {
        get
        {
            lock (_lock)
            {
                return [.. _watches.Select(w => w.Options)];
            }
        }
    }

    public IDisposable Watch(string moduleId, ClipboardWatchOptions options, Action<ClipboardChange> changed)
    {
        var entry = (moduleId, options, changed);
        lock (_lock)
        {
            _watches.Add(entry);
            Started++;
        }

        return new Gone(() =>
        {
            lock (_lock)
            {
                _watches.Remove(entry);
            }
        });
    }

    public void Raise(ClipboardChange change)
    {
        List<Action<ClipboardChange>> handlers;
        lock (_lock)
        {
            handlers = [.. _watches.Select(w => w.Changed)];
        }

        foreach (var handler in handlers)
        {
            handler(change);
        }
    }

    private sealed class Gone(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Records what modules write to the clipboard.</summary>
internal sealed class FakeWriter : IHostClipboardWriter, IClipboardAccess
{
    private readonly Lock _lock = new();
    private readonly List<(string Module, ClipboardContent Content)> _writes = [];

    public IReadOnlyList<(string Module, ClipboardContent Content)> Writes
    {
        get
        {
            lock (_lock)
            {
                return [.. _writes];
            }
        }
    }

    public Task<bool> SetContentAsync(string moduleId, ClipboardContent content, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _writes.Add((moduleId, content));
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken) => SetContentAsync("?", new ClipboardContent { Text = text }, cancellationToken);

    public Task<bool> SetImageAsync(byte[] png, CancellationToken cancellationToken) => SetContentAsync("?", new ClipboardContent { ImagePng = png }, cancellationToken);
}

internal sealed class FakePaste : IHostPaste
{
    public List<(string Module, ClipboardContent Content, PasteTarget? Target)> Pastes { get; } = [];

    public bool Answer { get; set; } = true;

    public PasteTarget? CurrentTarget() => new(4242) { Title = "Word" };

    public Task<bool> PasteAsync(string moduleId, ClipboardContent content, PasteTarget? target, CancellationToken cancellationToken)
    {
        lock (Pastes)
        {
            Pastes.Add((moduleId, content, target));
        }

        return Task.FromResult(Answer);
    }
}

/// <summary>
/// Stands in for the choice popup: records every request, runs the scripted "keep open" actions through the request's
/// own callback (as the core does) and then answers with the scripted choice.
/// </summary>
internal sealed class FakePopup : IHostChoicePopup
{
    public List<ChoicePopupRequest> Requests { get; } = [];

    public List<ChoicePopupUpdate?> Updates { get; } = [];

    /// <summary>(row index, action id) pressed while open – row index in the list as it is at that moment.</summary>
    public List<(int Row, string Action)> Script { get; } = [];

    /// <summary>The final choice: row index and action, null = Esc.</summary>
    public (int Row, string Action)? Answer { get; set; }

    public PasteTarget Target { get; } = new(4242) { Title = "Dokument1 - Word" };

    public async Task<ChoicePopupResult?> ShowAsync(string moduleId, ChoicePopupRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var items = request.Items;
        foreach (var (row, action) in Script)
        {
            var update = await request.OnAction!(new ChoicePopupResult(items[row].Id, action, Target), cancellationToken);
            Updates.Add(update);
            items = update?.Items ?? items;
        }

        return Answer is { } a && a.Row < items.Count ? new ChoicePopupResult(items[a.Row].Id, a.Action, Target) : null;
    }
}

/// <summary>A hotkey takeover the test controls.</summary>
internal sealed class FakeTakeover : IHostHotkeyTakeover
{
    public List<Held> Taken { get; } = [];

    public HotkeyTakeoverState StartState { get; set; } = HotkeyTakeoverState.Active;

    public IHotkeyTakeover? TakeOver(string moduleId, string hotkey, Action pressed)
    {
        var held = new Held(hotkey, pressed, StartState);
        Taken.Add(held);
        return held;
    }

    internal sealed class Held(string hotkey, Action pressed, HotkeyTakeoverState state) : IHotkeyTakeover
    {
        public string Hotkey { get; } = hotkey;

        public bool Disposed { get; private set; }

        public HotkeyTakeoverState State { get; private set; } = state;

        public string? Problem { get; private set; }

        public event EventHandler? StateChanged;

        public void Press() => pressed();

        public void Fail(string problem)
        {
            State = HotkeyTakeoverState.Failed;
            Problem = problem;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>DPAPI, counting calls – the real encryption, so tests can look into the files.</summary>
internal sealed class CountingProtector : IClipProtector
{
    private readonly DpapiProtector _inner = new();

    public int Protected { get; private set; }

    public byte[] Protect(byte[] data)
    {
        Protected++;
        return _inner.Protect(data);
    }

    public byte[] Unprotect(byte[] data) => _inner.Unprotect(data);
}

/// <summary>PNG files made in the test (no imaging library in the plugin or its tests).</summary>
internal static class Png
{
    /// <summary>An RGB image; <paramref name="noise"/> = random pixels (compresses badly, like a photo).</summary>
    public static byte[] Make(int width, int height, int seed = 1, bool noise = false)
    {
        var random = new Random(seed);
        var raw = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            raw[row] = 0; // filter none
            for (var x = 0; x < width; x++)
            {
                var at = row + 1 + (x * 3);
                if (noise)
                {
                    raw[at] = (byte)random.Next(256);
                    raw[at + 1] = (byte)random.Next(256);
                    raw[at + 2] = (byte)random.Next(256);
                }
                else
                {
                    raw[at] = (byte)(x * 255 / Math.Max(1, width - 1));
                    raw[at + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                    raw[at + 2] = (byte)(seed * 37);
                }
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        stream.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
    }
}

/// <summary>Another plugin that may use the clipboard over the bus.</summary>
internal sealed class ConsumerPlugin : IModule
{
    public IModuleBus? Bus { get; private set; }

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

/// <summary>The clipboard plugin inside the real core (registry, guard, bus, pins) with fakes for the app's parts.</summary>
internal sealed class ClipEnv : IAsyncDisposable
{
    private readonly List<UeNotification> _notifications = [];

    public ClipEnv(string? folder = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Guard = new ModuleGuard(Log);
        Bus = new ModuleBus(Guard, new GrantListPermissionPolicy(() => Grants), Log);
        DataDirectory = Path.Combine(Path.GetTempPath(), "uebar-clip-tests", Guid.NewGuid().ToString("N"));
        Folder = folder ?? Path.Combine(DataDirectory, "clipboard");
        Registry = new ModuleRegistry(Guard, Bus, Log, State, DataDirectory);
        Pins = new PinManager(Path.Combine(DataDirectory, "pins"), Log, Time);
        Registry.Host.Pins = Pins;
        Registry.Host.Clipboard = Writer;
        Registry.Host.ClipboardWriter = Writer;
        Registry.Host.ClipboardWatcher = Watcher;
        Registry.Host.Paste = Paste;
        Registry.Host.ChoicePopup = Popup;
        Registry.Host.HotkeyTakeover = Hook;
        Module = new ClipboardModule(Protector, Time, Folder);
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

    public FakeWatcher Watcher { get; } = new();

    public FakeWriter Writer { get; } = new();

    public FakePaste Paste { get; } = new();

    public FakePopup Popup { get; } = new();

    public FakeTakeover Hook { get; } = new();

    public CountingProtector Protector { get; } = new();

    public ClipboardModule Module { get; }

    public string DataDirectory { get; }

    public string Folder { get; }

    public ClipStore Store => Module.Store!;

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

    public async Task<ClipEnv> StartAsync()
    {
        Assert.True(await Registry.StartModuleAsync(ClipboardModule.ModuleId));
        return this;
    }

    public void Grant(string consumer, string provider, BusAccess access) =>
        Grants.Add(new BusGrant { Consumer = consumer, Provider = provider, Access = access });

    public async Task SettingsAsync(params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
        {
            State.SetValue(ClipboardModule.ModuleId, key, value);
        }

        await Module.ApplySettingsAsync(State.GetModuleSettings(ClipboardModule.ModuleId), default);
    }

    /// <summary>A change as the app reports it – and waits until the plugin has handled it (through the guard, on the pool).</summary>
    public async Task CopyAsync(ClipboardChange change, int expectedEntries = -1)
    {
        var before = Module.Store?.Entries.Select(e => (e.Id, e.UsedAt)).ToList() ?? [];
        var handled = Log.Lines.Count;
        Watcher.Raise(change);
        await Wait.UntilAsync(() =>
            expectedEntries >= 0 ? Store.Entries.Count == expectedEntries
            : !Store.Entries.Select(e => (e.Id, e.UsedAt)).SequenceEqual(before) || Log.Lines.Count > handled, 3000);
        await Task.Delay(30);
    }

    public ClipboardChange Text(string text, string app = "WINWORD", string? html = null, string? rtf = null, string? writtenBy = null) =>
        new(new ClipboardContent { Text = text, Html = html, Rtf = rtf }, Time.GetUtcNow())
        {
            Source = new ClipboardSourceApp(app, app == "WINWORD" ? "Microsoft Word" : app) { IconPng = Png.Make(4, 4, 9) },
            WrittenByModule = writtenBy,
        };

    public ClipboardChange Image(byte[] png, int width, int height, string app = "mspaint") =>
        new(new ClipboardContent { ImagePng = png }, Time.GetUtcNow())
        {
            Source = new ClipboardSourceApp(app, "Paint"),
            ImageWidth = width,
            ImageHeight = height,
            ThumbnailPng = Png.Make(16, 9, 3),
        };

    public ClipboardChange Files(params string[] paths) =>
        new(new ClipboardContent { Files = paths }, Time.GetUtcNow()) { Source = new ClipboardSourceApp("explorer", "Windows Explorer") };

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
