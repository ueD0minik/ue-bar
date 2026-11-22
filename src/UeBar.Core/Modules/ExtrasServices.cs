// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Sdk;
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

    /// <summary>
    /// Order of the sources set by the user (module IDs, first = preferred, P2-2). Modules that are
    /// not listed follow by their <see cref="INowPlayingSource.NowPlayingPriority"/>.
    /// </summary>
    public Func<IReadOnlyList<string>>? SourceOrder { get; set; }

    /// <summary>Best playback from several sources (public for tests).</summary>
    public static NowPlayingEntry? Choose(IEnumerable<NowPlayingEntry> candidates) => Choose(candidates, [], _ => 0);

    /// <summary>
    /// Best playback (P2-2): what is playing wins; then the user's order, then the module's priority
    /// (higher first), then what played last.
    /// </summary>
    public static NowPlayingEntry? Choose(IEnumerable<NowPlayingEntry> candidates, IReadOnlyList<string> order, Func<string, int> priority) =>
        candidates
            .OrderByDescending(c => c.Info.IsPlaying)
            .ThenBy(c => RankOf(order, c.ModuleId))
            .ThenByDescending(c => priority(c.ModuleId))
            .ThenByDescending(c => c.Info.LastActive)
            .FirstOrDefault();

    /// <summary>
    /// All modules that can report playback, in the effective order (settings page): the user's
    /// order first, then by priority, then by registration.
    /// </summary>
    public IReadOnlyList<ModuleEntry> OrderedSources()
    {
        var order = SourceOrder?.Invoke() ?? [];
        return
        [
            .. _registry.Modules.Where(m => m.Module is INowPlayingSource)
                .Select((m, index) => (Module: m, Index: index, Rank: RankOf(order, m.Id), Priority: Priority(m)))
                .OrderBy(x => x.Rank)
                .ThenByDescending(x => x.Priority)
                .ThenBy(x => x.Index)
                .Select(x => x.Module),
        ];
    }

    private static int RankOf(IReadOnlyList<string> order, string moduleId)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], moduleId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private int Priority(ModuleEntry entry) =>
        entry.IsLoaded
            ? _registry.Invoke(entry.Id, "Playback priority", m => ((INowPlayingSource)m).NowPlayingPriority).Value
            : 0;

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

        var priorities = candidates.ToDictionary(c => c.ModuleId, c => Priority(_registry.Find(c.ModuleId)!), StringComparer.OrdinalIgnoreCase);
        var best = Choose(candidates, SourceOrder?.Invoke() ?? [], id => priorities.GetValueOrDefault(id));
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

    private readonly List<HostCommand> _hostCommands = [];

    /// <summary>All commands offered by the host itself (P2-5: "pin text") and by running modules.</summary>
    public IReadOnlyList<(string ModuleId, QuickCommandInfo Command)> Commands =>
    [
        .. HostCommands.Select(c => (c.OwnerId, c.Info)),
        .. registry.GetCapabilities<IQuickCommandProvider>()
            .SelectMany(p => (registry.Invoke(p.Module.Id, "Quick commands", m => ((IQuickCommandProvider)m).QuickCommands).Value ?? [])
                .Select(c => (p.Module.Id, c))),
    ];

    private IReadOnlyList<HostCommand> HostCommands
    {
        get
        {
            lock (_hostCommands)
            {
                return [.. _hostCommands];
            }
        }
    }

    /// <summary>A command of the host (P2-5, e.g. pins): runs directly, not through a module.</summary>
    public void AddHostCommand(string ownerId, QuickCommandInfo info, Func<string, Task<QuickCommandResult>> run)
    {
        lock (_hostCommands)
        {
            _hostCommands.Add(new HostCommand(ownerId, info, run));
        }
    }

    /// <summary>Display name of the owner of a command (a module, or the host's own name).</summary>
    public Func<string, string?>? HostDisplayName { get; set; }

    private sealed record HostCommand(string OwnerId, QuickCommandInfo Info, Func<string, Task<QuickCommandResult>> Run);

    /// <summary>
    /// Recognizes "prefix text": the text must start with the prefix, a space may
    /// follow; the rest must not be empty. Longer prefixes win.
    /// </summary>
    public static QuickCommandMatch? Parse(string? text, IEnumerable<(string ModuleId, QuickCommandInfo Command)> commands)
    {
        var trimmed = text?.Trim() ?? "";
        foreach (var (moduleId, command) in commands.Where(c => !string.IsNullOrWhiteSpace(c.Command.Prefix)).OrderByDescending(c => c.Command.Prefix.Length))
        {
            var prefix = command.Prefix.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A word prefix ("t", "pomo") needs a space or the end after it: "toaster" is no timer (P2-3).
            if (char.IsLetterOrDigit(prefix[^1]) && trimmed.Length > prefix.Length && !char.IsWhiteSpace(trimmed[prefix.Length]))
            {
                continue;
            }

            var argument = trimmed[prefix.Length..].Trim();
            if (argument.Length > 0 || command.ArgumentOptional)
            {
                return new QuickCommandMatch(moduleId, command, argument);
            }
        }

        return null;
    }

    public QuickCommandMatch? Parse(string? text) => Parse(text, Commands);

    public async Task<QuickCommandResult> ExecuteAsync(QuickCommandMatch match)
    {
        if (HostCommands.FirstOrDefault(c => c.OwnerId == match.ModuleId && c.Info.Prefix == match.Command.Prefix) is { } host)
        {
            try
            {
                return await host.Run(match.Argument).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error(null, $"Quick command {match.Command.Prefix} failed", ex);
                return new QuickCommandResult(false, CoreText.T["Command_Failed"]);
            }
        }

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
