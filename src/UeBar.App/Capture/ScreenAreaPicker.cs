// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UeBar.App.Interop;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.App.Capture;

/// <summary>
/// The core's "pick a screen area" (P2-5, <see cref="IScreenAreaPicker"/>): hides the bar, freezes the screen
/// (one capture of all monitors), shows one overlay per monitor and lets the user drag a rectangle (also
/// across monitors), click a window or a monitor; Esc or a right click cancels. With actions a small bar
/// appears at the area. The result carries the area as PNG from the frozen screen. One pick at a time.
/// </summary>
internal sealed class ScreenAreaPicker(Dispatcher dispatcher, IUeLogger log, Func<ICaptureHideTarget?> barTarget) : IScreenAreaPicker
{
    private bool _busy;

    /// <summary>The last picked area and when (P2-5-Fix 1: the answer to the action appears there).</summary>
    public (PixelRect Area, DateTimeOffset At)? LastPick { get; private set; }

    public Task<ScreenAreaResult?> PickScreenAreaAsync(ScreenAreaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return dispatcher.InvokeAsync(() => PickAsync(request, cancellationToken)).Task.Unwrap();
    }

    private async Task<ScreenAreaResult?> PickAsync(ScreenAreaRequest request, CancellationToken cancellationToken)
    {
        if (_busy)
        {
            log.Info(null, "Screen area: already picking – second request ignored");
            return null;
        }

        _busy = true;
        try
        {
            // P2-5-Fix 1: the bar comes back in every case (action, Esc, error, exception).
            return barTarget() is { } bar
                ? await CaptureSafety.RunWithBarHiddenAsync(bar, wasVisible => PickHiddenAsync(request, wasVisible, cancellationToken))
                : await PickHiddenAsync(request, false, cancellationToken);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<ScreenAreaResult?> PickHiddenAsync(ScreenAreaRequest request, bool barWasVisible, CancellationToken cancellationToken)
    {
        PickSession? session = null;
        try
        {
            if (barWasVisible)
            {
                await Task.Delay(60, CancellationToken.None);
            }

            PinInterop.DwmFlush();
            var monitors = MonitorService.GetMonitors();
            var desktop = ScreenSelection.VirtualBounds(monitors);
            if (monitors.Count == 0 || desktop.IsEmpty)
            {
                return null;
            }

            var started = Environment.TickCount64;
            var frozen = ScreenGrab.Capture(desktop);
            var windows = WindowFinder.Snapshot([]);
            log.Info(null, $"Screen area: {request.Mode}, {monitors.Count} monitor(s) {desktop.Width}×{desktop.Height}, captured in {Environment.TickCount64 - started} ms");
            session = new PickSession(request, monitors, desktop, frozen, windows, log);
            using var registration = cancellationToken.Register(() => dispatcher.BeginInvoke(() => session.Cancel()));
            var result = await session.RunAsync();
            if (result is not null)
            {
                LastPick = (ScreenSelection.FromSdk(result.Bounds), DateTimeOffset.UtcNow);
            }

            log.Info(null, result is null ? "Screen area: cancelled" : $"Screen area: {result.Bounds.Width}×{result.Bounds.Height}{(result.ActionId is { } a ? $", action {a}" : "")}");
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            log.Error(null, "Screen area could not be picked", ex);
            return null;
        }
        finally
        {
            // Whatever happened: no overlay may stay behind (it would catch the mouse and cover the bar).
            session?.Close();
        }
    }

    /// <summary>One pick: the overlays and what the mouse does.</summary>
    private sealed class PickSession
    {
        private readonly ScreenAreaRequest _request;
        private readonly IReadOnlyList<MonitorInfo> _monitors;
        private readonly PixelRect _desktop;
        private readonly BitmapSource _frozen;
        private readonly IReadOnlyList<ScreenWindow> _windows;
        private readonly IUeLogger _log;
        private readonly List<AreaOverlay> _overlays = [];
        private readonly TaskCompletionSource<ScreenAreaResult?> _done = new();
        private PixelPoint _start;
        private bool _dragging;
        private PixelRect? _selection;
        private ScreenWindow? _selectedWindow;
        private bool _waitingForAction;
        private CapturedPixels? _windowShot;
        private int _selectionGeneration;

        public PickSession(ScreenAreaRequest request, IReadOnlyList<MonitorInfo> monitors, PixelRect desktop, BitmapSource frozen, IReadOnlyList<ScreenWindow> windows, IUeLogger log)
        {
            _request = request;
            _monitors = monitors;
            _desktop = desktop;
            _frozen = frozen;
            _windows = windows;
            _log = log;
        }

        public Task<ScreenAreaResult?> RunAsync()
        {
            var hint = _request.Mode switch
            {
                ScreenAreaMode.Window => Loc.Get("Pick_HintWindow"),
                ScreenAreaMode.Screen => Loc.Get("Pick_HintScreen"),
                _ => Loc.Get("Pick_HintRegion"),
            };
            foreach (var monitor in _monitors)
            {
                var part = new Int32Rect(monitor.Bounds.X - _desktop.X, monitor.Bounds.Y - _desktop.Y, monitor.Bounds.Width, monitor.Bounds.Height);
                var slice = new CroppedBitmap(_frozen, part);
                slice.Freeze();
                var overlay = new AreaOverlay(monitor, slice, hint);
                overlay.MouseLeftButtonDown += (_, e) => { e.Handled = true; Down(overlay); };
                overlay.MouseMove += (_, _) => Move();
                overlay.MouseLeftButtonUp += (_, e) => { e.Handled = true; Up(overlay); };
                overlay.MouseRightButtonUp += (_, e) => { e.Handled = true; Cancel(); };
                overlay.PreviewKeyDown += (_, e) => Key(e);
                overlay.ActionChosen += Choose;
                overlay.Closed += (_, _) => Cancel();
                _overlays.Add(overlay);
            }

            foreach (var overlay in _overlays)
            {
                overlay.Show();
            }

            var underMouse = MonitorService.CursorPosition() is { } c ? _overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(c)) : null;
            var focus = underMouse ?? _overlays[0];
            focus.Activate();
            if (focus.Handle != IntPtr.Zero)
            {
                Win32.SetForegroundWindow(focus.Handle);
            }

            focus.Focus();
            Keyboard.Focus(focus);

            if (_request.Mode == ScreenAreaMode.Screen && Cursor() is { } point)
            {
                Select(MonitorAt(point).Bounds, null);
            }
            else
            {
                Move();
            }

            return _done.Task;
        }

        private PixelPoint? Cursor() => MonitorService.CursorPosition();

        private MonitorInfo MonitorAt(PixelPoint point) => _monitors.FirstOrDefault(m => m.Bounds.Contains(point)) ?? _monitors.First(m => m.IsPrimary || ReferenceEquals(m, _monitors[0]));

        /// <summary>What a click at the point would take in the current mode: a window or the monitor.</summary>
        private (PixelRect Area, ScreenWindow? Window) ClickTarget(PixelPoint point)
        {
            if (_request.Mode != ScreenAreaMode.Screen && WindowFinder.At(_windows, point) is { } window)
            {
                return (ScreenSelection.Clip(window.Bounds, _desktop), window);
            }

            return (MonitorAt(point).Bounds, null);
        }

        private void Down(AreaOverlay overlay)
        {
            if (Cursor() is not { } point || overlay.IsOnActions(point))
            {
                return;
            }

            if (_waitingForAction)
            {
                // A press beside the picked area: pick again.
                _waitingForAction = false;
                foreach (var o in _overlays)
                {
                    o.HideActions();
                }
            }

            _start = point;
            _dragging = true;
            _selection = null;
            _selectedWindow = null;
            _windowShot = null;
            _selectionGeneration++; // a window capture still running belongs to the old pick
            overlay.CaptureMouse();
            Refresh();
        }

        private void Move()
        {
            if (Cursor() is not { } point || _waitingForAction)
            {
                return;
            }

            if (_dragging && _request.Mode == ScreenAreaMode.Region && !ScreenSelection.IsClick(_start, point))
            {
                _selection = ScreenSelection.FromDrag(_start, point, _desktop);
                foreach (var overlay in _overlays)
                {
                    overlay.ShowHover(null);
                }

                Refresh();
                return;
            }

            if (_dragging)
            {
                return;
            }

            var (area, _) = ClickTarget(point);
            foreach (var overlay in _overlays)
            {
                overlay.ShowHover(area);
            }
        }

        private void Up(AreaOverlay overlay)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            overlay.ReleaseMouseCapture();
            if (Cursor() is not { } point)
            {
                return;
            }

            if (_request.Mode == ScreenAreaMode.Region && !ScreenSelection.IsClick(_start, point))
            {
                var area = ScreenSelection.FromDrag(_start, point, _desktop);
                if (area.Width >= 2 && area.Height >= 2)
                {
                    Select(area, null);
                }

                return;
            }

            var (target, window) = ClickTarget(point);
            Select(target, window);
        }

        private void Select(PixelRect area, ScreenWindow? window)
        {
            _selection = area;
            _selectedWindow = window;
            _windowShot = null;
            foreach (var overlay in _overlays)
            {
                overlay.ShowHover(null);
            }

            Refresh();
            if (_request.Mode == ScreenAreaMode.Window && window is not null)
            {
                _ = CaptureWindowThenOfferAsync(area, window);
                return;
            }

            Offer(area, null);
        }

        /// <summary>
        /// P2-6-Fix 1: mode "window" – only the window's own content, also if something lies in front of it. Taken now
        /// (not from the frozen screen); if Windows cannot give it, the frozen screen is used and the bar says so.
        /// </summary>
        private async Task CaptureWindowThenOfferAsync(PixelRect area, ScreenWindow window)
        {
            var generation = ++_selectionGeneration;
            var scale = MonitorAt(area.Center).Scale;
            var started = Environment.TickCount64;
            var shot = await Task.Run(() => WindowCapture.Capture(window.Handle, scale, message => _log.Info(null, message)));
            if (generation != _selectionGeneration || _done.Task.IsCompleted)
            {
                return; // picked something else meanwhile, or closed
            }

            if (shot is { } s)
            {
                _windowShot = s.Image;
                _log.Info(null, $"Window capture: {s.Method}, {s.Image.Width}×{s.Image.Height} in {Environment.TickCount64 - started} ms");
                Offer(area, null);
            }
            else
            {
                _log.Info(null, "Window capture: not possible – cut from the screen (windows in front are included)");
                Offer(area, Loc.Get("Pick_OverlapIncluded"));
            }
        }

        private void Offer(PixelRect area, string? notice)
        {
            if (_request.Actions.Count == 0)
            {
                Finish(null);
                return;
            }

            _waitingForAction = true;
            var main = _overlays.OrderByDescending(o => Overlap(o.Monitor.Bounds, area)).First();
            main.ShowActions(_request.Actions, area, notice);
        }

        private void Refresh()
        {
            var sizeOn = _selection is { } s
                ? _overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(new PixelPoint(s.X, s.Y))) ?? _overlays.OrderByDescending(o => Overlap(o.Monitor.Bounds, s)).First()
                : null;
            foreach (var overlay in _overlays)
            {
                overlay.UpdateMask(_selection, ReferenceEquals(overlay, sizeOn));
            }
        }

        private void Key(KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
            else if (_waitingForAction && _request.Actions.Count > 0)
            {
                // P2-5-Fix 3: Enter = first action, the actions' own keys (Ctrl+C, Ctrl+S) – only while the bar is shown.
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
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

                if (PickerKeys.ActionFor([.. _request.Actions.Select(a => (a.Id, a.Shortcut))], modifiers, KeyInterop.VirtualKeyFromKey(key)) is { } id)
                {
                    e.Handled = true;
                    Choose(id);
                }
            }
        }

        private void Choose(string actionId) => Finish(actionId);

        private void Finish(string? actionId)
        {
            if (_done.Task.IsCompleted || _selection is not { IsEmpty: false } area)
            {
                return;
            }

            byte[]? png = null;
            try
            {
                png = _windowShot is { } shot ? ScreenGrab.Png(shot) : ScreenGrab.Png(_frozen, new Int32Rect(area.X - _desktop.X, area.Y - _desktop.Y, area.Width, area.Height));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
            {
                _log.Error(null, "Picked area could not be encoded", ex);
            }

            var result = new ScreenAreaResult(ScreenSelection.ToSdk(area), ScreenSelection.ToSdk(_monitors))
            {
                Mode = _request.Mode,
                Image = png,
                ActionId = actionId,
                WindowTitle = _selectedWindow?.Title is { Length: > 0 } title ? title : null,
            };
            _done.TrySetResult(result); // before closing: closing an overlay counts as "cancel"
            Close();
        }

        public void Cancel()
        {
            if (_done.Task.IsCompleted)
            {
                return;
            }

            _done.TrySetResult(null);
            Close();
        }

        /// <summary>Close every overlay for real (not only made transparent) – safe to call twice.</summary>
        public void Close()
        {
            foreach (var overlay in _overlays.ToList())
            {
                _overlays.Remove(overlay);
                overlay.Hide();
                overlay.Close();
            }

            _done.TrySetResult(null);
        }

        private static long Overlap(PixelRect a, PixelRect b)
        {
            var r = ScreenSelection.Clip(a, b);
            return r.IsEmpty ? 0 : (long)r.Width * r.Height;
        }
    }
}
