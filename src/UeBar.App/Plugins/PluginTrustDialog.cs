// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UeBar.App.Localization;
using UeBar.App.Settings;
using UeBar.Core.Plugins;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Plugins;

/// <summary>What the dialog shows about a plugin.</summary>
internal sealed record PluginTrustRequest(string Name, string Author, string Version, string Folder, bool FilesChanged, bool Bundled, bool FromPackage = false)
{
    public static PluginTrustRequest From(PluginInfo plugin) => new(
        plugin.DisplayName,
        plugin.Manifest?.Author ?? "",
        plugin.Manifest?.Version ?? "",
        plugin.Directory,
        plugin.FilesChanged,
        plugin.Origin == PluginOrigin.Bundled);

    public static PluginTrustRequest From(PluginPackage package, string source, bool filesChanged) => new(
        package.Manifest.DisplayName(),
        package.Manifest.Author,
        package.Manifest.Version,
        source,
        filesChanged,
        false,
        FromPackage: true);
}

/// <summary>
/// "Allow this plugin?" (SPEC 4.4): shown before a plugin that does not come with ue Bar is loaded
/// for the first time, and again whenever its files have changed. The safe answer is the default.
/// </summary>
internal sealed class PluginTrustDialog : Window
{
    private bool _allowed;

    private PluginTrustDialog(PluginTrustRequest request)
    {
        Title = Loc.Get("Trust_Title");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        var panel = (Color)Application.Current.FindResource("PanelColor");
        panel.A = 255;
        Background = new SolidColorBrush(panel);
        Foreground = Res("TextBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("BarFont");
        FontSize = 13;
        UseLayoutRounding = true;
        Icon = AppIcon.ImageSource;
        SourceInitialized += (_, _) => DarkChrome(this);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock
        {
            Text = Loc.F("Trust_Heading", request.Name),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 14),
        });

        if (request.FilesChanged)
        {
            root.Children.Add(new TextBlock
            {
                Text = Loc.Get(request.Bundled ? "Trust_ChangedBundled" : "Trust_Changed"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D)),
                Margin = new Thickness(4, 0, 0, 12),
            });
        }

        root.Children.Add(Card(
            Detail(Loc.Get("Trust_Name"), request.Name),
            Detail(Loc.Get("Trust_Author"), request.Author.Length > 0 ? request.Author : Loc.Get("Plugins_UnknownAuthor")),
            Detail(Loc.Get("Trust_Version"), request.Version.Length > 0 ? request.Version : "–"),
            Detail(Loc.Get(request.FromPackage ? "Trust_Source" : "Trust_Folder"), request.Folder)));

        var warning = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x6B, 0x4D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x4D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 18),
            Child = new TextBlock
            {
                Text = Loc.Get("Trust_Warning"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
            },
        };
        root.Children.Add(warning);

        var deny = Button(Loc.Get("Trust_Deny"), () => Close(), accent: true);
        deny.IsDefault = true;
        deny.IsCancel = true;
        var allow = Button(Loc.Get("Trust_Allow"), () =>
        {
            _allowed = true;
            Close();
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        allow.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => Keyboard.Focus(deny);
    }

    /// <summary>Asks the user. true = "Allow". Closing the window or Esc counts as "Do not load".</summary>
    public static bool Ask(Window? owner, PluginTrustRequest request)
    {
        var dialog = new PluginTrustDialog(request);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog._allowed;
    }

    private static Grid Detail(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Res("SubTextBrush") });
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}
