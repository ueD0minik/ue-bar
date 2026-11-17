// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;

namespace HelloPlugin;

/// <summary>
/// Sample plugin for ue Bar: one tile that counts how often it was tapped.
/// Shows the parts most plugins need:
/// <list type="bullet">
/// <item><see cref="IModule"/> – the module itself with one tile type (<see cref="CounterTileType"/>)</item>
/// <item><see cref="IModuleSettings"/> – a settings page ue Bar builds from two fields</item>
/// <item><see cref="IEntitySource"/> – the counter can be found when adding a tile and in the quick search</item>
/// <item><see cref="ILayoutSuggestionProvider"/> – a suggested tab for the first start</item>
/// <item>two languages (Resources/Strings*.resx) and a value that survives restarts (data directory)</item>
/// </list>
/// Rules (see docs/WRITING-A-PLUGIN.md): a public class with a constructor without parameters that
/// does nothing heavy; no <c>async void</c>, no own timers or threads – background work only through
/// <see cref="IModuleContext.RunBackground"/>; thread-safe, because ue Bar calls from the thread pool.
/// </summary>
public sealed class HelloModule : IModule, IModuleSettings, IEntitySource, ILayoutSuggestionProvider
{
    /// <summary>Must be the "id" in plugin.json.</summary>
    public const string ModuleId = "hello";

    /// <summary>The one "thing" of this module. IDs of things and tile types start with the module ID.</summary>
    public const string CounterId = "hello.counter";

    private const string KeyStep = "step";
    private const string KeyLabel = "label";

    private readonly Lock _lock = new();
    private readonly List<WeakReference<TileModel>> _tiles = [];
    private readonly CounterTileType _tileType;
    private IModuleContext? _context;
    private int _count;
    private int _step = 1;
    private string _label = "";

    public HelloModule()
    {
        _tileType = new CounterTileType(this);
    }

    public string Id => ModuleId;

    public string DisplayName => HelloText.T["Hello_ModuleName"];

    /// <summary>Name of an icon from the Segoe Fluent Icons font.</summary>
    public string Icon => "Star";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public IReadOnlyList<ITileType> TileTypes => [_tileType];

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;

        // Language: ue Bar tells the module which one is set and when it changes.
        if (context is IUiLanguageInfo language)
        {
            HelloText.Culture = language.UiCulture;
            language.UiCultureChanged += OnLanguageChanged;
        }

        lock (_lock)
        {
            _count = ReadCount(context.DataDirectory);
        }

        context.Log.Info($"Hello plugin started, counter at {_count}");
        SetStatus(new ModuleStatus(ModuleState.Connected, HelloText.T["Hello_Running"]));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ IModuleSettings

    /// <summary>ue Bar builds the settings page from these fields and stores the values.</summary>
    public IReadOnlyList<SettingField> SettingFields =>
    [
        new(KeyStep, HelloText.T["Hello_Setting_Step"], SettingFieldKind.Number, "1", HelloText.T["Hello_Setting_Step_Hint"], Min: 1, Max: 10),
        new(KeyLabel, HelloText.T["Hello_Setting_Label"], SettingFieldKind.Text, null, HelloText.T["Hello_Setting_Label_Hint"]),
    ];

    /// <summary>Called before every start and whenever the user changes a value.</summary>
    public Task ApplySettingsAsync(ModuleSettingValues values, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _step = (int)Math.Clamp(values.GetNumber(KeyStep, 1), 1, 10);
            _label = values.GetString(KeyLabel)?.Trim() ?? "";
        }

        RefreshTiles();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ IEntitySource

    public event EventHandler<EntityInfo>? EntityChanged;

    public Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var info = CounterInfo();
        var matches = string.IsNullOrWhiteSpace(query)
            || info.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || info.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<EntityInfo>>(matches ? [info] : []);
    }

    /// <summary>Enter in the quick search: count one up.</summary>
    public Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        Change(count => count + _step);
        return Task.CompletedTask;
    }

    /// <summary>The tile ue Bar offers when the user adds the counter in edit mode.</summary>
    public TileConfig? SuggestTile(EntityInfo entity) => entity.Id == CounterId
        ? new TileConfig
        {
            Id = $"hello-{Guid.NewGuid():N}"[..14],
            Module = ModuleId,
            Type = CounterTileType.TypeId,
            Size = TileSize.Wide,
            Entities = new Dictionary<string, string> { ["main"] = CounterId },
        }
        : null;

    // ------------------------------------------------------------------ ILayoutSuggestionProvider

    public LayoutDocument GetSuggestedLayout() => new()
    {
        Tabs =
        [
            new LayoutTab
            {
                Id = "hello",
                Name = HelloText.T["Hello_Tab"],
                Icon = "Star",
                Tiles =
                [
                    new TileConfig
                    {
                        Id = "hello-counter",
                        Module = ModuleId,
                        Type = CounterTileType.TypeId,
                        Size = TileSize.Wide,
                        Entities = new Dictionary<string, string> { ["main"] = CounterId },
                    },
                ],
            },
        ],
    };

    // ------------------------------------------------------------------ the tile

    /// <summary>Builds the tile as data. ue Bar renders it and sends taps back to <see cref="HandleAsync"/>.</summary>
    internal TileModel CreateTile()
    {
        var tile = new TileModel
        {
            Icon = "Star",
            Elements =
            [
                new TapAction { Id = "tap" },
                new ValueDisplayElement { Id = "value" },
                new ButtonRowElement { Id = "buttons", Buttons = [new TileButton { Id = "reset", Icon = "Remove" }] },
            ],
            Handler = new DelegateInteractionHandler(HandleAsync),
        };
        Fill(tile);
        lock (_lock)
        {
            _tiles.RemoveAll(t => !t.TryGetTarget(out _));
            _tiles.Add(new WeakReference<TileModel>(tile));
        }

        return tile;
    }

    /// <summary>
    /// The user did something on the tile. Return quickly: show the result right away and do slow
    /// things (network …) through <see cref="IModuleContext.RunBackground"/>.
    /// </summary>
    private Task HandleAsync(TileModel tile, TileInteraction interaction, CancellationToken cancellationToken)
    {
        switch (interaction)
        {
            case { Kind: InteractionKind.Tap }:
                Change(count => count + _step);
                break;
            case { Kind: InteractionKind.Press, Text: "reset" }:
                Change(_ => 0);
                break;
        }

        return Task.CompletedTask;
    }

    private void Change(Func<int, int> change)
    {
        int count;
        lock (_lock)
        {
            _count = count = change(_count);
        }

        RefreshTiles();
        EntityChanged?.Invoke(this, CounterInfo());

        // Saving is "slow" work: off the caller's thread, attributed to this module if it fails.
        _context?.RunBackground("Save counter", _ => File.WriteAllTextAsync(CountFile(_context.DataDirectory), count.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
    }

    /// <summary>The tile models are observable: setting a property updates the tile on screen.</summary>
    private void RefreshTiles()
    {
        List<TileModel> tiles = [];
        lock (_lock)
        {
            foreach (var reference in _tiles)
            {
                if (reference.TryGetTarget(out var tile))
                {
                    tiles.Add(tile);
                }
            }
        }

        foreach (var tile in tiles)
        {
            Fill(tile);
        }
    }

    private void Fill(TileModel tile)
    {
        int count;
        string label;
        lock (_lock)
        {
            count = _count;
            label = _label;
        }

        tile.Title = label.Length > 0 ? label : HelloText.T["Hello_Counter"];
        tile.StateText = HelloText.T.Format("Hello_Count", count);
        tile.IsActive = count > 0;
        if (tile.FindElement<ValueDisplayElement>("value") is { } value)
        {
            value.Value = count.ToString(CultureInfo.CurrentCulture);
        }

        if (tile.FindElement<ButtonRowElement>("buttons")?.Buttons.FirstOrDefault() is { } reset)
        {
            reset.Label = HelloText.T["Hello_Reset"];
            reset.IsEnabled = count > 0;
        }
    }

    private EntityInfo CounterInfo()
    {
        int count;
        string label;
        lock (_lock)
        {
            count = _count;
            label = _label;
        }

        return new EntityInfo(CounterId, label.Length > 0 ? label : HelloText.T["Hello_Counter"], "Star", StateText: HelloText.T.Format("Hello_Count", count));
    }

    private void OnLanguageChanged(object? sender, CultureInfo culture)
    {
        HelloText.Culture = culture;
        if (Status.State == ModuleState.Connected)
        {
            SetStatus(new ModuleStatus(ModuleState.Connected, HelloText.T["Hello_Running"]));
        }

        RefreshTiles();
    }

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string CountFile(string dataDirectory) => Path.Combine(dataDirectory, "counter.txt");

    private static int ReadCount(string dataDirectory)
    {
        var file = CountFile(dataDirectory);
        return File.Exists(file) && int.TryParse(File.ReadAllText(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }
}

/// <summary>The tile type "hello.counter": what it is called, which sizes it allows, what it needs.</summary>
internal sealed class CounterTileType(HelloModule module) : ITileType
{
    /// <summary>Tile type IDs start with the module ID.</summary>
    public const string TypeId = "hello.counter";

    public string Id => TypeId;

    public string DisplayName => HelloText.T["Hello_TileType"];

    public string Icon => "Star";

    public IReadOnlyList<TileSize> AllowedSizes => TileSize.All;

    public TileSize DefaultSize => TileSize.Wide;

    /// <summary>One slot: the thing the tile shows.</summary>
    public IReadOnlyList<TileSlot> Slots => [new TileSlot("main", HelloText.T["Hello_Slot"])];

    /// <summary>Must be fast – no network calls here.</summary>
    public TileModel CreateTile(TileConfig config) => module.CreateTile();
}

/// <summary>The plugin's texts: Resources/Strings.resx (English) and Strings.de.resx (German).</summary>
internal static class HelloText
{
    /// <summary>Set from <see cref="IUiLanguageInfo"/>; null = the current UI culture.</summary>
    public static CultureInfo? Culture { get; set; }

    public static TextCatalog T { get; } = TextCatalog.For(typeof(HelloText).Assembly, "HelloPlugin", () => Culture);
}
