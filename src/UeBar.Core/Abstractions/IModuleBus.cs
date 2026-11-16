// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;

namespace UeBar.Core.Abstractions;

/// <summary>What a module may use over the bus – permission per pair and direction.</summary>
[Flags]
public enum BusAccess
{
    None = 0,
    Actions = 1,
    Events = 2,
    Values = 4,
    All = Actions | Events | Values,
}

public enum BusErrorKind
{
    None,

    /// <summary>Action/event/value is not published (any more).</summary>
    NotFound,

    /// <summary>No permission for this module pair.</summary>
    Denied,

    /// <summary>Target module is stopped, disabled or faulted.</summary>
    ModuleUnavailable,

    /// <summary>Target module threw an exception while executing.</summary>
    Failed,

    /// <summary>Target module did not answer in time.</summary>
    Timeout,

    /// <summary>Invalid call (e.g. duplicate publication).</summary>
    Invalid,
}

/// <summary>Result of a bus call. Never throws – errors come back as data.</summary>
public sealed record BusResult(bool Success, BusErrorKind Error = BusErrorKind.None, string? Message = null, JsonNode? Value = null)
{
    public static BusResult Ok(JsonNode? value = null) => new(true, BusErrorKind.None, null, value);

    public static BusResult Fail(BusErrorKind error, string message) => new(false, error, message);
}

/// <summary>Subscription to an event/value. Dispose ends it.</summary>
public sealed class BusSubscription : IDisposable
{
    private Action? _unsubscribe;

    internal BusSubscription(BusResult result, Action? unsubscribe)
    {
        Result = result;
        _unsubscribe = unsubscribe;
    }

    public BusResult Result { get; }

    public bool Success => Result.Success;

    public void Dispose()
    {
        Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}

public sealed record BusCall(string CallerModuleId, string ActionId, JsonNode? Arguments);

public sealed record BusEvent(string SourceModuleId, string EventId, JsonNode? Payload, DateTimeOffset Timestamp);

public sealed record BusValueChanged(string SourceModuleId, string ValueId, JsonNode? Value, DateTimeOffset Timestamp);

public delegate Task<JsonNode?> BusActionHandler(BusCall call, CancellationToken cancellationToken);

/// <summary>Something a module has published.</summary>
public sealed record BusPublication(string Id, string OwnerModuleId, BusAccess Kind, string? Description);

/// <summary>
/// Module bus (SPEC 4.7) from a module's point of view. Modules never know each other directly; everything
/// goes through here, only with permission (default: denied) and through the fault protection.
/// </summary>
public interface IModuleBus
{
    /// <summary>Module this bus access belongs to.</summary>
    string ModuleId { get; }

    // --- Publish (own things) ---
    BusResult PublishAction(string actionId, BusActionHandler handler, string? description = null);

    BusResult PublishEvent(string eventId, string? description = null);

    BusResult RaiseEvent(string eventId, JsonNode? payload = null);

    BusResult PublishValue(string valueId, JsonNode? initialValue = null, string? description = null);

    BusResult SetValue(string valueId, JsonNode? value);

    // --- Use (other modules' things, only with permission) ---
    Task<BusResult> InvokeAsync(string actionId, JsonNode? arguments = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to an event. If it is already published and not permitted,
    /// <see cref="BusErrorKind.Denied"/> comes back immediately. If it is not published yet, the subscription waits;
    /// the permission is then checked on every delivery.
    /// </summary>
    BusSubscription SubscribeEvent(string eventId, Action<BusEvent> handler);

    BusResult GetValue(string valueId);

    BusSubscription SubscribeValue(string valueId, Action<BusValueChanged> handler);

    /// <summary>Everything this module may see (own + permitted).</summary>
    IReadOnlyList<BusPublication> GetVisiblePublications();

    /// <summary>Request a permission; the user confirms it once later.</summary>
    void RequestPermission(string providerModuleId, BusAccess access, string? reason = null);
}
