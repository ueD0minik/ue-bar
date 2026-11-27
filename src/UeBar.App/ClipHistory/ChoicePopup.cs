// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UeBar.App.Bar.Converters;
using UeBar.App.Interop;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.ClipHistory;

/// <summary>
/// The core's choice popup for plugins (P2-6, <see cref="IChoicePopup"/>) – the look of the quick search as a small
/// free window: title, search field (typing = searching), the list with text/picture previews and group headings,
/// the actions with their keys at the bottom. Appears at the text cursor of the window in front (or at the mouse),
/// on that monitor and in its scale. One at a time: a second call closes the open one (both answer null).
/// </summary>
internal sealed class ChoicePopupHost(Dispatcher dispatcher, IUeLogger log) : IHostChoicePopup
{
    private const double PopupWidth = 440;
    private const double ListHeight = 420;

    private readonly CaretLocator _caretLocator = new();
    private Session? _open;

    public Task<ChoicePopupResult?> ShowAsync(string moduleId, ChoicePopupRequest request, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<ChoicePopupResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(() =>
        {
            if (_open is { } open)
            {
                open.Close(null); // the same hotkey again: close
                done.TrySetResult(null);
                return;
            }

            var target = PasteService.TargetOf(Win32.GetForegroundWindow());
            var (caret, source) = request.Placement == ChoicePlacement.Caret && target is { } t ? FindCaret(new IntPtr(t.WindowHandle)) : (null, CaretSource.Mouse);
            var session = new Session(this, moduleId, request, target, caret, source, done, log);
            _open = session;
            cancellationToken.Register(() => dispatcher.BeginInvoke(() => session.Close(null)));
            session.Show();
        });
        return done.Task;
    }

    private void Closed(Session session)
    {
        if (_open == session)
        {
            _open = null;
        }
    }

    /// <summary>
    /// P2-6-Fix 1: the old interface first (where it gives a position, it stays decisive), then accessibility on its own
    /// thread for at most 150 ms, then the mouse.
    /// </summary>
    private (PixelRect? Caret, CaretSource Source) FindCaret(IntPtr window)
    {
        var started = Environment.TickCount64;
        var thread = ClipInterop.GetWindowThreadProcessId(window, out _);
        var info = new ClipInterop.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ClipInterop.GUITHREADINFO>() };
        var focus = thread != 0 && ClipInterop.GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
        PixelRect? bounds = Win32.WindowBounds(window) is { } r ? PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom) : null;
        AutomationCaret.Trace ??= step => log.Debug(null, $"Caret lookup: {step}");
        var found = _caretLocator.Locate(() => CaretOf(window), () => AutomationCaret.Find(window, focus), bounds);
        log.Debug(null, $"Caret: {found.Source} in {Environment.TickCount64 - started} ms{(found.Source == CaretSource.Mouse ? $" ({_caretLocator.LastProblem})" : "")}");
        return found;
    }

    /// <summary>The caret of a window's thread on the screen, if the app uses the system caret.</summary>
    private static PixelRect? CaretOf(IntPtr window)
    {
        var thread = ClipInterop.GetWindowThreadProcessId(window, out _);
        var info = new ClipInterop.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ClipInterop.GUITHREADINFO>() };
        if (thread == 0 || !ClipInterop.GetGUIThreadInfo(thread, ref info) || info.hwndCaret == IntPtr.Zero)
        {
            return null;
        }

        var r = info.rcCaret;
        var topLeft = new Win32.POINT { X = r.Left, Y = r.Top };
        var bottomRight = new Win32.POINT { X = r.Right, Y = r.Bottom };
        if (!ClipInterop.ClientToScreen(info.hwndCaret, ref topLeft) || !ClipInterop.ClientToScreen(info.hwndCaret, ref bottomRight))
        {
            return null;
        }

        var rect = PixelRect.FromEdges(topLeft.X, topLeft.Y, Math.Max(bottomRight.X, topLeft.X + 1), Math.Max(bottomRight.Y, topLeft.Y + 1));
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    /// <summary>One open popup.</summary>
    private sealed class Session
    {
        private readonly ChoicePopupHost _host;
        private readonly string _moduleId;
        private readonly ChoicePopupRequest _request;
        private readonly PasteTarget? _target;
        private readonly PixelRect? _caret;
        private readonly CaretSource _source;
        private readonly TaskCompletionSource<ChoicePopupResult?> _done;
        private readonly IUeLogger _log;
        private readonly Window _window;
        private readonly TextBox _search;
        private readonly TextBlock _placeholder;
        private readonly TextBlock _hint;
        private readonly StackPanel _rows;
        private readonly ScrollViewer _scroller;
        private readonly TextBlock _empty;
        private readonly List<(ChoiceItem Item, Border Row)> _visible = [];
        private IReadOnlyList<ChoiceItem> _items;
        private int _selected = -1;
        private bool _busy;
        private bool _closed;
        private IntPtr _hwnd;

        public Session(ChoicePopupHost host, string moduleId, ChoicePopupRequest request, PasteTarget? target, PixelRect? caret, CaretSource source, TaskCompletionSource<ChoicePopupResult?> done, IUeLogger log)
        {
            _host = host;
            _moduleId = moduleId;
            _request = request;
            _target = target;
            _caret = caret;
            _source = source;
            _done = done;
            _log = log;
            _items = request.Items;

            _search = new TextBox
            {
                Style = (Style)Application.Current.FindResource("EditTextBox"),
                Padding = new Thickness(30, 7, 8, 7),
                FontSize = 13,
            };
            _placeholder = new TextBlock
            {
                Text = request.Placeholder ?? Loc.Get("Choice_Search"),
                Style = (Style)Application.Current.FindResource("SubText"),
                FontSize = 13,
                Margin = new Thickness(32, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            _hint = new TextBlock { Style = (Style)Application.Current.FindResource("SubText"), FontSize = 11, Margin = new Thickness(4, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            _rows = new StackPanel();
            _empty = new TextBlock
            {
                Text = request.EmptyText ?? Loc.Get("Choice_Empty"),
                Style = (Style)Application.Current.FindResource("SubText"),
                FontSize = 12,
                Margin = new Thickness(6, 12, 6, 12),
                TextWrapping = TextWrapping.Wrap,
            };
            _scroller = new ScrollViewer
            {
                Content = _rows,
                MaxHeight = ListHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Margin = new Thickness(0, 8, 0, 0),
            };
            _window = BuildWindow();
            _search.TextChanged += (_, _) =>
            {
                _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Refill(keepId: null);
            };
            SetHint(request.Hint);
            Refill(keepId: null);
        }

        public void Show()
        {
            _window.Show();
            _window.UpdateLayout();
            Place();
            _window.Activate();
            PasteService.Focus(_hwnd);
            _search.Focus();
            Keyboard.Focus(_search);
            _log.Info(_moduleId, $"Choice popup open ({_items.Count} rows, at {_source})");
        }

        public void Close(ChoicePopupResult? result)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _host.Closed(this);
            _window.Close();
            _done.TrySetResult(result);
                UeBar.App.Infrastructure.MemoryTrim.Soon("the choice popup"); // P2-6-Fix 2: its pictures and the accessibility objects of the caret lookup
        }

        private Window BuildWindow()
        {
            var title = new TextBlock { Text = _request.Title ?? "", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 4) };
            title.Visibility = string.IsNullOrWhiteSpace(_request.Title) ? Visibility.Collapsed : Visibility.Visible;
            var searchIcon = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("IconText"),
                FontSize = 13,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Text = "",
            };
            var searchGrid = new Grid();
            searchGrid.Children.Add(_search);
            searchGrid.Children.Add(_placeholder);
            searchGrid.Children.Add(searchIcon);

            var body = new StackPanel { Width = PopupWidth };
            body.Children.Add(title);
            body.Children.Add(_hint);
            body.Children.Add(searchGrid);
            body.Children.Add(_scroller);
            body.Children.Add(_empty);
            body.Children.Add(Footer());

            var frame = new Border
            {
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(12, 12, 12, 10),
                Margin = new Thickness(8),
                Background = (Brush)Application.Current.FindResource("PanelBrush"),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = body,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black },
            };

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Title = _request.Title ?? "ue Bar",
                FontFamily = (FontFamily)Application.Current.FindResource("BarFont"),
                Foreground = (Brush)Application.Current.FindResource("TextBrush"),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Content = frame,
            };
            window.SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(window).Handle;
                PinInterop.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW, Win32.WS_EX_APPWINDOW);
            };
            window.PreviewKeyDown += OnKeyDown;
            window.Deactivated += (_, _) =>
            {
                if (!_busy)
                {
                    Close(null); // clicked somewhere else
                }
            };
            return window;
        }

        private FrameworkElement Footer()
        {
            var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var action in _request.Actions)
            {
                var key = KeyChord.TryParse(action.Shortcut)?.DisplayText ?? (action == _request.Actions[0] ? BarText.KeyName("Enter") : null);
                var chip = new Button
                {
                    Style = (Style)Application.Current.FindResource("BarButton"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 4, 4),
                    Focusable = false,
                    ToolTip = ChoiceKeys.Tooltip(action.Label, action.Shortcut ?? (action == _request.Actions[0] ? "Enter" : null)),
                    Content = new TextBlock
                    {
                        FontSize = 10.5,
                        Inlines =
                        {
                            new System.Windows.Documents.Run(key is null ? "" : key + "  ") { FontWeight = FontWeights.SemiBold },
                            new System.Windows.Documents.Run(action.Label) { Foreground = (Brush)Application.Current.FindResource("SubTextBrush") },
                        },
                    },
                };
                chip.Click += (_, _) => _ = RunAsync(action);
                footer.Children.Add(chip);
            }

            var close = new Button
            {
                Style = (Style)Application.Current.FindResource("BarButton"),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Focusable = false,
                ToolTip = ChoiceKeys.Tooltip(Loc.Get("Choice_Close"), "Esc"),
                Content = new TextBlock
                {
                    FontSize = 10.5,
                    Inlines =
                    {
                        new System.Windows.Documents.Run("Esc  ") { FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run(Loc.Get("Choice_Close")) { Foreground = (Brush)Application.Current.FindResource("SubTextBrush") },
                    },
                },
            };
            close.Click += (_, _) => Close(null);
            footer.Children.Add(close);
            return footer;
        }

        private void SetHint(string? hint)
        {
            _hint.Text = hint ?? "";
            _hint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ list

        private void Refill(string? keepId)
        {
            var previousId = _selected >= 0 && _selected < _visible.Count ? _visible[_selected].Item.Id : null;
            var previousIndex = Math.Max(_selected, 0);
            var hits = ChoiceSearch.Filter(_items, i => $"{i.Title} {i.Detail} {i.SearchText}", _search.Text);
            _rows.Children.Clear();
            _visible.Clear();
            string? group = null;
            foreach (var index in hits)
            {
                var item = _items[index];
                if (item.Group is { } g && g != group)
                {
                    _rows.Children.Add(new TextBlock
                    {
                        Text = g,
                        Style = (Style)Application.Current.FindResource("SubText"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(6, _rows.Children.Count == 0 ? 0 : 8, 0, 4),
                    });
                }

                group = item.Group;
                var row = Row(item, _visible.Count);
                _rows.Children.Add(row);
                _visible.Add((item, row));
            }

            _empty.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _scroller.Visibility = _visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var ids = _visible.Select(v => v.Item.Id).ToList();
            Select(_search.Text.Length > 0 && keepId is null ? (ids.Count > 0 ? 0 : -1) : ChoiceSearch.Reselect(ids, previousId, previousIndex, keepId));
        }

        private Border Row(ChoiceItem item, int index)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement leading = item.ImagePng is { Length: > 0 } png && Picture(png) is { } picture
                ? new Border
                {
                    Width = 64,
                    Height = 44,
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                    Child = new Image { Source = picture, Stretch = Stretch.Uniform },
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new TextBlock
                {
                    Style = (Style)Application.Current.FindResource("IconText"),
                    FontSize = 15,
                    Width = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = IconGlyphs.Get(item.Icon ?? "U+E8C8"),
                };
            grid.Children.Add(leading);

            var texts = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = item.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                texts.Children.Add(new TextBlock { Text = item.Detail, Style = (Style)Application.Current.FindResource("SubText"), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            }

            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            if (item.IsMarked)
            {
                var star = new TextBlock { Style = (Style)Application.Current.FindResource("IconText"), FontSize = 12, Text = "", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("AccentBrush") };
                Grid.SetColumn(star, 2);
                grid.Children.Add(star);
            }

            var row = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 3),
                Background = Brushes.Transparent,
                Child = grid,
                Cursor = Cursors.Hand,
                ToolTip = Tooltip(item),
            };
            row.MouseEnter += (_, _) => Select(_visible.FindIndex(v => v.Row == row));
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Select(_visible.FindIndex(v => v.Row == row));
                if (_request.Actions.Count > 0)
                {
                    _ = RunAsync(_request.Actions[0]);
                }
            };
            return row;
        }

        private string Tooltip(ChoiceItem item)
        {
            var first = _request.Actions.Count > 0 ? ChoiceKeys.Tooltip(_request.Actions[0].Label, "Enter") : "";
            var keys = string.Join(" · ", _request.Actions.Skip(1).Where(a => a.Shortcut is not null).Select(a => ChoiceKeys.Tooltip(a.Label, a.Shortcut)));
            return string.Join("\n", new[] { first, keys }.Where(s => s.Length > 0));
        }

        private static BitmapImage? Picture(byte[] png)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelHeight = 96;
                image.StreamSource = new MemoryStream(png);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
            {
                return null;
            }
        }

        private void Select(int index)
        {
            if (index < -1 || index >= _visible.Count)
            {
                return;
            }

            _selected = index;
            var accent = (Brush)Application.Current.FindResource("ControlHoverBrush");
            for (var i = 0; i < _visible.Count; i++)
            {
                _visible[i].Row.Background = i == index ? accent : Brushes.Transparent;
            }

            if (index >= 0)
            {
                _visible[index].Row.BringIntoView();
            }
        }

        // ------------------------------------------------------------------ keys and actions

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Close(null);
                    return;
                case Key.Down:
                    e.Handled = true;
                    Select(Math.Min(_selected + 1, _visible.Count - 1));
                    return;
                case Key.Up:
                    e.Handled = true;
                    Select(Math.Max(_selected - 1, _visible.Count > 0 ? 0 : -1));
                    return;
                case Key.PageDown:
                    e.Handled = true;
                    Select(Math.Min(_selected + 6, _visible.Count - 1));
                    return;
                case Key.PageUp:
                    e.Handled = true;
                    Select(Math.Max(_selected - 6, _visible.Count > 0 ? 0 : -1));
                    return;
            }

            var modifiers = HotkeyModifiers.None;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                modifiers |= HotkeyModifiers.Control;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                modifiers |= HotkeyModifiers.Shift;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                modifiers |= HotkeyModifiers.Alt;
            }

            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0x2E && _search.CaretIndex < _search.Text.Length)
            {
                return; // Delete inside the search text deletes text
            }

            var actions = _request.Actions.Select(a => (a.Id, a.Shortcut)).ToList();
            if (ChoiceKeys.ActionFor(actions, modifiers, vk) is { } id && _request.Actions.FirstOrDefault(a => a.Id == id) is { } action)
            {
                e.Handled = true;
                _ = RunAsync(action);
            }
        }

        private async Task RunAsync(ChoiceAction action)
        {
            if (_busy || _closed || _selected < 0 || _selected >= _visible.Count)
            {
                return;
            }

            var item = _visible[_selected].Item;
            var choice = new ChoicePopupResult(item.Id, action.Id, _target);
            if (!action.KeepOpen || _request.OnAction is not { } onAction)
            {
                Close(choice);
                return;
            }

            _busy = true;
            try
            {
                var update = await onAction(choice, CancellationToken.None);
                if (_closed || update is null)
                {
                    return;
                }

                if (update.Close)
                {
                    Close(null);
                    return;
                }

                _items = update.Items;
                if (update.Hint is not null)
                {
                    SetHint(update.Hint);
                }

                Refill(update.SelectId);
                _window.UpdateLayout();
                Place();
            }
            finally
            {
                _busy = false;
                if (!_closed && !_window.IsActive)
                {
                    _window.Activate();
                    _search.Focus();
                }
            }
        }

        // ------------------------------------------------------------------ placement

        private void Place()
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

            var mouse = MonitorService.CursorPosition() ?? PinGeometry.Primary(monitors)!.WorkArea.Center;
            var anchor = _caret is { } c ? new PixelPoint(c.X, c.Bottom) : mouse;
            var monitor = PinGeometry.MonitorAt(anchor, monitors) ?? PinGeometry.Primary(monitors)!;
            var scale = monitor.Scale <= 0 ? 1 : monitor.Scale;
            var width = (int)Math.Ceiling(_window.ActualWidth * scale);
            var height = (int)Math.Ceiling(_window.ActualHeight * scale);
            var at = ChoicePlacementRules.Place(_caret, mouse, width, height, monitors, _request.Placement == ChoicePlacement.Center);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, at.X, at.Y, width, height, 0);
        }
    }
}
