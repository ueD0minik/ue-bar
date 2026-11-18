// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using UeBar.Core.Bus;
using UeBar.Core.Localization;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Settings;
using UeBar.Sdk;

// One shared UI language and clock per test run.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UeBar.Modules.Timer.Tests;

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
                // a tile changed while the condition looked at it – look again
            }

            await Task.Delay(10);
        }

        return condition();
    }
}

/// <summary>Stands in for another plugin that publishes an action (e.g. a home automation plugin).</summary>
internal sealed class ProviderPlugin : IModule
{
    private int _calls;

    public string Id => "home";

    public string DisplayName => "Home";

    public string Icon => "Home";

    public ModuleStatus Status => ModuleStatus.Connected;

    public IReadOnlyList<ITileType> TileTypes => [];

    public int Calls => Volatile.Read(ref _calls);

    public JsonNode? LastArguments { get; private set; }

    public event EventHandler<ModuleStatus>? StatusChanged { add { } remove { } }

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Bus.PublishAction("home.run", (call, _) =>
        {
            LastArguments = call.Arguments?.DeepClone();
            Interlocked.Increment(ref _calls);
            return Task.FromResult<JsonNode?>(null);
        }, "Run something at home: { entity }");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Another plugin that uses the timer over the bus.</summary>
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

/// <summary>The timer plugin inside the real core (registry, guard, bus) with a fake clock.</summary>
internal sealed class TimerEnv : IAsyncDisposable
{
    private readonly List<UeNotification> _notifications = [];

    public TimerEnv(DateTimeOffset? start = null)
    {
        Time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero));
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Guard = new ModuleGuard(Log);
        Policy = new GrantListPermissionPolicy(() => Grants);
        Bus = new ModuleBus(Guard, Policy, Log);
        DataDirectory = Path.Combine(Path.GetTempPath(), "uebar-timer-tests", Guid.NewGuid().ToString("N"));
        Registry = new ModuleRegistry(Guard, Bus, Log, State, DataDirectory);
        Module = new TimerModule(Time);
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

    public GrantListPermissionPolicy Policy { get; }

    public ModuleBus Bus { get; }

    public InMemoryModuleStateStore State { get; } = new();

    public ModuleRegistry Registry { get; }

    public TimerModule Module { get; }

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

    public async Task<TimerEnv> StartAsync()
    {
        Assert.True(await Registry.StartModuleAsync(TimerModule.ModuleId));
        return this;
    }

    public void Grant(string consumer, string provider, BusAccess access) =>
        Grants.Add(new BusGrant { Consumer = consumer, Provider = provider, Access = access });

    public static TileConfig Config(string type, string entity) => new()
    {
        Id = "t-" + Guid.NewGuid().ToString("N")[..6],
        Module = TimerModule.ModuleId,
        Type = type,
        Size = TileSize.Large,
        Entities = new Dictionary<string, string> { ["main"] = entity },
    };

    public Task<TileModel> TileAsync(string type, string entity) => Registry.CreateTileAsync(Config(type, entity));

    public Task InteractAsync(TileModel tile, string element, InteractionKind kind, double? number = null, string? text = null) =>
        Registry.InteractAsync(tile, new TileInteraction(element, kind, number, text));

    /// <summary>Moves the fake clock in steps (the worker sees every step) and lets it catch up.</summary>
    public async Task AdvanceAsync(TimeSpan span, TimeSpan? step = null)
    {
        var each = step ?? span;
        for (var done = TimeSpan.Zero; done < span; done += each)
        {
            Time.Advance(each < span - done ? each : span - done);
            await Task.Delay(15);
        }

        await Task.Delay(30);
    }

    public async Task SettingsAsync(params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
        {
            State.SetValue(TimerModule.ModuleId, key, value);
        }

        await Module.ApplySettingsAsync(State.GetModuleSettings(TimerModule.ModuleId), default);
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

internal static class TileRead
{
    /// <summary>
    /// <see cref="TileModel.FindElement{T}"/> for tests: the plugin changes the elements on its own
    /// thread; like the app, try again when the list was caught half way through a change.
    /// </summary>
    public static T? Find<T>(this TileModel tile, string id)
        where T : TileElement
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return tile.FindElement<T>(id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IndexOutOfRangeException or ArgumentException && attempt < 100)
            {
                Thread.Sleep(1);
            }
        }
    }
}
