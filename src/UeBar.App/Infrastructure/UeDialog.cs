// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UeBar.App.Localization;
using UeBar.App.Settings;
using UeBar.Bar;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.Infrastructure;

/// <summary>
/// The app's own message and question window in the bar design (P3-1) – instead of the Windows message box, so that
/// "hide from screen captures" also hides it (a normal WPF window of the app gets the protection like every other).
/// Modal, Enter = the focused button, Esc = Cancel/No, Tab between the buttons; texts from the app's resources.
/// Exceptions (Windows' own): file dialogs, SmartScreen, the installer and messages before the settings are loaded
/// (<see cref="EarlyMessage"/>).
/// </summary>
internal sealed class UeDialog : Window
{
    private readonly DialogButtons _buttons;
    private DialogAnswer? _answer;

    internal UeDialog(string message, string? title, DialogKind kind, DialogButtons buttons, bool safeDefault, string? confirmText, string? declineText)
    {
        _buttons = buttons;
        Title = title ?? "ue Bar";
        Width = 460;
        MinHeight = 140;
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

        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new TextBlock
        {
            Style = StyleOf("IconText"),
            Text = DialogRules.Glyph(kind),
            FontSize = 26,
            Margin = new Thickness(0, 2, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = kind switch
            {
                DialogKind.Error => Res("AlertBrush"),
                DialogKind.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D)),
                _ => Res("AccentBrush"),
            },
        };
        root.Children.Add(icon);

        var text = new TextBox
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Res("TextBrush"),
            FontSize = 14,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false, // readable and selectable with the mouse, the keyboard stays on the buttons
            Cursor = Cursors.Arrow,
        };
        Grid.SetColumn(text, 1);
        root.Children.Add(text);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetRow(row, 1);
        Grid.SetColumnSpan(row, 2);
        root.Children.Add(row);

        var focus = DialogRules.Default(buttons, safeDefault);
        Button? focused = null;
        foreach (var answer in DialogRules.Buttons(buttons))
        {
            var label = answer == DialogAnswer.Confirmed ? confirmText : declineText;
            var button = Button(label ?? Loc.Get(LabelKey(buttons, answer)), () => Finish(answer), accent: answer == focus);
            button.Tag = answer;
            button.MinWidth = 96;
            button.Margin = new Thickness(8, 0, 0, 0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            AutomationProperties(button, answer);
            row.Children.Add(button);
            if (answer == focus)
            {
                focused = button;
            }
        }

        Content = root;
        Loaded += (_, _) => Keyboard.Focus(focused);
        PreviewKeyDown += OnKey;
        Closing += (_, _) => _answer ??= DialogRules.OnClose(_buttons);
    }

    public DialogAnswer Answer => _answer ?? DialogRules.OnClose(_buttons);

    /// <summary>Shows the dialog modally; true = OK/Yes. Without a visible owner it appears in the middle of the screen.</summary>
    public static bool Show(Window? owner, string message, string? title = null, DialogKind kind = DialogKind.Info, DialogButtons buttons = DialogButtons.Ok, bool safeDefault = false, string? confirmText = null, string? declineText = null)
    {
        var dialog = new UeDialog(message, title, kind, buttons, safeDefault, confirmText, declineText);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not UeDialog);
        if (owner is { IsVisible: true } && owner.Width > 200)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Answer == DialogAnswer.Confirmed;
    }

    public static void Info(Window? owner, string message, string? title = null) => Show(owner, message, title, DialogKind.Info);

    public static void Warning(Window? owner, string message, string? title = null) => Show(owner, message, title, DialogKind.Warning);

    public static void Error(Window? owner, string message, string? title = null) => Show(owner, message, title, DialogKind.Error);

    /// <summary>A yes/no question; <paramref name="safeDefault"/> = Enter means "No".</summary>
    public static bool Ask(Window? owner, string message, string? title = null, bool safeDefault = false, DialogButtons buttons = DialogButtons.YesNo) =>
        Show(owner, message, title, DialogKind.Question, buttons, safeDefault);

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.Enter => DialogKey.Enter,
            Key.Escape => DialogKey.Escape,
            _ => DialogKey.Other,
        };
        var focused = (Keyboard.FocusedElement as FrameworkElement)?.Tag is DialogAnswer answer ? answer : DialogRules.Default(_buttons, false);
        if (DialogRules.OnKey(key, focused, _buttons) is { } result)
        {
            e.Handled = true;
            Finish(result);
        }
    }

    private void Finish(DialogAnswer answer)
    {
        _answer = answer;
        Close();
    }

    /// <summary>Text keys of the buttons.</summary>
    internal static string LabelKey(DialogButtons buttons, DialogAnswer answer) => (buttons, answer) switch
    {
        (DialogButtons.YesNo, DialogAnswer.Confirmed) => "Dialog_Yes",
        (DialogButtons.YesNo, DialogAnswer.Declined) => "Dialog_No",
        (DialogButtons.OkCancel, DialogAnswer.Declined) => "Dialog_Cancel",
        _ => "Dialog_Ok",
    };

    private static void AutomationProperties(Button button, DialogAnswer answer) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, answer == DialogAnswer.Confirmed ? "DialogConfirm" : "DialogDecline");
}

/// <summary>
/// The Windows message box – only for the moments before the app's settings and resources are ready (another instance
/// hangs, the start failed before the first window). Everything else uses <see cref="UeDialog"/>.
/// </summary>
internal static class EarlyMessage
{
    public static bool Show(string message, bool question = false, bool error = false) =>
        MessageBox.Show(message, "ue Bar", question ? MessageBoxButton.YesNo : MessageBoxButton.OK,
            question ? MessageBoxImage.Warning : error ? MessageBoxImage.Error : MessageBoxImage.Information) is MessageBoxResult.Yes or MessageBoxResult.OK;
}
