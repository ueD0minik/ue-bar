// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Modules;

namespace UeBar.Core.Tests;

/// <summary>Remote store to play with: available/offline, content, change notification.</summary>
internal sealed class FakeRemoteStore : ILayoutStore
{
    public bool Available { get; set; } = true;

    public LayoutDocument? Document { get; set; }

    public int Saves { get; private set; }

    public string StoreId => "fake";

    public string StoreDisplayName => "Fake-Server";

    public event EventHandler? Changed;

    public Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken) =>
        Available ? Task.FromResult(Document is null ? null : LayoutLayers.Clone(Document)) : throw new LayoutStoreUnavailableException("offline");

    public Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            throw new LayoutStoreUnavailableException("offline");
        }

        Saves++;
        Document = LayoutLayers.Clone(layout);
        return Task.CompletedTask;
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeRemoteSource(ILayoutStore? store) : IRemoteLayoutSource
{
    public ILayoutStore? Current { get; set; } = store;

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public class SyncedLayoutStoreTests
{
    private sealed class Env : IDisposable
    {
        public Env(FakeRemoteStore? remote = null)
        {
            Remote = remote ?? new FakeRemoteStore();
            Source = new FakeRemoteSource(Remote);
            Cache = new LocalFileLayoutStore(Path.Combine(Dir.Path, "layout.json"), Backups);
            Store = new SyncedLayoutStore(Cache, Source, SyncFile, Backups);
        }

        public TempDirectory Dir { get; } = new();

        public string Backups => Path.Combine(Dir.Path, "backups");

        public string SyncFile => Path.Combine(Dir.Path, "layout.sync.json");

        public FakeRemoteStore Remote { get; }

        public FakeRemoteSource Source { get; }

        public LocalFileLayoutStore Cache { get; }

        public SyncedLayoutStore Store { get; }

        public SyncedLayoutStore Restart() => new(Cache, Source, SyncFile, Backups);

        public void Dispose()
        {
            Store.Dispose();
            Cache.Dispose();
            Dir.Dispose();
        }
    }

    private static LayoutDocument Layout(string name, DateTimeOffset? at = null) => new()
    {
        Tabs = [new LayoutTab { Id = "t", Name = name, Tiles = [new TileConfig { Id = "k", Module = "m", Type = "m.x", Entities = new() { ["main"] = "a" } }] }],
        UpdatedAt = at ?? DateTimeOffset.Now,
        UpdatedBy = "PC",
    };

    [Fact]
    public async Task Saves_locally_first_then_uploads()
    {
        using var env = new Env();

        await env.Store.SaveAsync(Layout("A"), CancellationToken.None);
        await env.Store.LastSync;

        Assert.Equal("A", (await env.Cache.LoadAsync(CancellationToken.None))!.Tabs[0].Name);
        Assert.Equal("A", env.Remote.Document!.Tabs[0].Name);
        Assert.False(env.Store.IsPending);
        Assert.Equal(LayoutSyncOutcome.Uploaded, env.Store.Status.LastOutcome);
    }

    [Fact]
    public async Task Offline_save_is_pending_survives_restart_and_uploads_on_reconnect()
    {
        using var env = new Env();
        env.Remote.Available = false;

        await env.Store.SaveAsync(Layout("Offline"), CancellationToken.None);
        await env.Store.LastSync;
        Assert.True(env.Store.IsPending);
        Assert.Null(env.Remote.Document);

        using var restarted = env.Restart(); // pending is stored in layout.sync.json
        Assert.True(restarted.IsPending);

        env.Remote.Available = true;
        env.Source.RaiseChanged(); // reconnected
        await restarted.LastSync;

        Assert.Equal("Offline", env.Remote.Document!.Tabs[0].Name);
        Assert.False(restarted.IsPending);
    }

    [Fact]
    public async Task Remote_change_replaces_cache_with_backup_and_reports_it()
    {
        using var env = new Env();
        await env.Store.SaveAsync(Layout("Hier"), CancellationToken.None);
        await env.Store.LastSync;
        var reloads = 0;
        env.Store.Changed += (_, _) => reloads++;

        env.Remote.Document = Layout("Anderes Gerät", DateTimeOffset.Now.AddMinutes(1));
        Assert.Equal(LayoutSyncOutcome.Downloaded, await env.Store.SyncAsync());

        Assert.Equal(1, reloads);
        Assert.Equal("Anderes Gerät", (await env.Cache.LoadAsync(CancellationToken.None))!.Tabs[0].Name);
        Assert.Contains("Hier", await File.ReadAllTextAsync(Directory.GetFiles(env.Backups).Single()));
    }

    [Fact]
    public async Task Both_changed_newer_wins_and_the_other_is_backed_up()
    {
        // local newer
        using (var env = new Env())
        {
            await env.Store.SaveAsync(Layout("Basis"), CancellationToken.None);
            await env.Store.LastSync;
            env.Remote.Document = Layout("Server", DateTimeOffset.Now.AddMinutes(-5));
            env.Remote.Available = false;
            await env.Store.SaveAsync(Layout("Lokal", DateTimeOffset.Now), CancellationToken.None);
            await env.Store.LastSync;
            env.Remote.Available = true;

            Assert.Equal(LayoutSyncOutcome.Uploaded, await env.Store.SyncAsync());
            Assert.Equal("Lokal", env.Remote.Document!.Tabs[0].Name);
            Assert.Contains("Server", await File.ReadAllTextAsync(Directory.GetFiles(env.Backups, "*-fake.json").Single()));
        }

        // remote newer
        using (var env = new Env())
        {
            await env.Store.SaveAsync(Layout("Basis"), CancellationToken.None);
            await env.Store.LastSync;
            env.Remote.Available = false;
            await env.Store.SaveAsync(Layout("Lokal", DateTimeOffset.Now.AddMinutes(-5)), CancellationToken.None);
            await env.Store.LastSync;
            env.Remote.Document = Layout("Server", DateTimeOffset.Now);
            env.Remote.Available = true;

            Assert.Equal(LayoutSyncOutcome.Downloaded, await env.Store.SyncAsync());
            Assert.Equal("Server", (await env.Cache.LoadAsync(CancellationToken.None))!.Tabs[0].Name);
            Assert.Contains(Directory.GetFiles(env.Backups), f => File.ReadAllText(f).Contains("Lokal", StringComparison.Ordinal));
            Assert.False(env.Store.IsPending);
        }
    }

    [Fact]
    public async Task Only_local_pending_change_is_uploaded_even_if_older_stamp()
    {
        // Remote unchanged since the last reconcile → no conflict, the local change goes up.
        using var env = new Env();
        await env.Store.SaveAsync(Layout("Basis", DateTimeOffset.Now), CancellationToken.None);
        await env.Store.LastSync;
        env.Remote.Available = false;
        await env.Store.SaveAsync(Layout("Neu", DateTimeOffset.Now.AddHours(-1)), CancellationToken.None); // clock wrong
        await env.Store.LastSync;
        env.Remote.Available = true;

        Assert.Equal(LayoutSyncOutcome.Uploaded, await env.Store.SyncAsync());
        Assert.Equal("Neu", env.Remote.Document!.Tabs[0].Name);
        Assert.Empty(Directory.GetFiles(env.Backups, "*-fake.json"));
    }

    [Fact]
    public async Task Same_content_with_other_stamp_counts_as_in_sync()
    {
        using var env = new Env();
        await env.Cache.SaveAsync(Layout("Gleich", DateTimeOffset.Now.AddDays(-1)), CancellationToken.None);
        env.Remote.Document = Layout("Gleich", DateTimeOffset.Now);

        Assert.Equal(LayoutSyncOutcome.InSync, await env.Store.SyncAsync());
        Assert.Equal(0, env.Remote.Saves);
        Assert.False(Directory.Exists(env.Backups) && Directory.GetFiles(env.Backups).Length > 0);
    }

    [Fact]
    public async Task Migration_uploads_local_layout_when_remote_has_none()
    {
        using var env = new Env();
        await env.Cache.SaveAsync(Layout("Bestand"), CancellationToken.None);

        Assert.Equal(LayoutSyncOutcome.Uploaded, await env.Store.SyncAsync());
        Assert.Equal("Bestand", env.Remote.Document!.Tabs[0].Name);
    }

    [Fact]
    public async Task First_sync_with_both_existing_decides_by_updated_at()
    {
        using var env = new Env();
        await env.Cache.SaveAsync(Layout("Lokal", DateTimeOffset.Now.AddDays(-2)), CancellationToken.None);
        env.Remote.Document = Layout("Server", DateTimeOffset.Now.AddDays(-1));

        Assert.Equal(LayoutSyncOutcome.Downloaded, await env.Store.SyncAsync());
        Assert.Equal("Server", (await env.Cache.LoadAsync(CancellationToken.None))!.Tabs[0].Name);
        Assert.Contains("Lokal", await File.ReadAllTextAsync(Directory.GetFiles(env.Backups).Single()));
    }

    [Fact]
    public async Task Without_cache_loads_from_remote_and_without_both_returns_null()
    {
        using var env = new Env();
        Assert.Null(await env.Store.LoadAsync(CancellationToken.None));

        env.Remote.Document = Layout("Von HA");
        var loaded = await env.Store.LoadAsync(CancellationToken.None);

        Assert.Equal("Von HA", loaded!.Tabs[0].Name);
        Assert.True(File.Exists(env.Cache.FilePath)); // stored locally
        Assert.False(env.Store.IsPending);
    }

    [Fact]
    public async Task Without_remote_everything_stays_local_and_pending()
    {
        using var env = new Env();
        env.Source.Current = null;

        await env.Store.SaveAsync(Layout("Nur lokal"), CancellationToken.None);
        await env.Store.LastSync;

        Assert.Equal(LayoutSyncOutcome.LocalOnly, env.Store.Status.LastOutcome);
        Assert.True(env.Store.IsPending); // goes up as soon as there is a store
        Assert.Equal("Lokale Datei (kein Sync)", env.Store.StoreDisplayName);
    }

    [Fact]
    public async Task Hand_edited_file_is_pending_reported_and_uploaded()
    {
        using var env = new Env();
        await env.Store.SaveAsync(Layout("Vorher"), CancellationToken.None);
        await env.Store.LastSync;
        var reloads = 0;
        env.Store.Changed += (_, _) => Interlocked.Increment(ref reloads);

        await File.WriteAllTextAsync(env.Cache.FilePath, UeBarJson.Serialize(Layout("Von Hand", DateTimeOffset.Now.AddDays(-9))));

        Assert.True(await Wait.UntilAsync(() => env.Remote.Document?.Tabs[0].Name == "Von Hand"));
        Assert.True(Volatile.Read(ref reloads) >= 1);
    }
}

public class LayoutSetupAndModuleStoreTests
{
    private sealed class OptionsModule(string id) : IModule, ILayoutSuggestionOptions
    {
        public string Id { get; } = id;

        public string DisplayName => "Optionen";

        public string Icon => "Home";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public bool Ready { get; set; } = true;

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public LayoutSuggestionOptions? GetSuggestionOptions() => Ready
            ? new LayoutSuggestionOptions("Welche?", [new LayoutSuggestionChoice("a", "A"), new LayoutSuggestionChoice("b", "B", Selected: true)])
            : null;

        public LayoutDocument? CreateSuggestedLayout(IReadOnlyCollection<string> selectedChoiceIds) => new()
        {
            Tabs = [.. selectedChoiceIds.Select(c => new LayoutTab { Id = c, Name = c.ToUpperInvariant(), Tiles = [new TileConfig { Id = "k1", Module = Id, Type = "x" }] })],
        };
    }

    private sealed class SimpleModule(string id) : IModule, ILayoutSuggestionProvider
    {
        public string Id { get; } = id;

        public string DisplayName => "Einfach";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public LayoutDocument? GetSuggestedLayout() => new()
        {
            Tabs = [new LayoutTab { Id = "test", Name = "Test", Tiles = [new TileConfig { Id = "k1", Module = Id, Type = "y" }] }],
        };
    }

    private sealed class StoreModule(string id, ILayoutStore store) : IModule, ILayoutStoreProvider
    {
        public string Id { get; } = id;

        public string DisplayName => "Speicher";

        public string Icon => "Puzzle";

        public ModuleStatus Status => ModuleStatus.Connected;

        public IReadOnlyList<ITileType> TileTypes => [];

        public bool Offer { get; set; } = true;

        public IReadOnlyList<ILayoutStore> LayoutStores => Offer ? [store] : [];

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(IModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingStore : ILayoutStore
    {
        public string StoreId => "kaputt";

        public string StoreDisplayName => "Kaputt";

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Bug in the module");

        public Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken) => throw new InvalidOperationException("Bug in the module");
    }

    [Fact]
    public async Task Setup_lists_module_choices_and_builds_merged_layout_with_unique_ids()
    {
        await using var env = new TestEnv();
        var options = new OptionsModule("opt");
        env.Registry.Register(options);
        env.Registry.Register(new SimpleModule("simple"));
        await env.Registry.StartAllAsync();

        var sections = LayoutSetup.GetSections(env.Registry);

        Assert.Equal(["opt", "simple"], sections.Select(s => s.ModuleId));
        Assert.Equal("Welche?", sections[0].Question);
        Assert.Equal([LayoutSetup.WholeSuggestion], sections[1].Choices.Select(c => c.Id));
        var layout = LayoutSetup.Build(env.Registry, new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["opt"] = ["a", "b"],
            ["simple"] = [LayoutSetup.WholeSuggestion],
        });
        Assert.Equal(["A", "B", "Test"], layout.Tabs.Select(t => t.Name));
        Assert.Equal(3, layout.AllTiles.Select(t => t.Id).Distinct().Count()); // k1, k1-2, k1-3

        Assert.Empty(LayoutSetup.Build(env.Registry, new Dictionary<string, IReadOnlyCollection<string>>()).Tabs); // "Start empty"

        options.Ready = false;
        Assert.Empty(LayoutSetup.GetSections(env.Registry)[0].Choices); // not ready yet: section with a hint
    }

    [Fact]
    public async Task Module_store_is_found_guarded_and_a_buggy_store_does_not_fault_the_module()
    {
        await using var env = new TestEnv();
        var remote = new FakeRemoteStore();
        var module = new StoreModule("sync", remote);
        env.Registry.Register(new TestModule("ohne"));
        env.Registry.Register(module);
        await env.Registry.StartAllAsync();
        using var source = new ModuleLayoutStoreSource(env.Registry, env.Log);
        var changes = 0;
        source.Changed += (_, _) => changes++;

        Assert.NotNull(source.Current);
        Assert.Equal("Fake-Server", source.Current!.StoreDisplayName);
        remote.RaiseChanged();
        Assert.Equal(1, changes); // the module's notification arrives

        remote.Available = false;
        await Assert.ThrowsAsync<LayoutStoreUnavailableException>(() => source.Current.LoadAsync(CancellationToken.None));

        module.Offer = false;
        source.Refresh();
        Assert.Null(source.Current);

        await using var env2 = new TestEnv();
        env2.Registry.Register(new StoreModule("bug", new ThrowingStore()));
        await env2.Registry.StartAllAsync();
        using var source2 = new ModuleLayoutStoreSource(env2.Registry, env2.Log);
        await Assert.ThrowsAsync<LayoutStoreUnavailableException>(() => source2.Current!.LoadAsync(CancellationToken.None));
        Assert.Null(env2.Registry.Find("bug")!.LastError); // the module keeps running
    }

    [Fact]
    public async Task Layout_store_preference_automatic_local_only_or_a_specific_module()
    {
        // M7: storage location in the settings (automatic / local only / module X).
        await using var env = new TestEnv();
        env.Registry.Register(new StoreModule("a", new FakeRemoteStore()));
        env.Registry.Register(new StoreModule("b", new FakeRemoteStore()));
        await env.Registry.StartAllAsync();
        using var source = new ModuleLayoutStoreSource(env.Registry, env.Log);
        var changes = 0;
        source.Changed += (_, _) => changes++;

        Assert.Equal(["a", "b"], source.Providers.Select(p => p.Id));
        Assert.Equal("a", ((GuardedLayoutStore)source.Current!).ModuleId); // automatic: the first one

        source.Preference = "b";
        Assert.Equal("b", ((GuardedLayoutStore)source.Current!).ModuleId);
        source.Preference = ModuleLayoutStoreSource.LocalOnly;
        Assert.Null(source.Current);
        source.Preference = null;
        Assert.Equal("a", ((GuardedLayoutStore)source.Current!).ModuleId);
        Assert.Equal(3, changes); // every switch triggers a reconcile
    }
}
