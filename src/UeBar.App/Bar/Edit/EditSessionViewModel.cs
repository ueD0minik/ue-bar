// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.App.Localization;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Bar.Edit;

/// <summary>A choice in a list (icon, size, target tab …).</summary>
internal sealed class Option<T>(T value, string label, string? icon = null) : ObservableObject
{
    public T Value { get; } = value;

    public string Label { get; } = label;

    public string? Icon { get; } = icon;

    public bool IsCurrent
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>A value of the status line in edit mode (M6).</summary>
internal sealed class StatusChip(int index, string label, string? icon)
{
    public int Index { get; } = index;

    public string Label { get; } = label;

    public string? Icon { get; } = icon;
}

/// <summary>Search hit when adding (module + thing).</summary>
internal sealed record SearchHit(string ModuleId, string ModuleName, EntityInfo Entity)
{
    public string Detail => string.Join(" · ", new[] { Entity.Area, Entity.StateText, ModuleName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>A slot of the new tile with candidates.</summary>
internal sealed class SlotRow : ObservableObject
{
    public SlotRow(TileSlot slot)
    {
        Slot = slot;
        SelectCommand = new RelayCommand<Option<string?>>(o =>
        {
            if (o is not null)
            {
                SelectedId = o.Value;
            }
        });
    }

    public RelayCommand<Option<string?>> SelectCommand { get; }

    public TileSlot Slot { get; }

    public string Label => Slot.Required ? Slot.DisplayName : Slot.DisplayName + " (optional)";

    public ObservableCollection<Option<string?>> Candidates { get; } = [];

    public string? SelectedId
    {
        get;
        set
        {
            SetProperty(ref field, value);
            foreach (var candidate in Candidates)
            {
                candidate.IsCurrent = candidate.Value == value;
            }
        }
    }
}

/// <summary>
/// Edit mode (SPEC 7) for the UI: holds the <see cref="LayoutEditor"/> (core, WPF-free)
/// and the selection. Talks to modules only through core interfaces (IEntitySource, ITileType,
/// ITileSlotAssistant) – nothing module-specific. Lives on the UI thread. Texts are taken when the session starts.
/// </summary>
internal sealed class EditSessionViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> IconChoices =
    [
        "Home", "Lightbulb", "Lightning", "Tv", "MusicNote", "Thermometer", "Snow", "Fire", "Robot", "Moon",
        "Sun", "Star", "Heart", "Laptop", "Monitor", "Speaker", "Settings", "Calendar", "Bell", "Car", "Leaf",
        "Person", "Drop", "Puzzle",
    ];

    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly Dictionary<string, TileModel> _models = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _searchTimer;
    private bool _syncing;
    private int _searchGeneration;
    private TileConfig? _draft;

    public EditSessionViewModel(LayoutEditor editor, ModuleRegistry registry, IUeLogger log, string deviceName, IEnumerable<TileModel> existingModels, IEnumerable<TileConfig> existingConfigs)
    {
        Editor = editor;
        _registry = registry;
        _log = log;
        DeviceName = deviceName;

        // Reuse existing tile models (same configuration = same tile).
        var byId = existingModels.GroupBy(m => m.TileId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var config in existingConfigs)
        {
            if (byId.TryGetValue(config.Id, out var model))
            {
                _models[Key(config)] = model;
            }
        }

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchAsync();
        };

        ScopeOptions = [new(TabOrigin.Base, Loc.Get("Edit_ScopeAll")), new(TabOrigin.Device, Loc.F("Edit_ScopeDevice", deviceName))];
        VisibilityOptions = [new(TabVisibility.Always, Loc.Get("Edit_VisibleAlways")), new(TabVisibility.HomeOnly, Loc.Get("Edit_VisibleHome")), new(TabVisibility.AwayOnly, Loc.Get("Edit_VisibleAway"))];
        TabIcons = [.. IconChoices.Select(i => new Option<string?>(i, i, i))];
        TileIcons = [new Option<string?>(null, Loc.Get("Edit_IconDefault"), "Puzzle"), .. IconChoices.Select(i => new Option<string?>(i, i, i))];

        UndoCommand = new RelayCommand(Editor.Undo, () => Editor.CanUndo);
        DoneCommand = new RelayCommand(() => Finished?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => Finished?.Invoke(this, false));
        AddTabCommand = new RelayCommand(AddTab);
        MoveTabLeftCommand = new RelayCommand(() => MoveTab(-1));
        MoveTabRightCommand = new RelayCommand(() => MoveTab(+1));
        DeleteTabCommand = new RelayCommand(DeleteTab);
        CancelDeleteTabCommand = new RelayCommand(() => ConfirmDeleteTab = false);
        SetTabIconCommand = new RelayCommand<string?>(icon => { if (SelectedTabId is { } id) Editor.SetTabIcon(id, icon); });
        SetTileIconCommand = new RelayCommand<string?>(icon => { if (SelectedTileId is { } id) Editor.SetTileIcon(id, icon); });
        ResizeTileCommand = new RelayCommand<TileSize>(size => { if (SelectedTileId is { } id) Editor.ResizeTile(id, size); });
        DeleteTileCommand = new RelayCommand(() =>
        {
            if (SelectedTileId is { } id && Editor.RemoveTile(id))
            {
                SelectedTileId = null;
            }
        });
        MoveTileToTabCommand = new RelayCommand<string?>(tabId =>
        {
            if (SelectedTileId is { } id && tabId is not null && Editor.MoveTile(id, tabId))
            {
                SelectedTileId = null;
            }
        });
        StartAddCommand = new RelayCommand(StartAdd);
        CancelAddCommand = new RelayCommand(() => IsAdding = false);
        BackToSearchCommand = new RelayCommand(() => Picked = null);
        PickCommand = new RelayCommand<SearchHit>(async hit => await PickAsync(hit));
        ConfirmAddCommand = new RelayCommand(ConfirmAdd, () => CanAdd);
        SetScopeCommand = new RelayCommand<Option<TabOrigin>>(o => TabScope = o);
        SetShowWhenCommand = new RelayCommand<Option<TabVisibility>>(o => TabShowWhen = o);
        SelectTypeCommand = new RelayCommand<Option<string>>(o => SelectedType = o);

        StartAddStatusCommand = new RelayCommand(StartAddStatus, () => Editor.StatusItems.Count < LayoutEditor.MaxStatusItems);
        RemoveStatusCommand = new RelayCommand<StatusChip>(c => { if (c is not null) Editor.RemoveStatus(c.Index); });
        MoveStatusLeftCommand = new RelayCommand<StatusChip>(c => { if (c is not null) Editor.MoveStatus(c.Index, c.Index - 1); });
        MoveStatusRightCommand = new RelayCommand<StatusChip>(c => { if (c is not null) Editor.MoveStatus(c.Index, c.Index + 1); });

        Editor.Changed += (_, _) => OnEditorChanged();
        SelectedTabId = Editor.Tabs.FirstOrDefault()?.Tab.Id;
        SyncStatus();
    }

    // ------------------------------------------------------------------ Status line (M6)

    public ObservableCollection<StatusChip> StatusChips { get; } = [];

    public RelayCommand StartAddStatusCommand { get; }

    public RelayCommand<StatusChip> RemoveStatusCommand { get; }

    public RelayCommand<StatusChip> MoveStatusLeftCommand { get; }

    public RelayCommand<StatusChip> MoveStatusRightCommand { get; }

    /// <summary>"Add" adds a status value instead of a tile.</summary>
    public bool IsAddingStatus
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(AddTitle));
            }
        }
    }

    public string AddTitle => IsAddingStatus ? Loc.Get("Edit_AddStatus") : Loc.Get("Edit_AddTile");

    public string StatusHint => Editor.StatusItems.Count >= LayoutEditor.MaxStatusItems
        ? Loc.F("Edit_StatusMax", LayoutEditor.MaxStatusItems)
        : Editor.StatusItems.Count == 0 ? Loc.Get("Edit_StatusNone") : "";

    private void SyncStatus()
    {
        StatusChips.Clear();
        var items = Editor.StatusItems;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var module = _registry.Find(item.Module);
            var value = module?.Module is IStatusProvider
                ? _registry.Invoke(module.Id, "Status value", m => ((IStatusProvider)m).GetStatusValue(item.Entity)).Value
                : null;
            var label = value?.Tooltip?.Split(':', 2)[0] is { Length: > 0 } name ? name : item.Entity;
            StatusChips.Add(new StatusChip(i, label, value?.Icon));
        }

        OnPropertyChanged(nameof(StatusHint));
        StartAddStatusCommand.NotifyCanExecuteChanged();
    }

    private void StartAddStatus()
    {
        SelectedTileId = null;
        IsAddingStatus = true;
        IsAdding = true;
        _ = SearchAsync();
    }

    public LayoutEditor Editor { get; }

    public string DeviceName { get; }

    /// <summary>The working state's layout changed → rebuild the grid.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>"Done" (true) or "Cancel" (false).</summary>
    public event EventHandler<bool>? Finished;

    public RelayCommand UndoCommand { get; }

    public RelayCommand DoneCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AddTabCommand { get; }

    public RelayCommand MoveTabLeftCommand { get; }

    public RelayCommand MoveTabRightCommand { get; }

    public RelayCommand DeleteTabCommand { get; }

    public RelayCommand CancelDeleteTabCommand { get; }

    public RelayCommand<string?> SetTabIconCommand { get; }

    public RelayCommand<string?> SetTileIconCommand { get; }

    public RelayCommand<TileSize> ResizeTileCommand { get; }

    public RelayCommand DeleteTileCommand { get; }

    public RelayCommand<string?> MoveTileToTabCommand { get; }

    public RelayCommand StartAddCommand { get; }

    public RelayCommand CancelAddCommand { get; }

    public RelayCommand BackToSearchCommand { get; }

    public RelayCommand<SearchHit> PickCommand { get; }

    public RelayCommand ConfirmAddCommand { get; }

    public RelayCommand<Option<TabOrigin>> SetScopeCommand { get; }

    public RelayCommand<Option<TabVisibility>> SetShowWhenCommand { get; }

    public RelayCommand<Option<string>> SelectTypeCommand { get; }

    public IReadOnlyList<Option<TabOrigin>> ScopeOptions { get; }

    public IReadOnlyList<Option<TabVisibility>> VisibilityOptions { get; }

    public IReadOnlyList<Option<string?>> TabIcons { get; }

    public IReadOnlyList<Option<string?>> TileIcons { get; }

    // ------------------------------------------------------------------ Selection

    public string? SelectedTabId
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ConfirmDeleteTab = false;
                SyncTab();
            }
        }
    }

    public string? SelectedTileId
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                SyncTile();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Selected tile changed (for the highlight in the grid).</summary>
    public event EventHandler? SelectionChanged;

    // ------------------------------------------------------------------ Tab properties

    public string TabName
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !_syncing && SelectedTabId is { } id)
            {
                Editor.RenameTab(id, value);
            }
        }
    } = "";

    public Option<TabOrigin>? TabScope
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !_syncing && value is not null && SelectedTabId is { } id)
            {
                Editor.SetTabOrigin(id, value.Value);
            }

            foreach (var o in ScopeOptions)
            {
                o.IsCurrent = ReferenceEquals(o, value);
            }
        }
    }

    public Option<TabVisibility>? TabShowWhen
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !_syncing && value is not null && SelectedTabId is { } id)
            {
                Editor.SetTabVisibility(id, value.Value);
            }

            foreach (var o in VisibilityOptions)
            {
                o.IsCurrent = ReferenceEquals(o, value);
            }
        }
    }

    public bool TabHiddenHere
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !_syncing && SelectedTabId is { } id)
            {
                Editor.SetTabHiddenOnDevice(id, value);
            }
        }
    }

    /// <summary>Only base tabs can be hidden on this device.</summary>
    public bool CanHideTab
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool HasSelectedTab => SelectedTabId is not null && Editor.FindTab(SelectedTabId) is not null;

    /// <summary>Show tab properties only while no tile is selected and nothing is being added (space).</summary>
    public bool ShowTabProperties => HasSelectedTab && !HasSelectedTile && !IsAdding;

    public bool ConfirmDeleteTab
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string DeleteTabQuestion
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    // ------------------------------------------------------------------ Tile properties

    public bool HasSelectedTile => SelectedTileId is not null && Editor.FindTile(SelectedTileId) is not null;

    public string TileLabel
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !_syncing && SelectedTileId is { } id)
            {
                Editor.SetTileLabel(id, value);
            }
        }
    } = "";

    /// <summary>Name from the module (as a hint in the empty name field).</summary>
    public string TileDefaultName
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    public string TileTypeName
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    public ObservableCollection<Option<TileSize>> TileSizes { get; } = [];

    public ObservableCollection<Option<string?>> MoveTargets { get; } = [];

    // ------------------------------------------------------------------ Adding

    public bool IsAdding
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !value)
            {
                Picked = null;
                SearchText = "";
                Results.Clear();
                IsAddingStatus = false;
            }

            OnPropertyChanged(nameof(ShowTabProperties));
        }
    }

    public string SearchText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    } = "";

    public ObservableCollection<SearchHit> Results { get; } = [];

    public string SearchHint
    {
        get;
        private set => SetProperty(ref field, value);
    } = Loc.Get("Edit_SearchHint");

    public SearchHit? Picked
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasPicked));
            }
        }
    }

    public bool HasPicked => Picked is not null;

    public ObservableCollection<Option<string>> TypeOptions { get; } = [];

    public Option<string>? SelectedType
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !_syncing && value is not null)
            {
                _ = BuildDraftAsync(value.Value);
            }

            foreach (var o in TypeOptions)
            {
                o.IsCurrent = ReferenceEquals(o, value);
            }
        }
    }

    public ObservableCollection<SlotRow> Slots { get; } = [];

    public bool CanAdd => Picked is not null && _draft is not null && Slots.All(s => !s.Slot.Required || s.SelectedId is not null) && HasSelectedTab;

    // ------------------------------------------------------------------ Models for the grid

    /// <summary>Tile models for all tiles of the working state (missing ones are created).</summary>
    public async Task<IReadOnlyDictionary<string, TileModel>> ModelsAsync()
    {
        var result = new Dictionary<string, TileModel>(StringComparer.Ordinal);
        foreach (var composed in Editor.Tabs)
        {
            foreach (var config in composed.Tab.Tiles)
            {
                var key = Key(config);
                if (!_models.TryGetValue(key, out var model))
                {
                    model = await _registry.CreateTileAsync(config);
                    _models[key] = model;
                }

                result[config.Id] = model;
            }
        }

        if (SelectedTileId is not null)
        {
            SyncTile(); // the name from the module is only known now (new tile)
        }

        return result;
    }

    // ------------------------------------------------------------------ internal

    private void OnEditorChanged()
    {
        if (SelectedTabId is not null && Editor.FindTab(SelectedTabId) is null)
        {
            SelectedTabId = Editor.Tabs.FirstOrDefault()?.Tab.Id;
        }

        if (SelectedTileId is not null && Editor.FindTile(SelectedTileId) is null)
        {
            SelectedTileId = null;
        }

        SyncTab();
        SyncTile();
        SyncStatus();
        UndoCommand.NotifyCanExecuteChanged();
        ConfirmAddCommand.NotifyCanExecuteChanged();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncTab()
    {
        _syncing = true;
        try
        {
            var tab = SelectedTabId is null ? null : Editor.FindTab(SelectedTabId);
            var origin = SelectedTabId is null ? null : Editor.OriginOf(SelectedTabId);
            TabName = tab?.Name ?? "";
            TabScope = ScopeOptions.FirstOrDefault(o => o.Value == origin);
            TabShowWhen = VisibilityOptions.FirstOrDefault(o => o.Value == (tab?.Visibility ?? TabVisibility.Always));
            CanHideTab = origin == TabOrigin.Base;
            TabHiddenHere = tab is not null && Editor.Tabs.Any(t => t.Tab.Id == tab.Id && t.HiddenOnDevice);
            foreach (var icon in TabIcons)
            {
                icon.IsCurrent = icon.Value == tab?.Icon;
            }

            var count = tab?.Tiles.Count ?? 0;
            DeleteTabQuestion = count == 0 ? Loc.Get("Edit_DeleteTab") : count == 1 ? Loc.Get("Edit_DeleteTabOne") : Loc.F("Edit_DeleteTabMany", count);
            OnPropertyChanged(nameof(HasSelectedTab));
            OnPropertyChanged(nameof(ShowTabProperties));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncTile()
    {
        _syncing = true;
        try
        {
            var found = SelectedTileId is null ? null : Editor.FindTile(SelectedTileId);
            TileSizes.Clear();
            MoveTargets.Clear();
            if (found is { } f)
            {
                TileLabel = f.Tile.Label ?? "";
                var model = _models.TryGetValue(Key(f.Tile), out var m) ? m : null;
                TileDefaultName = model?.Title ?? f.Tile.Type;
                TileTypeName = _registry.FindTileType(f.Tile.Type)?.Type.DisplayName ?? f.Tile.Type;
                foreach (var size in Editor.AllowedSizes(f.Tile).OrderBy(s => s.Columns * s.Rows))
                {
                    TileSizes.Add(new Option<TileSize>(size, size.ToString()) { IsCurrent = size == f.Tile.Size });
                }

                foreach (var tab in Editor.Tabs.Where(t => t.Tab.Id != f.Tab.Id))
                {
                    MoveTargets.Add(new Option<string?>(tab.Tab.Id, tab.Tab.Name, tab.Tab.Icon));
                }

                foreach (var icon in TileIcons)
                {
                    icon.IsCurrent = icon.Value == f.Tile.Icon;
                }
            }
            else
            {
                TileLabel = "";
            }

            OnPropertyChanged(nameof(HasSelectedTile));
            OnPropertyChanged(nameof(ShowTabProperties));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void AddTab()
    {
        var index = SelectedTabId is null ? (int?)null : Editor.Tabs.ToList().FindIndex(t => t.Tab.Id == SelectedTabId) + 1;
        var id = Editor.AddTab(UeBar.Core.Localization.CoreText.T["Layout_NewTab"], "Star", TabOrigin.Base, index);
        SelectedTabId = id;
    }

    private void MoveTab(int delta)
    {
        if (SelectedTabId is not { } id)
        {
            return;
        }

        var index = Editor.Tabs.ToList().FindIndex(t => t.Tab.Id == id);
        Editor.MoveTab(id, index + delta);
    }

    private void DeleteTab()
    {
        if (SelectedTabId is not { } id)
        {
            return;
        }

        if ((Editor.FindTab(id)?.Tiles.Count ?? 0) > 0 && !ConfirmDeleteTab)
        {
            ConfirmDeleteTab = true; // ask first
            return;
        }

        ConfirmDeleteTab = false;
        Editor.RemoveTab(id);
    }

    private void StartAdd()
    {
        SelectedTileId = null;
        IsAddingStatus = false;
        IsAdding = true;
        _ = SearchAsync();
    }

    private async Task SearchAsync()
    {
        var generation = ++_searchGeneration;
        var query = SearchText.Trim();
        var sources = _registry.GetCapabilities<IEntitySource>()
            .Where(s => !IsAddingStatus || s.Module.Module is IStatusProvider) // status values only from modules with a status line
            .ToList();
        if (sources.Count == 0)
        {
            Results.Clear();
            SearchHint = Loc.Get("Edit_NothingToAdd");
            return;
        }

        var tasks = sources.Select(async s =>
        {
            var result = await _registry.InvokeAsync<IReadOnlyList<EntityInfo>>(
                s.Module.Id, "Search", (m, ct) => ((IEntitySource)m).SearchAsync(query, 15, ct), TimeSpan.FromSeconds(6));
            return (s.Module, Hits: result.Value ?? []);
        });
        var all = await Task.WhenAll(tasks);
        if (generation != _searchGeneration)
        {
            return; // typed on in the meantime
        }

        Results.Clear();
        foreach (var (module, hits) in all)
        {
            foreach (var hit in hits)
            {
                Results.Add(new SearchHit(module.Id, module.DisplayName, hit));
            }
        }

        SearchHint = Results.Count == 0 ? (query.Length == 0 ? Loc.Get("Edit_SearchHint") : Loc.Get("Search_NothingFound")) : "";
    }

    private async Task PickAsync(SearchHit? hit)
    {
        if (hit is null || _registry.Find(hit.ModuleId) is not { } module)
        {
            return;
        }

        if (IsAddingStatus)
        {
            Editor.AddStatus(module.Id, hit.Entity.Id);
            IsAdding = false;
            return;
        }

        var suggestion = _registry.Invoke(module.Id, "Suggest tile", m => ((IEntitySource)m).SuggestTile(hit.Entity)).Value;
        _syncing = true;
        try
        {
            Picked = hit;
            TypeOptions.Clear();
            foreach (var type in module.TileTypes)
            {
                TypeOptions.Add(new Option<string>(type.Id, type.DisplayName, type.Icon));
            }

            SelectedType = TypeOptions.FirstOrDefault(t => t.Value == suggestion?.Type) ?? TypeOptions.FirstOrDefault();
        }
        finally
        {
            _syncing = false;
        }

        if (SelectedType is { } selected)
        {
            await BuildDraftAsync(selected.Value, suggestion);
        }
    }

    /// <summary>Draft for the chosen type: the chosen thing into the first slot, let the module pre-fill the rest.</summary>
    private async Task BuildDraftAsync(string typeId, TileConfig? suggestion = null)
    {
        if (Picked is not { } hit || _registry.FindTileType(typeId) is not { } registration)
        {
            return;
        }

        var type = registration.Type;
        var draft = new TileConfig
        {
            Module = hit.ModuleId,
            Type = typeId,
            Size = suggestion?.Type == typeId ? suggestion.Size : type.DefaultSize,
            Entities = suggestion?.Type == typeId && suggestion.Entities.Count > 0
                ? new Dictionary<string, string>(suggestion.Entities)
                : type.Slots.Count > 0 ? new Dictionary<string, string> { [type.Slots[0].Name] = hit.Entity.Id } : [],
        };

        if (registration.Module.Module is ITileSlotAssistant)
        {
            var completed = _registry.Invoke(hit.ModuleId, "Pre-fill slots", m => ((ITileSlotAssistant)m).CompleteSlots(draft));
            if (completed.Succeeded && completed.Value is { } value)
            {
                draft = value;
            }
        }

        _draft = draft;
        Slots.Clear();
        foreach (var slot in type.Slots)
        {
            var row = new SlotRow(slot);
            if (!slot.Required)
            {
                row.Candidates.Add(new Option<string?>(null, "(keins)"));
            }

            var candidates = await CandidatesAsync(hit.ModuleId, draft, slot);
            var current = draft.GetEntity(slot.Name);
            if (current is not null && candidates.All(c => c.Id != current))
            {
                row.Candidates.Add(new Option<string?>(current, current));
            }

            foreach (var c in candidates)
            {
                row.Candidates.Add(new Option<string?>(c.Id, string.IsNullOrWhiteSpace(c.Area) ? c.Name : $"{c.Name} · {c.Area}"));
            }

            row.SelectedId = current;
            row.PropertyChanged += (_, _) => ConfirmAddCommand.NotifyCanExecuteChanged();
            Slots.Add(row);
        }

        OnPropertyChanged(nameof(CanAdd));
        ConfirmAddCommand.NotifyCanExecuteChanged();
    }

    private async Task<IReadOnlyList<EntityInfo>> CandidatesAsync(string moduleId, TileConfig draft, TileSlot slot)
    {
        if (_registry.Find(moduleId)?.Module is ITileSlotAssistant)
        {
            var result = await _registry.InvokeAsync<IReadOnlyList<EntityInfo>>(
                moduleId, "Slot suggestions", (m, ct) => ((ITileSlotAssistant)m).SuggestForSlotAsync(draft, slot.Name, "", 25, ct), TimeSpan.FromSeconds(4));
            if (result.Succeeded && result.Value is { } list)
            {
                return list;
            }
        }

        // Without help from the module: the search hits of the same module.
        return [.. Results.Where(r => r.ModuleId == moduleId).Select(r => r.Entity)];
    }

    private void ConfirmAdd()
    {
        if (_draft is null || SelectedTabId is not { } tabId)
        {
            return;
        }

        var config = LayoutLayers.Clone(_draft);
        config.Entities = Slots.Where(s => s.SelectedId is not null).ToDictionary(s => s.Slot.Name, s => s.SelectedId!);
        var id = Editor.AddTile(tabId, config);
        IsAdding = false;
        if (id is not null)
        {
            SelectedTileId = id;
            _log.Info(null, $"Edit: tile {config.Type} added");
        }
    }

    private static string Key(TileConfig config) => UeBarJson.Serialize(config);
}
