// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;

namespace UeBar.Modules.HomeAssistant;

/// <summary>State of an entity (only for subscribed entities in memory).</summary>
internal sealed record HaEntityState(string EntityId, string State, JsonObject Attributes, DateTimeOffset? LastChanged)
{
    public string Domain => EntityId.Split('.', 2)[0];

    public string? FriendlyName => Attributes["friendly_name"]?.GetValue<string>();

    public T? Attribute<T>(string name)
    {
        try
        {
            return Attributes[name] is JsonValue v && v.TryGetValue<T>(out var value) ? value : default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>Number from an attribute – no matter whether stored as double, int or text.</summary>
    public double? Number(string name) => ToNumber(Attributes[name]);

    /// <summary>Attribute as text (numbers are converted invariantly), null if missing/null.</summary>
    public string? Text(string name) => Attributes[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => ToNumber(v)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? v.ToJsonString(),
        var other => other.ToJsonString(),
    };

    /// <summary>List of texts (e.g. options, supported_color_modes, source_list).</summary>
    public IReadOnlyList<string> Strings(string name) =>
        Attributes[name] is JsonArray array
            ? [.. array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()]
            : [];

    /// <summary>supported_features as a bit mask (0 if missing).</summary>
    public long Features => (long)(Number("supported_features") ?? 0);

    public bool Supports(long feature) => (Features & feature) == feature;

    /// <summary>State as a number (sensors, number entities).</summary>
    public double? StateNumber =>
        double.TryParse(State, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    public bool IsUnavailable => State is "unavailable";

    public bool IsUnknown => State is "unknown";

    /// <summary>Copy with changed state and/or attributes (for optimistic display).</summary>
    public HaEntityState With(string? state = null, params (string Key, JsonNode? Value)[] attributes)
    {
        var attrs = (JsonObject)Attributes.DeepClone();
        foreach (var (key, value) in attributes)
        {
            attrs[key] = value?.DeepClone();
        }

        return this with { State = state ?? State, Attributes = attrs };
    }

    private static double? ToNumber(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (v.TryGetValue<decimal>(out var m))
        {
            return (double)m;
        }

        return v.TryGetValue<string>(out var s)
            && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)
            ? p
            : null;
    }
}

internal sealed record HaStateChange(string EntityId, HaEntityState? OldState, HaEntityState? NewState, DateTimeOffset At);

/// <summary>
/// Thread-safe store of the subscribed states. Processes the compact format of
/// <c>subscribe_entities</c>: <c>a</c> (added/complete), <c>c</c> (changes with
/// <c>+</c>/<c>-</c>), <c>r</c> (removed). Short keys: s = state, a = attributes, lc = last_changed.
/// </summary>
internal sealed class EntityStore
{
    private readonly Dictionary<string, HaEntityState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly LinkedList<HaStateChange> _recent = new();

    /// <summary>Fires outside the lock, on the receive thread.</summary>
    public event Action<HaStateChange>? Changed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _states.Count;
            }
        }
    }

    public HaEntityState? Get(string entityId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(entityId, out var s) ? s : null;
        }
    }

    /// <summary>Recent changes (newest first).</summary>
    public IReadOnlyList<HaStateChange> Recent(int count)
    {
        lock (_lock)
        {
            return [.. _recent.Take(count)];
        }
    }

    /// <summary>Drop everything except the given entities (after the subscription changed).</summary>
    public void RetainOnly(IReadOnlySet<string> entityIds)
    {
        lock (_lock)
        {
            foreach (var id in _states.Keys.Where(k => !entityIds.Contains(k)).ToList())
            {
                _states.Remove(id);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _states.Clear();
        }
    }

    /// <summary>Apply an event of subscribe_entities.</summary>
    public void ApplyCompressed(JsonNode evt, DateTimeOffset now)
    {
        var changes = new List<HaStateChange>();
        lock (_lock)
        {
            if (evt["a"] is JsonObject added)
            {
                foreach (var (id, node) in added)
                {
                    if (node is not JsonObject compressed)
                    {
                        continue;
                    }

                    _states.TryGetValue(id, out var old);
                    var state = new HaEntityState(
                        id,
                        compressed["s"]?.ToString() ?? "unknown",
                        compressed["a"] as JsonObject is { } attrs ? (JsonObject)attrs.DeepClone() : [],
                        ParseTimestamp(compressed["lc"]) ?? ParseTimestamp(compressed["lu"]));
                    _states[id] = state;
                    changes.Add(new HaStateChange(id, old, state, now));
                }
            }

            if (evt["c"] is JsonObject changed)
            {
                foreach (var (id, node) in changed)
                {
                    if (node is not JsonObject diff || !_states.TryGetValue(id, out var old))
                    {
                        continue;
                    }

                    var attributes = (JsonObject)old.Attributes.DeepClone();
                    var stateText = old.State;
                    var lastChanged = old.LastChanged;

                    if (diff["+"] is JsonObject plus)
                    {
                        if (plus["s"] is { } s)
                        {
                            stateText = s.ToString();
                        }

                        if (plus["a"] is JsonObject plusAttrs)
                        {
                            foreach (var (key, value) in plusAttrs)
                            {
                                attributes[key] = value?.DeepClone();
                            }
                        }

                        lastChanged = ParseTimestamp(plus["lc"]) ?? (plus["s"] is not null ? now : lastChanged);
                    }

                    if (diff["-"] is JsonObject minus && minus["a"] is JsonArray removedAttrs)
                    {
                        foreach (var key in removedAttrs)
                        {
                            if (key?.ToString() is { } name)
                            {
                                attributes.Remove(name);
                            }
                        }
                    }

                    var state = old with { State = stateText, Attributes = attributes, LastChanged = lastChanged };
                    _states[id] = state;
                    changes.Add(new HaStateChange(id, old, state, now));
                }
            }

            if (evt["r"] is JsonArray removed)
            {
                foreach (var node in removed)
                {
                    if (node?.ToString() is { } id && _states.Remove(id, out var old))
                    {
                        changes.Add(new HaStateChange(id, old, null, now));
                    }
                }
            }

            foreach (var change in changes)
            {
                Remember(change);
            }
        }

        foreach (var change in changes)
        {
            Changed?.Invoke(change);
        }
    }

    /// <summary>Fallback path (get_states / state_changed): complete state in the normal HA format.</summary>
    public void ApplyFull(JsonObject fullState, DateTimeOffset now)
    {
        var id = fullState["entity_id"]?.GetValue<string>();
        if (id is null)
        {
            return;
        }

        HaStateChange change;
        lock (_lock)
        {
            _states.TryGetValue(id, out var old);
            var state = new HaEntityState(
                id,
                fullState["state"]?.ToString() ?? "unknown",
                fullState["attributes"] is JsonObject attrs ? (JsonObject)attrs.DeepClone() : [],
                DateTimeOffset.TryParse(fullState["last_changed"]?.GetValue<string>(), out var lc) ? lc : null);
            _states[id] = state;
            change = new HaStateChange(id, old, state, now);
            Remember(change);
        }

        Changed?.Invoke(change);
    }

    public void Remove(string entityId, DateTimeOffset now)
    {
        HaStateChange? change = null;
        lock (_lock)
        {
            if (_states.Remove(entityId, out var old))
            {
                change = new HaStateChange(entityId, old, null, now);
                Remember(change);
            }
        }

        if (change is not null)
        {
            Changed?.Invoke(change);
        }
    }

    private void Remember(HaStateChange change)
    {
        // Only remember real state changes (for the diagnostics), not every attribute change.
        if (change.OldState?.State == change.NewState?.State && change.OldState is not null)
        {
            return;
        }

        _recent.AddFirst(change);
        while (_recent.Count > 20)
        {
            _recent.RemoveLast();
        }
    }

    private static DateTimeOffset? ParseTimestamp(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        return null;
    }
}
