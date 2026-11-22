// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UeBar.App.Interop;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Sdk;

namespace UeBar.App.Capture;

/// <summary>
/// The overlay on one monitor while picking a screen area (P2-5): the frozen screen of this monitor, darkened
/// except for the picked area, a frame with the size, a short hint and – once picked – the action bar. Opaque
/// (the frozen image is the background), topmost, not in the taskbar. All coordinates come in physical
/// pixels; the window maps them to its own device-independent pixels from its real size.
/// </summary>
internal sealed class AreaOverlay : Window
{
    private readonly Canvas _canvas = new();
    private readonly Path _mask = new() { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Rectangle _hover = new() { StrokeThickness = 2, StrokeDashArray = [4, 3], IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _size = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x14, 0x18)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _sizeText = new() { FontSize = 12, Foreground = Brushes.White };
    private readonly Border _hint;
    private readonly Border _bar = new() { CornerRadius = new CornerRadius(18), Padding = new Thickness(6), Visibility = Visibility.Collapsed };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
    private IntPtr _hwnd;

    public AreaOverlay(MonitorInfo monitor, BitmapSource image, string hint)
    {
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        Title = "ue Bar";
        WindowStartupLocation = WindowStartupLocation.Manual;
        var scale = monitor.Scale <= 0 ? 1 : monitor.Scale;
        Left = monitor.Bounds.X / scale;
        Top = monitor.Bounds.Y / scale;
        Width = monitor.Bounds.Width / scale;
        Height = monitor.Bounds.Height / scale;
        FontFamily = (FontFamily)Application.Current.FindResource("BarFont");

        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        _frame.Stroke = accent;
        _hover.Stroke = accent;
        _size.Child = _sizeText;
        _bar.Background = (Brush)Application.Current.FindResource("PanelBrush");
        _bar.Child = _buttons;

        var hintText = new TextBlock { Text = hint, FontSize = 13, Foreground = Brushes.White };
        _hint = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 14, 6),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x14, 0x18)),
            Child = hintText,
            IsHitTestVisible = false,
        };

        var background = new Image { Source = image, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.NearestNeighbor);
        var root = new Grid();
        root.Children.Add(background);
        root.Children.Add(_canvas);
        _canvas.Background = Brushes.Transparent; // receives the mouse everywhere
        _canvas.Children.Add(_mask);
        _canvas.Children.Add(_hover);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_size);
        _canvas.Children.Add(_hint);
        _canvas.Children.Add(_bar);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            PinInterop.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW, Win32.WS_EX_APPWINDOW);
            PlaceExactly();
        };
        Loaded += (_, _) =>
        {
            PlaceExactly(); // again after a DPI change on the way to the monitor
            UpdateMask(null);
        };
        SizeChanged += (_, _) => LayoutHint();
    }

    public MonitorInfo Monitor { get; }

    public IntPtr Handle => _hwnd;

    /// <summary>The window's real rectangle in physical pixels (for the mapping).</summary>
    public PixelRect PhysicalBounds => _hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out var r) ? MonitorService.ToRect(r) : Monitor.Bounds;

    /// <summary>A button of the action bar was chosen.</summary>
    public event Action<string>? ActionChosen;

    private void PlaceExactly()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var b = Monitor.Bounds;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Win32.SWP_NOACTIVATE);
    }

    private (double X, double Y, double Width, double Height) Local(PixelRect rect) =>
        ScreenSelection.ToLocal(rect, PhysicalBounds, _canvas.ActualWidth > 0 ? _canvas.ActualWidth : ActualWidth, _canvas.ActualHeight > 0 ? _canvas.ActualHeight : ActualHeight);

    /// <summary>Shows the picked area (null = none yet: everything darkened); <paramref name="showSize"/> = this overlay shows "W × H".</summary>
    public void UpdateMask(PixelRect? selection, bool showSize = false)
    {
        var width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : ActualWidth;
        var height = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : ActualHeight;
        Geometry full = new RectangleGeometry(new Rect(0, 0, width, height));
        var mine = selection is { } s ? ScreenSelection.Clip(s, PhysicalBounds) : default;
        if (!mine.IsEmpty)
        {
            var (x, y, w, h) = Local(mine);
            full = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(new Rect(x, y, w, h)));
            Place(_frame, x - 1, y - 1, w + 2, h + 2);
            _frame.Visibility = Visibility.Visible;
        }
        else
        {
            _frame.Visibility = Visibility.Collapsed;
        }

        _mask.Data = full;
        if (showSize && selection is { } area && !mine.IsEmpty)
        {
            var (x, y, _, _) = Local(mine);
            _sizeText.Text = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{area.Width} × {area.Height}");
            _size.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_size, x);
            Canvas.SetTop(_size, y - _size.DesiredSize.Height - 6 < 0 ? y + 6 : y - _size.DesiredSize.Height - 6);
            _size.Visibility = Visibility.Visible;
        }
        else
        {
            _size.Visibility = Visibility.Collapsed;
        }

        _hint.Visibility = selection is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Dashed frame around the window/monitor a click would take (null = none).</summary>
    public void ShowHover(PixelRect? target)
    {
        var mine = target is { } t ? ScreenSelection.Clip(t, PhysicalBounds) : default;
        if (mine.IsEmpty)
        {
            _hover.Visibility = Visibility.Collapsed;
            return;
        }

        var (x, y, w, h) = Local(mine);
        Place(_hover, x + 1, y + 1, Math.Max(0, w - 2), Math.Max(0, h - 2));
        _hover.Visibility = Visibility.Visible;
    }

    /// <summary>Action bar at the picked area (only on the overlay that holds most of it).</summary>
    public void ShowActions(IReadOnlyList<ScreenAreaAction> actions, PixelRect area)
    {
        _buttons.Children.Clear();
        var first = true;
        foreach (var action in actions)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var compact = action.Compact && !first; // P2-5-Fix 1: a small icon right at the previous button ("Save as …")
            if (!string.IsNullOrWhiteSpace(action.Icon))
            {
                content.Children.Add(new TextBlock
                {
                    Text = Bar.Converters.IconGlyphs.Get(action.Icon),
                    Style = (Style)Application.Current.FindResource("IconText"),
                    FontSize = compact ? 11 : 14,
                    Margin = new Thickness(0, 0, compact ? 0 : 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (!compact || string.IsNullOrWhiteSpace(action.Icon))
            {
                content.Children.Add(new TextBlock { Text = action.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            }

            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("BarButton"),
                Content = content,
                Height = 36,
                Padding = compact ? new Thickness(7, 0, 7, 0) : new Thickness(14, 0, 14, 0),
                Margin = new Thickness(first ? 0 : compact ? 2 : 6, 0, 0, 0),
                Cursor = Cursors.Hand,
                Focusable = true,
                ToolTip = compact ? action.Label : null,
            };
            if (first)
            {
                button.SetResourceReference(BackgroundProperty, "AccentBrush");
                button.Foreground = (Brush)Application.Current.FindResource("OnAccentTextBrush");
            }

            var id = action.Id;
            button.Click += (_, _) => ActionChosen?.Invoke(id);
            System.Windows.Automation.AutomationProperties.SetName(button, action.Label);
            _buttons.Children.Add(button);
            first = false;
        }

        _bar.Visibility = Visibility.Visible; // a collapsed element measures as 0 × 0
        _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var physical = PhysicalBounds;
        var scaleX = physical.Width / Math.Max(1, ActualWidth);
        var barW = (int)Math.Ceiling(_bar.DesiredSize.Width * scaleX);
        var barH = (int)Math.Ceiling(_bar.DesiredSize.Height * scaleX);
        var at = ScreenSelection.ActionBarPosition(area, barW, barH, physical, (int)Math.Round(10 * scaleX));
        var (x, y, _, _) = Local(new PixelRect(at.X, at.Y, barW, barH));
        Canvas.SetLeft(_bar, x);
        Canvas.SetTop(_bar, y);
        _bar.Visibility = Visibility.Visible;
    }

    public void HideActions() => _bar.Visibility = Visibility.Collapsed;

    /// <summary>Is the physical point on the action bar (a press there is no new pick)?</summary>
    public bool IsOnActions(PixelPoint point)
    {
        if (_bar.Visibility != Visibility.Visible)
        {
            return false;
        }

        var physical = PhysicalBounds;
        var local = (X: (point.X - physical.X) * ActualWidth / Math.Max(1, physical.Width), Y: (point.Y - physical.Y) * ActualHeight / Math.Max(1, physical.Height));
        var left = Canvas.GetLeft(_bar);
        var top = Canvas.GetTop(_bar);
        return local.X >= left && local.X <= left + _bar.ActualWidth && local.Y >= top && local.Y <= top + _bar.ActualHeight;
    }

    private void LayoutHint()
    {
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hint, (ActualWidth - _hint.DesiredSize.Width) / 2);
        Canvas.SetTop(_hint, 28);
    }

    private static void Place(FrameworkElement element, double x, double y, double w, double h)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, w);
        element.Height = Math.Max(0, h);
    }
}
