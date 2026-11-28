// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UeBar.App.Bar;
using UeBar.App.Infrastructure;
using UeBar.App.Interop;
using UeBar.App.Localization;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Pins;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.App.Pins;

/// <summary>
/// The pins on the screen (P2-5): one <see cref="PinWindow"/> per visible pin, follows the
/// <see cref="PinManager"/>, places pins per monitor (missing monitor → primary), dims them (OLED), moves them
/// by a pixel now and then, hides them over fullscreen apps and owns the hotkey "show/hide all pins".
/// Timers run only while pins are visible, and only as often as needed (no millisecond ticks).
/// UI thread only (changes of the manager are handed over).
/// </summary>
internal sealed class PinController : IDisposable
{
    private static readonly TimeSpan FullscreenCheck = TimeSpan.FromSeconds(2);

    private readonly PinManager _pins;
    private readonly SettingsService _settings;
    private readonly GlobalHotkeys _hotkeys;
    private readonly ClipboardAccess _clipboard;
    private readonly Dispatcher _dispatcher;
    private readonly IUeLogger _log;
    private readonly FullscreenDetector _fullscreen;
    private readonly Dictionary<string, PinWindow> _windows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fullscreenMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _glowTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly PinGlow _glow;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private PinSettings _options;
    private PixelPoint _offset;
    private bool _disposed;

    /// <summary>Content actions of the plugins for the pin menu (P3-1); null = no "Actions" entry.</summary>
    public UeBar.Core.Modules.ContentActionService? ContentActions { get; set; }

    public PinController(PinManager pins, SettingsService settings, GlobalHotkeys hotkeys, ClipboardAccess clipboard, Dispatcher dispatcher, IUeLogger log)
    {
        _pins = pins;
        _settings = settings;
        _hotkeys = hotkeys;
        _clipboard = clipboard;
        _dispatcher = dispatcher;
        _log = log;
        _fullscreen = new FullscreenDetector(() => IntPtr.Zero);
        _options = settings.GetSection<PinSettings>(PinSettings.Section);
        _glow = new PinGlow(DateTimeOffset.UtcNow);
        _glowTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _glowTimer.Tick += (_, _) => OnGlowTick();
        _fullscreenTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FullscreenCheck };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
    }

    public PinSettings Options => _options;

    public PinManager Manager => _pins;

    /// <summary>Raised after the list of pins changed (for the pin list in the bar), on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Status of the hotkey "show/hide all pins" (settings page).</summary>
    public (HotkeyRegistration Result, string Message) HotkeyStatus =>
        _hotkeys.Set.StatusOf(HotkeyName) ?? (HotkeyRegistration.Invalid, "");

    private const string HotkeyName = "pins.toggle";

    public void Start()
    {
        ApplyOptions();
        _pins.Changed += OnManagerChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        RefreshMonitors();
        foreach (var pin in _pins.Pins.Where(p => !p.Hidden))
        {
            Show(pin);
        }

        UpdateTimers();
        _log.Info(null, $"Pins shown: {_windows.Count} of {_pins.Count}");
    }

    // ------------------------------------------------------------------ settings

    public void UpdateOptions(Action<PinSettings> change)
    {
        var next = _settings.GetSection<PinSettings>(PinSettings.Section);
        change(next);
        _settings.SetSection(PinSettings.Section, next);
        _options = next;
        ApplyOptions();
    }

    /// <summary>New hotkey "show/hide all pins" – only taken if it works; empty = none.</summary>
    public (HotkeyRegistration Result, string Message) ChangeHotkey(string text)
    {
        if (text.Length == 0)
        {
            UpdateOptions(o => o.Hotkey = "");
            return HotkeyStatus;
        }

        var result = _hotkeys.Set.TryChange(HotkeyName, text, ToggleAll);
        if (result.Result == HotkeyRegistration.Registered && Hotkey.TryParse(text) is { } hotkey)
        {
            _options.Hotkey = hotkey.Text;
            var saved = _settings.GetSection<PinSettings>(PinSettings.Section);
            saved.Hotkey = hotkey.Text;
            _settings.SetSection(PinSettings.Section, saved);
            _log.Info(null, $"Pins hotkey: {hotkey.Text}");
        }

        return result;
    }

    private void ApplyOptions()
    {
        _glow.DimAfter = _options.DimAfter;
        _glow.PixelShift = _options.PixelShift;
        _hotkeys.Bind(HotkeyName, _options.Hotkey, ToggleAll);
        if (!_options.PixelShift)
        {
            ApplyOffset(new PixelPoint(0, 0));
        }

        if (!_options.HideInFullscreen && _fullscreenMonitors.Count > 0)
        {
            _fullscreenMonitors.Clear();
            ApplyFullscreenVisibility();
        }

        foreach (var window in _windows.Values)
        {
            window.SetDimmed(_glow.IsDimmed(window.Id), _options.EffectiveDimLevel, animate: false);
        }

        UpdateTimers();
    }

    public double FontSizeOf(PinRecord pin) => pin.FontSize > 0 ? pin.FontSize : Math.Clamp(_options.FontSize, 8, 96);

    // ------------------------------------------------------------------ show / hide

    /// <summary>
    /// Hotkey and pin list: hide all if any is visible, otherwise show all. Showing all also ends "Let clicks
    /// through" – the way back to a pin that cannot be clicked any more (besides the pin list in the bar).
    /// </summary>
    public void ToggleAll()
    {
        var anyVisible = _pins.Pins.Any(p => !p.Hidden);
        if (!anyVisible)
        {
            foreach (var pin in _pins.Pins.Where(p => p.ClickThrough))
            {
                SetClickThrough(pin.Id, false);
            }
        }

        _pins.SetAllHidden(anyVisible);
        _log.Info(null, anyVisible ? "All pins hidden" : "All pins shown");
    }

    public void SetHidden(string id, bool hidden) => _pins.SetHidden(id, hidden);

    public void Close(string id) => _pins.Close(id);

    public void CloseAll() => _pins.CloseAll();

    /// <summary>Quick note from the bar.</summary>
    public bool CreateNote(string text) => _pins.CreateText(text, fontSize: null) is not null;

    /// <summary>Click-through on/off. Switching it on shows once, at the pin, how to get it back (Ctrl).</summary>
    public void SetClickThrough(string id, bool on)
    {
        _pins.Update(id, PinChangeKind.Options, p => p.ClickThrough = on);
        if (on && _windows.TryGetValue(id, out var window))
        {
            window.ShowHint(Loc.Get("Pin_ClickThroughHint"));
        }
    }

    /// <summary>Tray "Make all pins clickable again" (P2-5-Fix 1).</summary>
    public int ReleaseAllClickThrough()
    {
        var through = _pins.Pins.Where(p => p.ClickThrough).Select(p => p.Id).ToList();
        foreach (var id in through)
        {
            _pins.Update(id, PinChangeKind.Options, p => p.ClickThrough = false);
        }

        _log.Info(null, $"Pins clickable again: {through.Count}");
        return through.Count;
    }

    public bool AnyClickThrough => _pins.Pins.Any(p => p.ClickThrough);

    // ------------------------------------------------------------------ click-through pins and the mouse (P2-5-Fix 1)

    private BarController? _pointer;

    /// <summary>
    /// The core's mouse polling watches the pins that let clicks through: over one it becomes bright, with Ctrl
    /// held it is usable for a moment (thin frame, right click works); Ctrl released or mouse gone → through again.
    /// No timer of its own; the watch is only active while such pins are visible.
    /// </summary>
    public void AttachPointer(BarController bar)
    {
        _pointer = bar;
        bar.WatchedPointerChanged += OnPointer;
        RefreshWatch();
    }

    private void RefreshWatch()
    {
        if (_pointer is null || _disposed)
        {
            return;
        }

        var areas = _windows.Values
            .Where(w => w.Pin.ClickThrough && w.IsVisible && w.PhysicalBounds is not null)
            .Select(w => (w.Id, w.PhysicalBounds!.Value))
            .ToList();
        _pointer.SetWatchedAreas(areas);
    }

    private void OnPointer(PointerChange change)
    {
        if (change.Left is { } left && _windows.TryGetValue(left, out var gone))
        {
            gone.SetTemporarilyUsable(false);
            Hover(gone, inside: false);
        }

        if (change.Over is { } over && _windows.TryGetValue(over, out var window) && window.Pin.ClickThrough)
        {
            Hover(window, inside: true);
            window.SetTemporarilyUsable(change.Ctrl);
        }
    }

    private void OnManagerChanged(object? sender, PinChangedEventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            Apply(e);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Apply(e));
        }
    }

    private void Apply(PinChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var pin = _pins.Find(e.Pin.Id);
        switch (e.Kind)
        {
            case PinChangeKind.Added when pin is { Hidden: false }:
                Show(pin);
                break;
            case PinChangeKind.Removed:
                Remove(e.Pin.Id);
                break;
            case PinChangeKind.Visibility when pin is not null:
                if (pin.Hidden)
                {
                    Remove(pin.Id);
                }
                else
                {
                    Show(pin);
                }

                break;
            case PinChangeKind.Content or PinChangeKind.Options when pin is not null && _windows.TryGetValue(pin.Id, out var window):
                window.Update(pin, e.Kind);
                break;
        }

        UpdateTimers();
        RefreshWatch();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Show(PinRecord pin)
    {
        if (_windows.ContainsKey(pin.Id))
        {
            return;
        }

        RefreshMonitors();
        PixelRect rect;
        if (pin.Placement is { } placement && PinGeometry.Resolve(placement, _monitors) is { } resolved)
        {
            rect = resolved;
        }
        else
        {
            rect = PinGeometry.Initial(pin, _monitors, MonitorService.CursorPosition(), _windows.Count);
            var save = PinGeometry.ToPlacement(rect, _monitors);
            _pins.Update(pin.Id, PinChangeKind.Placement, p => p.Placement = save);
        }

        PinWindow window;
        try
        {
            window = new PinWindow(pin, this);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Pin {pin.Id} cannot be shown: {ex.Message}");
            return;
        }

        _windows[pin.Id] = window;
        window.Closed += (_, _) => _windows.Remove(pin.Id);
        window.Place(Shifted(rect));
        _glow.Track(pin.Id, DateTimeOffset.UtcNow);
        if (IsOnFullscreenMonitor(rect))
        {
            return; // shown when the fullscreen app is gone
        }

        window.Show();
        window.Place(Shifted(rect)); // again after a DPI change on the way to the monitor
    }

    private void Remove(string id)
    {
        _glow.Forget(id);
        if (_windows.Remove(id, out var window))
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ placement

    private void RefreshMonitors() => _monitors = MonitorService.GetMonitors();

    private void OnDisplayChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // Monitor plugged in/out or scaling changed: every pin back to its saved place (or the primary monitor).
        RefreshMonitors();
        foreach (var window in _windows.Values.ToList())
        {
            if (_pins.Find(window.Id)?.Placement is { } placement && PinGeometry.Resolve(placement, _monitors) is { } rect)
            {
                window.Place(Shifted(rect));
            }
        }

        RefreshWatch();
        _log.Info(null, $"Pins: displays changed ({_monitors.Count} monitors)");
    });

    public void SavePlacement(PinWindow window)
    {
        if (window.PhysicalBounds is not { } rect)
        {
            return;
        }

        RefreshMonitors();
        var placement = PinGeometry.ToPlacement(Unshifted(rect), _monitors);
        _pins.Update(window.Id, PinChangeKind.Placement, p => p.Placement = placement);
        Touch(window);
        RefreshWatch();
    }

    private int SnapThreshold(PixelRect rect) =>
        (int)Math.Round(PinGeometry.SnapDistance * (PinGeometry.MonitorAt(rect, _monitors)?.Scale ?? 1));

    public PixelRect SnapMove(PixelRect rect) => PinGeometry.SnapMove(rect, _monitors.Select(m => m.WorkArea), SnapThreshold(rect));

    public PixelRect SnapResize(PixelRect rect, bool left, bool top, bool right, bool bottom, int minimum)
    {
        var snapped = PinGeometry.SnapResize(rect, _monitors.Select(m => m.WorkArea), SnapThreshold(rect), left, top, right, bottom);
        return snapped.Width < minimum || snapped.Height < minimum ? rect with { Width = Math.Max(minimum, rect.Width), Height = Math.Max(minimum, rect.Height) } : snapped;
    }

    private PixelRect Shifted(PixelRect rect) => rect with { X = rect.X + _offset.X, Y = rect.Y + _offset.Y };

    private PixelRect Unshifted(PixelRect rect) => rect with { X = rect.X - _offset.X, Y = rect.Y - _offset.Y };

    private void ApplyOffset(PixelPoint offset)
    {
        if (offset == _offset)
        {
            return;
        }

        var dx = offset.X - _offset.X;
        var dy = offset.Y - _offset.Y;
        _offset = offset;
        foreach (var window in _windows.Values)
        {
            if (window.PhysicalBounds is { } b)
            {
                window.Place(b with { X = b.X + dx, Y = b.Y + dy });
            }
        }

        RefreshWatch();
    }

    // ------------------------------------------------------------------ dimming (OLED)

    public void Hover(PinWindow window, bool inside)
    {
        if (_glow.Hover(window.Id, inside, DateTimeOffset.UtcNow) || inside)
        {
            window.SetDimmed(false, _options.EffectiveDimLevel, animate: true);
        }

        UpdateTimers();
    }

    /// <summary>The user did something with the pin: bright again, waiting time starts over.</summary>
    public void Touch(PinWindow window)
    {
        _glow.Touch(window.Id, DateTimeOffset.UtcNow);
        window.SetDimmed(false, _options.EffectiveDimLevel, animate: true);
        UpdateTimers();
    }

    private void OnGlowTick()
    {
        _glowTimer.Stop();
        var (changed, shifted) = _glow.Update(DateTimeOffset.UtcNow);
        foreach (var id in changed)
        {
            if (_windows.TryGetValue(id, out var window))
            {
                window.SetDimmed(_glow.IsDimmed(id), _options.EffectiveDimLevel, animate: true);
            }
        }

        if (shifted)
        {
            ApplyOffset(_glow.Offset);
        }

        UpdateTimers();
    }

    private void UpdateTimers()
    {
        if (_disposed)
        {
            return;
        }

        var anyShown = _windows.Count > 0;
        if (anyShown && _glow.NextDue(DateTimeOffset.UtcNow) is { } due)
        {
            _glowTimer.Interval = due < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : due;
            _glowTimer.Start();
        }
        else
        {
            _glowTimer.Stop();
        }

        if (anyShown && _options.HideInFullscreen)
        {
            if (!_fullscreenTimer.IsEnabled)
            {
                _fullscreenTimer.Start();
            }
        }
        else
        {
            _fullscreenTimer.Stop();
        }
    }

    // ------------------------------------------------------------------ fullscreen

    private void CheckFullscreen()
    {
        if (!_options.HideInFullscreen || _windows.Count == 0)
        {
            return;
        }

        var foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero && PinInterop.GetWindowThreadProcessId(foreground, out var owner) != 0 && owner == Environment.ProcessId)
        {
            return; // our own window is in front: a pin being edited, the picker's overlay, settings – no fullscreen app
        }

        var now = new HashSet<string>(_monitors.Where(m => _fullscreen.IsFullscreenOn(m)).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        if (now.SetEquals(_fullscreenMonitors))
        {
            return;
        }

        _fullscreenMonitors.Clear();
        _fullscreenMonitors.UnionWith(now);
        _log.Info(null, now.Count > 0 ? $"Pins hidden over fullscreen ({now.Count} monitor(s))" : "Pins back after fullscreen");
        ApplyFullscreenVisibility();
    }

    private bool IsOnFullscreenMonitor(PixelRect rect) =>
        PinGeometry.HiddenByFullscreen(rect, _monitors, _fullscreenMonitors, _options.HideInFullscreen);

    private void ApplyFullscreenVisibility()
    {
        foreach (var window in _windows.Values)
        {
            if (window.PhysicalBounds is not { } rect)
            {
                continue;
            }

            if (IsOnFullscreenMonitor(Unshifted(rect)))
            {
                window.Hide();
            }
            else if (!window.IsVisible)
            {
                window.Show();
            }
        }

        RefreshWatch();
    }

    // ------------------------------------------------------------------ content and menu

    public BitmapSource? LoadImage(PinRecord pin)
    {
        if (_pins.ImagePath(pin) is not { } path || !File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // the file stays free (can be replaced or deleted)
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void SetText(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return; // an empty pin makes no sense – keep the old text
        }

        _pins.Update(id, PinChangeKind.Content, p =>
        {
            var auto = p.Title == PinManager.TitleOf(null, p.Text ?? "");
            p.Text = text;
            if (auto)
            {
                p.Title = PinManager.TitleOf(null, text);
            }
        });
    }

    public void SetZoom(string id, double zoom, double panX, double panY) =>
        _pins.Update(id, PinChangeKind.Options, p =>
        {
            p.Zoom = zoom;
            p.PanX = panX;
            p.PanY = panY;
        });

    public void ChangeFontSize(string id, int direction)
    {
        if (_pins.Find(id) is not { } pin)
        {
            return;
        }

        var size = FontSizeOf(pin) + (direction * (FontSizeOf(pin) >= 24 ? 2 : 1));
        _pins.Update(id, PinChangeKind.Options, p => p.FontSize = Math.Clamp(size, 8, 96));
    }

    public void ShowMenu(PinWindow window)
    {
        Touch(window);
        var pin = window.Pin;
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("DarkContextMenu"), PlacementTarget = window };

        MenuItem Item(string text, string glyph, Action click, bool? check = null, string? gesture = null)
        {
            var item = new MenuItem
            {
                Header = text,
                Tag = glyph,
                Style = (Style)Application.Current.FindResource("DarkMenuItem"),
                InputGestureText = gesture ?? "",
            };
            if (check is { } c)
            {
                item.IsCheckable = true;
                item.IsChecked = c;
            }

            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }

        void Separator() => menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") });

        Item(Loc.Get("Pin_Copy"), "", () => _ = CopyAsync(window));
        ActionsItem(menu, window);
        if (pin.Kind == PinKind.Image)
        {
            Item(Loc.Get("Pin_SaveAs"), "", () => SaveAs(window));
            if (pin.Zoom > 1.001)
            {
                Item(Loc.Get("Pin_ZoomReset"), "", () => SetZoom(pin.Id, 1, 0, 0));
            }
        }
        else
        {
            Item(Loc.Get("Pin_Edit"), "", window.BeginEdit, gesture: Loc.Get("Pin_Edit_Gesture"));
            Item(Loc.Get("Pin_FontLarger"), "", () => ChangeFontSize(pin.Id, 1));
            Item(Loc.Get("Pin_FontSmaller"), "", () => ChangeFontSize(pin.Id, -1));
        }

        Separator();
        menu.Items.Add(OpacityItem(window, pin));
        Item(Loc.Get("Pin_Lock"), "", () => _pins.Update(pin.Id, PinChangeKind.Options, p => p.Locked = !p.Locked), pin.Locked);
        Item(Loc.Get("Pin_ClickThrough"), "", () => SetClickThrough(pin.Id, !pin.ClickThrough), pin.ClickThrough)
            .ToolTip = Loc.F("Pin_ClickThrough_Tip", Hotkey.TryParse(_options.Hotkey)?.DisplayText ?? Loc.Get("Pins_PanelName"));
        Separator();
        Item(Loc.Get("Pin_Hide"), "", () => SetHidden(pin.Id, true));
        Item(Loc.Get("Pin_Close"), "", () => Close(pin.Id));
        menu.IsOpen = true;
    }

    /// <summary>
    /// Opacity in the menu: the same slider as everywhere (<see cref="OneUiSliderInput"/>) – drag with a live
    /// preview on the pin, saved once on release (P2-5-Fix 1). No fixed stops.
    /// </summary>
    private MenuItem OpacityItem(PinWindow window, PinRecord pin)
    {
        var slider = new Slider
        {
            Minimum = 20,
            Maximum = 100,
            SmallChange = 1,
            Value = Math.Round(pin.Opacity * 100),
            Width = 160,
            Style = (Style)Application.Current.FindResource("OneUiSlider"),
            Margin = new Thickness(0, 6, 0, 2),
        };
        slider.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        var label = new TextBlock { Text = Loc.F("Pin_Opacity", (int)slider.Value) };
        OneUiSliderInput.Attach(
            slider,
            live: value =>
            {
                label.Text = Loc.F("Pin_Opacity", (int)Math.Round(value));
                window.PreviewOpacity(value / 100);
            },
            commit: value =>
            {
                label.Text = Loc.F("Pin_Opacity", (int)Math.Round(value));
                _pins.Update(pin.Id, PinChangeKind.Options, p => p.Opacity = value / 100);
            });
        var content = new StackPanel();
        content.Children.Add(label);
        content.Children.Add(slider);
        return new MenuItem
        {
            Header = content,
            Tag = "",
            StaysOpenOnClick = true,
            Style = (Style)Application.Current.FindResource("DarkMenuItem"),
        };
    }

    /// <summary>
    /// P3-1: "Actions" – a submenu with the content actions of the plugins for this pin (text or image). Content goes to a
    /// plugin only when the user clicks one of them.
    /// </summary>
    private void ActionsItem(ContextMenu menu, PinWindow window)
    {
        var pin = window.Pin;
        var kind = pin.Kind == PinKind.Text ? ContentKinds.Text : ContentKinds.Image;
        if (ContentActions is not { } service || service.GetActions(kind) is not { Count: > 0 } actions)
        {
            return;
        }

        var parent = new MenuItem { Header = Loc.Get("Pin_Actions"), Tag = "E8BD", Style = (Style)Application.Current.FindResource("DarkMenuItem") };
        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                Header = action.Action.Label,
                Tag = action.Action.Icon is { Length: > 2 } icon && int.TryParse(icon[2..], System.Globalization.NumberStyles.HexNumber, null, out var code) ? char.ConvertFromUtf32(code) : "E8BD",
                ToolTip = action.ModuleName,
                Style = (Style)Application.Current.FindResource("DarkMenuItem"),
            };
            var chosen = action;
            item.Click += (_, _) =>
            {
                var content = pin.Kind == PinKind.Text
                    ? new ContentItem { Text = pin.Text, SourceTitle = pin.Title }
                    : new ContentItem { ImagePng = PinWindow.Png(window.ImageSource), SourceTitle = pin.Title };
                if (!service.Run(null, chosen.ModuleId, chosen.Action.Id, content, new ContentActionStart { Origin = ContentOrigin.Pin }))
                {
                    _log.Info(null, "Pin action could not start");
                }
            };
            parent.Items.Add(item);
        }

        menu.Items.Add(parent);
    }

    private async Task CopyAsync(PinWindow window)
    {
        var ok = window.Pin.Kind == PinKind.Text
            ? await _clipboard.SetTextAsync(window.Pin.Text ?? "", CancellationToken.None)
            : PinWindow.Png(window.ImageSource) is { } png && await _clipboard.SetImageAsync(png, CancellationToken.None);
        _log.Info(null, $"Pin copied: {ok}");
    }

    /// <summary>The shared "Save as" dialog (last folder remembered per PC, P2-5-Fix 1).</summary>
    public SaveDialog? SaveDialog { get; set; }

    private void SaveAs(PinWindow window)
    {
        if (PinWindow.Png(window.ImageSource) is not { } png || SaveDialog is not { } dialog)
        {
            return;
        }

        var name = string.Concat(window.Pin.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = dialog.Ask(PinManager.HostId, new SaveFileRequest((name.Length > 0 ? name : "Pin") + ".png")
        {
            Title = Loc.Get("Pin_SaveAs"),
            TypeName = Loc.Get("Pin_SaveType"),
        });
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(path, png);
            _log.Info(null, "Pin image saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(null, $"Pin image not saved: {ex.Message}");
            UeDialog.Warning(null, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _pointer?.SetWatchedAreas([]);
        if (_pointer is not null)
        {
            _pointer.WatchedPointerChanged -= OnPointer;
        }

        _disposed = true;
        _pins.Changed -= OnManagerChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _glowTimer.Stop();
        _fullscreenTimer.Stop();
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }

        _windows.Clear();
    }
}
