// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Optional capabilities from M6 (extras). Extension only, nothing in the contract broken (SPEC 4.8).

/// <summary>What is playing right now (music, video …) – for the pill (SPEC 10).</summary>
public sealed record NowPlayingInfo(
    string SourceId,
    string Title,
    string? Artist = null,
    string? ImageId = null,
    bool IsPlaying = false,
    string? SourceName = null)
{
    /// <summary>When it last played (for "last played" across several sources).</summary>
    public DateTimeOffset LastActive { get; init; } = DateTimeOffset.Now;
}

/// <summary>Control commands for the current playback.</summary>
public enum NowPlayingCommand
{
    PlayPause,
    Next,
    Previous,
}

/// <summary>
/// Module knows the current playback. <see cref="CurrentNowPlaying"/> must be fast (memory reads
/// only); the event comes from any thread. Images through <see cref="IImageSource"/>.
/// </summary>
public interface INowPlayingSource
{
    NowPlayingInfo? CurrentNowPlaying { get; }

    event EventHandler? NowPlayingChanged;

    Task ExecuteNowPlayingAsync(NowPlayingCommand command, CancellationToken cancellationToken);
}

/// <summary>A quick command with a prefix (e.g. "+" = add to the shopping list).</summary>
public sealed record QuickCommandInfo(string Prefix, string Description, string? Icon = null);

public sealed record QuickCommandResult(bool Success, string Message);

/// <summary>Module offers quick commands for the quick search and the Assist field (SPEC 10).</summary>
public interface IQuickCommandProvider
{
    IReadOnlyList<QuickCommandInfo> QuickCommands { get; }

    Task<QuickCommandResult> ExecuteQuickCommandAsync(string prefix, string argument, CancellationToken cancellationToken);
}

/// <summary>
/// Store small secrets of a module permanently (e.g. a webhook ID), encrypted like the
/// token. The core offers it on the <see cref="IModuleContext"/>: <c>context as IModuleSecrets</c>.
/// </summary>
public interface IModuleSecrets
{
    string? GetSecret(string key);

    /// <summary>Stores (null = delete).</summary>
    void SetSecret(string key, string? value);
}

/// <summary>
/// Assist with conversation context (M6-Fix 2), in addition to <see cref="IAssistProvider"/>. The core keeps
/// the context ID and starts a new conversation if the last message is older than
/// <see cref="ConversationTimeout"/> – so the display matches what the provider still remembers.
/// </summary>
public interface IAssistConversationProvider
{
    /// <summary>After this long a pause since the last message, the provider forgets the context.</summary>
    TimeSpan ConversationTimeout { get; }

    /// <summary>
    /// Ask in conversation <paramref name="conversationId"/> (null = new conversation). The answer
    /// carries the (possibly new) context ID in <see cref="AssistResponse.ConversationId"/>.
    /// </summary>
    Task<AssistResponse> AskAsync(string text, string? conversationId, CancellationToken cancellationToken);
}
