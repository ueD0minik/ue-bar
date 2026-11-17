// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Text.Json.Nodes;

namespace UeBar.Sdk;

// Optional capabilities of a module (SPEC 4.1). The core checks for them with is/as.
// New capabilities are always added as a separate interface (SPEC 4.8).

/// <summary>A searchable "thing" of a module (lamp, script, volume …).</summary>
public sealed record EntityInfo(
    string Id,
    string Name,
    string? Icon = null,
    string? Area = null,
    string? StateText = null,
    string? Category = null);

/// <summary>Searchable, observable things with a default action (edit mode, quick search, tile suggestion).</summary>
public interface IEntitySource
{
    Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);

    /// <summary>Default action (e.g. toggle/run), e.g. for Enter in the quick search.</summary>
    Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>Suggests a tile for the thing (type, size, slots) or null.</summary>
    TileConfig? SuggestTile(EntityInfo entity);

    event EventHandler<EntityInfo>? EntityChanged;
}

/// <summary>Small observable value for the status line.</summary>
public sealed class StatusValue : ObservableModel
{
    public string Key { get; set => SetProperty(ref field, value); } = "";

    public string? Icon { get; set => SetProperty(ref field, value); }

    public string Text { get; set => SetProperty(ref field, value); } = "";

    public string? Tooltip { get; set => SetProperty(ref field, value); }
}

public sealed record StatusValueInfo(string Key, string DisplayName, string? Icon = null);

/// <summary>Provides small values for the status line.</summary>
public interface IStatusProvider
{
    IReadOnlyList<StatusValueInfo> AvailableStatusValues { get; }

    /// <summary>Observable value for the key (from <see cref="StatusItemConfig.Entity"/>) or null.</summary>
    StatusValue? GetStatusValue(string key);
}

/// <summary>A notification, no matter which module it comes from.</summary>
public sealed record UeNotification(
    string Title,
    string Message,
    string? Icon = null,
    TimeSpan? Duration = null,
    string? TargetDevice = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Origin (module ID); set by the core on receipt (M6).</summary>
    public string? Source { get; init; }
}

/// <summary>Delivers notifications to the core; the core shows them.</summary>
public interface INotificationSource
{
    event EventHandler<UeNotification>? NotificationReceived;
}

public sealed record AssistResponse(string Text, bool Success = true)
{
    /// <summary>Context ID of the conversation after this answer (M6-Fix 2, only with <see cref="IAssistConversationProvider"/>).</summary>
    public string? ConversationId { get; init; }
}

/// <summary>Takes text and returns an answer. Several providers possible, one is the default.</summary>
public interface IAssistProvider
{
    string AssistId { get; }

    string AssistDisplayName { get; }

    Task<AssistResponse> AskAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Provides storage locations for the base layout (e.g. sync through a server). The core treats them
/// as remote: it always keeps a local copy, marks changes made while offline as "pending"
/// and reconciles on the next <see cref="ILayoutStore.Changed"/> (M5b). If the store is currently
/// unreachable, load/save throw <see cref="LayoutStoreUnavailableException"/>. Empty list =
/// none available right now (e.g. module not set up).
/// </summary>
public interface ILayoutStoreProvider
{
    IReadOnlyList<ILayoutStore> LayoutStores { get; }
}

/// <summary>Provides a suggested layout for the first start (SPEC 9).</summary>
public interface ILayoutSuggestionProvider
{
    LayoutDocument? GetSuggestedLayout();
}

/// <summary>A measurement a publisher reports to a target.</summary>
public sealed record SensorReading(
    string Key,
    string? Name,
    JsonNode? Value,
    string? Unit = null,
    string? DeviceClass = null);

/// <summary>
/// Interface ONLY, no implementation yet (SPEC 4.1): later report data to a target
/// (MQTT, Webhook …).
/// </summary>
public interface ISensorPublisher
{
    string PublisherId { get; }

    Task PublishAsync(IReadOnlyList<SensorReading> readings, CancellationToken cancellationToken);
}
