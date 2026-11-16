// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.App.Localization;
using System.Globalization;
using System.Windows.Threading;
using UeBar.App.Bar;
using UeBar.App.Settings;
using UeBar.Bar;
using UeBar.Core.Abstractions;
using UeBar.Core.Modules;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UeBar.App.Tray;

/// <summary>
/// Tray icon (tidied up in M7): Settings · Pause bar · Reset bar · Layout ▸ ·
/// Exit. Diagnostics and module menus moved to the settings window. Double-click opens the
/// settings. The menu is only built when it opens – nothing happens while idle. Texts in the UI language (R1).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ModuleRegistry _registry;
    private readonly BarController _bar;
    private readonly Dispatcher _dispatcher;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly Dictionary<string, ModuleState> _lastStates = new(StringComparer.OrdinalIgnoreCase);

    public TrayIcon(ModuleRegistry registry, BarController bar, Dispatcher dispatcher)
    {
        _registry = registry;
        _bar = bar;
        _dispatcher = dispatcher;
        _icon = AppIcon.TrayIcon();

        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Items.Add("ue Bar"); // placeholder so the menu opens; replaced when opening
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "ue Bar",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => _dispatcher.BeginInvoke(() => OpenSettings?.Invoke());

        foreach (var module in _registry.Modules)
        {
            _lastStates[module.Id] = module.Status.State;
        }

        _registry.ModuleStatusChanged += OnModuleStatusChanged;
    }

    public event EventHandler? ExitRequested;

    /// <summary>Tray → "Settings …" (M7).</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>Tray → "Quick search" – only visible if the hotkey could not be registered.</summary>
    public Action? OpenQuickSearch { get; set; }

    /// <summary>Tray → "Reset bar" (M6-Fix 1, emergency exit without restart).</summary>
    public Func<Task>? ResetBar { get; set; }

    /// <summary>Tray → Layout → "Export …".</summary>
    public Action? ExportLayout { get; set; }

    /// <summary>Tray → Layout → "Import …".</summary>
    public Action? ImportLayout { get; set; }

    /// <summary>State of the layout sync for the display in the menu (M5b).</summary>
    public Func<UeBar.Core.Layout.LayoutSyncStatus>? LayoutStatus { get; set; }

    public void ShowInfo(string title, string text) =>
        _notifyIcon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _registry.ModuleStatusChanged -= OnModuleStatusChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private void OnModuleStatusChanged(object? sender, ModuleEntry module)
    {
        // Comes from any thread.
        var state = module.Status.State;
        var text = module.Status.DisplayText;
        _dispatcher.BeginInvoke(() =>
        {
            var previous = _lastStates.GetValueOrDefault(module.Id);
            _lastStates[module.Id] = state;
            if (state == ModuleState.Error && previous != ModuleState.Error)
            {
                _notifyIcon.ShowBalloonTip(5000, Loc.F("Tray_ModuleError", module.DisplayName), Truncate(text, 200), Forms.ToolTipIcon.Warning);
            }
        });
    }

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();

        var header = new Forms.ToolStripMenuItem($"ue Bar {AppInfo.Version}") { Enabled = false };
        header.Font = new Drawing.Font(header.Font, Drawing.FontStyle.Bold);
        _menu.Items.Add(header);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var settings = new Forms.ToolStripMenuItem(Loc.Get("Tray_Settings"));
        settings.Font = new Drawing.Font(settings.Font, Drawing.FontStyle.Bold); // default (double-click)
        settings.Click += (_, _) => _dispatcher.BeginInvoke(() => OpenSettings?.Invoke());
        _menu.Items.Add(settings);

        var paused = _bar.Settings.Paused;
        var pause = new Forms.ToolStripMenuItem(Loc.Get("Tray_Pause")) { Checked = paused };
        pause.Click += (_, _) => _bar.SetPaused(!paused);
        _menu.Items.Add(pause);

        var reset = new Forms.ToolStripMenuItem(Loc.Get("Tray_Reset")) { ToolTipText = Loc.Get("Tray_Reset_Tip") };
        reset.Click += (_, _) => _dispatcher.BeginInvoke(() => _ = ResetBar?.Invoke());
        _menu.Items.Add(reset);

        _menu.Items.Add(BuildLayoutMenu());

        if (_bar.HotkeyStatus.Result != HotkeyRegistration.Registered)
        {
            var search = new Forms.ToolStripMenuItem(Loc.Get("Tray_QuickSearch")) { ToolTipText = _bar.HotkeyStatus.Message };
            search.Click += (_, _) => _dispatcher.BeginInvoke(() => OpenQuickSearch?.Invoke());
            _menu.Items.Add(search);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem(Loc.Get("Tray_Exit"));
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(exit);

        _menu.ResumeLayout();
    }

    private Forms.ToolStripMenuItem BuildLayoutMenu()
    {
        var layout = new Forms.ToolStripMenuItem("Layout");
        if (LayoutStatus?.Invoke() is { } sync)
        {
            // Display only: where the layout lives and whether changes still need to be uploaded (M5b).
            var when = sync.LastSyncedAt is { } at ? at.ToLocalTime().ToString("g", Loc.FormatCulture) : "–";
            var state = sync.Pending ? Loc.Get("Layout_Pending") : Loc.F("Layout_Synced", when);
            layout.DropDownItems.Add(new Forms.ToolStripMenuItem(Truncate($"{sync.StoreName} · {state}", 120)) { Enabled = false });
            layout.DropDownItems.Add(new Forms.ToolStripSeparator());
        }

        var export = new Forms.ToolStripMenuItem(Loc.Get("Tray_Export"));
        export.Click += (_, _) => ExportLayout?.Invoke();
        layout.DropDownItems.Add(export);
        var import = new Forms.ToolStripMenuItem(Loc.Get("Tray_Import"));
        import.Click += (_, _) => ImportLayout?.Invoke();
        layout.DropDownItems.Add(import);
        return layout;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
