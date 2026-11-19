// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UeBar.Sdk;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Logging;

namespace UeBar.App.Bar;

/// <summary>
/// The bar window: borderless, transparent, topmost, not in the taskbar/Alt-Tab, never steals
/// focus. Window size = content (transparent windows render in software). When collapsed it is
/// invisible through Hide() – then nothing is rendered.
/// Position and size are set in physical pixels (per-monitor DPI v2).
/// </summary>
internal sealed partial class BarWindow : Window
{
    public const double TopGap = 8;
    public const double PillHeight = 40;
    public const double PillMinWidth = 150;
    public const double PanelRadius = 28;

    public static readonly DependencyProperty MorphProperty = DependencyProperty.Register(
        nameof(Morph), typeof(double), typeof(BarWindow), new PropertyMetadata(0.0, (d, _) => ((BarWindow)d).ApplyMorph()));

    private static readonly Duration SlideDuration = TimeSpan.FromMilliseconds(150);
    private static readonly Duration MorphDuration = TimeSpan.FromMilliseconds(250);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(100);

    private readonly SolidColorBrush _shellBrush = new();
    private readonly RectangleGeometry _clip = new();
    private Color _pillColor;
    private Color _panelColor;
    private IntPtr _hwnd;
    private MonitorInfo? _monitor;
    private PixelRect _rect;
    private Size _pillSize = new(PillMinWidth, PillHeight);
    private Size _panelSize = new(GridMetrics.PanelWidth, 300);
    private bool _panelMode;
    private bool _pinned;
    private PixelPoint? _pinOffset;
    private int _generation;
    private PixelPoint _dragStartCursor;
    private PixelRect _dragStartRect;
    private bool _dragging;

    public BarWindow()
    {
        InitializeComponent();
        Shell.Background = _shellBrush;
        Shell.Clip = _clip;
        PanelLayer.Width = GridMetrics.PanelWidth;
        PanelLayer.GripArea.MouseLeftButtonDown += OnGripDown;
        PanelLayer.GripArea.MouseMove += OnGripMove;
        PanelLayer.GripArea.MouseLeftButtonUp += OnGripUp;
        PanelLayer.GripArea.LostMouseCapture += (_, _) => _dragging = false;
        PreviewKeyDown += OnPreviewKeyDown;
        PanelLayer.ContentHeightChanged += (_, _) =>
        {
            // Coalesce and with low priority: must never push aside mouse ticks/input.
            if (_panelRefreshQueued)
            {
                return;
            }

            _panelRefreshQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                _panelRefreshQueued = false;
                RefreshPanelSize();
            });
        };
    }

    private bool _panelRefreshQueued;

    /// <summary>The visible area changed (size/position) – for the mouse check.</summary>
    public event EventHandler? SurfaceChanged;

    /// <summary>Window message (display/session/power changes) for the controller.</summary>
    public event Action<int, IntPtr, IntPtr>? MessageReceived;

    /// <summary>The pinned bar was moved (new window rectangle in physical pixels).</summary>
    public event Action<PixelRect>? DragCompleted;

    public IntPtr Handle => _hwnd;

    /// <summary>What the window currently shows – to compare with the state machine (for the log).</summary>
    public string Shows => !IsVisible ? "nothing" : _panelMode ? "panel" : "pill";

    /// <summary>The panel content (for focus requests of the app).</summary>
    public Views.PanelView Panel => PanelLayer;

    public double Morph
    {
        get => (double)GetValue(MorphProperty);
        set => SetValue(MorphProperty, value);
    }

    /// <summary>Visible area (pill or panel) in physical pixels – for the mouse check.</summary>
    public PixelRect? SurfaceRect
    {
        get
        {
            if (!IsVisible || _monitor is null)
            {
                return null;
            }

            var frame = FrameSize(_panelMode);
            var shell = _panelMode ? _panelSize : _pillSize;
            var s = _monitor.Scale;
            var x = _rect.X + (int)Math.Round((frame.Width - shell.Width) / 2 * s);
            var y = _rect.Y + (int)Math.Round(TopGap * s);
            return new PixelRect(x, y, (int)Math.Ceiling(shell.Width * s), (int)Math.Ceiling(shell.Height * s));
        }
    }

    /// <summary>Create the window handle (without showing), set styles, listen to messages.</summary>
    public void EnsureCreated()
    {
        _pillColor = (Color)FindResource("PillColor");
        _panelColor = (Color)FindResource("PanelColor");
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        var ex = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        ex &= ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyMorph();
    }

    // ------------------------------------------------------------------ Focus (edit mode)

    private bool _allowActivation;
    private IntPtr _previousForeground;

    /// <summary>
    /// Edit mode: the window may exceptionally get the focus (text input, Ctrl+Z).
    /// Afterwards <c>WS_EX_NOACTIVATE</c> again and the previous window gets the focus back.
    /// </summary>
    public void AllowActivation(bool allow, bool restorePrevious = true)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_allowActivation == allow)
        {
            if (allow && Win32.GetForegroundWindow() != _hwnd)
            {
                // Already allowed, but lost the focus (e.g. hotkey pressed again) → get it again.
                var again = Activate();
                Focus();
                Log?.Info(null, $"Focus: bar activated again ({(again ? "ok" : "refused")})");
            }

            return;
        }

        _allowActivation = allow;
        var ex = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        ex = allow ? ex & ~Win32.WS_EX_NOACTIVATE : ex | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        if (allow)
        {
            _previousForeground = Win32.GetForegroundWindow();
            Focusable = true; // otherwise the window does not accept keyboard focus and WPF does not route keys (Ctrl+Z)
            var activated = Activate();
            Focus();
            Log?.Info(null, $"Focus: bar may become active ({(activated ? "activated" : "activation refused")}, before 0x{_previousForeground.ToInt64():X})");
        }
        else
        {
            Keyboard.ClearFocus();
            Focusable = false;
            var back = restorePrevious ? "no previous window" : "left to Windows (an action ran – a started app keeps the focus)";
            if (restorePrevious && _previousForeground != IntPtr.Zero && _previousForeground != _hwnd && Win32.IsWindow(_previousForeground))
            {
                back = Win32.SetForegroundWindow(_previousForeground) ? $"back to 0x{_previousForeground.ToInt64():X}" : $"return to 0x{_previousForeground.ToInt64():X} refused";
            }

            Log?.Info(null, $"Focus: bar not activatable again ({back})");
            _previousForeground = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Emergency exit (tray "Reset bar"): hide immediately without animation, window styles and focus
    /// back to the base state, release mouse capture.
    /// </summary>
    public void ResetWindow()
    {
        AllowActivation(false); // focus back to the previous window (if allowed)
        _generation++;
        _dragging = false;
        if (Mouse.Captured is Visual captured && (ReferenceEquals(captured, this) || IsAncestorOf(captured)))
        {
            Mouse.Capture(null);
        }

        if (_hwnd != IntPtr.Zero)
        {
            var ex = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
            ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            ex &= ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        }

        Keyboard.ClearFocus();
        Focusable = false;
        _allowActivation = false;
        _previousForeground = IntPtr.Zero;
        _pinned = false;
        _pinOffset = null;
        FinishHide();
    }

    /// <summary>Ctrl+Z in edit mode = undo the last step (only a text box with its own input undoes that first).</summary>
    /// <summary>For debug lines (set by the controller).</summary>
    public UeBar.Core.Logging.IUeLogger? Log { get; set; }

    /// <summary>Esc in the window (close quick search/Assist) – the app decides.</summary>
    public event EventHandler? EscapePressed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control
            && DataContext is BarViewModel { Edit: { } edit }
            && Keyboard.FocusedElement is not System.Windows.Controls.TextBox { CanUndo: true })
        {
            edit.UndoCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ Transitions

    /// <summary>Hidden → pill: slides in from the top at the top center (~150 ms).</summary>
    public void ShowPill(MonitorInfo monitor, bool reduceAnimations)
    {
        _generation++;
        _monitor = monitor;
        _panelMode = false;
        _pinned = false;
        StopAnimations();
        MeasurePill();
        MeasurePanel();
        Morph = 0;
        ApplyMorph();
        Place(RectFor(panel: false));
        ShowWithoutActivation();

        if (reduceAnimations)
        {
            Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
        }
        else
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-(TopGap + PillHeight + 2), 0, SlideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    /// <summary>Pill → panel: morphs width/height/radius with a spring curve (~250 ms).</summary>
    public void ExpandToPanel(bool reduceAnimations)
    {
        if (_monitor is null)
        {
            return;
        }

        _generation++;
        _panelMode = true;
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Slide.Y = 0;
        Shell.BeginAnimation(OpacityProperty, null);
        Shell.Opacity = 1;
        MeasurePanel();
        Place(RectFor(panel: true));

        if (reduceAnimations)
        {
            BeginAnimation(MorphProperty, null);
            Morph = 1;
        }
        else
        {
            BeginAnimation(MorphProperty, new DoubleAnimation(Morph, 1, MorphDuration)
            {
                EasingFunction = new SpringEase(),
            });
        }
    }

    /// <summary>Show directly as a pinned panel (restart, monitor change).</summary>
    public void ShowPinned(MonitorInfo monitor, PixelPoint? offset, bool reduceAnimations)
    {
        _generation++;
        _monitor = monitor;
        _panelMode = true;
        _pinned = true;
        _pinOffset = offset;
        StopAnimations();
        MeasurePill();
        MeasurePanel();
        Morph = 1;
        ApplyMorph();
        Place(RectFor(panel: true));
        ShowWithoutActivation();
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, reduceAnimations ? FadeDuration : SlideDuration));
    }

    /// <summary>Pinning on/off. Unpinning puts the bar back to the top edge.</summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        if (!pinned)
        {
            _pinOffset = null;
            if (IsVisible)
            {
                Place(RectFor(_panelMode));
            }
        }
    }

    /// <summary>Hide; then Hide() – no more rendering.</summary>
    public void HideBar(bool reduceAnimations)
    {
        if (!IsVisible)
        {
            return;
        }

        var generation = ++_generation;
        _dragging = false;

        // Completed must be attached before BeginAnimation.
        DoubleAnimation Done(DoubleAnimation animation)
        {
            animation.Completed += (_, _) =>
            {
                if (generation == _generation)
                {
                    FinishHide();
                }
            };
            return animation;
        }

        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        if (reduceAnimations)
        {
            Shell.BeginAnimation(OpacityProperty, Done(new DoubleAnimation(Shell.Opacity, 0, FadeDuration)));
        }
        else if (Morph < 0.5)
        {
            Slide.BeginAnimation(
                TranslateTransform.YProperty,
                Done(new DoubleAnimation(Slide.Y, -(TopGap + Shell.ActualHeight + 2), SlideDuration) { EasingFunction = easeIn }));
        }
        else
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Slide.Y, -16, SlideDuration) { EasingFunction = easeIn });
            Shell.BeginAnimation(OpacityProperty, Done(new DoubleAnimation(Shell.Opacity, 0, SlideDuration)));
        }
    }

    /// <summary>Pill content changed (message, playback) → measure the pill again, place it visibly again.</summary>
    public void RefreshPill()
    {
        var before = _pillSize;
        MeasurePill();
        if (!IsVisible || _monitor is null || before == _pillSize)
        {
            return;
        }

        ApplyMorph();
        if (!_panelMode)
        {
            Place(RectFor(panel: false));
        }

        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Panel content changed (tab, tiles) → new size.</summary>
    public void RefreshPanelSize()
    {
        if (!IsVisible || !_panelMode)
        {
            MeasurePanel();
            return;
        }

        var before = _panelSize;
        MeasurePanel();
        if (before == _panelSize)
        {
            return;
        }

        ApplyMorph();
        Place(RectFor(panel: true));
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Monitor geometry/DPI changed → place again.</summary>
    public void Reposition(MonitorInfo monitor)
    {
        _monitor = monitor;
        if (IsVisible)
        {
            MeasurePanel();
            ApplyMorph();
            Place(RectFor(_panelMode));
        }
    }

    // ------------------------------------------------------------------ Layout

    private void MeasurePill()
    {
        PillLayer.Measure(new Size(double.PositiveInfinity, PillHeight));
        _pillSize = new Size(Math.Max(PillMinWidth, Math.Ceiling(PillLayer.DesiredSize.Width)), PillHeight);
    }

    private void MeasurePanel()
    {
        if (_monitor is not null)
        {
            // The grid may take at most ~70 % of the monitor height, the rest scrolls.
            PanelLayer.MaxGridHeight = Math.Max(200, (_monitor.WorkArea.Height / _monitor.Scale * 0.85) - 140);
            PanelLayer.MaxBodyHeight = Math.Max(300, (_monitor.WorkArea.Height / _monitor.Scale * 0.92) - 90);
        }

        PanelLayer.Measure(new Size(GridMetrics.PanelWidth, double.PositiveInfinity));
        _panelSize = new Size(GridMetrics.PanelWidth, Math.Ceiling(PanelLayer.DesiredSize.Height));
    }

    /// <summary>Window size in DIP: content + gap at the top + reserve for the spring's overshoot.</summary>
    private Size FrameSize(bool panel)
    {
        if (!panel)
        {
            return new Size(_pillSize.Width + 4, TopGap + _pillSize.Height + 2);
        }

        var reserve = Spring.MaxOvershoot + 0.01;
        var w = _panelSize.Width + Math.Max(0, _panelSize.Width - _pillSize.Width) * reserve;
        var h = TopGap + _panelSize.Height + (Math.Max(0, _panelSize.Height - _pillSize.Height) * reserve);
        return new Size(Math.Ceiling(w) + 4, Math.Ceiling(h) + 2);
    }

    private PixelRect RectFor(bool panel)
    {
        var frame = FrameSize(panel);
        return _pinned && _pinOffset is { } offset
            ? BarGeometry.AtOffset(_monitor!, offset, frame.Width, frame.Height)
            : BarGeometry.TopCenter(_monitor!, frame.Width, frame.Height);
    }

    private void ApplyMorph()
    {
        var p = Morph;
        var clamped = Math.Clamp(p, 0, 1);
        var w = Math.Max(1, _pillSize.Width + ((_panelSize.Width - _pillSize.Width) * p));
        var h = Math.Max(1, _pillSize.Height + ((_panelSize.Height - _pillSize.Height) * p));
        var r = (PillHeight / 2) + (((PanelRadius - (PillHeight / 2)) * clamped));
        r = Math.Min(r, Math.Min(w, h) / 2);

        Shell.Width = w;
        Shell.Height = h;
        Shell.CornerRadius = new CornerRadius(r);
        _clip.Rect = new Rect(0, 0, w, h);
        _clip.RadiusX = r;
        _clip.RadiusY = r;

        // Hidden instead of Collapsed: is not drawn, but can still be measured.
        PillLayer.Opacity = Math.Clamp(1 - (p / 0.35), 0, 1);
        PillLayer.Visibility = p >= 1 ? Visibility.Hidden : Visibility.Visible;
        PanelLayer.Opacity = Math.Clamp((p - 0.35) / 0.65, 0, 1);
        PanelLayer.Visibility = p <= 0 ? Visibility.Hidden : Visibility.Visible;
        PanelLayer.IsHitTestVisible = p >= 0.6;

        var c = clamped;
        _shellBrush.Color = Color.FromArgb(
            Lerp(_pillColor.A, _panelColor.A, c),
            Lerp(_pillColor.R, _panelColor.R, c),
            Lerp(_pillColor.G, _panelColor.G, c),
            Lerp(_pillColor.B, _panelColor.B, c));
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + ((b - a) * t));

    private void Place(PixelRect rect)
    {
        _rect = rect;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // OLED pixel shift (M7) only when pinned: 1–2 px offset, the area stays the same.
        var shift = _pinned ? _shift : default;

        // Set topmost again on every placement – other topmost windows could cover it otherwise.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, rect.X + shift.X, rect.Y + shift.Y, rect.Width, rect.Height, Win32.SWP_NOACTIVATE);
    }

    private PixelPoint _shift;
    private bool _dimmed;

    /// <summary>OLED protection (M7): dim the pinned panel or make it bright again.</summary>
    public void SetDimmed(bool dimmed)
    {
        if (_dimmed == dimmed)
        {
            return;
        }

        _dimmed = dimmed;
        DimOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(dimmed ? 0.72 : 0, dimmed ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>OLED pixel shift (M7): offset in physical pixels.</summary>
    public void SetShift(PixelPoint shift)
    {
        if (_shift == shift)
        {
            return;
        }

        _shift = shift;
        if (IsVisible)
        {
            Place(_rect);
        }
    }

    /// <summary>Number of columns changed (M7): new panel width.</summary>
    public void ApplyPanelWidth()
    {
        PanelLayer.Width = GridMetrics.PanelWidth;
        RefreshPanelSize();
    }

    private void ShowWithoutActivation()
    {
        if (!IsVisible)
        {
            Show(); // ShowActivated=false → SW_SHOWNA
        }

        Place(_rect); // set exactly once more after showing (DPI change)
    }

    private void FinishHide()
    {
        Hide();
        StopAnimations();
        Morph = 0;
        _panelMode = false;
        _monitor = null;
    }

    private void StopAnimations()
    {
        BeginAnimation(MorphProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Shell.BeginAnimation(OpacityProperty, null);
        Slide.Y = 0;
        Shell.Opacity = 1;
    }

    // ------------------------------------------------------------------ Moving (pinned)

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (!_pinned || MonitorService.CursorPosition() is not { } cursor)
        {
            return;
        }

        _dragging = true;
        _dragStartCursor = cursor;
        _dragStartRect = _rect;
        PanelLayer.GripArea.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _monitor is null || MonitorService.CursorPosition() is not { } cursor)
        {
            return;
        }

        var moved = _dragStartRect with
        {
            X = _dragStartRect.X + (cursor.X - _dragStartCursor.X),
            Y = _dragStartRect.Y + (cursor.Y - _dragStartCursor.Y),
        };
        _rect = BarGeometry.Clamp(moved, _monitor.Bounds);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, _rect.X, _rect.Y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _monitor is null)
        {
            return;
        }

        _dragging = false;
        PanelLayer.GripArea.ReleaseMouseCapture();
        _pinOffset = BarGeometry.OffsetIn(_monitor, _rect);
        e.Handled = true;
        DragCompleted?.Invoke(_rect);
    }

    // ------------------------------------------------------------------ Messages

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE when !_allowActivation:
                // A click on the bar does not activate it – the other window keeps the focus.
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);

            case Win32.WM_DPICHANGED:
                // WPF takes care of rescaling; afterwards restore our exact position.
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsVisible)
                    {
                        Place(_rect);
                    }
                });
                break;
        }

        MessageReceived?.Invoke(msg, wParam, lParam);
        return IntPtr.Zero;
    }

    /// <summary>Damped spring from <see cref="Spring"/> as WPF easing.</summary>
    private sealed class SpringEase : EasingFunctionBase
    {
        public SpringEase()
        {
            EasingMode = EasingMode.EaseIn; // EaseInCore is used directly
        }

        protected override double EaseInCore(double normalizedTime) => Spring.Evaluate(normalizedTime);

        protected override Freezable CreateInstanceCore() => new SpringEase();
    }
}
