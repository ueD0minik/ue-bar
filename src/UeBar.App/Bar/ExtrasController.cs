// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows.Threading;
using UeBar.App.Bar.ViewModels;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Sdk;
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
    private readonly ModuleRegistry _registry;
    private readonly NotificationQueue _queue = new();
    private readonly DispatcherTimer _queueTimer;
    private readonly Func<Core.Settings.SettingsService> _settings;

    /// <summary>Playback across the modules (settings page: order of the sources).</summary>
    public NowPlayingService NowPlaying => _nowPlaying;

    public ExtrasController(
        BarViewModel viewModel,
        BarController bar,
        ModuleRegistry registry,
        NotificationHub notifications,
        NowPlayingService nowPlaying,
        PillActivityService activities,
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
        _registry = registry;
        _nowPlaying = nowPlaying;
        _nowPlaying.SourceOrder = () => _bar.Settings.NowPlayingOrder;
        _settings = settings;
        _queueTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _queueTimer.Tick += (_, _) => AdvanceQueue();

        notifications.Received += (_, n) => _dispatcher.BeginInvoke(() => OnNotification(n));
        nowPlaying.Changed += (_, _) => _dispatcher.BeginInvoke(UpdateNowPlaying);
        activities.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
            _viewModel.Activity = activities.Current is { } activity ? new PillActivityViewModel(activity) : null);
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
            CloseQuickSearch("action run", restoreFocus: false);
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

    public void CloseQuickSearch(string reason = "Esc", bool restoreFocus = true)
    {
        if (!_viewModel.IsQuickSearchOpen)
        {
            return;
        }

        _viewModel.IsQuickSearchOpen = false;
        _bar.CloseQuickSearch(restoreFocus);
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
        // P2-4: one decision for all (paused, locked, fullscreen, "Do not disturb") – important ones may pass.
        var request = new NotificationRequest(notification.Priority == NotificationPriority.Important, notification.IgnoreDoNotDisturb, notification.ShowInFullscreen)
        {
            ActionFeedback = notification.IsActionFeedback,
        };
        var decision = NotificationGate.Decide(request, _bar.CurrentAnnounceConditions(settings.RespectDoNotDisturb && ExtrasInterop.IsDoNotDisturbActive()), settings);
        var actions = (notification.Actions ?? []).Where(a => a is not null).Select(a => (a.Id, a.Label)).ToList();
        if (decision.AsActionFeedback && FeedbackToast is { } toast)
        {
            // P2-5-Fix 1: the answer to the user's own action – the app shows it itself (also during "Do not disturb").
            _log.Info(notification.Source, "Notification as action feedback at the place of the action");
            _queue.Enqueue(new BarNotification(notification.Title, notification.Message, notification.Icon, duration, notification.Timestamp, notification.Source)
            {
                Id = notification.Id,
                Actions = actions,
            }, suppress: true);
            _viewModel.SetHistory(_queue.History);
            toast.Show(notification.Title, notification.Message, duration, actions, actionId => RunModuleAction(notification.Source, notification.Id, actionId));
            return;
        }

        var suppress = !decision.Show;
        var reason = decision.Reason;
        if (decision.OverridesPause)
        {
            _log.Info(notification.Source, "Important notification comes out although the bar is paused");
        }

        if (suppress)
        {
            _log.Info(notification.Source, $"Notification only in the history ({reason})");
        }
        else if (notification.Sound is { Length: > 0 } sound)
        {
            // P2-3: a sound only goes with a notification that is shown (never for a suppressed one).
            Infrastructure.NotificationSound.Play(sound, notification.SoundVolume, _log);
        }

        _queue.Enqueue(new BarNotification(notification.Title, notification.Message, notification.Icon, duration, notification.Timestamp, notification.Source)
        {
            Id = notification.Id,
            Actions = [.. (notification.Actions ?? []).Where(a => a is not null).Select(a => (a.Id, a.Label))],
            OverridesPause = decision.OverridesPause,
        }, suppress);
        _viewModel.SetHistory(_queue.History);
        AdvanceQueue();
    }

    /// <summary>"×" on the banner: the notification goes away (it stays in the history).</summary>
    private void DismissNotification(BarNotification notification)
    {
        if (!ReferenceEquals(_queue.Current, notification))
        {
            return;
        }

        _queue.Dismiss();
        _bar.EndAnnounce();
        _log.Info(notification.Source, "Notification dismissed");
        AdvanceQueue();
        if (_queue.Current is null)
        {
            _viewModel.Notification = null;
        }
    }

    /// <summary>A button on the banner (e.g. "+5 min"): the module handles it, the notification goes away.</summary>
    private void RunNotificationAction(BarNotification notification, string actionId)
    {
        DismissNotification(notification);
        RunModuleAction(notification.Source, notification.Id, actionId);
    }

    /// <summary>P2-5-Fix 1: the app's own message for the answer to an action (set by the app; null = none).</summary>
    public Capture.ActionFeedbackToast? FeedbackToast { get; set; }

    /// <summary>A notification button: the module that sent it handles it.</summary>
    private void RunModuleAction(string? source, string? notificationId, string actionId)
    {
        if (source is not { } moduleId || notificationId is not { } id)
        {
            return;
        }

        _ = RunAsync();

        async Task RunAsync()
        {
            var result = await _registry.InvokeAsync(moduleId, $"Notification action {actionId}", async (m, ct) =>
            {
                if (m is INotificationActionHandler handler)
                {
                    await handler.HandleNotificationActionAsync(id, actionId, ct).ConfigureAwait(false);
                }

                return true;
            }).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _log.Warn(moduleId, $"Notification action {actionId} failed ({result.Outcome})");
            }
        }
    }

    private NotificationViewModel Show(BarNotification notification) =>
        new(notification, DismissNotification, RunNotificationAction);

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
                _viewModel.Notification = _queue.Current is { } current ? Show(current) : null;
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
            _viewModel.Notification = current is null ? null : Show(current);
            if (current is not null)
            {
                _bar.Announce(current.Duration, current.OverridesPause);
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
