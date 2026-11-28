// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.App.Infrastructure;
using UeBar.App.Localization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Tiles;
using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Bar.Edit;

/// <summary>
/// Layout in the app (M5a/M5b): load (base + device), first start without a layout, start/end
/// edit mode, save, import/export. Runs on the UI thread.
/// </summary>
internal sealed class LayoutCoordinator
{
    private readonly LayoutManager _manager;
    private readonly ILayoutStore _store;
    private readonly ModuleRegistry _registry;
    private readonly TileHost _tiles;
    private readonly IUeLogger _log;
    private readonly Dispatcher _dispatcher;
    private EditSessionViewModel? _session;
    private bool _reloadPending;
    private bool _setupAgain;

    public LayoutCoordinator(LayoutManager manager, ILayoutStore store, ModuleRegistry registry, TileHost tiles, IUeLogger log, Dispatcher dispatcher)
    {
        _manager = manager;
        _store = store;
        _registry = registry;
        _tiles = tiles;
        _log = log;
        _dispatcher = dispatcher;

        // layout.json changed by hand or a new state from the remote store: reload –
        // while editing only afterwards.
        _store.Changed += (_, _) => _dispatcher.BeginInvoke(async () =>
        {
            if (_session is not null)
            {
                _reloadPending = true;
                return;
            }

            _log.Info(null, "Layout changed from outside – reloading");
            await ReloadAsync();
        });

        // First start: the modules' offers change as soon as they are connected.
        _registry.ModuleStatusChanged += (_, _) => _dispatcher.BeginInvoke(() => ViewModel?.Setup?.Refresh());
    }

    public BarViewModel? ViewModel { get; set; }

    public BarController? Bar { get; set; }

    public bool IsEditing => _session is not null;

    public LayoutManager Manager => _manager;

    /// <summary>Load and show the layout.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            var composed = await _manager.LoadAsync();
            await _registry.NotifyLayoutAsync(composed.Document); // modules learn which of their things are in the layout
            await _tiles.SetLayoutAsync(composed.Document);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Layout could not be loaded", ex);
        }

        UpdateSetup();
    }

    /// <summary>
    /// Settings → Layout → "Add suggestion" (P2-2): appends the tabs a plugin suggests to the base
    /// layout (backed up first, like an import). Returns the number of added tabs (0 = nothing done).
    /// </summary>
    public async Task<int> AddSuggestionAsync(string moduleId)
    {
        if (_session is not null)
        {
            return 0;
        }

        var current = _manager.Base;
        if (LayoutSetup.AppendSuggestion(_registry, moduleId, current) is not { } merged || merged.Tabs.Count == current.Tabs.Count)
        {
            return 0;
        }

        await _manager.ImportAsync(merged);
        _log.Info(moduleId, $"Suggestion added: {merged.Tabs.Count - current.Tabs.Count} tabs");
        await ReloadAsync();
        return merged.Tabs.Count - current.Tabs.Count;
    }

    /// <summary>P2-5: tabs a tile can be put into (all tabs of this device, also hidden ones) – for the settings page.</summary>
    public IReadOnlyList<(string Id, string Name)> TabChoices =>
        [.. (_session?.Editor.Tabs ?? _manager.Composed.AllTabs).Where(t => !t.HiddenOnDevice).Select(t => (t.Tab.Id, string.IsNullOrWhiteSpace(t.Tab.Name) ? t.Tab.Id : t.Tab.Name))];

    /// <summary>The tab the bar shows right now (default for new tiles from the settings).</summary>
    public string? CurrentTabId => ViewModel?.SelectedTab?.Id ?? TabChoices.FirstOrDefault().Id;

    /// <summary>
    /// P2-5: puts a tile from outside the bar (settings: "Add as tile") into a tab. During editing it becomes part
    /// of the edit (Cancel discards it), otherwise it is saved right away like any change (and synchronized).
    /// </summary>
    public async Task<bool> AddTileAsync(string tabId, TileConfig tile)
    {
        if (_session is { } session)
        {
            var added = session.Editor.AddTile(tabId, tile) is not null;
            _log.Info(tile.Module, $"Tile added from the settings to the running edit: {added}");
            return added;
        }

        var editor = new LayoutEditor(_manager.Base, _manager.Device, config => _registry.FindTileType(config.Type)?.Type.AllowedSizes);
        if (editor.AddTile(tabId, tile) is null)
        {
            return false;
        }

        var (baseLayout, device) = editor.Result;
        await _manager.SaveAsync(baseLayout, device);
        _log.Info(tile.Module, $"Tile added from the settings: {tile.Type} → tab {tabId}");
        await ReloadAsync();
        return true;
    }

    /// <summary>Show the first start as long as there is no base layout (M5b).</summary>
    public void UpdateSetup()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (_setupAgain)
        {
            return; // "Show first start again" is running – do not clear it
        }

        if (!_manager.NeedsSetup || _session is not null)
        {
            viewModel.HideSetup();
            return;
        }

        if (viewModel.Setup is not null)
        {
            viewModel.Setup.Refresh();
            return;
        }

        _log.Info(null, "No layout present – first start is offered");
        var setup = new SetupViewModel(_registry);
        setup.Completed += async (_, layout) => await CompleteSetupAsync(setup, layout);
        viewModel.ShowSetup(setup);
        SetupShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The first start is being offered right now (for a hint in the tray).</summary>
    public event EventHandler? SetupShown;

    /// <summary>
    /// Settings → Layout → "Show first start again" (M7): the choice from M5b once more,
    /// with cancel. "Apply" replaces the base layout (the old one is backed up first).
    /// </summary>
    public bool ShowSetupAgain()
    {
        if (ViewModel is not { } viewModel || _session is not null || viewModel.IsSetup)
        {
            return false;
        }

        _setupAgain = true;
        _log.Info(null, "First start shown again");
        var setup = new SetupViewModel(_registry, canCancel: true);
        setup.Completed += async (_, layout) => await CompleteSetupAsync(setup, layout);
        setup.Cancelled += (_, _) =>
        {
            _setupAgain = false;
            _log.Info(null, "First start cancelled – layout stays");
            viewModel.HideSetup();
        };
        viewModel.ShowSetup(setup);
        return true;
    }

    private async Task CompleteSetupAsync(SetupViewModel setup, LayoutDocument layout)
    {
        try
        {
            if (!_manager.NeedsSetup && !_setupAgain)
            {
                // A layout arrived in the meantime (e.g. from Home Assistant) – do not overwrite it.
                _log.Info(null, "First start dropped: there is a layout by now");
                await ReloadAsync();
                return;
            }

            await _manager.ApplySetupAsync(layout);
            _setupAgain = false;
            ViewModel?.HideSetup();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            setup.IsBusy = false;
            _log.Error(null, "First start: layout could not be saved", ex);
            UeDialog.Error(null, Loc.F("Layout_SaveFailed", ex.Message));
        }
    }

    // ------------------------------------------------------------------ Editing

    public void StartEdit()
    {
        if (_session is not null || ViewModel is null || Bar is null || ViewModel.IsSetup)
        {
            return;
        }

        var editor = new LayoutEditor(_manager.Base, _manager.Device, config => _registry.FindTileType(config.Type)?.Type.AllowedSizes);
        var models = _tiles.Snapshot().SelectMany(t => t.Tiles).ToList();
        _session = new EditSessionViewModel(editor, _registry, _log, _manager.DeviceName, models, _manager.Base.AllTiles.Concat(_manager.Device.Tabs.SelectMany(t => t.Tiles)));
        _session.Finished += async (_, save) => await FinishAsync(save);
        _session.LayoutChanged += (_, _) => _ = _registry.NotifyLayoutAsync(editor.Composed.Document); // new tiles live right away

        Bar.BeginEditHold();
        ViewModel.BeginEdit(_session);
        _log.Info(null, "Edit mode started");
    }

    /// <summary>End editing from outside (tray "Reset bar"): with saving, so nothing gets lost.</summary>
    public Task FinishEditAsync() => FinishAsync(save: true);

    private async Task FinishAsync(bool save)
    {
        if (_session is not { } session || ViewModel is null || Bar is null)
        {
            return;
        }

        if (save && session.Editor.StepCount > 0)
        {
            var (baseLayout, device) = session.Editor.Result;
            try
            {
                await _manager.SaveAsync(baseLayout, device);
            }
            catch (Exception ex)
            {
                _log.Error(null, "Layout could not be saved", ex);
                UeDialog.Error(null, Loc.F("Layout_SaveFailed", ex.Message));
                return; // stay in edit mode, nothing gets lost
            }
        }

        _log.Info(null, save ? $"Editing finished: saved ({session.Editor.StepCount} steps)" : "Editing cancelled – changes discarded");
        if (_reloadPending)
        {
            _log.Info(null, "layout.json was changed from outside while editing – the saved state applies");
        }

        _session = null;
        ViewModel.EndEdit();
        Bar.EndEditHold();
        _reloadPending = false;
        await ReloadAsync();
        ReleaseEditMemory();
    }

    /// <summary>
    /// Edit mode creates many short-lived objects (editor copies, tile models,
    /// controls) that hold native WPF memory until the GC collects them. After
    /// editing (rare, triggered by the user) clean up thoroughly once and return memory to
    /// Windows – otherwise the process stays noticeably larger for a long time (RAM limit).
    /// </summary>
    private void ReleaseEditMemory() =>
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var before = Environment.WorkingSet;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            _log.Debug(null, $"Cleaned up after editing: working set {before / 1024 / 1024} → {Environment.WorkingSet / 1024 / 1024} MB");
        });

    // ------------------------------------------------------------------ Import / export

    public async void Export()
    {
        // async void only as a menu handler; errors are caught below.
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("Layout_ExportTitle"),
                Filter = Loc.Get("Layout_FileFilter"),
                FileName = $"ue-bar-layout-{DateTime.Now:yyyy-MM-dd}.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await _manager.ExportAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Export failed", ex);
            UeDialog.Error(null, Loc.F("Layout_ExportFailed", ex.Message));
        }
    }

    public async void Import()
    {
        try
        {
            if (_session is not null)
            {
                UeDialog.Info(null, Loc.Get("Layout_FinishEditFirst"));
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("Layout_ImportTitle"),
                Filter = Loc.Get("Layout_FileFilterAll"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            LayoutDocument layout;
            try
            {
                layout = LayoutManager.ParseImport(await File.ReadAllTextAsync(dialog.FileName));
            }
            catch (InvalidDataException ex)
            {
                UeDialog.Warning(null, Loc.F("Layout_ImportInvalid", ex.Message));
                return;
            }

            var unknown = layout.AllTiles
                .Where(t => _registry.Find(t.Module) is null || _registry.FindTileType(t.Type) is null)
                .Select(t => t.Module)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var text = Loc.F("Layout_ImportSummary", Path.GetFileName(dialog.FileName), layout.Tabs.Count, layout.AllTiles.Count());
            if (unknown.Count > 0)
            {
                text += "\n\n" + Loc.F("Layout_ImportUnknown", string.Join(", ", unknown));
            }

            if (!UeDialog.Ask(null, text + "\n\n" + Loc.Get("Layout_ImportNow"), Loc.Get("Layout_ImportTitle")))
            {
                return;
            }

            await _manager.ImportAsync(layout);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.Error(null, "Import failed", ex);
            UeDialog.Error(null, Loc.F("Layout_ImportFailed", ex.Message));
        }
    }
}
