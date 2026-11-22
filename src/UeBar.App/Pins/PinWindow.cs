// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using UeBar.App.Interop;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Core.Pins;
using UeBar.Sdk;

namespace UeBar.App.Pins;

/// <summary>
/// One pin on the screen (P2-5): borderless, rounded, in the bar's look, always on top, never in the taskbar
/// or Alt-Tab and – except while editing text – never takes the focus. Moved by dragging, resized at the
/// edges (snaps to the edges of the work areas), position lockable. Image: mouse wheel = zoom, dragging a
/// zoomed image = pan, double click = fit. Text: double click = edit, Ctrl + mouse wheel = font size.
/// Right click = menu. The window only shows – what it does goes to the <see cref="PinController"/>.
/// </summary>
internal sealed class PinWindow : Window
{
    private const double Radius = 14;
    private const int ResizeBorder = 7;

    private readonly PinController _controller;
    private readonly Border _shell = new() { CornerRadius = new CornerRadius(Radius), ClipToBounds = true };
    private readonly Border _dim = new() { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false, CornerRadius = new CornerRadius(Radius) };
    private readonly Grid _content = new();
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), Visibility = Visibility.Collapsed };
    private readonly Border _frame = new() { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(Radius), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _hintText = new() { FontSize = 12, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Border _hint = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x14, 0x18)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _zoom = new();
    private readonly TranslateTransform _pan = new();
    private Image? _image;
    private TextBlock? _text;
    private TextBox? _editor;
    private ScrollViewer? _textScroll;
    private IntPtr _hwnd;
    private bool _panning;
    private Point _panStart;
    private Point _panOrigin;
    private bool _dimmed;

    public PinWindow(PinRecord pin, PinController controller)
    {
        _controller = controller;
        Pin = pin;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        UseLayoutRounding = true;
        Title = "ue Bar – Pin";
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = (FontFamily)Application.Current.FindResource("BarFont");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Width = 200;
        Height = 120;

        // Opaque: text must stay readable over anything (the panel's color is slightly see-through).
        _shell.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D));
        _shell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        _shell.BorderThickness = new Thickness(1);
        var root = new Grid();
        root.Children.Add(_shell);
        _shell.Child = _content;
        root.Children.Add(_dim);
        root.Children.Add(_tools);
        _frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        root.Children.Add(_frame);
        _hint.Child = _hintText;
        root.Children.Add(_hint);
        Content = root;
        BuildTools();
        ShowContent();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            PinInterop.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE, Win32.WS_EX_APPWINDOW);
            ApplyClickThrough();
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        MouseEnter += (_, _) =>
        {
            _tools.Visibility = Visibility.Visible;
            _controller.Hover(this, inside: true);
        };
        MouseLeave += (_, _) =>
        {
            if (!_panning)
            {
                _tools.Visibility = Visibility.Collapsed;
            }

            _controller.Hover(this, inside: false);
        };
        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnLeftUp;
        LostMouseCapture += (_, _) => _panning = false;
        MouseWheel += OnWheel;
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _controller.ShowMenu(this);
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => CommitEdit();
        SizeChanged += (_, _) => ClampPan();

        // Border.CornerRadius does not clip the content – an image would keep square corners.
        _content.SizeChanged += (_, e) => _content.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius - 1, Radius - 1);
    }

    public PinRecord Pin { get; private set; }

    public string Id => Pin.Id;

    public IntPtr Handle => _hwnd;

    public bool IsEditing => _editor is not null;

    /// <summary>The window's rectangle in physical pixels.</summary>
    public PixelRect? PhysicalBounds => _hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out var r) ? MonitorService.ToRect(r) : null;

    /// <summary>Places the window exactly (physical pixels, any monitor and scaling).</summary>
    public void Place(PixelRect rect)
    {
        if (_hwnd == IntPtr.Zero)
        {
            new WindowInteropHelper(this).EnsureHandle();
        }

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, Win32.SWP_NOACTIVATE);
    }

    /// <summary>New state of the pin (from the manager).</summary>
    public void Update(PinRecord pin, PinChangeKind kind)
    {
        var old = Pin;
        Pin = pin;
        if (kind == PinChangeKind.Content && !IsEditing)
        {
            ShowContent();
        }

        Opacity = pin.Opacity;
        if (old.ClickThrough != pin.ClickThrough)
        {
            ApplyClickThrough();
        }

        if (_text is not null)
        {
            _text.FontSize = _controller.FontSizeOf(pin);
        }

        if (kind == PinChangeKind.Options && _image is not null && (Math.Abs(old.Zoom - pin.Zoom) > 0.001))
        {
            ApplyZoom(pin.Zoom, pin.PanX, pin.PanY);
        }
    }

    /// <summary>While the opacity slider is dragged: show the value at once (saved on release).</summary>
    public void PreviewOpacity(double opacity) => Opacity = Math.Clamp(opacity, 0.2, 1);

    /// <summary>Darker (OLED) or bright again; the brightness while dimmed comes from the settings.</summary>
    public void SetDimmed(bool dimmed, double level, bool animate)
    {
        if (dimmed == _dimmed)
        {
            return;
        }

        _dimmed = dimmed;
        var target = dimmed ? 1 - level : 0;
        _dim.BeginAnimation(OpacityProperty, animate
            ? new DoubleAnimation(target, dimmed ? TimeSpan.FromSeconds(1.2) : TimeSpan.FromMilliseconds(120))
            : null);
        if (!animate)
        {
            _dim.Opacity = target;
        }
    }

    // ------------------------------------------------------------------ content

    private void ShowContent()
    {
        _content.Children.Clear();
        _image = null;
        _text = null;
        _textScroll = null;
        Opacity = Pin.Opacity;
        if (Pin.Kind == PinKind.Image)
        {
            _shell.BorderThickness = new Thickness(0); // a screenshot pin shows exactly the captured pixels
            var source = _controller.LoadImage(Pin);
            _image = new Image { Source = source, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0, 0) };
            var group = new TransformGroup();
            group.Children.Add(_zoom);
            group.Children.Add(_pan);
            _image.RenderTransform = group;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _content.Children.Add(_image);
            _content.Background = Brushes.Transparent;
            ApplyZoom(Pin.Zoom, Pin.PanX, Pin.PanY);
            return;
        }

        _text = new TextBlock
        {
            Text = Pin.Text ?? "",
            TextWrapping = TextWrapping.Wrap,
            FontSize = _controller.FontSizeOf(Pin),
            Margin = new Thickness(14, 12, 14, 12),
            Foreground = Foreground,
        };
        _textScroll = new ScrollViewer
        {
            Content = _text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Background = Brushes.Transparent,
        };
        _textScroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Application.Current.FindResource("DarkScrollBar"));
        _content.Children.Add(_textScroll);
    }

    private void BuildTools()
    {
        Button Tool(string glyph, string tip, Action click)
        {
            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("BarButton"),
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = tip,
                Content = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("IconText"), FontSize = 11 },
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x12, 0x14, 0x18)),
            };
            button.Click += (_, _) => click();
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            return button;
        }

        _tools.Children.Add(Tool("", Loc.Get("Pin_Menu"), () => _controller.ShowMenu(this)));
        _tools.Children.Add(Tool("", Loc.Get("Pin_Close"), () => _controller.Close(Pin.Id)));
    }

    // ------------------------------------------------------------------ text editing

    public void BeginEdit()
    {
        if (Pin.Kind != PinKind.Text || IsEditing || _textScroll is null)
        {
            return;
        }

        _editor = new TextBox
        {
            Text = Pin.Text ?? "",
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = _controller.FontSizeOf(Pin),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = (Style)Application.Current.FindResource("EditTextBox"),
            Margin = new Thickness(6),
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6),
        };
        _content.Children.Clear();
        _content.Children.Add(_editor);

        // The only moment a pin may take the focus: typing needs the keyboard.
        Focusable = true;
        PinInterop.SetExStyle(_hwnd, 0, Win32.WS_EX_NOACTIVATE);
        Activate();
        Win32.SetForegroundWindow(_hwnd);
        _editor.Focus();
        Keyboard.Focus(_editor);
        _editor.CaretIndex = _editor.Text.Length;
    }

    private void CommitEdit()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        _editor = null;
        PinInterop.SetExStyle(_hwnd, Win32.WS_EX_NOACTIVATE, 0);
        Focusable = false;
        var text = editor.Text;
        ShowContent();
        if (!string.Equals(text, Pin.Text, StringComparison.Ordinal))
        {
            _controller.SetText(Pin.Id, text);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (IsEditing && (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))))
        {
            e.Handled = true;
            CommitEdit();
        }
    }

    // ------------------------------------------------------------------ mouse

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (IsEditing || e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            if (Pin.Kind == PinKind.Text)
            {
                BeginEdit();
            }
            else
            {
                ApplyZoom(1, 0, 0);
                _controller.SetZoom(Pin.Id, 1, 0, 0);
            }

            return;
        }

        _controller.Touch(this);
        if (_image is not null && _zoom.ScaleX > 1.001)
        {
            // Dragging a zoomed image moves the image, not the window.
            _panning = true;
            _panStart = e.GetPosition(this);
            _panOrigin = new Point(_pan.X, _pan.Y);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (!Pin.Locked)
        {
            e.Handled = true;
            try
            {
                DragMove(); // native move loop: WM_MOVING snaps, WM_EXITSIZEMOVE saves
            }
            catch (InvalidOperationException)
            {
                // the button was already released
            }
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var now = e.GetPosition(this);
        _pan.X = _panOrigin.X + (now.X - _panStart.X);
        _pan.Y = _panOrigin.Y + (now.Y - _panStart.Y);
        ClampPan();
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        ReleaseMouseCapture();
        Cursor = null;
        SaveZoom();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _controller.Touch(this);
        if (_image is not null)
        {
            e.Handled = true;
            var factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
            var target = Math.Clamp(_zoom.ScaleX * factor, 1, 16);
            ZoomAround(e.GetPosition(_content), target);
            SaveZoom();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _controller.ChangeFontSize(Pin.Id, e.Delta > 0 ? 1 : -1);
        }
    }

    // ------------------------------------------------------------------ zoom

    private void ZoomAround(Point point, double zoom)
    {
        var old = _zoom.ScaleX;
        if (Math.Abs(old - zoom) < 0.0001)
        {
            return;
        }

        // The point under the mouse stays where it is.
        var x = point.X - ((point.X - _pan.X) * (zoom / old));
        var y = point.Y - ((point.Y - _pan.Y) * (zoom / old));
        _zoom.ScaleX = _zoom.ScaleY = zoom;
        _pan.X = x;
        _pan.Y = y;
        ClampPan();
    }

    private void ApplyZoom(double zoom, double panX, double panY)
    {
        _zoom.ScaleX = _zoom.ScaleY = Math.Clamp(zoom, 1, 16);
        var width = Math.Max(1, _content.ActualWidth > 0 ? _content.ActualWidth : Width);
        var height = Math.Max(1, _content.ActualHeight > 0 ? _content.ActualHeight : Height);
        _pan.X = panX * width;
        _pan.Y = panY * height;
        ClampPan();
    }

    private void ClampPan()
    {
        var width = _content.ActualWidth;
        var height = _content.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var z = _zoom.ScaleX;
        _pan.X = Math.Clamp(_pan.X, width - (width * z), 0);
        _pan.Y = Math.Clamp(_pan.Y, height - (height * z), 0);
    }

    private void SaveZoom()
    {
        var width = Math.Max(1, _content.ActualWidth);
        var height = Math.Max(1, _content.ActualHeight);
        _controller.SetZoom(Pin.Id, _zoom.ScaleX, _pan.X / width, _pan.Y / height);
    }

    // ------------------------------------------------------------------ window messages

    private void ApplyClickThrough()
    {
        if (!Pin.ClickThrough)
        {
            _usable = false;
            _frame.Visibility = Visibility.Collapsed;
        }

        var through = Pin.ClickThrough && !_usable;
        if (_hwnd != IntPtr.Zero)
        {
            PinInterop.SetExStyle(_hwnd, through ? PinInterop.WS_EX_TRANSPARENT : 0, through ? 0 : PinInterop.WS_EX_TRANSPARENT);
        }

        if (through)
        {
            _tools.Visibility = Visibility.Collapsed;
        }
    }

    private bool _usable;

    /// <summary>
    /// P2-5-Fix 1: Ctrl held over a pin that lets clicks through – usable for a moment (thin frame, the right-click
    /// menu works). Off again when Ctrl is released or the mouse leaves.
    /// </summary>
    public void SetTemporarilyUsable(bool usable)
    {
        if (!Pin.ClickThrough)
        {
            usable = false;
        }

        if (_usable == usable)
        {
            return;
        }

        _usable = usable;
        _frame.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        ApplyClickThrough();
    }

    /// <summary>A short hint right at the pin (P2-5-Fix 1: how to get a click-through pin back) – no notification.</summary>
    public void ShowHint(string text)
    {
        _hintText.Text = text;
        _hint.BeginAnimation(OpacityProperty, null);
        _hint.Opacity = 1;
        _hint.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromSeconds(3.5) };
        fade.Completed += (_, _) => _hint.Visibility = Visibility.Collapsed;
        _hint.BeginAnimation(OpacityProperty, fade);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE when !IsEditing:
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);

            case PinInterop.WM_NCHITTEST when !Pin.Locked && (!Pin.ClickThrough || _usable) && !IsEditing:
            {
                var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                var y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                if (HitEdge(x, y) is { } edge)
                {
                    handled = true;
                    return new IntPtr(edge);
                }

                break;
            }

            case PinInterop.WM_MOVING:
            {
                var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.RECT>(lParam);
                var snapped = _controller.SnapMove(MonitorService.ToRect(rect));
                WriteRect(lParam, snapped);
                handled = true;
                return new IntPtr(1);
            }

            case PinInterop.WM_SIZING:
            {
                var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.RECT>(lParam);
                var side = wParam.ToInt32();
                var snapped = _controller.SnapResize(
                    MonitorService.ToRect(rect),
                    left: side is PinInterop.WMSZ_LEFT or PinInterop.WMSZ_TOPLEFT or PinInterop.WMSZ_BOTTOMLEFT,
                    top: side is PinInterop.WMSZ_TOP or PinInterop.WMSZ_TOPLEFT or PinInterop.WMSZ_TOPRIGHT,
                    right: side is PinInterop.WMSZ_RIGHT or PinInterop.WMSZ_TOPRIGHT or PinInterop.WMSZ_BOTTOMRIGHT,
                    bottom: side is PinInterop.WMSZ_BOTTOM or PinInterop.WMSZ_BOTTOMLEFT or PinInterop.WMSZ_BOTTOMRIGHT,
                    minimum: (int)Math.Round(PinGeometry.MinSize * Scale));
                WriteRect(lParam, snapped);
                handled = true;
                return new IntPtr(1);
            }

            case PinInterop.WM_EXITSIZEMOVE:
                _controller.SavePlacement(this);
                break;
        }

        return IntPtr.Zero;
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private int? HitEdge(int x, int y)
    {
        if (PhysicalBounds is not { } b)
        {
            return null;
        }

        var border = (int)Math.Round(ResizeBorder * Scale);
        var left = x < b.X + border;
        var right = x >= b.Right - border;
        var top = y < b.Y + border;
        var bottom = y >= b.Bottom - border;
        return (left, top, right, bottom) switch
        {
            (true, true, _, _) => PinInterop.HTTOPLEFT,
            (_, true, true, _) => PinInterop.HTTOPRIGHT,
            (true, _, _, true) => PinInterop.HTBOTTOMLEFT,
            (_, _, true, true) => PinInterop.HTBOTTOMRIGHT,
            (true, _, _, _) => PinInterop.HTLEFT,
            (_, _, true, _) => PinInterop.HTRIGHT,
            (_, true, _, _) => PinInterop.HTTOP,
            (_, _, _, true) => PinInterop.HTBOTTOM,
            _ => null,
        };
    }

    private static void WriteRect(IntPtr target, PixelRect rect) =>
        System.Runtime.InteropServices.Marshal.StructureToPtr(new Win32.RECT { Left = rect.X, Top = rect.Y, Right = rect.Right, Bottom = rect.Bottom }, target, false);

    private static T? FindParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (var current = child; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>The image of the pin as PNG (for copying and saving).</summary>
    public static byte[]? Png(BitmapSource? source)
    {
        if (source is null)
        {
            return null;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public BitmapSource? ImageSource => _image?.Source as BitmapSource;
}
