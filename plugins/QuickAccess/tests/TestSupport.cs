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
using UeBar.Modules.QuickAccess.Platform;
using UeBar.Sdk;

// One shared UI language per test run.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UeBar.Modules.QuickAccess.Tests;

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

/// <summary>
/// Stands in for the Windows shell: a made-up user folder with files and folders, a few Start menu
/// apps, shortcuts – and every start is recorded instead of run.
/// </summary>
internal sealed class FakeShell : IQuickShell
{
    public const string Profile = @"C:\Users\demo";

    private readonly Lock _lock = new();
    private readonly List<LaunchRequest> _launches = [];
    private int _appReads;
    private int _iconReads;

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AppInfo> Apps { get; } =
    [
        new("Contoso.Notes_abc123!App", "Notizen"),
        new(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Fabrikam\Paint\paint.exe", "Fabrikam Paint"),
        new("Example.Writer.EXE.15", "Writer"),
    ];

    public Dictionary<string, ShortcutInfo> Shortcuts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> UrlShortcuts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Verbs the "installed apps" do not know (e.g. "new" without an Office).</summary>
    public HashSet<string> MissingVerbs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user says no to the administrator prompt.</summary>
    public bool DeclineAdmin { get; set; }

    public IReadOnlyList<LaunchRequest> Launches
    {
        get
        {
            lock (_lock)
            {
                return [.. _launches];
            }
        }
    }

    public int AppReads => Volatile.Read(ref _appReads);

    public int IconReads => Volatile.Read(ref _iconReads);

    public Task<IReadOnlyList<AppInfo>> GetAppsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _appReads);
        return Task.FromResult<IReadOnlyList<AppInfo>>([.. Apps]);
    }

    public LaunchResult Launch(LaunchRequest request)
    {
        lock (_lock)
        {
            _launches.Add(request);
        }

        if (request.Verb is { } verb && MissingVerbs.Contains(verb))
        {
            return new LaunchResult(false, "no such verb", NoSuchVerb: true);
        }

        return request.Verb == "runas" && DeclineAdmin ? new LaunchResult(false, "cancelled", Cancelled: true) : LaunchResult.Ok;
    }

    public bool FileExists(string path) => Files.Contains(path);

    public bool FolderExists(string path) => Folders.Contains(path);

    public ShortcutInfo? ReadShortcut(string path) => Shortcuts.TryGetValue(path, out var s) ? s : null;

    public string? ReadUrlShortcut(string path) => UrlShortcuts.TryGetValue(path, out var url) ? url : null;

    public byte[]? GetIconPng(string parsingName, int size)
    {
        Interlocked.Increment(ref _iconReads);
        return [0x89, 0x50, 0x4E, 0x47, (byte)size];
    }
}

/// <summary>Another plugin that uses quick access over the bus.</summary>
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

/// <summary>The quick access plugin inside the real core (registry, guard, bus) with a fake shell.</summary>
internal sealed class QuickEnv : IAsyncDisposable
{
    public QuickEnv()
    {
        Guard = new ModuleGuard(Log);
        Bus = new ModuleBus(Guard, new GrantListPermissionPolicy(() => Grants), Log);
        DataDirectory = Path.Combine(Path.GetTempPath(), "uebar-qa-tests", Guid.NewGuid().ToString("N"));
        Registry = new ModuleRegistry(Guard, Bus, Log, State, DataDirectory);
        Module = new QuickAccessModule(Shell, FakeShell.Profile, Time);
        Registry.Register(Module);
    }

    public ListLogger Log { get; } = new();

    public FakeShell Shell { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero));

    public List<BusGrant> Grants { get; } = [];

    public ModuleGuard Guard { get; }

    public ModuleBus Bus { get; }

    public InMemoryModuleStateStore State { get; } = new();

    public ModuleRegistry Registry { get; }

    public QuickAccessModule Module { get; }

    public string DataDirectory { get; }

    public async Task<QuickEnv> StartAsync()
    {
        Assert.True(await Registry.StartModuleAsync(QuickAccessModule.ModuleId));
        return this;
    }

    /// <summary>Sets the entries list like the settings page does (and applies it live).</summary>
    public async Task EntriesAsync(params Dictionary<string, string>[] entries)
    {
        State.SetValue(QuickAccessModule.ModuleId, QuickSettings.EntriesKey, ModuleSettingValues.FormatList(entries));
        Assert.True(await Registry.ApplyChangedSettingsAsync(QuickAccessModule.ModuleId));
    }

    public static Dictionary<string, string> Entry(string id, string name, string kind, string? file = null, string? folder = null, string? text = null, bool admin = false)
    {
        var entry = new Dictionary<string, string> { ["id"] = id, ["name"] = name, ["kind"] = kind, ["admin"] = admin ? "true" : "false" };
        if (file is not null)
        {
            entry["file"] = file;
        }

        if (folder is not null)
        {
            entry["folder"] = folder;
        }

        if (text is not null)
        {
            entry["text"] = text;
        }

        return entry;
    }

    public static TileConfig Item(string target, TileSize? size = null) => new()
    {
        Id = "q-" + Guid.NewGuid().ToString("N")[..6],
        Module = QuickAccessModule.ModuleId,
        Type = ItemTileType.TypeId,
        Size = size ?? TileSize.Small,
        Entities = new Dictionary<string, string> { ["main"] = target },
    };

    public Task<TileModel> TileAsync(TileConfig config) => Registry.CreateTileAsync(config);

    public Task TapAsync(TileModel tile) => Registry.InteractAsync(tile, new TileInteraction("open", InteractionKind.Tap));

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
    }
}
