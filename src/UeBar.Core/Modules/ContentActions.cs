// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Diagnostics;
using System.Text;
using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>The app's part of content actions (P3-1): the list of actions and the answer window. No content is kept.</summary>
public interface IHostContentActionUi
{
    /// <summary>The user picks one of <paramref name="actions"/> for the content (null = none).</summary>
    Task<ContentActionEntry?> ChooseAsync(IReadOnlyList<ContentActionEntry> actions, ContentItem content, ContentActionStart start, CancellationToken cancellationToken);

    /// <summary>Opens the answer window for the session (the window owns it from now on and disposes it when it closes).</summary>
    void Show(ContentActionSession session);
}

/// <summary>
/// Content actions of all running modules (P3-1, contract 1.8): who offers what, the list of actions, and the
/// start of one action in an answer window. Content reaches a provider only through <see cref="RunAsync"/>, i.e.
/// when the user started that action – no module is ever told about content otherwise. The log names the action,
/// the kind of content, the duration and errors – never content.
/// </summary>
public sealed class ContentActionService
{
    /// <summary>How long one turn may take (an answer is streamed, so it is long on purpose).</summary>
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(5);

    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;

    public ContentActionService(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
    }

    /// <summary>Set by the app; without it nothing can be shown and every start returns false.</summary>
    public IHostContentActionUi? Ui { get; set; }

    /// <summary>UI language for the requests ("de", "en").</summary>
    public Func<string?>? UiLanguage { get; set; }

    internal ModuleRegistry Registry => _registry;

    internal IUeLogger Log => _log;

    /// <summary>Actions of the running modules that work with at least one of <paramref name="kinds"/>.</summary>
    public IReadOnlyList<ContentActionEntry> GetActions(ContentKinds kinds)
    {
        var result = new List<ContentActionEntry>();
        if (kinds == ContentKinds.None)
        {
            return result;
        }

        foreach (var (entry, provider) in _registry.GetCapabilities<IContentActionProvider>())
        {
            var read = _registry.Invoke(entry.Id, "Read content actions", _ => provider.ContentActions);
            if (!read.Succeeded || read.Value is not { } actions)
            {
                continue;
            }

            foreach (var action in actions)
            {
                if (action is not null && !string.IsNullOrWhiteSpace(action.Id) && !string.IsNullOrWhiteSpace(action.Label) && (action.Kinds & kinds) != 0)
                {
                    result.Add(new ContentActionEntry(entry.Id, entry.DisplayName, action));
                }
            }
        }

        return result;
    }

    /// <summary>The action, if its module runs and offers it right now.</summary>
    public ContentActionEntry? Find(string moduleId, string actionId)
    {
        if (_registry.Find(moduleId) is not { IsAvailable: true } entry || entry.Module is not IContentActionProvider provider)
        {
            return null;
        }

        var read = _registry.Invoke(entry.Id, "Read content actions", _ => provider.ContentActions);
        return read.Succeeded && read.Value?.FirstOrDefault(a => a is not null && string.Equals(a.Id, actionId, StringComparison.Ordinal)) is { } action
            ? new ContentActionEntry(entry.Id, entry.DisplayName, action)
            : null;
    }

    /// <summary>
    /// Starts an action: opens the answer window. Content must have a kind the action works with – only a module's
    /// own chat (<see cref="ContentOrigin.Chat"/> from that module) may start without content.
    /// </summary>
    public bool Run(string? callerModuleId, string moduleId, string actionId, ContentItem content, ContentActionStart? start)
    {
        ArgumentNullException.ThrowIfNull(content);
        start ??= new ContentActionStart();
        if (Ui is not { } ui || Find(moduleId, actionId) is not { } action)
        {
            return false;
        }

        var ownChat = start.Origin == ContentOrigin.Chat && string.Equals(callerModuleId, moduleId, StringComparison.OrdinalIgnoreCase);
        if ((content.Kinds & action.Action.Kinds) == 0 && !(ownChat && content.IsEmpty))
        {
            _log.Info(callerModuleId, $"Content action {moduleId}/{actionId}: content does not fit ({content.Kinds})");
            return false;
        }

        var session = new ContentActionSession(this, action, content, start);
        _log.Info(moduleId, $"Content action {actionId} opened ({Describe(content)}, {start.Origin}{(callerModuleId is null ? "" : $", from {callerModuleId}")})");
        ui.Show(session);
        if (!action.Action.AsksQuestion || !string.IsNullOrWhiteSpace(start.Question))
        {
            _ = session.SendAsync(start.Question);
        }

        return true;
    }

    /// <summary>Shows the list of actions for the content and starts the chosen one.</summary>
    public async Task<bool> ChooseAsync(string? callerModuleId, ContentItem content, ContentActionStart? start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        start ??= new ContentActionStart();
        var actions = GetActions(content.Kinds);
        if (Ui is not { } ui || actions.Count == 0)
        {
            return false;
        }

        var chosen = await ui.ChooseAsync(actions, content, start, cancellationToken).ConfigureAwait(false);
        return chosen is not null && Run(callerModuleId, chosen.ModuleId, chosen.Action.Id, content, start);
    }

    /// <summary>For the log: kinds and sizes, never content.</summary>
    public static string Describe(ContentItem content)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            parts.Add($"text {content.Text.Length} chars");
        }

        if (content.ImagePng is { Length: > 0 } png)
        {
            parts.Add($"image {png.Length / 1024} KB");
        }

        if (content.Files is { Count: > 0 } files)
        {
            parts.Add($"{files.Count} files");
        }

        return parts.Count == 0 ? "no content" : string.Join(", ", parts);
    }
}

/// <summary>
/// One conversation in an answer window (P3-1): the content, the turns so far and the answer that is coming in.
/// Lives in memory only – <see cref="Clear"/> ("New chat") and <see cref="Dispose"/> (window closed, idle) forget
/// everything. Raises <see cref="Changed"/> on any thread.
/// </summary>
public sealed class ContentActionSession : IDisposable
{
    private readonly ContentActionService _service;
    private readonly Lock _lock = new();
    private readonly List<ContentTurn> _turns = [];
    private readonly StringBuilder _answer = new();
    private CancellationTokenSource? _running;
    private int _turnNumber;
    private string? _lastQuestion;
    private bool _disposed;

    internal ContentActionSession(ContentActionService service, ContentActionEntry action, ContentItem content, ContentActionStart start)
    {
        _service = service;
        Action = action;
        Content = content;
        Start = start;
        LastUsed = DateTimeOffset.UtcNow;
    }

    public ContentActionEntry Action { get; }

    public ContentActionStart Start { get; }

    /// <summary>The content of the first turn (empty after <see cref="Clear"/>).</summary>
    public ContentItem Content { get; private set; }

    public IReadOnlyList<ContentTurn> Turns
    {
        get
        {
            lock (_lock)
            {
                return [.. _turns];
            }
        }
    }

    /// <summary>The question of the turn that runs or ran last.</summary>
    public string? CurrentQuestion
    {
        get
        {
            lock (_lock)
            {
                return _lastQuestion;
            }
        }
    }

    /// <summary>The answer of the turn that runs or ran last (Markdown).</summary>
    public string CurrentAnswer
    {
        get
        {
            lock (_lock)
            {
                return _answer.ToString();
            }
        }
    }

    public string? Status { get; private set; }

    public string? Error { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>A turn has run (or runs) – the window shows the answer area.</summary>
    public bool HasStarted { get; private set; }

    public IReadOnlyList<ResponseOption> Options { get; private set; } = [];

    public string? SelectedOption { get; private set; }

    /// <summary>What "Copy", "Paste" and "Pin" use; the whole answer if the provider set nothing.</summary>
    public string ResultText
    {
        get
        {
            lock (_lock)
            {
                return _resultText ?? _answer.ToString();
            }
        }
    }

    private string? _resultText;

    public DateTimeOffset LastUsed { get; private set; }

    /// <summary>How long the provider keeps a conversation without use.</summary>
    public TimeSpan IdleTimeout
    {
        get
        {
            if (_service.Registry.Find(Action.ModuleId)?.Module is not IContentActionProvider provider)
            {
                return TimeSpan.FromMinutes(15);
            }

            var read = _service.Registry.Invoke(Action.ModuleId, "Read conversation timeout", _ => provider.ConversationIdleTimeout);
            return read.Succeeded && read.Value > TimeSpan.Zero ? read.Value : TimeSpan.FromMinutes(15);
        }
    }

    public bool IsDisposed => _disposed;

    /// <summary>Something changed – re-read the properties. Any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts a turn: the first one with the content, later ones as follow-ups with the turns so far. A running turn
    /// is cancelled first. Returns when the turn has finished.
    /// </summary>
    public Task SendAsync(string? question) => RunTurnAsync(question, replaceLast: false);

    /// <summary>Runs the last turn again with another option (e.g. another model).</summary>
    public Task SelectOptionAsync(string optionId)
    {
        lock (_lock)
        {
            SelectedOption = optionId;
        }

        if (!HasStarted)
        {
            Raise();
            return Task.CompletedTask;
        }

        // The last turn is asked again and its answer replaced.
        string? question;
        lock (_lock)
        {
            question = _lastQuestion;
        }

        return RunTurnAsync(question, replaceLast: true);
    }

    /// <summary>Stops the running turn (what came so far stays).</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _running?.Cancel();
        }
    }

    /// <summary>"New chat": forgets the content, the turns and the answer.</summary>
    public void Clear()
    {
        Cancel();
        lock (_lock)
        {
            _turns.Clear();
            _answer.Clear();
            _resultText = null;
            _lastQuestion = null;
            Content = ContentItem.Empty;
            Error = null;
            Status = null;
            HasStarted = false;
            _turnNumber++;
        }

        LastUsed = DateTimeOffset.UtcNow;
        _service.Log.Info(Action.ModuleId, $"Content action {Action.Action.Id}: new chat");
        Raise();
    }

    /// <summary>Marks the conversation as used (typing counts as use).</summary>
    public void Touch() => LastUsed = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        lock (_lock)
        {
            _disposed = true;
            _turns.Clear();
            _answer.Clear();
            _resultText = null;
            _lastQuestion = null;
            Content = ContentItem.Empty;
            _turnNumber++;
        }

        _service.Log.Info(Action.ModuleId, $"Content action {Action.Action.Id}: closed, conversation forgotten");
    }

    private async Task RunTurnAsync(string? question, bool replaceLast)
    {
        if (_disposed || _service.Registry.Find(Action.ModuleId) is not { IsAvailable: true } entry || entry.Module is not IContentActionProvider provider)
        {
            Fail(_disposed ? null : "module");
            return;
        }

        CancellationTokenSource cts;
        ContentActionRequest request;
        int turn;
        lock (_lock)
        {
            // A turn that is still running is finished as it is (its partial answer becomes history).
            if (IsRunning)
            {
                _running?.Cancel();
            }

            if (!replaceLast && HasStarted && (_answer.Length > 0 || _lastQuestion is not null))
            {
                FinishTurnLocked();
            }

            _running?.Dispose();
            _running = cts = new CancellationTokenSource();
            turn = ++_turnNumber;
            _answer.Clear();
            _resultText = null;
            _lastQuestion = string.IsNullOrWhiteSpace(question) ? null : question.Trim();
            Error = null;
            Status = null;
            IsRunning = true;
            HasStarted = true;
            request = new ContentActionRequest(Action.Action.Id, Content)
            {
                Question = _lastQuestion,
                History = [.. _turns],
                OptionId = SelectedOption,
                Origin = Start.Origin,
                UiLanguage = _service.UiLanguage?.Invoke(),
            };
        }

        LastUsed = DateTimeOffset.UtcNow;
        Raise();
        var writer = new Writer(this, turn);
        var clock = Stopwatch.StartNew();
        var result = await _service.Registry.InvokeAsync<bool>(
            entry.Id,
            $"Content action {Action.Action.Id}",
            async (_, ct) =>
            {
                await provider.RunContentActionAsync(request, writer, ct).ConfigureAwait(false);
                return true;
            },
            ContentActionService.TurnTimeout,
            reportFaults: false,
            cts.Token).ConfigureAwait(false);

        lock (_lock)
        {
            if (turn != _turnNumber)
            {
                return; // a newer turn (or "New chat") took over
            }

            IsRunning = false;
            Status = null;
            Error = result.Outcome switch
            {
                GuardOutcome.Success or GuardOutcome.Canceled => null,
                GuardOutcome.TimedOut => "timeout",
                _ => result.Exception?.Message is { Length: > 0 } message ? message : "error",
            };
        }

        LastUsed = DateTimeOffset.UtcNow;
        _service.Log.Info(entry.Id, $"Content action {Action.Action.Id}: turn {(result.Outcome == GuardOutcome.Success ? "done" : result.Outcome.ToString().ToLowerInvariant())} in {clock.Elapsed.TotalSeconds:0.0} s ({Turns.Count} earlier turns)");
        Raise();
    }

    /// <summary>Moves the current question and answer into the history.</summary>
    private void FinishTurnLocked()
    {
        if (_answer.Length > 0 || _lastQuestion is not null)
        {
            _turns.Add(new ContentTurn(_lastQuestion, _answer.ToString()));
        }

        IsRunning = false;
    }

    private void Fail(string? reason)
    {
        if (reason is null)
        {
            return;
        }

        lock (_lock)
        {
            Error = reason;
            IsRunning = false;
        }

        Raise();
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _service.Log.Error(Action.ModuleId, "Answer window update failed", ex);
        }
    }

    /// <summary>The provider's pen – bound to one turn, ignored afterwards.</summary>
    private sealed class Writer(ContentActionSession session, int turn) : IContentResponse
    {
        private bool Current(Action change)
        {
            lock (session._lock)
            {
                if (session._disposed || turn != session._turnNumber || !session.IsRunning)
                {
                    return false;
                }

                change();
            }

            session.Raise();
            return true;
        }

        public void Append(string markdown)
        {
            if (!string.IsNullOrEmpty(markdown))
            {
                Current(() => session._answer.Append(markdown));
            }
        }

        public void SetText(string markdown) => Current(() =>
        {
            session._answer.Clear();
            session._answer.Append(markdown);
        });

        public void SetStatus(string? text) => Current(() => session.Status = string.IsNullOrWhiteSpace(text) ? null : text);

        public void SetOptions(IReadOnlyList<ResponseOption> options, string? selectedId) => Current(() =>
        {
            session.Options = [.. (options ?? []).Where(o => o is not null && !string.IsNullOrWhiteSpace(o.Id))];
            session.SelectedOption = selectedId;
        });

        public void SetResultText(string? text) => Current(() => session._resultText = text);
    }
}
