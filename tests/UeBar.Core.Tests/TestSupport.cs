// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Abstractions;
using UeBar.Core.Bus;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Core.Tiles;

namespace UeBar.Core.Tests;

/// <summary>Freely configurable module for tests.</summary>
internal sealed class TestModule(string id) : IModule
{
    private int _startCount;
    private int _stopCount;

    public string Id { get; } = id;

    public string DisplayName => $"Test {Id}";

    public string Icon => "Puzzle";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public List<ITileType> Types { get; } = [];

    public IReadOnlyList<ITileType> TileTypes => Types;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public Func<IModuleContext, CancellationToken, Task>? OnStart { get; set; }

    public IModuleContext? Context { get; private set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    public async Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        Context = context;
        if (OnStart is not null)
        {
            await OnStart(context, cancellationToken);
        }

        SetStatus(ModuleStatus.Connected);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    public void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class TestTileType(string id, Func<TileConfig, TileModel>? factory = null) : ITileType
{
    public string Id { get; } = id;

    public string DisplayName => Id;

    public string Icon => "Puzzle";

    public IReadOnlyList<TileSize> AllowedSizes => TileSize.All;

    public TileSize DefaultSize => TileSize.Small;

    public IReadOnlyList<TileSlot> Slots => [new TileSlot("main", "Gerät")];

    public TileModel CreateTile(TileConfig config) =>
        factory?.Invoke(config) ?? new TileModel { Title = "Test", Elements = [new TapAction { Id = "tap" }] };
}

/// <summary>Logger that collects entries.</summary>
internal sealed class ListLogger : IUeLogger
{
    private readonly Lock _lock = new();
    private readonly List<(LogLevel Level, string? Module, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string? Module, string Message)> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public void Log(LogLevel level, string? moduleId, string message, Exception? exception = null)
    {
        lock (_lock)
        {
            _entries.Add((level, moduleId, message));
        }
    }
}

/// <summary>Core setup like in the app, but in memory.</summary>
internal sealed class TestEnv : IAsyncDisposable
{
    public TestEnv(GuardOptions? options = null, TimeProvider? time = null, params BusGrant[] grants)
    {
        Log = new ListLogger();
        Guard = new ModuleGuard(Log, options, time);
        Policy = new GrantListPermissionPolicy(grants);
        Bus = new ModuleBus(Guard, Policy, Log);
        State = new InMemoryModuleStateStore();
        DataDirectory = Path.Combine(Path.GetTempPath(), "uebar-tests", Guid.NewGuid().ToString("N"));
        Registry = new ModuleRegistry(Guard, Bus, Log, State, DataDirectory);
    }

    public ListLogger Log { get; }

    public ModuleGuard Guard { get; }

    public GrantListPermissionPolicy Policy { get; }

    public ModuleBus Bus { get; }

    public InMemoryModuleStateStore State { get; }

    public ModuleRegistry Registry { get; }

    public string DataDirectory { get; }

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

internal static class Wait
{
    /// <summary>Waits (real time) until the condition is met.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
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

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uebar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
