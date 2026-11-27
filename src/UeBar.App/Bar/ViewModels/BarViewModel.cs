// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.App.Bar.Edit;
using UeBar.App.Tiles;
using UeBar.Bar;
using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Bar.ViewModels;

/// <summary>Dimensions of the tile grid in DIP (SPEC 6).</summary>
internal static class GridMetrics
{
    /// <summary>Columns in the grid (setting, M7: 3–6).</summary>
    public static int Columns { get; set; } = 4;
    public const double Cell = 92;
    public const double Gap = 10;
    public const double PanelPadding = 16;

    public static double GridWidth => (Columns * Cell) + ((Columns - 1) * Gap);

    public static double PanelWidth => GridWidth + (2 * PanelPadding);

    public static double Span(int cells) => cells <= 0 ? 0 : (cells * Cell) + ((cells - 1) * Gap);
}

internal sealed class TabViewModel : ObservableObject, IDisposable
{
    public TabViewModel(LayoutTab tab, IReadOnlyList<TileModel> models, ModuleRegistry registry, IUeLogger log, ComposedTab? info = null, bool isEditing = false)
    {
        Id = tab.Id;
        Name = string.IsNullOrWhiteSpace(tab.Name) ? tab.Id : tab.Name;
        Icon = tab.Icon;
        IsEditing = isEditing;
        IsDeviceTab = info?.Origin == TabOrigin.Device;
        IsHiddenHere = info?.HiddenOnDevice == true;
        VisibilityBadge = tab.Visibility switch
        {
            TabVisibility.HomeOnly => UeBar.App.Localization.Loc.Get("Tab_HomeOnlyBadge"),
            TabVisibility.AwayOnly => UeBar.App.Localization.Loc.Get("Tab_AwayOnlyBadge"),
            _ => null,
        };

        var byId = models.GroupBy(m => m.TileId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var configs = tab.Tiles.Where(c => byId.ContainsKey(c.Id)).ToList();
        var placements = GridPacker.Pack([.. configs.Select(c => c.Size)], GridMetrics.Columns, out var rows);
        for (var i = 0; i < configs.Count; i++)
        {
            var p = placements[i];
            Tiles.Add(new TileViewModel(
                byId[configs[i].Id],
                new TileSize(p.ColumnSpan, p.RowSpan),
                p.Column * (GridMetrics.Cell + GridMetrics.Gap),
                p.Row * (GridMetrics.Cell + GridMetrics.Gap),
                GridMetrics.Span(p.ColumnSpan),
                GridMetrics.Span(p.RowSpan),
                registry,
                log,
                configs[i],
                isEditing));
        }

        GridHeight = Math.Max(GridMetrics.Span(rows), isEditing ? GridMetrics.Cell : 0);
        TileConfigs = configs;
    }

    /// <summary>Configurations of the tiles shown in this tab (for <see cref="IVisibilityAware"/>, P2-2).</summary>
    public IReadOnlyList<TileConfig> TileConfigs { get; }

    public string Id { get; }

    public string Name { get; }

    public string? Icon { get; }

    public bool IsEditing { get; }

    /// <summary>"Only on this device" (device.json).</summary>
    public bool IsDeviceTab { get; }

    /// <summary>Base tab hidden on this device (only visible in edit mode).</summary>
    public bool IsHiddenHere { get; }

    /// <summary>"home"/"away" if the tab is only visible there.</summary>
    public string? VisibilityBadge { get; }

    public bool HasVisibilityBadge => VisibilityBadge is not null;

    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    public double GridHeight { get; }

    public double GridWidth => GridMetrics.GridWidth;

    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }

    public void Dispose()
    {
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }
    }
}

internal sealed class ModuleDotViewModel : ObservableObject
{
    public ModuleDotViewModel(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }

    public string Tooltip
    {
        get;
        set => SetProperty(ref field, value);
    } = "";

    public Brush DotBrush
    {
        get;
        set => SetProperty(ref field, value);
    } = Brushes.Gray;

    public void Update(ModuleStatus status)
    {
        Tooltip = $"{Name}: {status.DisplayText}";
        DotBrush = DotBrushes.For(status.State);
    }
}

internal static class DotBrushes
{
    private static readonly SolidColorBrush Green = Frozen(0x34, 0xC7, 0x59);
    private static readonly SolidColorBrush Yellow = Frozen(0xFF, 0xB0, 0x20);
    private static readonly SolidColorBrush Red = Frozen(0xFF, 0x45, 0x3A);
    private static readonly SolidColorBrush Grey = Frozen(0x6B, 0x71, 0x7A);

    public static Brush For(ModuleState state) => state switch
    {
        ModuleState.Connected => Green,
        ModuleState.Starting or ModuleState.Disconnected or ModuleState.NotConfigured => Yellow,
        ModuleState.Error => Red,
        _ => Grey,
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Data for pill and panel. Lives on the UI thread; changes from outside come through the dispatcher.</summary>
internal sealed class BarViewModel : ObservableObject
{
    private readonly ModuleRegistry _registry;
    private readonly TileHost _tiles;
    private readonly Dispatcher _dispatcher;
    private readonly IUeLogger _log;
    private readonly LocationService? _location;
    private int _statusGeneration;
    private int _editGeneration;

    public BarViewModel(ModuleRegistry registry, TileHost tiles, Dispatcher dispatcher, IUeLogger log, LocationService? location = null)
    {
        _registry = registry;
        _tiles = tiles;
        _dispatcher = dispatcher;
        _log = log;
        _location = location;
        SelectTabCommand = new RelayCommand<TabViewModel>(tab =>
        {
            if (tab is not null)
            {
                SelectedTab = tab;
                if (Edit is { } edit)
                {
                    edit.SelectedTabId = tab.Id;
                    edit.SelectedTileId = null;
                }
            }
        });
        TogglePinCommand = new RelayCommand(() => PinToggleRequested?.Invoke(this, EventArgs.Empty));
        EditCommand = new RelayCommand(() => EditRequested?.Invoke(this, EventArgs.Empty));
        SelectTileCommand = new RelayCommand<TileViewModel>(tile =>
        {
            if (Edit is { } edit && tile?.Config is { } config)
            {
                edit.IsAdding = false;
                edit.SelectedTileId = edit.SelectedTileId == config.Id ? null : config.Id;
            }
        });
    }

    /// <summary>Pen icon: start edit mode (the app decides).</summary>
    public event EventHandler? EditRequested;

    public RelayCommand EditCommand { get; }

    public RelayCommand<TileViewModel> SelectTileCommand { get; }

    /// <summary>Running edit session or null.</summary>
    public EditSessionViewModel? Edit
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(IsNotEditing));
            }
        }
    }

    public bool IsEditing => Edit is not null;

    public bool IsNotEditing => Edit is null;

    /// <summary>First start (M5b): no layout yet – the panel shows the choice instead of the tabs.</summary>
    public SetupViewModel? Setup
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsSetup));
                OnPropertyChanged(nameof(ShowSetupSection));
                OnPropertyChanged(nameof(ShowTabs));
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsSetup => Setup is not null;

    /// <summary>Welcome assistant at the first start (M7) – before the first-start layout.</summary>
    public WelcomeViewModel? Welcome
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsWelcome));
                OnPropertyChanged(nameof(ShowSetupSection));
                OnPropertyChanged(nameof(ShowTabs));
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsWelcome => Welcome is not null;

    public void ShowWelcome(WelcomeViewModel welcome) => Welcome = welcome;

    public void HideWelcome() => Welcome = null;

    /// <summary>First-start choice visible (not during the welcome assistant).</summary>
    public bool ShowSetupSection => Setup is not null && Welcome is null;

    /// <summary>Tabs and grid visible (not in first start/welcome, not in the history).</summary>
    public bool ShowTabs => Setup is null && Welcome is null && !ShowHistory && !ShowPins;

    public void ShowSetup(SetupViewModel setup) => Setup = setup;

    public void HideSetup() => Setup = null;

    /// <summary>Show edit mode (grid from the editor's working state).</summary>
    public void BeginEdit(EditSessionViewModel session)
    {
        Edit = session;
        session.LayoutChanged += (_, _) => _ = RebuildEditingAsync();
        session.Editor.Changed += (_, _) => ShowStatus(session.Editor.StatusItems); // status line right away from the working state
        ShowHistory = false;
        ShowPins = false;
        IsQuickSearchOpen = false;
        session.SelectionChanged += (_, _) => MarkSelection();
        session.PropertyChanged += (_, e) =>
        {
            // The editor picks a tab itself (newly created, deleted …) → the display follows.
            if (e.PropertyName == nameof(EditSessionViewModel.SelectedTabId)
                && Tabs.FirstOrDefault(t => t.Id == session.SelectedTabId) is { } tab
                && !ReferenceEquals(tab, SelectedTab))
            {
                SelectedTab = tab;
                MarkSelection();
            }
        };
        session.SelectedTabId = SelectedTab?.Id ?? session.SelectedTabId;
        _ = RebuildEditingAsync();
    }

    /// <summary>Leave edit mode; the app rebuilds the normal grid after saving/discarding.</summary>
    public void EndEdit()
    {
        _editGeneration++;
        Edit = null;
        Rebuild();
    }

    /// <summary>Drop the dragged tile on the tile <paramref name="beforeTileId"/> or at the end (null), possibly into another tab.</summary>
    public void DropTile(string tileId, string targetTabId, string? beforeTileId)
    {
        if (Edit is not { } edit || edit.Editor.FindTab(targetTabId) is not { } tab)
        {
            return;
        }

        // Dropping onto a tile = taking its place: dragged forward → behind it, backward → before it.
        var full = tab.Tiles.Select(t => t.Id).ToList();
        var list = full.Where(id => id != tileId).ToList();
        int? index = null;
        if (beforeTileId is not null && list.IndexOf(beforeTileId) is var i and >= 0)
        {
            var from = full.IndexOf(tileId);
            index = from >= 0 && from < full.IndexOf(beforeTileId) ? i + 1 : i;
        }

        edit.Editor.MoveTile(tileId, targetTabId, index);
    }

    private void MarkSelection()
    {
        foreach (var tile in SelectedTab?.Tiles ?? [])
        {
            tile.IsSelected = tile.Config?.Id == Edit?.SelectedTileId;
        }
    }

    private async Task RebuildEditingAsync()
    {
        if (Edit is not { } edit)
        {
            return;
        }

        var generation = ++_editGeneration;
        var models = await edit.ModelsAsync();
        if (generation != _editGeneration || Edit != edit)
        {
            return;
        }

        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        foreach (var composed in edit.Editor.Tabs)
        {
            var list = composed.Tab.Tiles.Select(c => models.TryGetValue(c.Id, out var m) ? m : null).OfType<TileModel>().ToList();
            Tabs.Add(new TabViewModel(composed.Tab, list, _registry, _log, composed, isEditing: true));
        }

        var selected = Tabs.FirstOrDefault(t => t.Id == edit.SelectedTabId) ?? Tabs.FirstOrDefault();
        SelectedTab = null;
        SelectedTab = selected;
        MarkSelection();
        ContentSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public ObservableCollection<StatusValue> StatusItems { get; } = [];

    public ObservableCollection<ModuleDotViewModel> ModuleDots { get; } = [];

    public RelayCommand<TabViewModel> SelectTabCommand { get; }

    public RelayCommand TogglePinCommand { get; }

    public TabViewModel? SelectedTab
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                foreach (var tab in Tabs)
                {
                    tab.IsSelected = ReferenceEquals(tab, value);
                }

                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
                PublishVisibility();
            }
        }
    }

    public bool IsPinned
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// The panel is visible (open or pinned). Only then do the media progress and the loading
    /// of cover images run (M4); when collapsed nothing happens.
    /// </summary>
    public bool IsPanelVisible
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                Media.MediaClock.PanelVisible = value;
                PublishVisibility();
            }
        }
    }

    /// <summary>First status value – shown in the pill (otherwise "ue Bar").</summary>
    public StatusValue? PillStatus => StatusItems.FirstOrDefault();

    // ------------------------------------------------------------------ Extras (M6)

    /// <summary>Currently shown notification (pill or banner in the panel) or null.</summary>
    public NotificationViewModel? Notification
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasNotification));
                UpdatePill();
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasNotification => Notification is not null;

    /// <summary>History (newest first, at most 20).</summary>
    public ObservableCollection<NotificationViewModel> NotificationHistory { get; } = [];

    public bool HasHistory => NotificationHistory.Count > 0;

    /// <summary>P2-7: "hide from screen captures" is on – a quiet icon in the header of the open panel (never in the pill).</summary>
    public bool IsCaptureHidden
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Bell: show the history instead of the tabs.</summary>
    public bool ShowHistory
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ShowTabs));
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
                PublishVisibility();
            }
        }
    }

    public RelayCommand ToggleHistoryCommand => field ??= new RelayCommand(() =>
    {
        ShowPins = false;
        ShowHistory = !ShowHistory;
    });

    /// <summary>Pin list (P2-5); null while the pins are not set up.</summary>
    public PinsViewModel? Pins
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && value is not null)
            {
                value.ContentChanged += (_, _) =>
                {
                    if (ShowPins)
                    {
                        ContentSizeChanged?.Invoke(this, EventArgs.Empty);
                    }
                };
            }
        }
    }

    /// <summary>Pin icon: show the pin list instead of the tabs.</summary>
    public bool ShowPins
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (value && Pins is { } pins)
                {
                    pins.Refresh();
                }

                OnPropertyChanged(nameof(ShowTabs));
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
                PublishVisibility();
            }
        }
    }

    public RelayCommand TogglePinsCommand => field ??= new RelayCommand(() =>
    {
        ShowHistory = false;
        ShowPins = !ShowPins;
    });

    public RelayCommand ClearHistoryCommand => field ??= new RelayCommand(() => HistoryCleared?.Invoke(this, EventArgs.Empty));

    public event EventHandler? HistoryCleared;

    public void SetHistory(IEnumerable<BarNotification> history)
    {
        NotificationHistory.Clear();
        foreach (var item in history)
        {
            NotificationHistory.Add(new NotificationViewModel(item));
        }

        OnPropertyChanged(nameof(HasHistory));
        if (ShowHistory)
        {
            ContentSizeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Current playback or null.</summary>
    public NowPlayingViewModel? NowPlaying
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasNowPlaying));
                UpdatePill();
                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasNowPlaying => NowPlaying is not null;

    /// <summary>Activity for the pill (P2-3), e.g. a running timer – shown before Now Playing.</summary>
    public PillActivityViewModel? Activity
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                UpdatePill();
            }
        }
    }

    public RelayCommand<string> NowPlayingCommand => field ??= new RelayCommand<string>(c =>
        NowPlayingRequested?.Invoke(this, c switch { "next" => UeBar.Sdk.NowPlayingCommand.Next, "previous" => UeBar.Sdk.NowPlayingCommand.Previous, _ => UeBar.Sdk.NowPlayingCommand.PlayPause }));

    public event EventHandler<UeBar.Sdk.NowPlayingCommand>? NowPlayingRequested;

    /// <summary>What the pill shows: message > playback > status value.</summary>
    public PillContent PillContent
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Pill content changed (the window measures the pill again).</summary>
    public event EventHandler? PillChanged;

    private void UpdatePill()
    {
        PillContent = Notification is not null ? PillContent.Notification
            : Activity is not null ? PillContent.Activity
            : NowPlaying is not null ? PillContent.NowPlaying
            : PillContent.Status;
        PillChanged?.Invoke(this, EventArgs.Empty);
    }

    public AssistViewModel? Assist
    {
        get;
        set => SetProperty(ref field, value);
    }

    public QuickSearchViewModel? QuickSearch
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Quick search open: search field at the top, the tabs below stay.</summary>
    public bool IsQuickSearchOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (QuickSearch is { } search)
                {
                    search.IsOpen = value;
                }

                ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Reload status values (e.g. from the working state while editing).</summary>
    public void ShowStatus(IReadOnlyList<StatusItemConfig> items) => _ = LoadStatusAsync(items);

    /// <summary>Bar visible (pill or panel): covers may be loaded.</summary>
    public bool IsBarVisible
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (Media.ModuleImageLoader.Current is { } loader)
                {
                    loader.PanelVisible = value;
                }

                PublishVisibility();
            }
        }
    }

    /// <summary>Panel content changed in size (tab switched, new tiles).</summary>
    public event EventHandler? ContentSizeChanged;

    public event EventHandler? PinToggleRequested;

    /// <summary>Rebuild the grid (number of columns/accent color changed, M7) – not in edit mode.</summary>
    public void RefreshLayout()
    {
        if (!IsEditing)
        {
            Rebuild();
        }
    }

    /// <summary>Gear in the panel (M7): open the settings.</summary>
    public RelayCommand OpenSettingsCommand => field ??= new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));

    public event EventHandler? SettingsRequested;

    public void Attach()
    {
        _tiles.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            if (!IsEditing)
            {
                Rebuild();
            }
        });
        if (_location is not null)
        {
            _location.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
            {
                if (!IsEditing)
                {
                    Rebuild();
                }
            });
        }

        _registry.ModuleStatusChanged += (_, m) => _dispatcher.BeginInvoke(() => UpdateDot(m));
        foreach (var module in _registry.Modules)
        {
            var dot = new ModuleDotViewModel(module.Id, module.DisplayName);
            dot.Update(module.Status);
            ModuleDots.Add(dot);
        }

        Rebuild();
    }

    private void UpdateDot(ModuleEntry module)
    {
        ModuleDots.FirstOrDefault(d => d.Id == module.Id)?.Update(module.Status);

        // Status values depend on whether the module is running.
        _ = LoadStatusAsync(_tiles.Layout.Status);
    }

    private void Rebuild()
    {
        var selectedId = SelectedTab?.Id;
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        var location = _location?.Current ?? DeviceLocation.Unknown;
        foreach (var (tab, models) in _tiles.Snapshot())
        {
            // Visibility by location (SPEC 9): show/hide "Only at home"/"Only away" live.
            if (LayoutLayers.IsVisible(tab, location))
            {
                Tabs.Add(new TabViewModel(tab, models, _registry, _log));
            }
        }

        var selected = Tabs.FirstOrDefault(t => t.Id == selectedId) ?? Tabs.FirstOrDefault();
        var changed = !ReferenceEquals(SelectedTab, selected);
        SelectedTab = selected;
        if (!changed)
        {
            ContentSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        _ = LoadStatusAsync(_tiles.Layout.Status);
    }

    private async Task LoadStatusAsync(IReadOnlyList<StatusItemConfig> items)
    {
        var generation = ++_statusGeneration;
        var values = new List<StatusValue>();
        foreach (var item in items.Take(5))
        {
            if (await _registry.GetStatusValueAsync(item) is { } value)
            {
                values.Add(value);
            }
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (generation != _statusGeneration)
            {
                return;
            }

            StatusItems.Clear();
            foreach (var value in values)
            {
                StatusItems.Add(value);
            }

            OnPropertyChanged(nameof(PillStatus));
            UpdatePill();
            PublishVisibility();
        });
    }

    /// <summary>
    /// Tells the modules what of them is on screen (P2-2, <see cref="IVisibilityAware"/>): the tiles of
    /// the current tab while the panel is open, the status values while the bar is visible (the pill
    /// shows the first one). The registry passes only changes on.
    /// </summary>
    private void PublishVisibility()
    {
        var open = IsPanelVisible;
        IReadOnlyList<TileConfig> tiles = open && ShowTabs ? SelectedTab?.TileConfigs ?? [] : [];
        IReadOnlyList<StatusItemConfig> status = IsBarVisible ? [.. _tiles.Layout.Status.Take(5)] : [];
        _ = _registry.NotifyVisibilityAsync(open, tiles, status, barVisible: IsBarVisible);
    }
}
