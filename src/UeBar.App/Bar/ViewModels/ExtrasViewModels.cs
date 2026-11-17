// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UeBar.Bar;
using UeBar.Sdk;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.App.Bar.ViewModels;

/// <summary>What the pill currently shows (M6): message > playback > status value.</summary>
internal enum PillContent
{
    Status,
    NowPlaying,
    Notification,
}

/// <summary>A notification in the pill, the panel banner and the history.</summary>
internal sealed class NotificationViewModel(BarNotification notification)
{
    public BarNotification Source { get; } = notification;

    public string Title => Source.Title;

    public string Message => Source.Message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Source.Message);

    public string Icon => Source.Icon ?? "Bell";

    public string TimeText => Source.Timestamp.ToLocalTime().ToString(
        Source.Timestamp.Date == DateTimeOffset.Now.Date ? "t" : "g", CultureInfo.CurrentCulture);
}

/// <summary>Current playback for pill and panel.</summary>
internal sealed class NowPlayingViewModel(NowPlayingEntry entry)
{
    public string ModuleId { get; } = entry.ModuleId;

    public string Title { get; } = entry.Info.Title;

    public string? Artist { get; } = entry.Info.Artist;

    public string? ImageId { get; } = entry.Info.ImageId;

    public bool IsPlaying { get; } = entry.Info.IsPlaying;

    public string? SourceName { get; } = entry.Info.SourceName;

    public string Subtitle => string.Join(" · ", new[] { Artist, SourceName }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Pause or play icon (Segoe Fluent Icons).</summary>
    public string PlayPauseGlyph => IsPlaying ? "" : "";
}

/// <summary>
/// Input field "Ask … " at the bottom of the panel (M6). Quick commands work here as well.
/// M6-Fix 2: only the last exchange is visible, older ones behind "History (n)"; after the
/// conversation timeout a new conversation starts on the next opening (display empty, context dropped).
/// </summary>
internal sealed class AssistViewModel : ObservableObject
{
    private readonly AssistService _assist;
    private readonly AssistTranscript _transcript = new();
    private readonly IUeLogger? _log;

    public AssistViewModel(AssistService assist, IUeLogger? log = null)
    {
        _assist = assist;
        _log = log;
        SendCommand = new RelayCommand(async () => await SendAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Text));
        NewConversationCommand = new RelayCommand(() => StartNewConversation(manual: true), () => !IsBusy);
        ToggleHistoryCommand = new RelayCommand(() =>
        {
            _transcript.IsHistoryExpanded = !_transcript.IsHistoryExpanded;
            Publish();
        });
        Refresh();
        UeBar.Core.Localization.UiCultureSource.Shared.Changed += (_, _) =>
        {
            // Language switched (R1): placeholder and history label in the new language.
            Refresh();
            OnPropertyChanged(nameof(HistoryLabel));
        };
    }

    public RelayCommand SendCommand { get; }

    public RelayCommand NewConversationCommand { get; }

    public RelayCommand ToggleHistoryCommand { get; }

    /// <summary>Last exchange (or null).</summary>
    public AssistExchange? Latest => _transcript.Latest;

    /// <summary>Older entries (newest first), only visible when expanded.</summary>
    public IReadOnlyList<AssistExchange> Older => _transcript.Older;

    public bool HasHistory => _transcript.Older.Count > 0;

    public string HistoryLabel => UeBar.App.Localization.Loc.F("Assist_History", _transcript.Older.Count);

    public bool IsHistoryExpanded => _transcript.IsHistoryExpanded && HasHistory;

    public bool ShowNewConversationHint => _transcript.ShowsNewConversationHint;

    /// <summary>Show a long answer completely ("more").</summary>
    public bool IsAnswerExpanded
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Panel opens: expired conversation → start over.</summary>
    public void OnPanelOpened()
    {
        if (_transcript.StartNewIfExpired(_assist.IsConversationExpired()))
        {
            _assist.NewConversation();
            _log?.Info(null, "Assist: conversation timeout expired – new conversation");
            Publish();
        }
    }

    /// <summary>Panel closes: collapse the history and the long answer.</summary>
    public void OnPanelClosed()
    {
        _transcript.OnPanelClosed();
        IsAnswerExpanded = false;
        Publish();
    }

    private void StartNewConversation(bool manual)
    {
        _assist.NewConversation();
        _transcript.StartNew(announce: true);
        IsAnswerExpanded = false;
        _log?.Info(null, manual ? "Assist: new conversation (button \"New\")" : "Assist: new conversation");
        Publish();
    }

    private void Publish()
    {
        OnPropertyChanged(nameof(Latest));
        OnPropertyChanged(nameof(Older));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryLabel));
        OnPropertyChanged(nameof(IsHistoryExpanded));
        OnPropertyChanged(nameof(ShowNewConversationHint));
    }

    public string Text
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    } = "";

    public bool IsBusy
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                SendCommand.NotifyCanExecuteChanged();
                NewConversationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsAvailable
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string Placeholder
    {
        get;
        private set => SetProperty(ref field, value);
    } = "…";

    /// <summary>After sending (for returning the focus).</summary>
    public event EventHandler? Submitted;

    /// <summary>Determine the provider again (module status changed or language switched).</summary>
    public void Refresh()
    {
        var name = _assist.ProviderName;
        IsAvailable = name is not null;
        Placeholder = UeBar.App.Localization.Loc.F("Assist_Placeholder", name ?? "…");
    }

    public async Task SendAsync()
    {
        var question = Text.Trim();
        if (question.Length == 0 || IsBusy)
        {
            return;
        }

        // The panel stayed open (pinned) while the timeout expired → the display no longer matches the context.
        if (_transcript.StartNewIfExpired(_assist.IsConversationExpired()))
        {
            _assist.NewConversation();
            _log?.Info(null, "Assist: conversation timeout expired – new conversation");
        }

        IsBusy = true;
        Text = "";
        Submitted?.Invoke(this, EventArgs.Empty);
        try
        {
            var answer = await _assist.AskAsync(question);
            _transcript.Add(new AssistExchange(question, answer.Text, answer.Success));
            IsAnswerExpanded = false;
            Publish();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A quick search hit (a module's thing or a recognized quick command).</summary>
internal sealed class QuickSearchHit(string moduleId, string moduleName, EntityInfo? entity, QuickCommandMatch? command) : ObservableObject
{
    public string ModuleId { get; } = moduleId;

    public EntityInfo? Entity { get; } = entity;

    public QuickCommandMatch? Command { get; } = command;

    public string Title => Command is { } c ? $"{c.Command.Prefix} {c.Argument}" : Entity?.Name ?? "";

    public string Detail => Command is { } c
        ? $"{c.Command.Description} · {moduleName}"
        : string.Join(" · ", new[] { Entity?.Area, Entity?.StateText, moduleName }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Icon => Command?.Command.Icon ?? Entity?.Icon ?? "Search";

    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>
/// Quick search (M6): searches all <see cref="IEntitySource"/>, arrow keys select, Enter runs the
/// default action. If the text starts with a quick command prefix, that command is on top.
/// </summary>
internal sealed class QuickSearchViewModel : ObservableObject
{
    private readonly ModuleRegistry _registry;
    private readonly QuickCommandService _commands;
    private readonly IUeLogger _log;
    private readonly DispatcherTimer _timer;
    private int _generation;

    public QuickSearchViewModel(ModuleRegistry registry, QuickCommandService commands, IUeLogger log)
    {
        _registry = registry;
        _commands = commands;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            await SearchAsync();
        };
        PickCommand = new RelayCommand<QuickSearchHit>(async hit => await ExecuteAsync(hit));
    }

    public RelayCommand<QuickSearchHit> PickCommand { get; }

    public ObservableCollection<QuickSearchHit> Results { get; } = [];

    public bool IsOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && value)
            {
                Query = "";
                Results.Clear();
                Hint = UeBar.App.Localization.Loc.Get("Search_Hint");
            }
        }
    }

    public string Query
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _timer.Stop();
                _timer.Start();
            }
        }
    } = "";

    public string? Hint
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Something was run (message for a short pill) – the search closes afterwards.</summary>
    public event EventHandler<QuickCommandResult>? Executed;

    public QuickSearchHit? Selected => Results.FirstOrDefault(r => r.IsSelected);

    public void Move(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = Selected is { } s ? Results.IndexOf(s) : -1;
        Select(Math.Clamp(index + delta, 0, Results.Count - 1));
    }

    public Task ExecuteSelectedAsync() => ExecuteAsync(Selected ?? Results.FirstOrDefault());

    private void Select(int index)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            Results[i].IsSelected = i == index;
        }
    }

    private async Task SearchAsync()
    {
        var generation = ++_generation;
        var query = Query.Trim();
        var hits = new List<QuickSearchHit>();
        if (_commands.Parse(query) is { } match)
        {
            hits.Add(new QuickSearchHit(match.ModuleId, _registry.Find(match.ModuleId)?.DisplayName ?? match.ModuleId, null, match));
        }

        if (query.Length > 0)
        {
            var sources = _registry.GetCapabilities<IEntitySource>();
            var all = await Task.WhenAll(sources.Select(async s =>
            {
                var result = await _registry.InvokeAsync<IReadOnlyList<EntityInfo>>(
                    s.Module.Id, "Quick search", (m, ct) => ((IEntitySource)m).SearchAsync(query, 8, ct), TimeSpan.FromSeconds(4));
                return (s.Module, Hits: result.Value ?? []);
            }));
            if (generation != _generation)
            {
                return;
            }

            hits.AddRange(all.SelectMany(a => a.Hits.Select(h => new QuickSearchHit(a.Module.Id, a.Module.DisplayName, h, null))));
        }

        Results.Clear();
        foreach (var hit in hits.Take(10))
        {
            Results.Add(hit);
        }

        Select(Results.Count > 0 ? 0 : -1);
        Hint = query.Length == 0 ? UeBar.App.Localization.Loc.Get("Search_Hint") : Results.Count == 0 ? UeBar.App.Localization.Loc.Get("Search_NothingFound") : null;
    }

    private async Task ExecuteAsync(QuickSearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        if (_executing)
        {
            _log.Info(null, "Quick search: Enter ignored – action still running");
            return;
        }

        _executing = true;
        try
        {
            if (hit.Command is { } command)
            {
                _log.Info(hit.ModuleId, $"Quick search: quick command \"{command.Command.Prefix}\"");
                var commandResult = await _commands.ExecuteAsync(command);
                _log.Info(hit.ModuleId, $"Quick search: quick command \"{command.Command.Prefix}\" {(commandResult.Success ? "ok" : "failed")}");
                Executed?.Invoke(this, commandResult);
                return;
            }

            if (hit.Entity is { } entity)
            {
                _log.Info(hit.ModuleId, "Quick search: default action");
                var result = await _registry.InvokeAsync(hit.ModuleId, "Default action", async (m, ct) =>
                {
                    await ((IEntitySource)m).ExecuteDefaultActionAsync(entity.Id, ct);
                    return true;
                });
                if (!result.Succeeded)
                {
                    _log.Warn(hit.ModuleId, $"Default action for {entity.Id} failed ({result.Outcome})");
                }
                else
                {
                    _log.Info(hit.ModuleId, "Quick search: default action ok");
                }

                Executed?.Invoke(this, new QuickCommandResult(result.Succeeded, result.Succeeded ? entity.Name : $"{entity.Name}: fehlgeschlagen"));
            }
        }
        finally
        {
            _executing = false;
        }
    }

    private bool _executing;
}
