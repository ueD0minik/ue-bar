// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.TestPlugin.Dependency;

namespace UeBar.TestPlugin;

/// <summary>
/// Base for the fixture modules: one tile type, runs in memory. The ID is the name of the folder
/// the assembly was loaded from, so one assembly can play any plugin.
/// </summary>
public abstract class FixtureModule : IModule
{
    protected FixtureModule()
    {
        TileTypes = [new FixtureTileType(FolderId)];
    }

    internal static string FolderId { get; } =
        Path.GetFileName(Path.GetDirectoryName(typeof(FixtureModule).Assembly.Location)!)!;

    public virtual string Id => FolderId;

    public virtual string DisplayName => "Fixture";

    public string Icon => "Puzzle";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public IReadOnlyList<ITileType> TileTypes { get; }

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        Status = ModuleStatus.Connected;
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Status = ModuleStatus.Stopped;
        return Task.CompletedTask;
    }
}

public sealed class FixtureTileType(string moduleId) : ITileType
{
    public string Id => moduleId + ".tile";

    public string DisplayName => "Fixture tile";

    public string Icon => "Puzzle";

    public IReadOnlyList<TileSize> AllowedSizes { get; } = [TileSize.Small];

    public TileSize DefaultSize => TileSize.Small;

    public IReadOnlyList<TileSlot> Slots { get; } = [];

    public TileModel CreateTile(TileConfig config) => new() { Title = "Fixture", StateText = "ok" };
}

/// <summary>Loads and runs.</summary>
public sealed class GoodModule : FixtureModule;

/// <summary>Constructor throws.</summary>
public sealed class ThrowingModule : FixtureModule
{
    public ThrowingModule()
    {
        throw new InvalidOperationException("boom");
    }
}

/// <summary>Constructor hangs for a while.</summary>
public sealed class SlowModule : FixtureModule
{
    public SlowModule()
    {
        Thread.Sleep(TimeSpan.FromSeconds(5));
    }
}

/// <summary>Reports another ID than plugin.json.</summary>
public sealed class OtherIdModule : FixtureModule
{
    public override string Id => "something-else";
}

/// <summary>Uses its private dependency in the constructor; the display name tells where that came from.</summary>
public sealed class DependencyModule : FixtureModule
{
    private readonly string _name;

    public DependencyModule()
    {
        _name = $"{Helper.Greeting} ({Helper.LoadContextName})";
    }

    public override string DisplayName => _name;
}
