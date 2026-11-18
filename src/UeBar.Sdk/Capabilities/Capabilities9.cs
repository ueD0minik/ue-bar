// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Sdk;

// Optional capabilities from P2-3 (contract 1.2). Extension only (SPEC 4.8).

/// <summary>A button on a notification (<see cref="UeNotification.Actions"/>).</summary>
public sealed record NotificationAction(string Id, string Label, string? Icon = null);

/// <summary>Module reacts to the buttons of its notifications.</summary>
public interface INotificationActionHandler
{
    /// <summary><paramref name="notificationId"/> = <see cref="UeNotification.Id"/>, <paramref name="actionId"/> = <see cref="NotificationAction.Id"/>.</summary>
    Task HandleNotificationActionAsync(string notificationId, string actionId, CancellationToken cancellationToken);
}

/// <summary>
/// Something going on right now that the pill shows when it comes out (like the Now Bar: a running
/// timer). Collapsed nothing is shown (OLED).
/// </summary>
/// <param name="Id">ID within the module.</param>
/// <param name="Title">e.g. "Pizza".</param>
/// <param name="Text">e.g. "4:32" – the module updates it while the bar is visible (<see cref="ModuleVisibility.IsBarVisible"/>).</param>
/// <param name="Icon">Icon name (Segoe Fluent Icons or "U+E7xx").</param>
public sealed record PillActivity(string Id, string Title, string Text, string? Icon = null)
{
    /// <summary>When it is due – of several activities the pill shows the one due first.</summary>
    public DateTimeOffset? Due { get; init; }
}

/// <summary>Module has an activity for the pill. <see cref="CurrentPillActivity"/> must be fast; the event comes from any thread.</summary>
public interface IPillActivitySource
{
    PillActivity? CurrentPillActivity { get; }

    event EventHandler? PillActivityChanged;
}

/// <summary>Module runs the buttons of its settings page (<see cref="SettingFieldKind.Button"/>).</summary>
public interface IModuleSettingsActions
{
    /// <summary>Returns a short result text to show under the button, or null.</summary>
    Task<string?> RunSettingsActionAsync(string key, CancellationToken cancellationToken);
}
