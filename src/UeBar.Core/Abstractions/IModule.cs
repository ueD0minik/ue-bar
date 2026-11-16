// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Localization;
using UeBar.Core.Logging;

namespace UeBar.Core.Abstractions;

public enum ModuleState
{
    Stopped,
    Starting,

    /// <summary>Running or connected.</summary>
    Connected,

    /// <summary>Running, but the connection is lost (e.g. server gone).</summary>
    Disconnected,

    /// <summary>Running, but settings are missing.</summary>
    NotConfigured,

    /// <summary>Switched off in the settings.</summary>
    Disabled,
    Error,
}

/// <summary>State of a module plus a short text for tray/settings (localized when <see cref="Text"/> is null).</summary>
public sealed record ModuleStatus(ModuleState State, string? Text = null)
{
    public static ModuleStatus Stopped { get; } = new(ModuleState.Stopped);
    public static ModuleStatus Starting { get; } = new(ModuleState.Starting);
    public static ModuleStatus Connected { get; } = new(ModuleState.Connected);
    public static ModuleStatus Disconnected { get; } = new(ModuleState.Disconnected);
    public static ModuleStatus NotConfigured { get; } = new(ModuleState.NotConfigured);
    public static ModuleStatus Disabled { get; } = new(ModuleState.Disabled);

    public static ModuleStatus Error(string text) => new(ModuleState.Error, text);

    public string DisplayText => Text ?? CoreText.T["ModuleState_" + State];
}

/// <summary>
/// A module (SPEC 4.1). Provides tile types; further capabilities are optional and implemented
/// through separate interfaces (<see cref="IEntitySource"/>, <see cref="IStatusProvider"/>,
/// <see cref="INotificationSource"/>, <see cref="IAssistProvider"/>, <see cref="IModuleSettings"/>,
/// <see cref="ILayoutStoreProvider"/>, <see cref="ILayoutSuggestionProvider"/>, <see cref="ISensorPublisher"/>).
/// The core calls everything exclusively through the ModuleGuard.
/// </summary>
public interface IModule
{
    /// <summary>Unique ID, e.g. "dummy". Appears in the layout as "module".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Icon name (Segoe Fluent Icons).</summary>
    string Icon { get; }

    ModuleStatus Status { get; }

    event EventHandler<ModuleStatus>? StatusChanged;

    IReadOnlyList<ITileType> TileTypes { get; }

    /// <summary>
    /// Starts the module. Should return quickly; start longer-running work through
    /// <see cref="IModuleContext.RunBackground"/>.
    /// </summary>
    Task StartAsync(IModuleContext context, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>What a module gets from the core when it starts.</summary>
public interface IModuleContext
{
    string ModuleId { get; }

    /// <summary>Module bus from this module's point of view (permissions are checked for this module).</summary>
    IModuleBus Bus { get; }

    IModuleLogger Log { get; }

    /// <summary>Cancelled when the module is stopped or faults.</summary>
    CancellationToken Stopping { get; }

    /// <summary>Own directory for cache files (%APPDATA%\ueBar\modules\&lt;id&gt;).</summary>
    string DataDirectory { get; }

    /// <summary>
    /// Starts a background task with the token <see cref="Stopping"/>.
    /// Exceptions are attributed to this module (status Error) instead of ending the app.
    /// </summary>
    void RunBackground(string name, Func<CancellationToken, Task> work);

    /// <summary>Reports an error the module caught itself and that makes it unusable.</summary>
    void ReportFault(Exception exception, string? operation = null);
}
