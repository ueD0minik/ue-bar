// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;

namespace UeBar.Modules.Dummy;

/// <summary>
/// Test module with fake tiles whose values live in memory only. Proves that the core runs
/// without Home Assistant, and serves as a test object for the bar (M2) and the fault protection
/// (tile "Trigger crash").
/// </summary>
public sealed class DummyModule : IModule, IEntitySource, ILayoutSuggestionProvider, IStatusProvider, IDeveloperModule
{
    public const string ModuleId = "dummy";

    private static StatusValueInfo[] StatusInfos =>
    [
        new("dummy.thermostat", DummyText.T["Dummy_Thermostat"], "Thermometer"),
        new("dummy.player", DummyText.T["Dummy_Music"], "MusicNote"),
    ];

    private readonly Dictionary<string, StatusValue> _statusValues = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DummyDevice> _devices;
    private readonly Dictionary<string, List<(WeakReference<TileModel> Tile, DummyTileType Type)>> _tracked = [];
    private readonly Lock _lock = new();
    private readonly DummyTileType[] _tileTypes;

    public DummyModule()
    {
        _devices = new Dictionary<string, DummyDevice>(StringComparer.OrdinalIgnoreCase)
        {
            ["dummy.fan"] = new("dummy.fan", "Dummy_Fan", "Power", DummyCategory.Switch),
            ["dummy.desk"] = new("dummy.desk", "Dummy_DeskLamp", "Lightbulb", DummyCategory.Dimmer) { On = true, Level = 60 },
            ["dummy.thermostat"] = new("dummy.thermostat", "Dummy_Thermostat", "Thermometer", DummyCategory.Thermostat),
            ["dummy.player"] = new("dummy.player", "Dummy_Music", "MusicNote", DummyCategory.Media),
            ["dummy.crash"] = new("dummy.crash", "Dummy_Crash", "Warning", DummyCategory.Crash),
        };

        _tileTypes =
        [
            new SwitchTileType(this),
            new DimmerTileType(this),
            new ThermostatTileType(this),
            new MediaTileType(this),
            new CrashTileType(this),
        ];
    }

    public string Id => ModuleId;

    public string DisplayName => DummyText.T["Dummy_ModuleName"];

    public string Icon => "Robot";

    public ModuleStatus Status { get; private set; } = ModuleStatus.Stopped;

    public IReadOnlyList<ITileType> TileTypes => _tileTypes;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler<EntityInfo>? EntityChanged;

    public Task StartAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Log.Info("Test module started (values in memory only)");
        if (context is IUiLanguageInfo language)
        {
            DummyText.Culture = language.UiCulture;
            language.UiCultureChanged += OnUiCultureChanged;
        }

        SetStatus(new ModuleStatus(ModuleState.Connected, DummyText.T["Dummy_Running"]));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SetStatus(ModuleStatus.Stopped);
        return Task.CompletedTask;
    }

    /// <summary>Language switched (R1): refresh status, status values and all tiles.</summary>
    private void OnUiCultureChanged(object? sender, CultureInfo culture)
    {
        DummyText.Culture = culture;
        if (Status.State == ModuleState.Connected)
        {
            SetStatus(new ModuleStatus(ModuleState.Connected, DummyText.T["Dummy_Running"]));
        }

        foreach (var id in _devices.Keys.ToArray())
        {
            Update(id, _ => { });
        }
    }

    // ------------------------------------------------------------------ IEntitySource

    public Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        IReadOnlyList<EntityInfo> result;
        lock (_lock)
        {
            result =
            [
                .. _devices.Values
                    .Where(d => string.IsNullOrWhiteSpace(query)
                        || d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || d.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(maxResults)
                    .Select(ToInfo),
            ];
        }

        return Task.FromResult(result);
    }

    public Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        var device = GetDevice(entityId);
        if (device.Category == DummyCategory.Crash)
        {
            throw new InvalidOperationException("Crash triggered on request (test of the fault protection).");
        }

        Update(entityId, d =>
        {
            if (d.Category == DummyCategory.Media)
            {
                d.Playing = !d.Playing;
            }
            else
            {
                d.On = !d.On;
            }
        });
        return Task.CompletedTask;
    }

    public TileConfig? SuggestTile(EntityInfo entity)
    {
        var device = TryGetDevice(entity.Id);
        if (device is null)
        {
            return null;
        }

        var type = _tileTypes.First(t => t.Category == device.Category);
        return new TileConfig
        {
            Id = $"{ModuleId}-{Guid.NewGuid():N}"[..14],
            Module = ModuleId,
            Type = type.Id,
            Size = type.DefaultSize,
            Entities = new Dictionary<string, string> { ["main"] = device.Id },
        };
    }

    // ------------------------------------------------------------------ ILayoutSuggestionProvider

    public LayoutDocument GetSuggestedLayout() => new()
    {
        Tabs =
        [
            new LayoutTab
            {
                Id = "test",
                Name = "Test",
                Icon = "TestBeaker",
                Tiles =
                [
                    Tile("d1", "dummy.switch", TileSize.Small, "dummy.fan"),
                    Tile("d2", "dummy.dimmer", TileSize.Wide, "dummy.desk"),
                    Tile("d3", "dummy.thermostat", TileSize.Large, "dummy.thermostat"),
                    Tile("d4", "dummy.media", TileSize.Large, "dummy.player"),
                    Tile("d5", "dummy.crash", TileSize.Small, "dummy.crash"),
                ],
            },
            new LayoutTab
            {
                Id = "test2",
                Name = DummyText.T["Dummy_SecondTab"],
                Icon = "Lightning",
                Tiles =
                [
                    Tile("e1", "dummy.dimmer", TileSize.Small, "dummy.desk"),
                    Tile("e2", "dummy.switch", TileSize.Small, "dummy.fan"),
                ],
            },
        ],
        Status =
        [
            new StatusItemConfig { Module = ModuleId, Entity = "dummy.thermostat" },
            new StatusItemConfig { Module = ModuleId, Entity = "dummy.player" },
        ],
    };

    // ------------------------------------------------------------------ IStatusProvider

    public IReadOnlyList<StatusValueInfo> AvailableStatusValues => StatusInfos;

    public StatusValue? GetStatusValue(string key)
    {
        var info = StatusInfos.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        var device = TryGetDevice(key);
        if (info is null || device is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (!_statusValues.TryGetValue(key, out var value))
            {
                value = new StatusValue { Key = info.Key, Icon = info.Icon };
                _statusValues[key] = value;
            }

            ApplyStatus(value, device);
            return value;
        }
    }

    private static void ApplyStatus(StatusValue value, DummyDevice device)
    {
        value.Text = device.Category switch
        {
            DummyCategory.Thermostat => device.On
                ? DummyText.T.Format("Dummy_TargetTemperature", device.Target)
                : DummyText.T["Dummy_ClimateOff"],
            DummyCategory.Media => device.Playing ? DummyDevice.Tracks[device.TrackIndex].Title : DummyText.T["Dummy_Paused"],
            _ => device.StateText,
        };
        value.Tooltip = $"{device.Name}: {device.StateText}";
    }

    // ------------------------------------------------------------------ internal for tile types

    internal DummyDevice GetDevice(string? entityId) =>
        TryGetDevice(entityId) ?? throw new KeyNotFoundException($"Unknown test device '{entityId}'");

    internal DummyDevice? TryGetDevice(string? entityId)
    {
        if (entityId is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _devices.TryGetValue(entityId, out var d) ? d : null;
        }
    }

    internal void Track(string entityId, TileModel tile, DummyTileType type)
    {
        lock (_lock)
        {
            if (!_tracked.TryGetValue(entityId, out var list))
            {
                list = [];
                _tracked[entityId] = list;
            }

            list.RemoveAll(x => !x.Tile.TryGetTarget(out _));
            list.Add((new WeakReference<TileModel>(tile), type));
        }
    }

    /// <summary>Change the state and update all tiles of this device live.</summary>
    internal void Update(string entityId, Action<DummyDevice> change)
    {
        DummyDevice device;
        List<(TileModel Tile, DummyTileType Type)> tiles = [];
        lock (_lock)
        {
            device = _devices[entityId];
            change(device);
            if (_statusValues.TryGetValue(entityId, out var status))
            {
                ApplyStatus(status, device);
            }

            if (_tracked.TryGetValue(entityId, out var list))
            {
                foreach (var (weak, type) in list)
                {
                    if (weak.TryGetTarget(out var tile))
                    {
                        tiles.Add((tile, type));
                    }
                }
            }
        }

        foreach (var (tile, type) in tiles)
        {
            type.Refresh(tile, device);
        }

        EntityChanged?.Invoke(this, ToInfo(device));
    }

    private static EntityInfo ToInfo(DummyDevice d) =>
        new(d.Id, d.Name, d.Icon, DummyText.T["Dummy_Room"], d.StateText, d.Category.ToString());

    private static TileConfig Tile(string id, string type, TileSize size, string entity) => new()
    {
        Id = id,
        Module = ModuleId,
        Type = type,
        Size = size,
        Entities = new Dictionary<string, string> { ["main"] = entity },
    };

    private void SetStatus(ModuleStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
