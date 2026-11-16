// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;

namespace UeBar.Core.Modules;

/// <summary>
/// Collects notifications of all modules with <see cref="INotificationSource"/> (M6). Sets the
/// origin (<see cref="UeNotification.Source"/>); <see cref="Received"/> comes from any thread.
/// </summary>
public sealed class NotificationHub
{
    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly HashSet<IModule> _subscribed = [];
    private readonly Lock _lock = new();

    public NotificationHub(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
        _registry.ModuleStatusChanged += (_, _) => Attach();
        Attach();
    }

    public event EventHandler<UeNotification>? Received;

    private void Attach()
    {
        foreach (var entry in _registry.Modules.Where(m => m.Module is INotificationSource))
        {
            lock (_lock)
            {
                if (!_subscribed.Add(entry.Module))
                {
                    continue;
                }
            }

            var id = entry.Id;
            _registry.Invoke(id, "Subscribe to notifications", m =>
            {
                ((INotificationSource)m).NotificationReceived += (_, n) => OnNotification(id, n);
                return true;
            });
        }
    }

    private void OnNotification(string moduleId, UeNotification notification)
    {
        try
        {
            var withSource = notification with { Source = moduleId };
            _log.Info(moduleId, $"Notification: {Shorten(withSource.Title)}");
            Received?.Invoke(this, withSource);
        }
        catch (Exception ex)
        {
            _log.Error(moduleId, "Error forwarding a notification", ex);
        }
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";
}

/// <summary>Current playback across all modules: playing now > last active (M6).</summary>
public sealed record NowPlayingEntry(string ModuleId, NowPlayingInfo Info);

/// <summary>
/// Picks the current playback from all running <see cref="INowPlayingSource"/>: what is playing now
/// wins, otherwise the last active one. <see cref="Changed"/> comes from any thread.
/// </summary>
public sealed class NowPlayingService
{
    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly HashSet<IModule> _subscribed = [];
    private readonly Lock _lock = new();
    private NowPlayingEntry? _current;

    public NowPlayingService(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
        _registry.ModuleStatusChanged += (_, _) => Refresh();
        Refresh();
    }

    public event EventHandler? Changed;

    public NowPlayingEntry? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Best playback from several sources (public for tests).</summary>
    public static NowPlayingEntry? Choose(IEnumerable<NowPlayingEntry> candidates) =>
        candidates
            .OrderByDescending(c => c.Info.IsPlaying)
            .ThenByDescending(c => c.Info.LastActive)
            .FirstOrDefault();

    public void Refresh()
    {
        var candidates = new List<NowPlayingEntry>();
        foreach (var entry in _registry.Modules.Where(m => m.Module is INowPlayingSource))
        {
            Subscribe(entry);
            if (!entry.IsAvailable)
            {
                continue;
            }

            var result = _registry.Invoke(entry.Id, "Query playback", m => ((INowPlayingSource)m).CurrentNowPlaying);
            if (result is { Succeeded: true, Value: { } info })
            {
                candidates.Add(new NowPlayingEntry(entry.Id, info));
            }
        }

        var best = Choose(candidates);
        bool changed;
        lock (_lock)
        {
            changed = !Equals(_current, best);
            _current = best;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ExecuteAsync(NowPlayingCommand command)
    {
        if (Current is not { } current)
        {
            return;
        }

        var result = await _registry.InvokeAsync(current.ModuleId, $"Wiedergabe {command}", async (m, ct) =>
        {
            await ((INowPlayingSource)m).ExecuteNowPlayingAsync(command, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _log.Warn(current.ModuleId, $"Wiedergabe-Befehl {command} fehlgeschlagen ({result.Outcome})");
        }
    }

    private void Subscribe(ModuleEntry entry)
    {
        lock (_lock)
        {
            if (!_subscribed.Add(entry.Module))
            {
                return;
            }
        }

        _registry.Invoke(entry.Id, "Subscribe to playback", m =>
        {
            ((INowPlayingSource)m).NowPlayingChanged += (_, _) => Refresh();
            return true;
        });
    }
}

/// <summary>A recognized quick command: module, prefix, rest of the text.</summary>
public sealed record QuickCommandMatch(string ModuleId, QuickCommandInfo Command, string Argument);

/// <summary>Quick commands of all modules (prefix + text, e.g. "+ milk") – recognize and run (M6).</summary>
public sealed class QuickCommandService(ModuleRegistry registry, IUeLogger log)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>All commands offered by running modules.</summary>
    public IReadOnlyList<(string ModuleId, QuickCommandInfo Command)> Commands =>
    [
        .. registry.GetCapabilities<IQuickCommandProvider>()
            .SelectMany(p => (registry.Invoke(p.Module.Id, "Quick commands", m => ((IQuickCommandProvider)m).QuickCommands).Value ?? [])
                .Select(c => (p.Module.Id, c))),
    ];

    /// <summary>
    /// Recognizes "prefix text": the text must start with the prefix, a space may
    /// follow; the rest must not be empty. Longer prefixes win.
    /// </summary>
    public static QuickCommandMatch? Parse(string? text, IEnumerable<(string ModuleId, QuickCommandInfo Command)> commands)
    {
        var trimmed = text?.Trim() ?? "";
        foreach (var (moduleId, command) in commands.Where(c => !string.IsNullOrWhiteSpace(c.Command.Prefix)).OrderByDescending(c => c.Command.Prefix.Length))
        {
            if (trimmed.StartsWith(command.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                var argument = trimmed[command.Prefix.Length..].Trim();
                if (argument.Length > 0)
                {
                    return new QuickCommandMatch(moduleId, command, argument);
                }
            }
        }

        return null;
    }

    public QuickCommandMatch? Parse(string? text) => Parse(text, Commands);

    public async Task<QuickCommandResult> ExecuteAsync(QuickCommandMatch match)
    {
        var result = await registry.InvokeAsync(
            match.ModuleId,
            $"Quick command {match.Command.Prefix}",
            (m, ct) => ((IQuickCommandProvider)m).ExecuteQuickCommandAsync(match.Command.Prefix, match.Argument, ct),
            Timeout).ConfigureAwait(false);
        if (result is { Succeeded: true, Value: { } value })
        {
            return value;
        }

        log.Warn(match.ModuleId, $"Quick command {match.Command.Prefix} failed ({result.Outcome})");
        return new QuickCommandResult(false, result.Outcome == GuardOutcome.TimedOut ? CoreText.T["Command_TimedOut"] : CoreText.T["Command_Failed"]);
    }
}

/// <summary>
/// Assist (M6): text to the default <see cref="IAssistProvider"/> (first running module) with a
/// time limit; quick commands ("+ milk") go to <see cref="QuickCommandService"/> first.
/// Conversation context (M6-Fix 2): if the provider supports <see cref="IAssistConversationProvider"/>, the
/// service keeps the context ID and starts over after the timeout (module value, can be overridden).
/// </summary>
public sealed class AssistService(ModuleRegistry registry, QuickCommandService quickCommands, IUeLogger log, TimeSpan? timeout = null, TimeProvider? time = null)
{
    /// <summary>Generous: the pipeline may use a cloud language model.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private string? _conversationId;
    private string? _conversationModule;
    private DateTimeOffset? _lastMessageAt;

    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    /// <summary>Overrides the module's conversation timeout (setting; null = module value).</summary>
    public Func<TimeSpan?>? ConversationTimeoutOverride { get; set; }

    /// <summary>Current context ID (null = new conversation).</summary>
    public string? ConversationId
    {
        get
        {
            lock (_lock)
            {
                return _conversationId;
            }
        }
    }

    /// <summary>When the last question went to the provider (null = none in the current conversation yet).</summary>
    public DateTimeOffset? LastMessageAt
    {
        get
        {
            lock (_lock)
            {
                return _lastMessageAt;
            }
        }
    }

    /// <summary>Conversation timeout: setting, otherwise the default provider's value; null = unknown (no automatic restart).</summary>
    public TimeSpan? ConversationTimeout
    {
        get
        {
            if (ConversationTimeoutOverride?.Invoke() is { } fixedTimeout && fixedTimeout > TimeSpan.Zero)
            {
                return fixedTimeout;
            }

            if (registry.GetCapabilities<IAssistProvider>() is [var (module, provider), ..] && provider is IAssistConversationProvider)
            {
                var result = registry.Invoke(module.Id, "Assist timeout", m => ((IAssistConversationProvider)m).ConversationTimeout);
                return result.Succeeded && result.Value > TimeSpan.Zero ? result.Value : null;
            }

            return null;
        }
    }

    /// <summary>Is the last message older than the timeout? Then the provider no longer knows the conversation.</summary>
    public bool IsConversationExpired()
    {
        var last = LastMessageAt;
        return last is { } at && ConversationTimeout is { } limit && _time.GetUtcNow() - at >= limit;
    }

    /// <summary>New conversation: drop the context ID.</summary>
    public void NewConversation()
    {
        lock (_lock)
        {
            _conversationId = null;
            _conversationModule = null;
            _lastMessageAt = null;
        }
    }

    /// <summary>Name of the default provider (for "Ask …") or null if there is none.</summary>
    public string? ProviderName =>
        registry.GetCapabilities<IAssistProvider>() is [var (module, provider), ..]
            ? registry.Invoke(module.Id, "Assist name", _ => provider.AssistDisplayName).Value ?? module.DisplayName
            : null;

    public async Task<AssistResponse> AskAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AssistResponse("", false);
        }

        if (quickCommands.Parse(text) is { } match)
        {
            var command = await quickCommands.ExecuteAsync(match).ConfigureAwait(false);
            return new AssistResponse(command.Message, command.Success);
        }

        if (registry.GetCapabilities<IAssistProvider>() is not [var (module, _), ..])
        {
            return new AssistResponse(CoreText.T["Assist_NoProvider"], false);
        }

        // Context: expired or different provider → new conversation.
        if (IsConversationExpired())
        {
            log.Info(module.Id, "Assist: conversation timeout expired – new conversation");
            NewConversation();
        }

        string? conversation;
        lock (_lock)
        {
            conversation = _conversationModule == module.Id ? _conversationId : null;
        }

        var sentAt = _time.GetUtcNow(); // the provider counts the timeout from receipt of the message
        var question = text.Trim();
        var result = await registry.InvokeAsync(
                module.Id,
                "Assist",
                (m, ct) => m is IAssistConversationProvider withContext
                    ? withContext.AskAsync(question, conversation, ct)
                    : ((IAssistProvider)m).AskAsync(question, ct),
                Timeout)
            .ConfigureAwait(false);
        switch (result.Outcome)
        {
            case GuardOutcome.Success when result.Value is { } answer:
                lock (_lock)
                {
                    _conversationModule = module.Id;
                    _conversationId = answer.ConversationId ?? conversation;
                    _lastMessageAt = sentAt;
                }

                return answer;
            case GuardOutcome.TimedOut:
                log.Warn(module.Id, $"Assist: no answer after {Timeout.TotalSeconds:0} s");
                return new AssistResponse(CoreText.T.Format("Assist_TimedOut", Timeout.TotalSeconds), false);
            default:
                return new AssistResponse(CoreText.T.Format("Assist_Unreachable", module.DisplayName), false);
        }
    }
}
