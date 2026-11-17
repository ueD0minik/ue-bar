// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;
using System.Windows.Threading;
using UeBar.Sdk;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Settings;

namespace UeBar.App.Bar;

/// <summary>
/// Connects state machine, mouse polling, window, fullscreen detection, monitor list and the
/// bar settings. Runs on the UI thread (the polling reports through the dispatcher).
/// </summary>
internal sealed class BarController : IDisposable
{
    public const string SettingsSection = "bar";

    private readonly BarWindow _window;
    private readonly BarViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly IUeLogger _log;
    private readonly Dispatcher _dispatcher;
    private readonly BarStateMachine _machine = new();
    private readonly CursorPoller _poller;
    private readonly FullscreenDetector _fullscreen;
    private BarSettings _settings = new();
    private IntPtr _powerNotification;
    private bool _sessionLocked;
    private bool _displayOff;
    private bool _monitorRefreshQueued;
    private bool? _pollerPaused;
    private readonly OledGuard _oled = new(DateTimeOffset.UtcNow);
    private DispatcherTimer? _oledTimer;

    public BarController(BarWindow window, BarViewModel viewModel, SettingsService settings, IUeLogger log, Dispatcher dispatcher)
    {
        _window = window;
        _viewModel = viewModel;
        _settingsService = settings;
        _log = log;
        _dispatcher = dispatcher;
        _fullscreen = new FullscreenDetector(() => _window.Handle);
        _poller = new CursorPoller(p => _dispatcher.BeginInvoke(DispatcherPriority.Input, () => OnTick(p)), log);
    }

    /// <summary>Settings or state changed (for the tray menu).</summary>
    public event EventHandler? Changed;

    public BarSettings Settings => _settings.Clone();

    public IReadOnlyList<MonitorInfo> Monitors => _machine.Monitors;

    public bool IsPinned => _machine.State == BarState.Pinned;

    /// <summary>Panel open or pinned (not only the pill).</summary>
    public bool IsPanelOpen => _machine.State is BarState.Open or BarState.Pinned;

    /// <summary>The global hotkey of the quick search was pressed.</summary>
    public event EventHandler? QuickSearchHotkeyPressed;

    /// <summary>Result of registering the hotkey (for the tray hint).</summary>
    public (HotkeyRegistration Result, string Message) HotkeyStatus { get; private set; } = (HotkeyRegistration.Invalid, "");

    private const int QuickSearchHotkeyId = 1;

    public void Start()
    {
        _settings = _settingsService.GetSection<BarSettings>(SettingsSection);
        _window.DataContext = _viewModel;
        _window.Log = _log;
        _window.EnsureCreated();
        _window.MessageReceived += OnWindowMessage;
        _window.DragCompleted += OnDragCompleted;
        _window.SurfaceChanged += (_, _) => _machine.BarBounds = _window.SurfaceRect;
        RegisterSystemNotifications();

        _machine.IsFullscreen = m => _fullscreen.IsFullscreenOn(m);
        _machine.StateChanged += OnStateChanged;
        _viewModel.PinToggleRequested += (_, _) => TogglePin();
        _viewModel.ContentSizeChanged += (_, _) =>
        {
            _window.RefreshPanelSize();
            _machine.BarBounds = _window.SurfaceRect;
        };

        RefreshMonitors();
        _poller.Start();
        UpdatePollerPause();
        RestorePinned();
        RegisterHotkey();
        ConfigureOled();
        _viewModel.PillChanged += (_, _) => _window.RefreshPill();
        _log.Info(null, $"Bar started: mode {_settings.MonitorMode}, {DescribeMonitors()}");
    }

    private void RegisterHotkey()
    {
        var (result, _, message) = HotkeyBinder.Register(_settings.QuickSearchHotkey, (mods, vk) => ExtrasInterop.RegisterHotKey(_window.Handle, QuickSearchHotkeyId, mods, vk));
        HotkeyStatus = (result, message);
        if (result == HotkeyRegistration.Registered)
        {
            _log.Info(null, message);
        }
        else
        {
            _log.Warn(null, message);
        }
    }

    // ------------------------------------------------------------------ Settings (M7)

    /// <summary>Change a bar setting: takes effect right away and is saved.</summary>
    public void Update(Action<BarSettings> change)
    {
        Apply(change);
        ConfigureOled();
    }

    /// <summary>
    /// New hotkey for the quick search: register first, then save. In use/invalid →
    /// the old one stays active and saved, the message explains why.
    /// </summary>
    public (HotkeyRegistration Result, string Message) ChangeHotkey(string text)
    {
        if (_window.Handle == IntPtr.Zero)
        {
            return (HotkeyRegistration.Invalid, UeBar.App.Localization.Loc.Get("Hotkey_WindowNotReady"));
        }

        _hotkeySuspended = false; // recording ends here: afterwards a hotkey is registered again
        ExtrasInterop.UnregisterHotKey(_window.Handle, QuickSearchHotkeyId);
        var (result, hotkey, message) = HotkeyBinder.Register(text, (mods, vk) => ExtrasInterop.RegisterHotKey(_window.Handle, QuickSearchHotkeyId, mods, vk));
        if (result == HotkeyRegistration.Registered && hotkey is not null)
        {
            HotkeyStatus = (result, message);
            _log.Info(null, $"Hotkey registered: {hotkey.Text}");
            Apply(s => s.QuickSearchHotkey = hotkey.Text);
            return (result, message);
        }

        // Restore the old hotkey.
        _log.Info(null, $"New hotkey rejected ({result}): {text}");
        RegisterHotkey();
        return (result, message);
    }

    /// <summary>While recording in the settings window, release the own hotkey briefly (otherwise the window never sees it).</summary>
    public void SuspendHotkey(bool suspend)
    {
        if (_window.Handle == IntPtr.Zero || suspend == _hotkeySuspended)
        {
            return;
        }

        _hotkeySuspended = suspend;
        if (suspend)
        {
            ExtrasInterop.UnregisterHotKey(_window.Handle, QuickSearchHotkeyId);
        }
        else
        {
            ExtrasInterop.UnregisterHotKey(_window.Handle, QuickSearchHotkeyId);
            RegisterHotkey();
        }
    }

    private bool _hotkeySuspended;

    /// <summary>Number of columns changed (M7).</summary>
    public void ApplyColumns()
    {
        _window.ApplyPanelWidth();
        _viewModel.RefreshLayout();
        _machine.BarBounds = _window.SurfaceRect;
    }

    // ------------------------------------------------------------------ OLED protection (SPEC 5.5, M7)

    private void ConfigureOled()
    {
        _oled.DimAfter = _settings.OledDimMinutes > 0 ? TimeSpan.FromMinutes(_settings.OledDimMinutes) : TimeSpan.Zero;
        _oled.PixelShift = _settings.OledPixelShift;
        UpdateOledPinned();
        _oled.Update(DateTimeOffset.UtcNow);
        ApplyOled();
    }

    /// <summary>Only "really" pinned counts – not quick search, input or editing (work is going on there).</summary>
    private void UpdateOledPinned()
    {
        var pinned = _machine.State == BarState.Pinned && !_machine.IsHeld && !_heldForEdit && !_heldForInput;
        _oled.SetPinned(pinned, DateTimeOffset.UtcNow);
        if (pinned && (_oled.DimAfter > TimeSpan.Zero || _oled.PixelShift))
        {
            if (_oledTimer is null)
            {
                _oledTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(20) };
                _oledTimer.Tick += (_, _) =>
                {
                    if (_oled.Update(DateTimeOffset.UtcNow))
                    {
                        ApplyOled();
                        _log.Debug(null, $"OLED protection: {(_oled.IsDimmed ? "dimmed" : "bright")}, offset {_oled.Offset.X},{_oled.Offset.Y}");
                    }
                };
            }

            _oledTimer.Start();
        }
        else
        {
            _oledTimer?.Stop();
        }

        ApplyOled();
    }

    private void ApplyOled()
    {
        _window.SetDimmed(_oled.IsDimmed);
        _window.SetShift(_oled.Offset);
    }

    // ------------------------------------------------------------------ Extras (M6)

    /// <summary>May a message appear as a pill now? (not paused, no fullscreen block) The reason is for the log.</summary>
    public bool CanAnnounce(out string? reason)
    {
        reason = null;
        if (_settings.Paused)
        {
            reason = "bar paused";
            return false;
        }

        if (_sessionLocked || _displayOff)
        {
            reason = "locked/display off";
            return false;
        }

        if (AnnouncementMonitor() is { } monitor && _settings.BlockInFullscreen && _fullscreen.IsFullscreenOn(monitor))
        {
            reason = "fullscreen block";
            return false;
        }

        return true;
    }

    /// <summary>Show the pill without the mouse (notification). If the panel is open, it shows the message itself.</summary>
    public bool Announce(TimeSpan duration)
    {
        if (IsPanelOpen)
        {
            return true;
        }

        return AnnouncementMonitor() is { } monitor && _machine.Announce(monitor, DateTimeOffset.UtcNow + duration);
    }

    public void EndAnnounce() => _machine.EndAnnounce();

    /// <summary>Quick search: open and hold the panel (also when paused, not with a fullscreen block), activate the window.</summary>
    public bool OpenQuickSearch()
    {
        var monitor = _machine.ActiveMonitor ?? AnnouncementMonitor();
        if (monitor is null)
        {
            return false;
        }

        if (_settings.BlockInFullscreen && _fullscreen.IsFullscreenOn(monitor))
        {
            _log.Info(null, "Quick search: fullscreen block active – not opened");
            return false;
        }

        if (!_machine.OpenHeld(monitor))
        {
            _log.Info(null, $"Quick search: bar already pinned ({StateText}) – stays as it is");
        }

        _window.AllowActivation(true);
        return true;
    }

    /// <summary>
    /// Emergency exit (tray "Reset bar", M6-Fix 1): state to "hidden", release holding,
    /// window off immediately, window styles/focus/mouse capture back to the base state, register the
    /// hotkey again. If the bar is set to pinned, it appears pinned again afterwards.
    /// </summary>
    public void Reset()
    {
        _log.Info(null, $"Reset bar (before {StateText}, window shows {_window.Shows})");
        _heldForInput = false;
        _heldForEdit = false;
        _machine.Hide();
        _window.ResetWindow();
        _viewModel.IsPinned = false;
        _viewModel.IsPanelVisible = false;
        _viewModel.IsBarVisible = false;
        _poller.SetBarVisible(false);
        if (_window.Handle != IntPtr.Zero)
        {
            ExtrasInterop.UnregisterHotKey(_window.Handle, QuickSearchHotkeyId);
        }

        RegisterHotkey();
        RefreshMonitors();
        UpdatePollerPause();
        RestorePinned();
        _machine.BarBounds = _window.SurfaceRect;
        Changed?.Invoke(this, EventArgs.Empty);
        _log.Info(null, $"Bar reset ({StateText}, window shows {_window.Shows})");
    }

    /// <summary>State for log lines, e.g. "Pinned, held".</summary>
    public string StateText =>
        $"{_machine.State}{(_machine.IsHeld ? ", held" : "")}{(_machine.IsAnnouncing ? ", announcing" : "")}{(_heldForInput ? ", input" : "")}{(_heldForEdit ? ", editing" : "")}";

    /// <summary>Quick search closed: focus back to the previous window, panel as before.</summary>
    public void CloseQuickSearch()
    {
        _window.AllowActivation(false);
        _machine.ReleaseHeld(DateTimeOffset.UtcNow);
    }

    private bool _heldForInput;

    /// <summary>Input (Assist field): the window may get the focus, the panel stays open.</summary>
    public void BeginInputHold()
    {
        if (_heldForEdit || _heldForInput)
        {
            return;
        }

        if (_machine.State != BarState.Pinned && _machine.Pin())
        {
            _heldForInput = true;
        }

        _window.AllowActivation(true);
        UpdateOledPinned();
    }

    /// <summary>Input finished (sent, Esc): focus back, the panel hides normally again.</summary>
    public void EndInputHold()
    {
        if (_heldForEdit)
        {
            return;
        }

        _window.AllowActivation(false);
        if (_heldForInput)
        {
            _heldForInput = false;
            _machine.Unpin(DateTimeOffset.UtcNow);
        }

        UpdateOledPinned();
    }

    private MonitorInfo? AnnouncementMonitor() =>
        MonitorSelector.ForAnnouncement(_machine.Monitors, _settings, MonitorService.CursorPosition());

    private string DescribeMonitors() =>
        string.Join("; ", _machine.Monitors.Select(m =>
            $"{m.Name}{(m.IsPrimary ? " (primary)" : "")} {m.Bounds.X},{m.Bounds.Y} {m.Bounds.Width}x{m.Bounds.Height} @{m.Scale * 100:0}%"))
        + " | trigger zones: " + string.Join("; ", _machine.TriggerZones.Select(z => $"{z.Rect.X},{z.Rect.Y} {z.Rect.Width}x{z.Rect.Height}"));

    // ------------------------------------------------------------------ Operation (tray / panel)

    public void TogglePin()
    {
        if (_machine.State == BarState.Pinned)
        {
            Unpin();
            return;
        }

        if (_machine.Pin() && _machine.ActiveMonitor is { } monitor)
        {
            _settings.IsPinned = true;
            _settings.PinnedMonitorId = monitor.Id;
            Save();
        }
    }

    /// <summary>"Unpin": back to the top edge, hides normally again.</summary>
    public void Unpin()
    {
        var monitor = _machine.ActiveMonitor;
        if (_machine.Unpin(DateTimeOffset.UtcNow) || _settings.IsPinned)
        {
            _settings.IsPinned = false;
            if (monitor is not null)
            {
                _settings.PinnedPositions.Remove(monitor.Id);
            }

            Save();
        }
    }

    public void SetPaused(bool paused) => Apply(s => s.Paused = paused);

    private bool _heldForEdit;

    /// <summary>
    /// Edit mode: the panel stays open (like pinned, but without changing the setting) and
    /// the window may get the focus.
    /// </summary>
    public void BeginEditHold()
    {
        if (_heldForInput)
        {
            _heldForInput = false; // editing takes over the holding
            _heldForEdit = true;
        }
        else if (_machine.State != BarState.Pinned && _machine.Pin())
        {
            _heldForEdit = true;
        }

        _window.AllowActivation(true);
        UpdateOledPinned();
    }

    /// <summary>Editing finished: focus back, and if the bar was not pinned before, it hides normally again.</summary>
    public void EndEditHold()
    {
        _window.AllowActivation(false);
        if (_heldForEdit)
        {
            _heldForEdit = false;
            _machine.Unpin(DateTimeOffset.UtcNow);
        }

        UpdateOledPinned();
    }

    public void SetMonitor(MonitorMode mode, string? monitorId) => Apply(s =>
    {
        s.MonitorMode = mode;
        s.MonitorId = monitorId;
    });

    public void SetHideDelay(int milliseconds) => Apply(s => s.HideDelayMs = milliseconds);

    public void SetBlockInFullscreen(bool block) => Apply(s => s.BlockInFullscreen = block);

    public void SetReduceAnimations(bool reduce) => Apply(s => s.ReduceAnimations = reduce);

    public void Dispose()
    {
        _poller.Dispose();
        if (_window.Handle != IntPtr.Zero)
        {
            ExtrasInterop.UnregisterHotKey(_window.Handle, QuickSearchHotkeyId);
            Win32.WTSUnRegisterSessionNotification(_window.Handle);
        }

        if (_powerNotification != IntPtr.Zero)
        {
            Win32.UnregisterPowerSettingNotification(_powerNotification);
        }

        _window.Close();
    }

    // ------------------------------------------------------------------ internal

    private void Apply(Action<BarSettings> change)
    {
        var wasPaused = _settings.Paused;
        change(_settings);
        Save();
        RefreshMonitors();
        UpdatePollerPause();
        if (wasPaused && !_settings.Paused)
        {
            RestorePinned();
        }
    }

    private void Save()
    {
        try
        {
            _settingsService.SetSection(SettingsSection, _settings);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Bar settings could not be saved", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RestorePinned()
    {
        if (!_settings.IsPinned || _settings.Paused || _machine.State != BarState.Hidden)
        {
            return;
        }

        if (MonitorSelector.ForPinned(_machine.Monitors, _settings) is { } monitor)
        {
            _machine.ShowPinned(monitor);
        }
    }

    private void OnTick(PixelPoint cursor)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            _machine.Update(now, cursor);

            // OLED protection: mouse over the pinned panel = interaction → bright again right away.
            if (_machine.State == BarState.Pinned && _machine.BarBounds is { } bounds && bounds.Contains(cursor) && _oled.Interaction(now))
            {
                ApplyOled();
            }
        }
        catch (Exception ex)
        {
            _log.Error(null, "Error in the bar state machine", ex);
        }
        finally
        {
            _poller.TickHandled();
        }
    }

    private void OnStateChanged(object? sender, BarStateChangedEventArgs e)
    {
        var action = BarWindowTransitions.For(e.OldState, e.NewState);
        _log.Info(null, $"Bar: {e.OldState} → {e.NewState} ({e.Monitor?.Name ?? "-"}){(_machine.IsHeld ? ", held" : "")}{(_machine.IsAnnouncing ? ", announcing" : "")} → window {action}");
        var reduce = _settings.ReduceAnimations;
        _viewModel.IsPinned = e.NewState == BarState.Pinned;
        switch (action)
        {
            case BarWindowAction.ShowPill when e.Monitor is not null:
                _window.ShowPill(e.Monitor, reduce);
                break;

            case BarWindowAction.Expand:
                _window.ExpandToPanel(reduce);
                break;

            case BarWindowAction.ExpandPinned:
                // Pill → pinned (quick search/tray while a message or the pill is visible):
                // expand to the panel first – otherwise only the pill would stay (M6-Fix 1).
                _window.ExpandToPanel(reduce);
                _window.SetPinned(true);
                break;

            case BarWindowAction.Unpin:
                _window.SetPinned(false);
                break;

            case BarWindowAction.ShowPinned when e.Monitor is not null:
                // Restart, quick search from "hidden" or monitor gone: show pinned directly (saved position per monitor)
                var offset = _settings.PinnedPositions.TryGetValue(e.Monitor.Id, out var o) ? o : (PixelPoint?)null;
                _window.ShowPinned(e.Monitor, offset, reduce);
                break;

            case BarWindowAction.Pin:
                _window.SetPinned(true);
                break;

            case BarWindowAction.Hide:
                _window.HideBar(reduce);
                break;
        }

        // Check: does the window show what the machine thinks? (Hiding runs on animated.)
        var expected = e.NewState switch
        {
            BarState.Pill => "pill",
            BarState.Open or BarState.Pinned => "panel",
            _ => null,
        };
        if (expected is not null && _window.Shows != expected)
        {
            _log.Warn(null, $"Bar window does not match the state: {e.NewState}, window shows {_window.Shows} – \"Reset bar\" in the tray helps");
        }

        _poller.SetBarVisible(e.NewState != BarState.Hidden);
        _viewModel.IsPanelVisible = e.NewState is BarState.Open or BarState.Pinned;
        _viewModel.IsBarVisible = e.NewState != BarState.Hidden;
        if (e.NewState == BarState.Hidden && _heldForInput)
        {
            _heldForInput = false;
            _window.AllowActivation(false);
        }
        _machine.BarBounds = _window.SurfaceRect;
        UpdateOledPinned();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnDragCompleted(PixelRect rect)
    {
        if (_machine.ActiveMonitor is { } monitor)
        {
            _settings.PinnedPositions[monitor.Id] = BarGeometry.OffsetIn(monitor, rect);
            _settings.PinnedMonitorId = monitor.Id;
            Save();
        }
    }

    private void RefreshMonitors()
    {
        _monitorRefreshQueued = false;
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = MonitorService.GetMonitors();
        }
        catch (Exception ex)
        {
            _log.Error(null, "Monitors could not be read", ex);
            return;
        }

        var before = _machine.ActiveMonitor;
        _machine.Configure(monitors, _settings);
        _poller.SetZones(_machine.TriggerZones.Select(z => z.Rect));

        // Same monitor, but new geometry/DPI → place again.
        if (_machine.IsVisible && _machine.ActiveMonitor is { } active && before is not null && active.Id == before.Id && active != before)
        {
            _window.Reposition(active);
        }

        _machine.BarBounds = _window.SurfaceRect;
    }

    private void QueueMonitorRefresh()
    {
        if (_monitorRefreshQueued)
        {
            return;
        }

        _monitorRefreshQueued = true;

        // Windows sends several messages when re-plugging – collect briefly.
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshMonitors();
            _log.Info(null, $"Display changed: {_machine.Monitors.Count} monitor(s)");
        };
        timer.Start();
    }

    private void UpdatePollerPause()
    {
        var paused = _settings.Paused || _sessionLocked || _displayOff;
        if (paused != _pollerPaused)
        {
            _pollerPaused = paused;
            _log.Info(null, paused
                ? $"Mouse polling paused (bar paused: {_settings.Paused}, locked: {_sessionLocked}, display off: {_displayOff})"
                : "Mouse polling running");
        }

        _poller.SetPaused(paused);
    }

    private void RegisterSystemNotifications()
    {
        Win32.WTSRegisterSessionNotification(_window.Handle, 0);
        var guid = Win32.GUID_CONSOLE_DISPLAY_STATE;
        _powerNotification = Win32.RegisterPowerSettingNotification(_window.Handle, ref guid, 0);
    }

    private void OnWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case ExtrasInterop.WM_HOTKEY when wParam == new IntPtr(QuickSearchHotkeyId):
                QuickSearchHotkeyPressed?.Invoke(this, EventArgs.Empty);
                break;

            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
            case Win32.WM_SETTINGCHANGE when wParam == new IntPtr(0x2F): // SPI_SETWORKAREA
                QueueMonitorRefresh();
                break;

            case Win32.WM_WTSSESSION_CHANGE:
                if (wParam == new IntPtr(Win32.WTS_SESSION_LOCK))
                {
                    _sessionLocked = true;
                }
                else if (wParam == new IntPtr(Win32.WTS_SESSION_UNLOCK))
                {
                    _sessionLocked = false;
                }

                UpdatePollerPause();
                break;

            case Win32.WM_POWERBROADCAST when wParam == new IntPtr(Win32.PBT_POWERSETTINGCHANGE) && lParam != IntPtr.Zero:
                var setting = Marshal.PtrToStructure<Win32.POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == Win32.GUID_CONSOLE_DISPLAY_STATE)
                {
                    _displayOff = setting.Data == 0; // 0 = off, 1 = on, 2 = dimmed
                    UpdatePollerPause();
                }

                break;
        }
    }
}
