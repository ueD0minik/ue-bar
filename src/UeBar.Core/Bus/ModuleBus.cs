// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;
using UeBar.Core.Abstractions;
using UeBar.Core.Logging;
using UeBar.Core.Modules;

namespace UeBar.Core.Bus;

/// <summary>
/// Hub of the module bus (SPEC 4.7). Modules get their own access through <see cref="For"/>
/// (<see cref="IModuleBus"/>), so permissions are always checked for the real caller.
/// Every call into a module (run an action, deliver an event) goes through the <see cref="ModuleGuard"/>.
/// </summary>
public sealed class ModuleBus
{
    private readonly ModuleGuard _guard;
    private readonly IBusPermissionPolicy _policy;
    private readonly IUeLogger _log;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private readonly Dictionary<string, ActionEntry> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EventEntry> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ValueEntry> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Subscription<BusEvent>> _eventSubscriptions = [];
    private readonly List<Subscription<BusValueChanged>> _valueSubscriptions = [];

    public ModuleBus(ModuleGuard guard, IBusPermissionPolicy policy, IUeLogger log, TimeProvider? time = null)
    {
        _guard = guard;
        _policy = policy;
        _log = log;
        _time = time ?? guard.Time;
    }

    /// <summary>Set by the registry: is the module running right now (not stopped/disabled/faulted)?</summary>
    public Func<string, bool> IsModuleAvailable { get; set; } = _ => true;

    /// <summary>Time limit for actions.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public IModuleBus For(string moduleId) => new ScopedBus(this, moduleId);

    /// <summary>Removes all publications and subscriptions of a module (when it stops).</summary>
    public void RemoveModule(string moduleId)
    {
        lock (_lock)
        {
            RemoveWhere(_actions, e => Same(e.Owner, moduleId));
            RemoveWhere(_events, e => Same(e.Owner, moduleId));
            RemoveWhere(_values, e => Same(e.Owner, moduleId));
            _eventSubscriptions.RemoveAll(s => Same(s.SubscriberId, moduleId));
            _valueSubscriptions.RemoveAll(s => Same(s.SubscriberId, moduleId));
        }
    }

    // ------------------------------------------------------------------ Publish

    private BusResult PublishAction(string owner, string actionId, BusActionHandler handler, string? description)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return BusResult.Fail(BusErrorKind.Invalid, "Empty action ID");
        }

        lock (_lock)
        {
            if (_actions.TryGetValue(actionId, out var existing) && !Same(existing.Owner, owner))
            {
                return BusResult.Fail(BusErrorKind.Invalid, $"Action '{actionId}' already belongs to module '{existing.Owner}'");
            }

            _actions[actionId] = new ActionEntry(owner, handler, description);
        }

        return BusResult.Ok();
    }

    private BusResult PublishEvent(string owner, string eventId, string? description)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return BusResult.Fail(BusErrorKind.Invalid, "Empty event ID");
        }

        lock (_lock)
        {
            if (_events.TryGetValue(eventId, out var existing) && !Same(existing.Owner, owner))
            {
                return BusResult.Fail(BusErrorKind.Invalid, $"Event '{eventId}' already belongs to module '{existing.Owner}'");
            }

            _events[eventId] = new EventEntry(owner, description);
        }

        return BusResult.Ok();
    }

    private BusResult PublishValue(string owner, string valueId, JsonNode? initial, string? description)
    {
        if (string.IsNullOrWhiteSpace(valueId))
        {
            return BusResult.Fail(BusErrorKind.Invalid, "Empty value ID");
        }

        lock (_lock)
        {
            if (_values.TryGetValue(valueId, out var existing) && !Same(existing.Owner, owner))
            {
                return BusResult.Fail(BusErrorKind.Invalid, $"Value '{valueId}' already belongs to module '{existing.Owner}'");
            }

            _values[valueId] = new ValueEntry(owner, description) { Value = initial?.DeepClone() };
        }

        return BusResult.Ok();
    }

    private BusResult RaiseEvent(string owner, string eventId, JsonNode? payload)
    {
        List<Subscription<BusEvent>> targets;
        lock (_lock)
        {
            if (!_events.TryGetValue(eventId, out var entry))
            {
                return BusResult.Fail(BusErrorKind.NotFound, $"Event '{eventId}' is not published");
            }

            if (!Same(entry.Owner, owner))
            {
                return BusResult.Fail(BusErrorKind.Invalid, $"Event '{eventId}' belongs to module '{entry.Owner}'");
            }

            targets = _eventSubscriptions.Where(s => Same(s.Id, eventId)).ToList();
        }

        var timestamp = _time.GetUtcNow();
        foreach (var sub in targets)
        {
            if (!Allowed(sub.SubscriberId, owner, BusAccess.Events) || !IsModuleAvailable(sub.SubscriberId))
            {
                continue;
            }

            var evt = new BusEvent(owner, eventId, payload?.DeepClone(), timestamp);
            Dispatch(sub, evt, $"Bus event {eventId}");
        }

        return BusResult.Ok();
    }

    private BusResult SetValue(string owner, string valueId, JsonNode? value)
    {
        List<Subscription<BusValueChanged>> targets;
        lock (_lock)
        {
            if (!_values.TryGetValue(valueId, out var entry))
            {
                return BusResult.Fail(BusErrorKind.NotFound, $"Value '{valueId}' is not published");
            }

            if (!Same(entry.Owner, owner))
            {
                return BusResult.Fail(BusErrorKind.Invalid, $"Value '{valueId}' belongs to module '{entry.Owner}'");
            }

            entry.Value = value?.DeepClone();
            targets = _valueSubscriptions.Where(s => Same(s.Id, valueId)).ToList();
        }

        var timestamp = _time.GetUtcNow();
        foreach (var sub in targets)
        {
            if (!Allowed(sub.SubscriberId, owner, BusAccess.Values) || !IsModuleAvailable(sub.SubscriberId))
            {
                continue;
            }

            Dispatch(sub, new BusValueChanged(owner, valueId, value?.DeepClone(), timestamp), $"Bus value {valueId}");
        }

        return BusResult.Ok();
    }

    // ------------------------------------------------------------------ Use

    private async Task<BusResult> InvokeAsync(string caller, string actionId, JsonNode? arguments, CancellationToken cancellationToken)
    {
        ActionEntry? entry;
        lock (_lock)
        {
            _actions.TryGetValue(actionId, out entry);
        }

        if (entry is null)
        {
            return BusResult.Fail(BusErrorKind.NotFound, $"Action '{actionId}' is not published");
        }

        if (!Allowed(caller, entry.Owner, BusAccess.Actions))
        {
            _log.Info(caller, $"Bus: call of '{actionId}' denied (no permission {caller} → {entry.Owner})");
            return BusResult.Fail(BusErrorKind.Denied, $"No permission: '{caller}' may not use actions of '{entry.Owner}'");
        }

        if (!IsModuleAvailable(entry.Owner))
        {
            return BusResult.Fail(BusErrorKind.ModuleUnavailable, $"Module '{entry.Owner}' is not available");
        }

        var call = new BusCall(caller, actionId, arguments?.DeepClone());
        var result = await _guard.RunAsync(
            entry.Owner,
            $"Bus action {actionId} (from {caller})",
            ct => entry.Handler(call, ct),
            CallTimeout,
            reportFaults: true,
            cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            GuardOutcome.Success => BusResult.Ok(result.Value),
            GuardOutcome.TimedOut => BusResult.Fail(BusErrorKind.Timeout, $"Module '{entry.Owner}' did not answer in time"),
            GuardOutcome.Canceled => BusResult.Fail(BusErrorKind.Failed, "Cancelled"),
            _ => BusResult.Fail(BusErrorKind.Failed, $"Module '{entry.Owner}' reports an error: {result.Exception?.Message}"),
        };
    }

    private BusSubscription SubscribeEvent(string subscriber, string eventId, Action<BusEvent> handler)
    {
        lock (_lock)
        {
            if (_events.TryGetValue(eventId, out var entry) && !Allowed(subscriber, entry.Owner, BusAccess.Events))
            {
                return new BusSubscription(
                    BusResult.Fail(BusErrorKind.Denied, $"No permission: '{subscriber}' may not subscribe to events of '{entry.Owner}'"),
                    null);
            }

            var sub = new Subscription<BusEvent>(subscriber, eventId, handler);
            _eventSubscriptions.Add(sub);
            return new BusSubscription(BusResult.Ok(), () =>
            {
                lock (_lock)
                {
                    _eventSubscriptions.Remove(sub);
                }
            });
        }
    }

    private BusSubscription SubscribeValue(string subscriber, string valueId, Action<BusValueChanged> handler)
    {
        lock (_lock)
        {
            if (_values.TryGetValue(valueId, out var entry) && !Allowed(subscriber, entry.Owner, BusAccess.Values))
            {
                return new BusSubscription(
                    BusResult.Fail(BusErrorKind.Denied, $"No permission: '{subscriber}' may not subscribe to values of '{entry.Owner}'"),
                    null);
            }

            var sub = new Subscription<BusValueChanged>(subscriber, valueId, handler);
            _valueSubscriptions.Add(sub);
            return new BusSubscription(BusResult.Ok(), () =>
            {
                lock (_lock)
                {
                    _valueSubscriptions.Remove(sub);
                }
            });
        }
    }

    private BusResult GetValue(string caller, string valueId)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(valueId, out var entry))
            {
                return BusResult.Fail(BusErrorKind.NotFound, $"Value '{valueId}' is not published");
            }

            if (!Allowed(caller, entry.Owner, BusAccess.Values))
            {
                return BusResult.Fail(BusErrorKind.Denied, $"No permission: '{caller}' may not read values of '{entry.Owner}'");
            }

            if (!IsModuleAvailable(entry.Owner))
            {
                return BusResult.Fail(BusErrorKind.ModuleUnavailable, $"Module '{entry.Owner}' is not available");
            }

            return BusResult.Ok(entry.Value?.DeepClone());
        }
    }

    private IReadOnlyList<BusPublication> GetVisiblePublications(string caller)
    {
        lock (_lock)
        {
            var list = new List<BusPublication>();
            list.AddRange(_actions.Where(kv => Allowed(caller, kv.Value.Owner, BusAccess.Actions))
                .Select(kv => new BusPublication(kv.Key, kv.Value.Owner, BusAccess.Actions, kv.Value.Description)));
            list.AddRange(_events.Where(kv => Allowed(caller, kv.Value.Owner, BusAccess.Events))
                .Select(kv => new BusPublication(kv.Key, kv.Value.Owner, BusAccess.Events, kv.Value.Description)));
            list.AddRange(_values.Where(kv => Allowed(caller, kv.Value.Owner, BusAccess.Values))
                .Select(kv => new BusPublication(kv.Key, kv.Value.Owner, BusAccess.Values, kv.Value.Description)));
            return list;
        }
    }

    private void RequestPermission(string consumer, string provider, BusAccess access, string? reason)
    {
        _log.Info(consumer, $"Bus: Freigabe angefragt {consumer} → {provider} ({access}){(reason is null ? "" : $": {reason}")}");
        _policy.RecordRequest(new BusPermissionRequest(consumer, provider, access, reason));
    }

    // ------------------------------------------------------------------ Helpers

    private bool Allowed(string consumer, string provider, BusAccess access) =>
        Same(consumer, provider) || _policy.IsAllowed(consumer, provider, access);

    private void Dispatch<T>(Subscription<T> sub, T payload, string operation)
    {
        // Delivery inside the subscriber's fault protection – not awaited, so a hanging
        // subscriber does not block the sender.
        _ = _guard.RunAsync(sub.SubscriberId, operation, _ =>
        {
            sub.Handler(payload);
            return Task.CompletedTask;
        });
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void RemoveWhere<TValue>(Dictionary<string, TValue> dict, Func<TValue, bool> predicate)
    {
        foreach (var key in dict.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList())
        {
            dict.Remove(key);
        }
    }

    private sealed record ActionEntry(string Owner, BusActionHandler Handler, string? Description);

    private sealed record EventEntry(string Owner, string? Description);

    private sealed record ValueEntry(string Owner, string? Description)
    {
        public JsonNode? Value { get; set; }
    }

    private sealed record Subscription<T>(string SubscriberId, string Id, Action<T> Handler);

    private sealed class ScopedBus(ModuleBus hub, string moduleId) : IModuleBus
    {
        public string ModuleId => moduleId;

        public BusResult PublishAction(string actionId, BusActionHandler handler, string? description = null) =>
            hub.PublishAction(moduleId, actionId, handler, description);

        public BusResult PublishEvent(string eventId, string? description = null) =>
            hub.PublishEvent(moduleId, eventId, description);

        public BusResult RaiseEvent(string eventId, JsonNode? payload = null) =>
            hub.RaiseEvent(moduleId, eventId, payload);

        public BusResult PublishValue(string valueId, JsonNode? initialValue = null, string? description = null) =>
            hub.PublishValue(moduleId, valueId, initialValue, description);

        public BusResult SetValue(string valueId, JsonNode? value) =>
            hub.SetValue(moduleId, valueId, value);

        public Task<BusResult> InvokeAsync(string actionId, JsonNode? arguments = null, CancellationToken cancellationToken = default) =>
            hub.InvokeAsync(moduleId, actionId, arguments, cancellationToken);

        public BusSubscription SubscribeEvent(string eventId, Action<BusEvent> handler) =>
            hub.SubscribeEvent(moduleId, eventId, handler);

        public BusResult GetValue(string valueId) => hub.GetValue(moduleId, valueId);

        public BusSubscription SubscribeValue(string valueId, Action<BusValueChanged> handler) =>
            hub.SubscribeValue(moduleId, valueId, handler);

        public IReadOnlyList<BusPublication> GetVisiblePublications() => hub.GetVisiblePublications(moduleId);

        public void RequestPermission(string providerModuleId, BusAccess access, string? reason = null) =>
            hub.RequestPermission(moduleId, providerModuleId, access, reason);
    }
}
