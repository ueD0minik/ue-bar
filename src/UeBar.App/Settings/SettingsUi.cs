// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UeBar.App.Settings;

/// <summary>
/// Building blocks for the settings window and the welcome assistant (M7) in One UI style:
/// cards with large corner radii, rows with title/description on the left and the control on the right.
/// Everything takes effect right away – text boxes when leaving or with Enter.
/// </summary>
internal static class SettingsUi
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, 0, 0, 14),
    };

    public static TextBlock Hint(string text, double top = 0) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = Res("SubTextBrush"),
        Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>Card with rows (thin separator lines in between).</summary>
    public static Border Card(params UIElement[] rows)
    {
        var panel = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 10, 0, 10) });
            }

            panel.Children.Add(rows[i]);
        }

        return new Border
        {
            Background = Res("TileOffBrush"),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 14),
            Child = panel,
        };
    }

    /// <summary>Row: title + description on the left, control on the right (or below if wide).</summary>
    public static FrameworkElement Row(string title, string? description, FrameworkElement? control, bool controlBelow = false)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(description))
        {
            text.Children.Add(Hint(description, 2));
        }

        if (control is null)
        {
            return text;
        }

        if (controlBelow)
        {
            var stack = new StackPanel();
            stack.Children.Add(text);
            control.Margin = new Thickness(0, 8, 0, 0);
            control.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(control);
            return stack;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    public static CheckBox Switch(bool value, Action<bool> changed)
    {
        var box = new CheckBox { Style = StyleOf("OneUiSwitch"), IsChecked = value, Focusable = true };
        box.Checked += (_, _) => changed(true); // Checked/Unchecked instead of Click: also works by keyboard and accessibility tools (UI Automation)
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    /// <summary>Switch row (title clickable).</summary>
    public static FrameworkElement SwitchRow(string title, string? description, bool value, Action<bool> changed)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(Hint(description, 2));
        }

        var box = new CheckBox { Style = StyleOf("OneUiSwitch"), IsChecked = value, Content = content, Focusable = true };
        box.Checked += (_, _) => changed(true); // Checked/Unchecked instead of Click: also works by keyboard and accessibility tools (UI Automation)
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    public static ComboBox Combo<T>(IEnumerable<(string Label, T Value)> items, T selected, Action<T> changed, double width = 260)
    {
        var list = items.ToList();
        var combo = new ComboBox { Style = StyleOf("DarkComboBox"), Width = width, Focusable = true };
        foreach (var (label, _) in list)
        {
            combo.Items.Add(new ComboBoxItem { Content = label });
        }

        combo.SelectedIndex = Math.Max(0, list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                changed(list[combo.SelectedIndex].Value);
            }
        };
        return combo;
    }

    /// <summary>Slider with value display; saved only once it rests briefly.</summary>
    public static FrameworkElement Slider(double min, double max, double step, double value, Func<double, string> format, Action<double> changed)
    {
        var label = new TextBlock { Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = format(value), FontSize = 13 };
        var slider = new Slider
        {
            Style = StyleOf("OneUiSlider"),
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            SmallChange = step,
            LargeChange = step,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 220,
            Foreground = Res("AccentBrush"),
        };
        slider.SetResourceReference(Control.ForegroundProperty, "AccentBrush");

        // The One UI slider has no thumb: value directly from the mouse position (click and drag).
        void SetFrom(MouseEventArgs e)
        {
            var ratio = slider.ActualWidth > 0 ? Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0, 1) : 0;
            slider.Value = Math.Clamp(min + (Math.Round((max - min) * ratio / step) * step), min, max);
        }

        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            slider.CaptureMouse();
            SetFrom(e);
            e.Handled = true;
        };
        slider.PreviewMouseMove += (_, e) =>
        {
            if (slider.IsMouseCaptured)
            {
                SetFrom(e);
            }
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => slider.ReleaseMouseCapture();
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            changed(slider.Value);
        };
        slider.ValueChanged += (_, _) =>
        {
            label.Text = format(slider.Value);
            debounce.Stop();
            debounce.Start();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return panel;
    }

    /// <summary>Text box with placeholder; applies when leaving or with Enter.</summary>
    public static FrameworkElement TextField(string? value, string? placeholder, Action<string> commit, double width = 300, bool password = false)
    {
        var grid = new Grid { Width = width };
        Control input;
        Func<string> read;
        if (password)
        {
            var box = new PasswordBox
            {
                Foreground = Res("TextBrush"),
                CaretBrush = Res("TextBrush"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = 13,
                PasswordChar = '•',
                VerticalAlignment = VerticalAlignment.Center,
            };
            input = box;
            read = () => box.Password;
            grid.Children.Add(new Border { Background = Res("ControlBrush"), CornerRadius = new CornerRadius(10), MinHeight = 34 });
            box.Margin = new Thickness(12, 0, 12, 0);
        }
        else
        {
            // EditTextBox sets the padding twice (template + TextBox) – keep it small so text and placeholder line up.
            var box = new TextBox { Style = StyleOf("EditTextBox"), Text = value ?? "", Padding = new Thickness(4, 7, 4, 7) };
            input = box;
            read = () => box.Text;
        }

        var hintText = new TextBlock
        {
            Text = placeholder ?? "",
            Foreground = Res("SubTextBrush"),
            FontSize = 13,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        void UpdateHint() => hintText.Visibility = string.IsNullOrEmpty(read()) ? Visibility.Visible : Visibility.Collapsed;
        UpdateHint();
        var last = read();
        void Commit()
        {
            var now = read();
            if (now == last)
            {
                return;
            }

            last = now;
            commit(now.Trim());
            if (password && input is PasswordBox p)
            {
                p.Clear();
                last = "";
            }
        }

        switch (input)
        {
            case TextBox t:
                t.TextChanged += (_, _) => UpdateHint();
                break;
            case PasswordBox p:
                p.PasswordChanged += (_, _) => UpdateHint();
                break;
        }

        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        grid.Children.Add(input);
        grid.Children.Add(hintText);
        return grid;
    }

    public static Button Button(string text, Action click, bool accent = false, string? icon = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            content.Children.Add(new TextBlock { Style = StyleOf("IconText"), FontSize = 13, Text = icon, Margin = new Thickness(0, 0, 8, 0) });
        }

        content.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Style = StyleOf("BarButton"),
            Content = content,
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = true,
        };
        if (accent)
        {
            button.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        }
        else
        {
            button.Background = Res("TabInactiveBrush");
        }

        button.Click += (_, _) => click();
        return button;
    }

    public static StackPanel Buttons(params UIElement[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons)
        {
            if (b is FrameworkElement f)
            {
                f.Margin = new Thickness(0, 0, 8, 0);
            }

            panel.Children.Add(b);
        }

        return panel;
    }

    /// <summary>Status line with ✓/✗ (test results).</summary>
    public static TextBlock ResultLine(string text, bool? success) => new()
    {
        Text = (success switch { true => "✓ ", false => "✗ ", _ => "" }) + text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(0, 2, 0, 2),
        Foreground = success switch
        {
            true => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
            false => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60)),
            _ => Res("SubTextBrush"),
        },
    };

    public static Brush StateBrush(UeBar.Core.Abstractions.ModuleState state) => new SolidColorBrush(state switch
    {
        UeBar.Core.Abstractions.ModuleState.Connected => Color.FromRgb(0x34, 0xC7, 0x59),
        UeBar.Core.Abstractions.ModuleState.Error => Color.FromRgb(0xFF, 0x45, 0x3A),
        UeBar.Core.Abstractions.ModuleState.Starting or UeBar.Core.Abstractions.ModuleState.Disconnected
            or UeBar.Core.Abstractions.ModuleState.NotConfigured => Color.FromRgb(0xFF, 0xB0, 0x20),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    });

    /// <summary>Small badge ("Developer").</summary>
    public static Border Badge(string text) => new()
    {
        Background = Res("ControlBrush"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Res("SubTextBrush") },
    };

    /// <summary>Color dot for the accent color.</summary>
    public static Button Swatch(string hex, string name, bool selected, Action click)
    {
        var (r, g, b) = UeBar.Bar.AccentColors.Rgb(hex);
        var button = new Button
        {
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = name,
            Cursor = Cursors.Hand,
            Focusable = true,
            Template = SwatchTemplate(),
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            BorderBrush = selected ? Brushes.White : Brushes.Transparent,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2.5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        template.VisualTree = border;
        return template;
    }

    /// <summary>Window with a dark title bar and round corners (Windows 11).</summary>
    public static void DarkChrome(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var round = 2;
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Popup or control should not force tab navigation into the card.</summary>
    public static void NoTabStop(params Control[] controls)
    {
        foreach (var c in controls)
        {
            KeyboardNavigation.SetIsTabStop(c, false);
        }
    }

    public static UniformGrid Grid2() => new() { Columns = 2 };
}
