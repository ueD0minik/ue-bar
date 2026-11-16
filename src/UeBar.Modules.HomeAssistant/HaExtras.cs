// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Assist (M6): <c>assist_pipeline/run</c> with text input (intent → intent). The pipeline that
/// matches the UI language is used (R1; HA's preferred one if it fits or none does). If the command is missing,
/// <c>conversation/process</c> follows (with <c>language</c>). HA keeps the context through the <c>conversation_id</c>. Never throws –
/// errors come back as understandable text.
/// </summary>
internal sealed class HaAssist(HaClient client, Func<IModuleLogger> log)
{
    /// <summary>
    /// This long after the last message HA still knows the conversation: <c>CONVERSATION_TIMEOUT =
    /// timedelta(minutes=5)</c> in <c>homeassistant/helpers/chat_session.py</c> (checked 2026.10.4).
    /// </summary>
    public static readonly TimeSpan ConversationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The pipeline list is read again after this long (pipelines rarely change).</summary>
    private static readonly TimeSpan PipelineCacheTime = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private string? _conversationId;
    private DateTimeOffset _lastUse;
    private bool _pipelineMissing;
    private (string Language, string? PipelineId, DateTimeOffset At)? _pipelineChoice;

    /// <summary>Without context handling by the caller: the module continues the conversation itself for 5 minutes.</summary>
    public async Task<AssistResponse> AskAsync(string text, CancellationToken ct)
    {
        string? conversation;
        DateTimeOffset sentAt;
        lock (_lock)
        {
            sentAt = DateTimeOffset.UtcNow;
            conversation = sentAt - _lastUse < ConversationTimeout ? _conversationId : null;
        }

        var answer = await AskAsync(text, conversation, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _conversationId = answer.ConversationId;
            _lastUse = sentAt;
        }

        return answer;
    }

    /// <summary>Ask in the given conversation (null = new); the answer carries HA's context ID.</summary>
    public async Task<AssistResponse> AskAsync(string text, string? conversation, CancellationToken ct)
    {
        if (!client.IsConnected)
        {
            return new AssistResponse(HaText.T["Assist_NotConnected"], false) { ConversationId = conversation };
        }

        var (answer, newConversation) = _pipelineMissing ? (null, null) : await RunPipelineAsync(text, conversation, ct).ConfigureAwait(false);
        if (answer is null)
        {
            (answer, newConversation) = await ProcessAsync(text, conversation, ct).ConfigureAwait(false);
        }

        return answer with { ConversationId = newConversation ?? conversation };
    }

    /// <summary>Returns (null, …) if the pipeline is not available (then conversation/process follows).</summary>
    private async Task<(AssistResponse? Answer, string? Conversation)> RunPipelineAsync(string text, string? conversation, CancellationToken ct)
    {
        var done = new TaskCompletionSource<(AssistResponse Answer, string? Conversation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new JsonObject
        {
            ["type"] = "assist_pipeline/run",
            ["start_stage"] = "intent",
            ["end_stage"] = "intent",
            ["input"] = new JsonObject { ["text"] = text },
        };
        if (await PipelineForAsync(HaText.Language, ct).ConfigureAwait(false) is { } pipeline)
        {
            command["pipeline"] = pipeline;
        }

        if (conversation is not null)
        {
            command["conversation_id"] = conversation;
        }

        var (result, id) = await client.SubscribeAsync(command, evt =>
        {
            switch (evt["type"]?.ToString())
            {
                case "intent-end":
                    var output = evt["data"]?["intent_output"];
                    done.TrySetResult((FromResponse(output?["response"]), output?["conversation_id"]?.ToString()));
                    break;
                case "error":
                    var message = evt["data"]?["message"]?.ToString() ?? evt["data"]?["code"]?.ToString() ?? HaText.T["Assist_UnknownError"];
                    done.TrySetResult((new AssistResponse(HaText.T.Format("Assist_Error", message), false), null));
                    break;
                case "run-end":
                    done.TrySetResult((new AssistResponse(HaText.T["Assist_NoAnswer"], false), null));
                    break;
            }
        }, ct).ConfigureAwait(false);

        try
        {
            if (!result.Success)
            {
                if (result.ErrorCode == "unknown_command")
                {
                    _pipelineMissing = true;
                    log().Log(LogLevel.Info, "assist_pipeline/run not available – using conversation/process");
                    return (null, null);
                }

                return (new AssistResponse(HaText.T.Format("Assist_NotPossible", result.ErrorMessage ?? result.ErrorCode), false), null);
            }

            var (answer, newConversation) = await done.Task.WaitAsync(ct).ConfigureAwait(false);
            return (answer, newConversation);
        }
        finally
        {
            client.ForgetSubscription(id);
        }
    }

    /// <summary>
    /// Pipeline for the UI language (R1): null = HA's preferred pipeline (it matches, or no pipeline
    /// has this language, or the list is not readable). Cached for <see cref="PipelineCacheTime"/>.
    /// </summary>
    private async Task<string?> PipelineForAsync(string language, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_pipelineChoice is { } cached && cached.Language == language && DateTimeOffset.UtcNow - cached.At < PipelineCacheTime)
            {
                return cached.PipelineId;
            }
        }

        var result = await client.SendCommandAsync(new JsonObject { ["type"] = "assist_pipeline/pipeline/list" }, ct).ConfigureAwait(false);
        var chosen = result.Success ? ChoosePipeline(result.Result, language) : null;
        log().Log(LogLevel.Info, $"Assist: language {language} → pipeline {chosen ?? "preferred"}{(result.Success ? "" : $" (list not readable: {result.ErrorCode})")}");
        lock (_lock)
        {
            _pipelineChoice = (language, chosen, DateTimeOffset.UtcNow);
        }

        return chosen;
    }

    /// <summary>
    /// From <c>assist_pipeline/pipeline/list</c>: null if the preferred pipeline has the language
    /// (or none has it), otherwise the ID of the first pipeline with this language ("de" also matches "de-AT").
    /// </summary>
    internal static string? ChoosePipeline(JsonNode? list, string language)
    {
        if (list?["pipelines"] is not JsonArray pipelines)
        {
            return null;
        }

        bool Matches(JsonNode? pipeline) =>
            pipeline?["language"]?.ToString() is { } lang
            && (lang.Equals(language, StringComparison.OrdinalIgnoreCase) || lang.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

        var preferred = list["preferred_pipeline"]?.ToString();
        if (pipelines.FirstOrDefault(p => p?["id"]?.ToString() == preferred) is { } preferredPipeline && Matches(preferredPipeline))
        {
            return null;
        }

        return pipelines.FirstOrDefault(Matches)?["id"]?.ToString();
    }

    private async Task<(AssistResponse Answer, string? Conversation)> ProcessAsync(string text, string? conversation, CancellationToken ct)
    {
        var command = new JsonObject { ["type"] = "conversation/process", ["text"] = text, ["language"] = HaText.Language };
        if (conversation is not null)
        {
            command["conversation_id"] = conversation;
        }

        var result = await client.SendCommandAsync(command, ct).ConfigureAwait(false);
        return result.Success
            ? (FromResponse(result.Result?["response"]), result.Result?["conversation_id"]?.ToString())
            : (new AssistResponse(HaText.T.Format("Assist_NotPossible", result.ErrorMessage ?? result.ErrorCode), false), null);
    }

    /// <summary>Answer from HA: spoken text; response_type "error" = not successful.</summary>
    internal static AssistResponse FromResponse(JsonNode? response)
    {
        var speech = response?["speech"]?["plain"]?["speech"]?.ToString();
        var failed = response?["response_type"]?.ToString() == "error";
        return new AssistResponse(string.IsNullOrWhiteSpace(speech) ? (failed ? HaText.T["Assist_Failed"] : HaText.T["Assist_Done"]) : speech.Trim(), !failed);
    }
}

/// <summary>
/// Now Playing from the HA media players (M6): candidates are the configured players, otherwise the
/// media players in the layout. If one is playing, it wins; otherwise the most recently changed paused one with a title.
/// Off/standby/without title does not count.
/// </summary>
internal static class HaNowPlaying
{
    public static NowPlayingInfo? Choose(IEnumerable<string> players, Func<string, HaEntityState?> state, Func<string, string> name)
    {
        NowPlayingInfo? best = null;
        foreach (var id in players.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (state(id) is not { } s || s.State is not ("playing" or "paused" or "buffering") || s.Attribute<string>("media_title") is not { Length: > 0 } title)
            {
                continue;
            }

            var info = new NowPlayingInfo(
                id,
                title,
                s.Attribute<string>("media_artist") ?? s.Attribute<string>("media_album_name"),
                HaImages.IdFor(s),
                s.State is "playing" or "buffering",
                name(id))
            {
                LastActive = s.LastChanged ?? DateTimeOffset.MinValue,
            };

            if (best is null
                || (info.IsPlaying && !best.IsPlaying)
                || (info.IsPlaying == best.IsPlaying && info.LastActive > best.LastActive))
            {
                best = info;
            }
        }

        return best;
    }
}
