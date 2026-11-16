// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;

namespace UeBar.Modules.HomeAssistant.Tiles;

/// <summary>A service call as the answer to an interaction.</summary>
internal sealed record HaAction(string Domain, string Service, string EntityId, JsonObject? Data = null)
{
    /// <summary>Expected state that is shown right away (optimistic).</summary>
    public HaOptimistic? Optimistic { get; init; }

    /// <summary>Short text after confirmed execution (e.g. "Done").</summary>
    public string? SuccessFlash { get; init; }
}

/// <summary>
/// Optimistic state of an entity: <paramref name="State"/> is shown until HA reports a
/// state that <paramref name="Confirms"/> matches – or the time limit expires.
/// </summary>
internal sealed record HaOptimistic(string EntityId, HaEntityState State, Func<HaEntityState, bool> Confirms);

/// <summary>
/// Runtime of all HA tiles: tracks them (weakly), re-renders them on state/connection changes,
/// shows interactions right away (optimistic) and resets after a time limit or an error.
/// Service calls run through <see cref="IModuleContext.RunBackground"/> – no own timers/threads.
/// </summary>
internal sealed class HaTileHost
{
    private readonly Func<IModuleContext?> _context;
    private readonly Func<IModuleLogger> _log;
    private readonly Lock _lock = new();
    private readonly List<WeakReference<HaTile>> _tiles = [];
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _lastCall = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    public HaTileHost(HaClient client, Func<IModuleContext?> context, Func<IModuleLogger> log)
    {
        Client = client;
        _context = context;
        _log = log;
    }

    public HaClient Client { get; }

    public HaClientOptions Options => Client.Options;

    /// <summary>For tests: number of open optimistic states.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>State to display: optimistic if there is one, otherwise the real one.</summary>
    public HaEntityState? Get(string? entityId)
    {
        if (entityId is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (_pending.TryGetValue(entityId, out var pending))
            {
                return pending.Optimistic.State;
            }
        }

        return Client.Store.Get(entityId);
    }

    public HaEntityEntry? Entry(string? entityId) => entityId is null ? null : Client.Index.Get(entityId);

    /// <summary>Why no tile is operable right now (connection), or null.</summary>
    public string? ConnectionProblem() => Client.Status.State switch
    {
        HaConnectionState.ConnectedLocal or HaConnectionState.ConnectedExternal => null,
        HaConnectionState.NotConfigured => HaText.T["Tile_NotSetUp"],
        HaConnectionState.AuthInvalid => HaText.T["Status_TokenInvalid"],
        HaConnectionState.Connecting when !Client.EverConnected => HaText.T["Status_Connecting"],
        _ => HaText.T["Status_Disconnected"],
    };

    public void Track(HaTile tile)
    {
        lock (_lock)
        {
            _tiles.RemoveAll(w => !w.TryGetTarget(out _));
            _tiles.Add(new WeakReference<HaTile>(tile));
        }
    }

    /// <summary>Living tiles (for tests/diagnostics).</summary>
    public IReadOnlyList<HaTile> Tiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _tiles.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<HaTile>()];
            }
        }
    }

    public void RenderAll()
    {
        foreach (var tile in Tiles)
        {
            tile.Render();
        }
    }

    /// <summary>New state from HA: confirm an optimistic state if needed, redraw the affected tiles.</summary>
    public void OnStateChanged(HaStateChange change)
    {
        Pending? confirmed = null;
        Pending? superseded = null;
        lock (_lock)
        {
            if (change.NewState is { } state && _pending.TryGetValue(change.EntityId, out var pending))
            {
                if (pending.Optimistic.Confirms(state))
                {
                    _pending.Remove(change.EntityId);
                    confirmed = pending;
                }
                else if (pending.AwaitingNextState)
                {
                    // Media players after the 3 s expired: the next state applies, even if it does not match.
                    _pending.Remove(change.EntityId);
                    superseded = pending;
                }
            }
        }

        confirmed?.Done.TrySetResult(true);
        if (superseded is not null)
        {
            _log().Log(LogLevel.Info, $"{change.EntityId}: next state after the interaction does not match the expectation – showing the real state");
            superseded.Done.TrySetResult(false);
        }

        RenderUsing(change.EntityId);
    }

    /// <summary>Run an interaction: show it right away, call the service in the background, check the feedback.</summary>
    public void Execute(HaTile tile, HaAction action)
    {
        Pending? pending = null;
        if (action.Optimistic is { } optimistic)
        {
            var real = Client.Store.Get(optimistic.EntityId);
            if (real is null || !optimistic.Confirms(real))
            {
                Pending? replaced;
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    pending = new Pending(optimistic, ++_version, now + Options.OptimisticTimeout)
                    {
                        // Media players: after expiry wait for the next state instead of jumping back right away.
                        PatientUntil = action.Domain == "media_player" ? now + Options.MediaPlayerOptimisticMax : null,
                    };
                    _pending.TryGetValue(optimistic.EntityId, out replaced);
                    _pending[optimistic.EntityId] = pending;
                }

                replaced?.Done.TrySetResult(false);
                RenderUsing(optimistic.EntityId);
            }
        }

        var context = _context();
        if (context is null)
        {
            // Module not running – hint without background work; it expires on the next draw.
            Revert(pending);
            tile.SetAlert(HaText.T["Tile_NotConnected"], DateTimeOffset.UtcNow + Options.AlertDuration);
            return;
        }

        // Send strictly in order per entity: pressing "+" twice quickly must reach HA as 17, 18
        // – not the other way round (own background tasks would have no fixed order).
        Task previous;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            previous = _lastCall.TryGetValue(action.EntityId, out var last) ? last : Task.CompletedTask;
            _lastCall[action.EntityId] = sent.Task;
        }

        context.RunBackground($"Interaction {action.Domain}.{action.Service}", ct => RunAsync(tile, action, pending, previous, sent, ct));
    }

    private async Task RunAsync(HaTile tile, HaAction action, Pending? pending, Task previous, TaskCompletionSource sent, CancellationToken ct)
    {
        try
        {
            HaResult result;
            try
            {
                await previous.WaitAsync(ct).ConfigureAwait(false);
                var target = new JsonObject { ["entity_id"] = action.EntityId };
                result = await Client.CallServiceAsync(action.Domain, action.Service, action.Data, target, ct).ConfigureAwait(false);
            }
            finally
            {
                sent.TrySetResult();
                lock (_lock)
                {
                    if (_lastCall.TryGetValue(action.EntityId, out var last) && last == sent.Task)
                    {
                        _lastCall.Remove(action.EntityId);
                    }
                }
            }

            if (!result.Success)
            {
                Revert(pending);
                await ShowAlertAsync(tile, ShortError(result), ct).ConfigureAwait(false);
                return;
            }

            if (pending is not null)
            {
                var remaining = pending.Deadline - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.WhenAny(pending.Done.Task, Task.Delay(remaining, ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                var waited = Options.OptimisticTimeout;
                if (pending.PatientUntil is { } patientUntil && !pending.Done.Task.IsCompleted && WaitForNextState(pending))
                {
                    _log().Log(LogLevel.Info, $"{action.EntityId}: {action.Domain}.{action.Service} ok, but after {Options.OptimisticTimeout.TotalSeconds:0.#} s still no matching feedback – staying optimistic until the next state");
                    var rest = patientUntil - DateTimeOffset.UtcNow;
                    if (rest > TimeSpan.Zero)
                    {
                        await Task.WhenAny(pending.Done.Task, Task.Delay(rest, ct)).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                    }

                    waited = Options.MediaPlayerOptimisticMax;
                }

                if (Revert(pending))
                {
                    _log().Log(LogLevel.Info, $"{action.EntityId}: no matching feedback to {action.Domain}.{action.Service} within {waited.TotalSeconds:0.#} s – showing the real state");
                    await ShowAlertAsync(tile, HaText.T["Tile_NoFeedback"], ct).ConfigureAwait(false);
                    return;
                }

                if (!pending.Done.Task.IsCompleted || !pending.Done.Task.Result)
                {
                    return; // replaced by a newer interaction
                }
            }

            if (action.SuccessFlash is { } flash)
            {
                await ShowFlashAsync(tile, flash, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Revert(pending);
        }
        catch (Exception ex)
        {
            // A failed interaction must not put the module into the error state.
            _log().Log(LogLevel.Warning, $"Interaction {action.Domain}.{action.Service} for {action.EntityId} failed", ex);
            Revert(pending);
            await ShowAlertAsync(tile, HaText.T["Tile_Error"], CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>From now on the entity's next state ends the optimistic display (if it is still this one).</summary>
    private bool WaitForNextState(Pending pending)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(pending.Optimistic.EntityId, out var current) && current.Version == pending.Version)
            {
                current.AwaitingNextState = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>Drop the optimistic state if it is still this one. true = was dropped.</summary>
    private bool Revert(Pending? pending)
    {
        if (pending is null)
        {
            return false;
        }

        bool removed;
        lock (_lock)
        {
            removed = _pending.TryGetValue(pending.Optimistic.EntityId, out var current)
                && current.Version == pending.Version
                && _pending.Remove(pending.Optimistic.EntityId);
        }

        if (removed)
        {
            pending.Done.TrySetResult(false);
            RenderUsing(pending.Optimistic.EntityId);
        }

        return removed;
    }

    private async Task ShowAlertAsync(HaTile tile, string text, CancellationToken ct)
    {
        var token = tile.SetAlert(text);
        try
        {
            await Task.Delay(Options.AlertDuration, ct).ConfigureAwait(false);
        }
        finally
        {
            tile.ClearAlert(token);
        }
    }

    private async Task ShowFlashAsync(HaTile tile, string text, CancellationToken ct)
    {
        var token = tile.SetFlash(text);
        try
        {
            await Task.Delay(Options.FlashDuration, ct).ConfigureAwait(false);
        }
        finally
        {
            tile.ClearFlash(token);
        }
    }

    private void RenderUsing(string entityId)
    {
        foreach (var tile in Tiles)
        {
            if (tile.Uses(entityId))
            {
                tile.Render();
            }
        }
    }

    /// <summary>Short error message for the tile (details are in the log).</summary>
    private static string ShortError(HaResult result) => result.ErrorCode switch
    {
        "not_connected" or "disconnected" => HaText.T["Tile_NotConnected"],
        "timeout" => HaText.T["Tile_NoAnswer"],
        "not_found" or "service_not_found" => HaText.T["Tile_ServiceMissing"],
        "unauthorized" => HaText.T["Tile_NoPermission"],
        "invalid_format" or "service_validation_error" => HaText.T["Tile_InvalidValue"],
        _ => HaText.T["Tile_Error"],
    };

    private sealed record Pending(HaOptimistic Optimistic, long Version, DateTimeOffset Deadline)
    {
        /// <summary>true = confirmed, false = dropped/replaced.</summary>
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Media players: stay optimistic at most this long (null = other domains, only <see cref="Deadline"/>).</summary>
        public DateTimeOffset? PatientUntil { get; init; }

        /// <summary>After the deadline: the next state ends the display (under <c>_lock</c>).</summary>
        public bool AwaitingNextState { get; set; }
    }
}
