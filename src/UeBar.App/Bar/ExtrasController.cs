// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Threading;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Bar;

/// <summary>
/// Extras in the app (M6): notifications (queue, history, fullscreen/"Do not disturb"),
/// Now Playing, Assist field, quick search with a global hotkey. Everything through generic
/// core services – nothing module-specific. Runs on the UI thread.
/// </summary>
internal sealed class ExtrasController
{
    private readonly BarViewModel _viewModel;
    private readonly BarController _bar;
    private readonly Dispatcher _dispatcher;
    private readonly IUeLogger _log;
    private readonly NowPlayingService _nowPlaying;
    private readonly NotificationQueue _queue = new();
    private readonly DispatcherTimer _queueTimer;
    private readonly Func<Core.Settings.SettingsService> _settings;

    public ExtrasController(
        BarViewModel viewModel,
        BarController bar,
        ModuleRegistry registry,
        NotificationHub notifications,
        NowPlayingService nowPlaying,
        AssistService assist,
        QuickCommandService quickCommands,
        Func<Core.Settings.SettingsService> settings,
        IUeLogger log,
        Dispatcher dispatcher)
    {
        _viewModel = viewModel;
        _bar = bar;
        _dispatcher = dispatcher;
        _log = log;
        _nowPlaying = nowPlaying;
        _settings = settings;
        _queueTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _queueTimer.Tick += (_, _) => AdvanceQueue();

        notifications.Received += (_, n) => _dispatcher.BeginInvoke(() => OnNotification(n));
        nowPlaying.Changed += (_, _) => _dispatcher.BeginInvoke(UpdateNowPlaying);
        _viewModel.NowPlayingRequested += (_, command) => _ = _nowPlaying.ExecuteAsync(command);
        _viewModel.HistoryCleared += (_, _) =>
        {
            _queue.ClearHistory();
            _viewModel.SetHistory(_queue.History);
        };

        // Conversation timeout: bar.assistConversationMinutes overrides the module's value (M6-Fix 2).
        assist.ConversationTimeoutOverride = () =>
            _bar.Settings.AssistConversationMinutes is { } minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        _viewModel.Assist = new AssistViewModel(assist, log);
        _viewModel.Assist.Submitted += (_, _) => _bar.EndInputHold();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BarViewModel.IsPanelVisible))
            {
                if (_viewModel.IsPanelVisible)
                {
                    _viewModel.Assist?.OnPanelOpened();
                }
                else
                {
                    _viewModel.Assist?.OnPanelClosed();
                }
            }
        };
        registry.ModuleStatusChanged += (_, _) => _dispatcher.BeginInvoke(() => _viewModel.Assist?.Refresh());

        _viewModel.QuickSearch = new QuickSearchViewModel(registry, quickCommands, log);
        _viewModel.QuickSearch.Executed += (_, result) =>
        {
            CloseQuickSearch("action run");
            ShowConfirmation(result);
        };

        _bar.QuickSearchHotkeyPressed += (_, _) => OpenQuickSearch();
        UpdateNowPlaying();
    }

    /// <summary>Esc in the window: close the quick search or end the input.</summary>
    public void OnEscape()
    {
        if (_viewModel.IsQuickSearchOpen)
        {
            CloseQuickSearch();
        }
        else
        {
            _bar.EndInputHold();
        }
    }

    /// <summary>Assist field clicked: the window may get the focus.</summary>
    public void BeginInput() => _bar.BeginInputHold();

    public void OpenQuickSearch()
    {
        if (_viewModel.IsEditing || _viewModel.IsSetup || _viewModel.IsWelcome)
        {
            _log.Info(null, $"Quick search not opened ({(_viewModel.IsEditing ? "edit mode" : "setup")})");
            return;
        }

        if (_viewModel.IsQuickSearchOpen)
        {
            // Hotkey while the search is open: only focus back into the search field.
            _log.Info(null, $"Quick search: hotkey while open → focus into the search field (bar {_bar.StateText})");
            _bar.OpenQuickSearch();
            QuickSearchOpened?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_bar.OpenQuickSearch())
        {
            return;
        }

        _viewModel.IsQuickSearchOpen = true;
        _log.Info(null, $"Quick search opened (bar {_bar.StateText})");
        QuickSearchOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The view then sets the focus into the search field.</summary>
    public event EventHandler? QuickSearchOpened;

    public void CloseQuickSearch(string reason = "Esc")
    {
        if (!_viewModel.IsQuickSearchOpen)
        {
            return;
        }

        _viewModel.IsQuickSearchOpen = false;
        _bar.CloseQuickSearch();
        _log.Info(null, $"Quick search closed ({reason}, bar {_bar.StateText})");
    }

    /// <summary>Settings → Notifications (M7): number of entries in the history.</summary>
    public int HistoryCount => _queue.History.Count;

    /// <summary>Settings → "Clear history" (M7).</summary>
    public void ClearHistory()
    {
        _queue.ClearHistory();
        _viewModel.SetHistory(_queue.History);
    }

    /// <summary>Emergency exit (tray "Reset bar"): search, input, message pill closed – the history stays.</summary>
    public void Reset()
    {
        _viewModel.IsQuickSearchOpen = false;
        _confirmation?.Stop();
        _confirmation = null;
        _queueTimer.Stop();
        _queue.ClearPending();
        _viewModel.Notification = null;
        _viewModel.SetHistory(_queue.History);
    }

    // ------------------------------------------------------------------ Notifications

    private void OnNotification(UeNotification notification)
    {
        var settings = _bar.Settings;
        var duration = notification.Duration ?? TimeSpan.FromSeconds(Math.Clamp(settings.NotificationSeconds, 2, 60));
        var suppress = !_bar.CanAnnounce(out var reason);
        if (!suppress && settings.RespectDoNotDisturb && ExtrasInterop.IsDoNotDisturbActive())
        {
            suppress = true;
            reason = "do not disturb";
        }

        if (suppress)
        {
            _log.Info(notification.Source, $"Notification only in the history ({reason})");
        }

        _queue.Enqueue(new BarNotification(notification.Title, notification.Message, notification.Icon, duration, notification.Timestamp, notification.Source), suppress);
        _viewModel.SetHistory(_queue.History);
        AdvanceQueue();
    }

    /// <summary>Short confirmation (quick command, default action) as a pill – does not go into the history.</summary>
    private void ShowConfirmation(QuickCommandResult result)
    {
        var item = new BarNotification(result.Success ? UeBar.App.Localization.Loc.Get("Pill_Done") : UeBar.App.Localization.Loc.Get("Pill_Failed"), result.Message, result.Success ? "Checkmark" : "Warning", TimeSpan.FromSeconds(3), DateTimeOffset.Now);
        _viewModel.Notification = new NotificationViewModel(item);
        var shown = _bar.Announce(item.Duration);
        _log.Info(null, $"Confirmation pill on ({(result.Success ? "ok" : "failed")}, {item.Duration.TotalSeconds:0} s, {(shown ? $"bar {_bar.StateText}" : "not shown")})");
        _confirmation?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = item.Duration };
        _confirmation = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_confirmation == timer)
            {
                _confirmation = null;
            }

            if (_viewModel.Notification?.Source == item)
            {
                _viewModel.Notification = _queue.Current is { } current ? new NotificationViewModel(current) : null;
                _log.Info(null, $"Confirmation pill off (bar {_bar.StateText})");
            }
        };
        timer.Start();
    }

    private DispatcherTimer? _confirmation;

    private void AdvanceQueue()
    {
        _queueTimer.Stop();
        var now = DateTimeOffset.UtcNow;
        if (_queue.Advance(now))
        {
            var current = _queue.Current;
            _viewModel.Notification = current is null ? null : new NotificationViewModel(current);
            if (current is not null)
            {
                _bar.Announce(current.Duration);
            }
        }

        if (_queue.CurrentUntil is { } until)
        {
            _queueTimer.Interval = until - now > TimeSpan.Zero ? until - now : TimeSpan.FromMilliseconds(10);
            _queueTimer.Start();
        }
    }

    // ------------------------------------------------------------------ Now Playing

    private void UpdateNowPlaying()
    {
        _viewModel.NowPlaying = _nowPlaying.Current is { } entry ? new NowPlayingViewModel(entry) : null;
    }
}
