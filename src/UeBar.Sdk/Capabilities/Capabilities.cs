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

    /// <summary>
    /// ID chosen by the module (contract 1.2) – comes back with a button press
    /// (<see cref="INotificationActionHandler"/>).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>Buttons on the notification (e.g. "+5 min", contract 1.2). Shown while the panel is open.</summary>
    public IReadOnlyList<NotificationAction>? Actions { get; init; }

    /// <summary>Show even while Windows "Do not disturb" is on (e.g. a timer, contract 1.2).</summary>
    public bool IgnoreDoNotDisturb { get; init; }

    /// <summary>Show even over fullscreen apps (contract 1.2).</summary>
    public bool ShowInFullscreen { get; init; }

    /// <summary>
    /// Sound when it is shown (contract 1.2): <see cref="DefaultSound"/> = a Windows alarm sound,
    /// otherwise the path of a sound file (.wav, .mp3); null = silent.
    /// </summary>
    public string? Sound { get; init; }

    /// <summary>Volume of <see cref="Sound"/>, 0–1.</summary>
    public double SoundVolume { get; init; } = 1;

    /// <summary>Value for <see cref="Sound"/>: the Windows alarm sound.</summary>
    public const string DefaultSound = "default";

    /// <summary>
    /// How important it is (contract 1.3). <see cref="NotificationPriority.Important"/> may come out while
    /// the bar is paused, during "Do not disturb" and over fullscreen apps – as the bar settings allow.
    /// Prefer this over <see cref="IgnoreDoNotDisturb"/>/<see cref="ShowInFullscreen"/> (still honored).
    /// </summary>
    public NotificationPriority Priority { get; init; }
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
