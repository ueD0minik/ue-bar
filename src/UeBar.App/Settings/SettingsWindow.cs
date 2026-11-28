// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UeBar.App.Infrastructure;
using UeBar.App.Bar;
using UeBar.App.Bar.Converters;
using UeBar.App.Bar.Edit;
using UeBar.Bar;
using UeBar.App.Localization;
using UeBar.Core;
using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Localization;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Core.Bus;
using UeBar.Core.Plugins;
using UeBar.Core.Settings;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Settings;

/// <summary>What the settings window needs (M7). Everything on the UI thread.</summary>
internal sealed class SettingsHost
{
    public required SettingsService Settings { get; init; }

    public required GeneralSettingsStore General { get; init; }

    public required BarController Bar { get; init; }

    /// <summary>P2-7: "Hide from screen captures".</summary>
    public required UeBar.App.Infrastructure.CaptureProtection Privacy { get; init; }

    public required ModuleRegistry Registry { get; init; }

    /// <summary>Module bus: what the plugins publish (page "May use", P2-3).</summary>
    public required ModuleBus Bus { get; init; }

    public required ModuleLayoutStoreSource LayoutSource { get; init; }

    public required SyncedLayoutStore LayoutStore { get; init; }

    public required LayoutCoordinator Layout { get; init; }

    public required AppPaths Paths { get; init; }

    public required IUeLogger Log { get; init; }

    /// <summary>Plugins found at the start, and adding/removing them (SPEC 4.4).</summary>
    public required PluginHost Plugins { get; init; }

    /// <summary>Pins on the screen (P2-5).</summary>
    public UeBar.App.Pins.PinController? Pins { get; init; }

    /// <summary>Global hotkeys besides the quick search (P2-5) – paused while recording.</summary>
    public Infrastructure.GlobalHotkeys? Hotkeys { get; init; }

    /// <summary>Hotkeys of plugins (P2-5).</summary>
    public UeBar.App.Plugins.PluginHotkeys? PluginHotkeys { get; init; }

    /// <summary>Hotkey "Send marked text to …" (P3-1).</summary>
    public UeBar.App.ContentActions.SelectionAction? Selection { get; init; }

    /// <summary>Ends this process and starts ue Bar again (plugin changes need it).</summary>
    public required Func<Task> Restart { get; init; }

    public required Func<Task> ResetBar { get; init; }

    public required Action ClearNotificationHistory { get; init; }

    public required Func<int> NotificationHistoryCount { get; init; }

    /// <summary>Now Playing across the modules: order of the sources (P2-2).</summary>
    public required NowPlayingService NowPlaying { get; init; }

    /// <summary>Device name/accent/columns/language changed → the app applies it.</summary>
    public required Action<GeneralSettings, GeneralSettings> GeneralChanged { get; init; }

    /// <summary>"Show first start again": open the panel and show the choice.</summary>
    public required Action ShowSetupAgain { get; init; }
}

/// <summary>
/// Settings window (SPEC 11, M7) in One UI style: sidebar on the left, page on the right. Every change
/// takes effect right away and is saved. Module pages are created automatically from <see cref="IModuleSettings"/>.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly SettingsHost _host;
    private readonly StackPanel _sidebar = new() { Margin = new Thickness(12, 16, 12, 16) };
    // Not focusable: otherwise the ScrollViewer takes the keyboard focus on every click (M7-Fix 1).
    private readonly ScrollViewer _pageHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly List<(string Key, Button Button)> _navButtons = [];
    private readonly DispatcherTimer _refresh;
    private string _currentPage = "general";
    private Action? _onRefresh;

    private SettingsWindow(SettingsHost host)
    {
        _host = host;
        Title = Loc.Get("Settings_WindowTitle");
        Width = 900;
        Height = 680;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = (Color)Application.Current.FindResource("PanelColor");
        panel.A = 255;
        Background = new SolidColorBrush(panel);
        Foreground = Res("TextBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("BarFont");
        FontSize = 13;
        UseLayoutRounding = true;
        Icon = AppIcon.ImageSource;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var side = new Border { Background = new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1E)), Child = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        root.Children.Add(side);
        Grid.SetColumn(_pageHost, 1);
        root.Children.Add(_pageHost);
        Content = root;

        // Dark, narrow scroll bars instead of the light Windows default bar.
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Application.Current.FindResource("DarkScrollBar"));

        BuildSidebar();
        ShowPage("general");

        // Refresh the status (modules, layout sync) every few seconds – only while the window is open.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refresh.Tick += (_, _) => _onRefresh?.Invoke();
        _refresh.Start();
        _host.Registry.ModuleStatusChanged += OnModuleStatusChanged;
        _host.Settings.BusPolicy.RequestsChanged += OnBusRequestsChanged;
        UiCultureSource.Shared.Changed += OnLanguageChanged;
        _host.Privacy.Changed += OnPrivacyChanged;
        SourceInitialized += (_, _) => DarkChrome(this);
        Closed += (_, _) =>
        {
            _refresh.Stop();
            _host.Privacy.Changed -= OnPrivacyChanged;
            _host.Registry.ModuleStatusChanged -= OnModuleStatusChanged;
            _host.Settings.BusPolicy.RequestsChanged -= OnBusRequestsChanged;
            UiCultureSource.Shared.Changed -= OnLanguageChanged;
            SuspendAllHotkeys(false);
            _open = null;
        };
    }

    /// <summary>Opens the window (or brings an open one to the front), optionally on a page.</summary>
    public static void Show(SettingsHost host, string? page = null)
    {
        if (_open is null)
        {
            _open = new SettingsWindow(host);
            _open.Show();
        }

        if (page is not null)
        {
            _open.ShowPage(page);
        }

        if (_open.WindowState == WindowState.Minimized)
        {
            _open.WindowState = WindowState.Normal;
        }

        _open.Activate();
    }

    private void OnModuleStatusChanged(object? sender, ModuleEntry e) => Dispatcher.BeginInvoke(() => _onRefresh?.Invoke());

    /// <summary>A plugin asked for a permission: show the hint on the pages that show requests.</summary>
    private void OnBusRequestsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_currentPage == "plugins" || _currentPage.StartsWith("module:", StringComparison.Ordinal))
        {
            ShowPage(_currentPage);
        }
    });

    /// <summary>Language switched (R1): rebuild title, sidebar and the current page in the new language.</summary>
    private void OnLanguageChanged(object? sender, System.Globalization.CultureInfo culture) => Dispatcher.BeginInvoke(() =>
    {
        Title = Loc.Get("Settings_WindowTitle");
        BuildSidebar();
        ShowPage(_currentPage);
    });

    // ------------------------------------------------------------------ Navigation

    private void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _navButtons.Clear();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 18) };
        title.Children.Add(new Image { Source = AppIcon.ImageSource, Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) });
        title.Children.Add(new TextBlock { Text = Loc.Get("Settings_Title"), FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _sidebar.Children.Add(title);

        Nav("general", Loc.Get("Settings_General"), "");
        Nav("bar", Loc.Get("Settings_Bar"), "");
        Nav("oled", Loc.Get("Settings_Oled"), "");
        Nav("notifications", Loc.Get("Settings_Notifications"), "");
        Nav("pins", Loc.Get("Settings_Pins"), "");
        Nav("layout", Loc.Get("Settings_Layout"), "");
        Nav("plugins", Loc.Get("Settings_Plugins"), "");
        foreach (var module in _host.Registry.Modules.Where(m => m.IsLoaded))
        {
            Nav("module:" + module.Id, module.DisplayName, IconGlyphs.Get(module.Icon), indent: true);
        }

        Nav("info", Loc.Get("Settings_Info"), "");
    }

    private void Nav(string key, string text, string? icon, bool indent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Style = StyleOf("IconText"),
            FontSize = 14,
            Text = icon ?? "",
            Width = 22,
            Margin = new Thickness(0, 0, 8, 0),
        });
        content.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Style = StyleOf("BarButton"),
            Content = content,
            Height = 38,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(indent ? 34 : 12, 0, 12, 0),
            Margin = new Thickness(0, 0, 0, 4),
            Background = Brushes.Transparent,
            Focusable = true,
        };
        // BarButton centers the content – with a fixed width it sits on the left in the sidebar.
        content.Width = indent ? 160 : 182;
        button.Click += (_, _) => ShowPage(key);
        _navButtons.Add((key, button));
        _sidebar.Children.Add(button);
    }

    private void ShowPage(string key)
    {
        _currentPage = key;
        _onRefresh = null;
        _privacySwitch = null;
        SuspendAllHotkeys(false); // a recording on the previous page ends here

        foreach (var (k, b) in _navButtons)
        {
            if (k == key)
            {
                b.SetResourceReference(BackgroundProperty, "AccentBrush");
            }
            else
            {
                b.Background = Brushes.Transparent;
            }
        }

        var page = new StackPanel { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        try
        {
            switch (key)
            {
                case "general":
                    BuildGeneral(page);
                    break;
                case "bar":
                    BuildBar(page);
                    break;
                case "oled":
                    BuildOled(page);
                    break;
                case "notifications":
                    BuildNotifications(page);
                    break;
                case "pins":
                    BuildPins(page);
                    break;
                case "layout":
                    BuildLayout(page);
                    break;
                case "plugins":
                    BuildPlugins(page);
                    break;
                case "info":
                    BuildInfo(page);
                    break;
                default:
                    if (key.StartsWith("module:", StringComparison.Ordinal) && _host.Registry.Find(key["module:".Length..]) is { } module)
                    {
                        _ = BuildModuleAsync(page, module);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _host.Log.Error(null, $"Settings page {key} could not be built", ex);
            page.Children.Add(ResultLine(Loc.F("Settings_PageFailed", ex.Message), false));
        }

        _pageHost.Content = page;
        _pageHost.ScrollToTop();
    }

    // ------------------------------------------------------------------ General

    private void BuildGeneral(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_General")));
        var g = _host.General.Current;

        FrameworkElement autostart;
        if (AppInfo.IsPortable)
        {
            autostart = SwitchRow(Loc.Get("General_Autostart"), Loc.Get("General_Autostart_Portable"), Autostart.IsEnabled, SetAutostart);
        }
        else
        {
            autostart = SwitchRow(Loc.Get("General_Autostart"), Loc.Get("General_Autostart_Hint"), Autostart.IsEnabled, SetAutostart);
        }

        var accent = new StackPanel();
        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        var current = AccentColors.Normalize(g.AccentColor) ?? AccentColors.Default;
        foreach (var (name, hex) in AccentColors.Suggestions)
        {
            var value = hex;
            swatches.Children.Add(Swatch(hex, name, hex == current, () => SetAccent(value)));
        }

        accent.Children.Add(swatches);
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal };
        hexRow.Children.Add(TextField(current, "#3E91FF", text =>
        {
            if (AccentColors.Normalize(text) is { } valid)
            {
                SetAccent(valid);
            }
            else
            {
                UeDialog.Info(this, Loc.F("General_NotAColor", text));
            }
        }, width: 140));
        hexRow.Children.Add(Hint(Loc.Get("General_OwnColor"), 0));
        ((FrameworkElement)hexRow.Children[1]).Margin = new Thickness(10, 0, 0, 0);
        ((FrameworkElement)hexRow.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        accent.Children.Add(hexRow);

        var language = UiLanguages.Normalize(g.Language);
        var languages = new List<(string, string)>
        {
            (Loc.F("Language_SystemWith", Loc.SystemLanguageName), UiLanguages.System),
            ("Deutsch", UiLanguages.German),
            ("English", UiLanguages.English),
        };

        page.Children.Add(Card(
            autostart,
            Row(Loc.Get("General_Language"), Loc.Get("General_Language_Hint"),
                Combo(languages, language, v => UpdateGeneral(s => s.Language = v == UiLanguages.System ? null : v), width: 220)),
            Row(Loc.Get("General_DeviceName"), Loc.F("General_DeviceName_Hint", Environment.MachineName),
                TextField(g.DeviceName, Environment.MachineName, text => UpdateGeneral(s => s.DeviceName = string.IsNullOrWhiteSpace(text) ? null : text), width: 220)),
            Row(Loc.Get("General_Accent"), null, accent, controlBelow: true),
            Row(Loc.Get("General_Columns"), Loc.Get("General_Columns_Hint"),
                Combo(Enumerable.Range(GeneralSettings.MinColumns, GeneralSettings.MaxColumns - GeneralSettings.MinColumns + 1).Select(n => (Loc.F("General_ColumnsN", n), n)), g.EffectiveColumns,
                    n => UpdateGeneral(s => s.Columns = n), width: 160)),
            SwitchRow(Loc.Get("General_ReduceAnimations"), Loc.Get("General_ReduceAnimations_Hint"), _host.Bar.Settings.ReduceAnimations, v => _host.Bar.Update(s => s.ReduceAnimations = v))));

        // P2-7: privacy – hide every window of ue Bar from screen captures.
        page.Children.Add(SubHeading(Loc.Get("Privacy_Heading")));
        var privacy = _host.Privacy;
        var description = Loc.Get("Privacy_Switch_Hint");
        if (!privacy.IsAvailable)
        {
            description = Loc.Get("Privacy_NotAvailable");
        }
        else if (privacy.Mode == CaptureShieldMode.Black)
        {
            description += " " + Loc.Get("Privacy_Black_Hint");
        }

        var capture = SwitchRow(Loc.Get("Privacy_Switch"), description, privacy.IsHidden, v => privacy.Set(v, "settings"));
        capture.IsEnabled = privacy.IsAvailable;
        _privacySwitch = capture as CheckBox;
        page.Children.Add(Card(capture, Hint(Loc.Get("Privacy_Switch_Limits"))));

        // P3-1: content actions of the plugins for the marked text of any window.
        if (_host.Selection is { } selection)
        {
            page.Children.Add(SubHeading(Loc.Get("Selection_Heading")));
            var binding = new HotkeyBinding(() => selection.CurrentHotkey, () => selection.HotkeyStatus, selection.ChangeHotkey, GeneralSettings.DefaultSelectionHotkey) { AllowOff = true };
            page.Children.Add(Card(
                Row(Loc.Get("Selection_Hotkey"), Loc.F("Selection_Hotkey_Hint", Hotkey.TryParse(GeneralSettings.DefaultSelectionHotkey)?.DisplayText ?? GeneralSettings.DefaultSelectionHotkey),
                    HotkeyRecorderUi.Create(this, binding, SuspendAllHotkeys, _host.Log), controlBelow: true)));
        }
    }

    /// <summary>The switch on the General page – follows a switch from the tray, the quick command or the bus.</summary>
    private CheckBox? _privacySwitch;

    private void OnPrivacyChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_privacySwitch is { } box && box.IsChecked != _host.Privacy.IsHidden)
        {
            box.IsChecked = _host.Privacy.IsHidden;
        }
    });

    private void SetAutostart(bool enabled)
    {
        try
        {
            Autostart.Set(enabled, _host.Log);
        }
        catch (Exception ex)
        {
            UeDialog.Warning(this, Loc.F("General_AutostartFailed", ex.Message));
        }
    }

    private void SetAccent(string hex)
    {
        UpdateGeneral(s => s.AccentColor = hex);
        ShowPage("general"); // mark the selection again
    }

    private void UpdateGeneral(Action<GeneralSettings> change)
    {
        var before = _host.General.Current;
        _host.General.Update(change);
        _host.GeneralChanged(before, _host.General.Current);
    }

    // ------------------------------------------------------------------ Bar

    private void BuildBar(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Bar")));
        var s = _host.Bar.Settings;

        var monitors = new List<(string, string)> { (Loc.Get("Bar_MonitorPrimary"), "primary"), (Loc.Get("Bar_MonitorMouse"), "mouse") };
        foreach (var m in _host.Bar.Monitors)
        {
            monitors.Add(($"{m.Name}{(m.IsPrimary ? " " + Loc.Get("Bar_PrimarySuffix") : "")} – {m.Bounds.Width}×{m.Bounds.Height}", "id:" + m.Id));
        }

        var selectedMonitor = s.MonitorMode switch
        {
            MonitorMode.UnderMouse => "mouse",
            MonitorMode.Specific => "id:" + s.MonitorId,
            _ => "primary",
        };
        if (!monitors.Any(m => m.Item2 == selectedMonitor))
        {
            monitors.Add((Loc.Get("Bar_MonitorMissing"), selectedMonitor));
        }

        page.Children.Add(Card(
            Row(Loc.Get("Bar_Monitor"), Loc.Get("Bar_Monitor_Hint"), Combo(monitors, selectedMonitor, v =>
            {
                if (v == "primary")
                {
                    _host.Bar.SetMonitor(MonitorMode.Primary, null);
                }
                else if (v == "mouse")
                {
                    _host.Bar.SetMonitor(MonitorMode.UnderMouse, null);
                }
                else if (v.StartsWith("id:", StringComparison.Ordinal))
                {
                    _host.Bar.SetMonitor(MonitorMode.Specific, v[3..]);
                }
            }, width: 300)),
            Row(Loc.Get("Bar_TriggerWidth"), Loc.Get("Bar_TriggerWidth_Hint"),
                Slider(20, 100, 5, s.TriggerWidthPercent, v => $"{v:0} %", v => _host.Bar.Update(b => b.TriggerWidthPercent = v))),
            Row(Loc.Get("Bar_Dwell"), Loc.Get("Bar_Dwell_Hint"),
                Slider(0, 1000, 50, s.DwellMs, v => $"{v:0} ms", v => _host.Bar.Update(b => b.DwellMs = (int)v))),
            Row(Loc.Get("Bar_HideDelay"), Loc.Get("Bar_HideDelay_Hint"),
                Slider(0, 5000, 100, s.HideDelayMs, v => (v / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " s", v => _host.Bar.Update(b => b.HideDelayMs = (int)v))),
            SwitchRow(Loc.Get("Bar_BlockFullscreen"), Loc.Get("Bar_BlockFullscreen_Hint"), s.BlockInFullscreen, v => _host.Bar.Update(b => b.BlockInFullscreen = v)),
            SwitchRow(Loc.Get("Bar_RespectDnd"), Loc.Get("Bar_RespectDnd_Hint"), s.RespectDoNotDisturb, v => _host.Bar.Update(b => b.RespectDoNotDisturb = v)),
            SwitchRow(Loc.Get("Tray_Pause"), Loc.Get("Bar_Pause_Hint"), s.Paused, v => _host.Bar.SetPaused(v))));

        page.Children.Add(Card(
            Row(Loc.Get("Hotkey_Title"), Loc.F("Hotkey_Hint", DefaultHotkeyText), HotkeyRecorder(), controlBelow: true)));

        page.Children.Add(Card(
            SwitchRow(Loc.Get("Bar_SoftwareRendering"), Loc.Get("Bar_SoftwareRendering_Hint"), s.SoftwareRendering,
                v => _host.Bar.Update(b => b.SoftwareRendering = v)),
            Row(Loc.Get("Bar_Unpin"), Loc.Get("Bar_Unpin_Hint"), Button(Loc.Get("Panel_Unpin"), () => _host.Bar.Unpin()))));

        page.Children.Add(NowPlayingOrderCard());
    }

    /// <summary>
    /// Now Playing with several sources (P2-2): order of the modules, moved with up/down. What is
    /// playing always wins over what is paused; the order decides between sources that both play.
    /// </summary>
    private Border NowPlayingOrderCard()
    {
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var card = Card(Row(Loc.Get("NowPlaying_Order"), Loc.Get("NowPlaying_Order_Hint"), list, controlBelow: true));

        void Fill()
        {
            list.Children.Clear();
            var sources = _host.NowPlaying.OrderedSources();
            if (sources.Count == 0)
            {
                list.Children.Add(Hint(Loc.Get("NowPlaying_NoSources")));
                return;
            }

            for (var i = 0; i < sources.Count; i++)
            {
                var index = i;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
                var up = SmallIconButton("", Loc.Get("NowPlaying_Up"), i > 0, () => Move(index, -1));
                var down = SmallIconButton("", Loc.Get("NowPlaying_Down"), i < sources.Count - 1, () => Move(index, +1));
                down.Margin = new Thickness(4, 0, 0, 0);
                DockPanel.SetDock(down, Dock.Right);
                DockPanel.SetDock(up, Dock.Right);
                row.Children.Add(down);
                row.Children.Add(up);
                var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(new TextBlock { Style = StyleOf("IconText"), FontSize = 14, Text = IconGlyphs.Get(sources[i].Icon), Width = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(new TextBlock { Text = $"{i + 1}. {sources[i].DisplayName}", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(name);
                list.Children.Add(row);
            }

            void Move(int from, int delta)
            {
                var ids = sources.Select(s => s.Id).ToList();
                var to = Math.Clamp(from + delta, 0, ids.Count - 1);
                (ids[from], ids[to]) = (ids[to], ids[from]);
                _host.Bar.Update(b => b.NowPlayingOrder = ids);
                _host.NowPlaying.Refresh();
                Fill();
            }
        }

        Fill();
        return card;

        static Button SmallIconButton(string glyph, string tooltip, bool enabled, Action click)
        {
            var button = new Button
            {
                Style = StyleOf("BarButton"),
                Content = new TextBlock { Style = StyleOf("IconText"), FontSize = 12, Text = glyph },
                Width = 34,
                Height = 30,
                ToolTip = tooltip,
                IsEnabled = enabled,
                Background = Res("TabInactiveBrush"),
            };
            button.Click += (_, _) => click();
            return button;
        }
    }

    private static string DefaultHotkeyText => Hotkey.TryParse(HotkeyRecording.DefaultHotkey)?.DisplayText ?? HotkeyRecording.DefaultHotkey;

    /// <summary>The quick search's hotkey (M7-Fix 1) through the shared recording field.</summary>
    private FrameworkElement HotkeyRecorder() => HotkeyRecorderUi.Create(this, new HotkeyBinding(
        () => _host.Bar.Settings.QuickSearchHotkey,
        () => _host.Bar.HotkeyStatus,
        text => _host.Bar.ChangeHotkey(text),
        HotkeyRecording.DefaultHotkey), SuspendAllHotkeys, _host.Log);

    /// <summary>While recording: release every own global hotkey, otherwise Windows catches it before the window does.</summary>
    private void SuspendAllHotkeys(bool suspend)
    {
        _host.Bar.SuspendHotkey(suspend);
        _host.Hotkeys?.Set.Suspend(suspend);
    }

    // ------------------------------------------------------------------ OLED

    private void BuildOled(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Oled")));
        var s = _host.Bar.Settings;
        int[] minutes = [0, 1, 2, 5, 10, 15, 30];
        page.Children.Add(Card(
            Row(Loc.Get("Oled_Dim"), Loc.Get("Oled_Dim_Hint"),
                Combo(minutes.Select(m => (m == 0 ? Loc.Get("Oled_Never") : m == 1 ? Loc.Get("Oled_OneMinute") : Loc.F("Oled_Minutes", m), m)), s.OledDimMinutes, m => _host.Bar.Update(b => b.OledDimMinutes = m), width: 160)),
            SwitchRow(Loc.Get("Oled_PixelShift"), Loc.Get("Oled_PixelShift_Hint"), s.OledPixelShift, v => _host.Bar.Update(b => b.OledPixelShift = v))));
        page.Children.Add(Hint(Loc.Get("Oled_Tip")));
    }

    // ------------------------------------------------------------------ Notifications

    private void BuildNotifications(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Notifications")));
        var s = _host.Bar.Settings;
        var count = Hint("");
        void UpdateCount() => count.Text = _host.NotificationHistoryCount() switch
        {
            0 => Loc.Get("Notify_HistoryEmpty"),
            1 => Loc.Get("Notify_HistoryOne"),
            var n => Loc.F("Notify_HistoryMany", n),
        };
        UpdateCount();
        var clear = Button(Loc.Get("Notify_ClearHistory"), () =>
        {
            _host.ClearNotificationHistory();
            UpdateCount();
        }, icon: "");
        page.Children.Add(Card(
            Row(Loc.Get("Notify_Duration"), Loc.Get("Notify_Duration_Hint"),
                Slider(2, 30, 1, s.NotificationSeconds, v => $"{v:0} s", v => _host.Bar.Update(b => b.NotificationSeconds = (int)v))),
            SwitchRow(Loc.Get("Bar_RespectDnd"), Loc.Get("Notify_RespectDnd_Hint"), s.RespectDoNotDisturb, v => _host.Bar.Update(b => b.RespectDoNotDisturb = v)),
            Row(Loc.Get("Notify_History"), null, clear, controlBelow: false)));
        page.Children.Add(count);

        // P2-4: what important notifications (e.g. a timer that ran out) may do.
        page.Children.Add(SubHeading(Loc.Get("Notify_Important")));
        page.Children.Add(Card(
            SwitchRow(Loc.Get("Notify_ImportantPaused"), Loc.Get("Notify_ImportantPaused_Hint"), s.ImportantWhenPaused, v => _host.Bar.Update(b => b.ImportantWhenPaused = v)),
            SwitchRow(Loc.Get("Notify_ImportantDnd"), null, s.ImportantDuringDoNotDisturb, v => _host.Bar.Update(b => b.ImportantDuringDoNotDisturb = v)),
            SwitchRow(Loc.Get("Notify_ImportantFullscreen"), null, s.ImportantInFullscreen, v => _host.Bar.Update(b => b.ImportantInFullscreen = v))));
        page.Children.Add(SubHeading(Loc.Get("Notify_Feedback")));
        page.Children.Add(Card(
            SwitchRow(Loc.Get("Notify_FeedbackDnd"), Loc.Get("Notify_FeedbackDnd_Hint"), s.ActionFeedbackDuringDoNotDisturb, v => _host.Bar.Update(b => b.ActionFeedbackDuringDoNotDisturb = v))));
        _onRefresh = UpdateCount;
    }

    // ------------------------------------------------------------------ Layout

    private void BuildLayout(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Layout")));
        var g = _host.General.Current;
        var choices = new List<(string, string?)> { (Loc.Get("Layout_StoreAuto"), null), (Loc.Get("Layout_StoreLocal"), ModuleLayoutStoreSource.LocalOnly) };
        foreach (var provider in _host.LayoutSource.Providers)
        {
            choices.Add(($"{provider.DisplayName}", provider.Id));
        }

        var status = Hint("");
        void UpdateStatus()
        {
            var sync = _host.LayoutStore.Status;
            var when = sync.LastSyncedAt is { } at ? at.ToLocalTime().ToString("g", Loc.FormatCulture) : "–";
            status.Text = Loc.F("Layout_StoreNow", sync.StoreName, sync.Pending ? Loc.Get("Layout_PendingUpload") : Loc.F("Layout_Synced", when))
                + (sync.LastProblem is { Length: > 0 } problem ? $" · {problem}" : "");
        }

        UpdateStatus();
        _onRefresh = UpdateStatus;
        var storeRow = new StackPanel();
        storeRow.Children.Add(Combo(choices, string.IsNullOrWhiteSpace(g.LayoutStore) ? null : g.LayoutStore, v =>
        {
            UpdateGeneral(s => s.LayoutStore = v);
            _host.LayoutSource.Preference = v;
            UpdateStatus();
        }, width: 260));
        status.Margin = new Thickness(0, 8, 0, 0);
        storeRow.Children.Add(status);

        page.Children.Add(Card(
            Row(Loc.Get("Layout_Store"), Loc.Get("Layout_Store_Hint"), storeRow, controlBelow: true)));
        page.Children.Add(Card(
            Row(Loc.Get("Layout_Transfer"), Loc.Get("Layout_Transfer_Hint"),
                Buttons(Button(Loc.Get("Tray_Export"), () => _host.Layout.Export(), icon: ""), Button(Loc.Get("Tray_Import"), () => _host.Layout.Import(), icon: "")), controlBelow: true),
            Row(Loc.Get("Layout_SetupAgain"), Loc.Get("Layout_SetupAgain_Hint"),
                Button(Loc.Get("Layout_Show"), () => _host.ShowSetupAgain()), controlBelow: false)));

        // P2-2: add the tabs a plugin suggests without replacing the layout.
        var addable = LayoutSetup.AddableSuggestions(_host.Registry);
        if (addable.Count > 0)
        {
            var chosen = addable[0].Id;
            var result = Hint("");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Combo(addable.Select(m => (m.DisplayName, m.Id)), chosen, v => chosen = v, width: 220));
            async Task AddAsync()
            {
                var name = addable.FirstOrDefault(m => m.Id == chosen)?.DisplayName ?? chosen;
                try
                {
                    var added = await _host.Layout.AddSuggestionAsync(chosen);
                    result.Text = added > 0 ? Loc.F("Layout_AddSuggestion_Done", name) : Loc.Get("Layout_AddSuggestion_Nothing");
                }
                catch (Exception ex)
                {
                    _host.Log.Error(chosen, "Adding the suggestion failed", ex);
                    result.Text = Loc.F("Layout_SaveFailed", ex.Message);
                }
            }

            var add = Button(Loc.Get("Layout_AddSuggestion_Button"), () => _ = AddAsync(), icon: "");
            add.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(add);
            var content = new StackPanel();
            content.Children.Add(row);
            result.Margin = new Thickness(2, 6, 0, 0);
            content.Children.Add(result);
            page.Children.Add(Card(Row(Loc.Get("Layout_AddSuggestion"), Loc.Get("Layout_AddSuggestion_Hint"), content, controlBelow: true)));
        }
    }

    // ------------------------------------------------------------------ Module pages (the list of plugins: SettingsWindow.Plugins.cs)

    private async Task BuildModuleAsync(StackPanel page, ModuleEntry module)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 14) };
        title.Children.Add(new TextBlock { Text = module.DisplayName, FontSize = 22, FontWeight = FontWeights.SemiBold });
        if (module.IsDeveloper)
        {
            title.Children.Add(Badge(Loc.Get("Modules_Developer")));
        }

        page.Children.Add(title);

        var statusText = Hint("");
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(dot);
        statusRow.Children.Add(statusText);
        void UpdateStatus()
        {
            var entry = _host.Registry.Find(module.Id) ?? module;
            dot.Fill = StateBrush(entry.Status.State);
            statusText.Text = entry.Status.DisplayText;
        }

        UpdateStatus();
        page.Children.Add(Card(
            Row(Loc.Get("Modules_Status"), null, statusRow),
            SwitchRow(Loc.Get("Modules_Enabled"), module.IsDeveloper ? Loc.Get("Modules_PluginHint") : null, module.IsEnabled,
                async v => await _host.Registry.SetEnabledAsync(module.Id, v)),
            Row(Loc.Get("Modules_Restart"), Loc.Get("Modules_Restart_Hint"), Button(Loc.Get("Modules_Restart"), async () => await _host.Registry.RestartModuleAsync(module.Id), icon: ""))));

        // Settings from IModuleSettings
        if (module.Module is IModuleSettings)
        {
            var fields = await _host.Registry.InvokeAsync(module.Id, "Read setting fields", (m, _) =>
                Task.FromResult(m is IModuleSettings s ? s.SettingFields : (IReadOnlyList<SettingField>)[]));
            if (fields is { Succeeded: true, Value: { Count: > 0 } list })
            {
                var editor = new ModuleFieldEditor(_host.Registry, _host.Settings, _host.Log, module, list, this)
                {
                    Hotkeys = _host.PluginHotkeys,
                    SuspendHotkeys = SuspendAllHotkeys,
                    Layout = _host.Layout,
                };
                page.Children.Add(editor.Build());
            }
            else if (!fields.Succeeded)
            {
                page.Children.Add(Card(ResultLine(Loc.Get("Modules_NotResponding"), false)));
            }
        }

        // What the plugin may use of the others (module bus, P2-3)
        AddBusPermissions(page, module);

        // Diagnostics (replaces the tray diagnostics)
        if (module.Module is IModuleDiagnostics)
        {
            var lines = new StackPanel();
            void FillDiagnostics()
            {
                lines.Children.Clear();
                var result = _host.Registry.Invoke(module.Id, "Diagnostics", m => ((IModuleDiagnostics)m).GetDiagnostics());
                foreach (var line in result.Succeeded && result.Value is { Count: > 0 } l ? l : [Loc.Get("Modules_NoDiagnostics")])
                {
                    lines.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 2), Foreground = Res("SubTextBrush") });
                }
            }

            FillDiagnostics();
            page.Children.Add(Card(Row(Loc.Get("Modules_Diagnostics"), Loc.Get("Modules_Diagnostics_Hint"), lines, controlBelow: true)));
            _onRefresh = () =>
            {
                UpdateStatus();
                FillDiagnostics();
            };
        }
        else
        {
            _onRefresh = UpdateStatus;
        }
    }

    // ------------------------------------------------------------------ Info

    private void BuildInfo(StackPanel page)
    {
        page.Children.Add(Heading(Loc.Get("Settings_Info")));
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image { Source = AppIcon.ImageSource, Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "ue Bar", FontSize = 20, FontWeight = FontWeights.SemiBold });
        name.Children.Add(Hint(Loc.F("Info_Version", AppInfo.Version, AppInfo.IsPortable ? Loc.Get("Info_Portable") : Loc.Get("Info_Installed"), AppInfo.BuildVariant, CoreApiVersion.Text)));
        header.Children.Add(name);

        page.Children.Add(Card(
            header,
            Row(Loc.Get("Info_Data"), _host.Paths.Root, Buttons(
                Button(Loc.Get("Info_OpenLogs"), () => OpenFolder(_host.Paths.Logs), icon: ""),
                Button(Loc.Get("Info_OpenData"), () => OpenFolder(_host.Paths.Root), icon: "")), controlBelow: true),
            Row(Loc.Get("Info_BarStuck"), Loc.Get("Info_BarStuck_Hint"), Button(Loc.Get("Tray_Reset"), () => _ = _host.ResetBar(), icon: ""))));

        // License and source code (GPL-3.0 "Appropriate Legal Notices": copyright, no warranty, where the license is).
        var license = new StackPanel();
        license.Children.Add(new TextBlock { Text = AppInfo.Copyright, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        license.Children.Add(Hint(Loc.Get("Info_LicenseLine"), 0));
        license.Children.Add(Hint(Loc.Get("Info_Warranty"), 6));
        var hasRepository = !string.IsNullOrWhiteSpace(AppInfo.RepositoryUrl);
        var repository = Button(Loc.Get("Info_OpenRepository"), () => OpenLink(AppInfo.RepositoryUrl), icon: "");
        repository.IsEnabled = hasRepository;
        var links = Buttons(Button(Loc.Get("Info_ReadLicense"), () => OpenLink(AppInfo.LicenseUrl), icon: ""), repository);
        links.Margin = new Thickness(0, 10, 0, 0);
        license.Children.Add(links);
        page.Children.Add(Card(
            Row(Loc.Get("Info_LicenseTitle"), null, license, controlBelow: true),
            Row(Loc.Get("Info_Source"), AppInfo.RepositoryUrl, new Border(), controlBelow: true)));

        page.Children.Add(Card(Row(Loc.Get("Info_Licenses"), null, new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Res("SubTextBrush"),
            Text = Loc.Get("Info_LicenseText"),
        }, controlBelow: true)));
    }

    private void OpenLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Log.Warn(null, $"Link could not be opened: {ex.Message}");
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Log.Warn(null, $"Folder could not be opened: {ex.Message}");
        }
    }
}
