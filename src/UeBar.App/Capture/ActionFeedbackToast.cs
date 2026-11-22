// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UeBar.App.Interop;
using UeBar.Bar;

namespace UeBar.App.Capture;

/// <summary>
/// The app's own small message for the answer to an action the user just started (P2-5-Fix 1): "233 characters
/// recognized and copied · Pin". Shown at the place of the last picked area (where the action bar was) or,
/// without one, at the top edge of the monitor under the mouse – for a few seconds, independent of Windows
/// "Do not disturb". Never takes the focus; the mouse over it keeps it open. One window, reused.
/// </summary>
internal sealed class ActionFeedbackToast
{
    private static readonly TimeSpan RecentPick = TimeSpan.FromSeconds(60);

    private readonly Dispatcher _dispatcher;
    private readonly Func<(PixelRect Area, DateTimeOffset At)?> _lastPick;
    private readonly DispatcherTimer _timer;
    private Window? _window;
    private IntPtr _hwnd;

    public ActionFeedbackToast(Dispatcher dispatcher, Func<(PixelRect Area, DateTimeOffset At)?> lastPick)
    {
        _dispatcher = dispatcher;
        _lastPick = lastPick;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += (_, _) =>
        {
            if (_window?.IsMouseOver == true)
            {
                return; // read or about to click – stays
            }

            _timer.Stop();
            Hide();
        };
    }

    /// <summary>Show (from any thread). A button runs <paramref name="onAction"/> and closes the message.</summary>
    public void Show(string title, string? message, TimeSpan duration, IReadOnlyList<(string Id, string Label)> actions, Action<string> onAction)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Show(title, message, duration, actions, onAction));
            return;
        }

        var window = _window ??= Create();
        window.Content = Build(title, message, actions, id =>
        {
            Hide();
            onAction(id);
        });
        window.Opacity = 0;
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.UpdateLayout();
        Place(window);
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _timer.Stop();
        _timer.Interval = duration < TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : duration > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : duration;
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        _window?.Hide();
    }

    private Window Create()
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Title = "ue Bar",
            FontFamily = (FontFamily)Application.Current.FindResource("BarFont"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };
        window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            PinInterop.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE, Win32.WS_EX_APPWINDOW);
            HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg == Win32.WM_MOUSEACTIVATE)
                {
                    handled = true;
                    return new IntPtr(Win32.MA_NOACTIVATE);
                }

                return IntPtr.Zero;
            });
        };
        return window;
    }

    private static FrameworkElement Build(string title, string? message, IReadOnlyList<(string Id, string Label)> actions, Action<string> click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(message))
        {
            texts.Children.Add(new TextBlock { Text = message, FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        }

        row.Children.Add(texts);
        foreach (var (id, label) in actions)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)Application.Current.FindResource("BarButton"),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => click(id);
            row.Children.Add(button);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 9, 10, 9),
            Margin = new Thickness(6),
            Background = (Brush)Application.Current.FindResource("PanelBrush"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    private void Place(Window window)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var cursor = MonitorService.CursorPosition();
        var pick = _lastPick() is { } last && DateTimeOffset.UtcNow - last.At < RecentPick ? last.Area : (PixelRect?)null;
        var monitor = pick is { } p
            ? PinGeometry.MonitorAt(p, monitors)
            : cursor is { } c ? PinGeometry.MonitorAt(c, monitors) : null;
        monitor ??= PinGeometry.Primary(monitors)!;
        var scale = monitor.Scale <= 0 ? 1 : monitor.Scale;
        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);
        var gap = (int)Math.Round(10 * scale);
        PixelPoint at;
        if (pick is { } area)
        {
            at = ScreenSelection.ActionBarPosition(area, width, height, monitor.WorkArea, gap);
        }
        else
        {
            var w = monitor.WorkArea;
            at = new PixelPoint(w.X + ((w.Width - width) / 2), w.Y + (int)Math.Round(64 * scale));
        }

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, at.X, at.Y, width, height, Win32.SWP_NOACTIVATE);
    }
}
