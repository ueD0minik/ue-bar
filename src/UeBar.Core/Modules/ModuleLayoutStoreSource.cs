// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using UeBar.Core.Layout;
using UeBar.Core.Logging;

namespace UeBar.Core.Modules;

/// <summary>
/// Finds the modules' layout store (<see cref="ILayoutStoreProvider"/>, M5b): the first
/// running provider in registration order that currently offers a store. Switches
/// on module status changes (set up, switched on/off, restart). All calls into the
/// module go through the guard; "unreachable" is not a module fault.
/// </summary>
public sealed class ModuleLayoutStoreSource : IRemoteLayoutSource, IDisposable
{
    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly Lock _lock = new();
    private GuardedLayoutStore? _current;
    private bool _disposed;
    private string? _preference;

    /// <summary>Value for "local only" in <see cref="Preference"/>.</summary>
    public const string LocalOnly = "local";

    public ModuleLayoutStoreSource(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
        _registry.ModuleStatusChanged += OnModuleStatusChanged;
        Refresh();
    }

    public ILayoutStore? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Store switched or the current one reports a change (may come from any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Storage location (setting, M7): null = automatic (first module with a store),
    /// <see cref="LocalOnly"/> = local only, otherwise the ID of a specific module.
    /// </summary>
    public string? Preference
    {
        get
        {
            lock (_lock)
            {
                return _preference;
            }
        }

        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (_lock)
            {
                if (string.Equals(_preference, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _preference = normalized;
            }

            Refresh();
        }
    }

    /// <summary>Modules that offer a layout store (for the choice in the settings).</summary>
    public IReadOnlyList<ModuleEntry> Providers =>
        [.. _registry.Modules.Where(m => m.Module is ILayoutStoreProvider)];

    /// <summary>Determine the provider again; raises <see cref="Changed"/> if the store changes.</summary>
    public void Refresh()
    {
        GuardedLayoutStore? next = null;
        var preference = Preference;
        foreach (var (module, provider) in _registry.GetCapabilities<ILayoutStoreProvider>())
        {
            if (preference == LocalOnly)
            {
                break;
            }

            if (preference is not null && !string.Equals(module.Id, preference, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stores = _registry.Invoke(module.Id, "Query layout stores", _ => provider.LayoutStores, reportFaults: false);
            if (stores is { Succeeded: true, Value: [var store, ..] })
            {
                next = new GuardedLayoutStore(_registry, module.Id, store, _log);
                break;
            }
        }

        GuardedLayoutStore? old;
        lock (_lock)
        {
            if (_disposed || SameStore(_current, next))
            {
                return;
            }

            old = _current;
            _current = next;
        }

        old?.Detach();
        next?.Attach(() => Changed?.Invoke(this, EventArgs.Empty));
        _log.Info(null, next is null
            ? "Layout store: local only"
            : $"Layout store: {next.StoreDisplayName} (module {next.ModuleId}), local copy as cache");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _registry.ModuleStatusChanged -= OnModuleStatusChanged;
        GuardedLayoutStore? old;
        lock (_lock)
        {
            _disposed = true;
            old = _current;
            _current = null;
        }

        old?.Detach();
    }

    private static bool SameStore(GuardedLayoutStore? a, GuardedLayoutStore? b) =>
        (a is null && b is null) || (a is not null && b is not null && ReferenceEquals(a.Inner, b.Inner));

    private void OnModuleStatusChanged(object? sender, ModuleEntry entry)
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            _log.Error(null, "Layout store could not be determined again", ex);
        }
    }
}

/// <summary>
/// A layout store from a module, called through the guard. Errors and timeouts
/// become <see cref="LayoutStoreUnavailableException"/> – the core then keeps working locally.
/// </summary>
internal sealed class GuardedLayoutStore(ModuleRegistry registry, string moduleId, ILayoutStore inner, IUeLogger log) : ILayoutStore
{
    /// <summary>Generous: loading may briefly wait for the connection at startup.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private EventHandler? _handler;

    public ILayoutStore Inner => inner;

    public string ModuleId => moduleId;

    public string StoreId => registry.Invoke(moduleId, "StoreId", _ => inner.StoreId, reportFaults: false).Value ?? moduleId;

    public string StoreDisplayName => registry.Invoke(moduleId, "StoreDisplayName", _ => inner.StoreDisplayName, reportFaults: false).Value ?? moduleId;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public async Task<LayoutDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        var result = await registry.InvokeAsync(moduleId, "Load layout", (_, ct) => inner.LoadAsync(ct), CallTimeout, reportFaults: false, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.Value : throw Unavailable("Load", result.Outcome, result.Exception);
    }

    public async Task SaveAsync(LayoutDocument layout, CancellationToken cancellationToken)
    {
        var copy = LayoutLayers.Clone(layout); // the module gets its own copy
        var result = await registry.InvokeAsync(
            moduleId,
            "Save layout",
            async (_, ct) =>
            {
                await inner.SaveAsync(copy, ct).ConfigureAwait(false);
                return true;
            },
            CallTimeout,
            reportFaults: false,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Unavailable("Save", result.Outcome, result.Exception);
        }
    }

    internal void Attach(Action onChanged)
    {
        _handler = (_, _) =>
        {
            try
            {
                onChanged();
            }
            catch (Exception ex)
            {
                log.Error(null, "Error in layout Changed handler", ex);
            }
        };
        registry.Invoke(moduleId, "Subscribe to layout changes", _ =>
        {
            inner.Changed += _handler;
            return true;
        }, reportFaults: false);
    }

    internal void Detach()
    {
        if (_handler is not { } handler)
        {
            return;
        }

        registry.Invoke(moduleId, "Unsubscribe from layout changes", _ =>
        {
            inner.Changed -= handler;
            return true;
        }, reportFaults: false);
        _handler = null;
    }

    private LayoutStoreUnavailableException Unavailable(string what, GuardOutcome outcome, Exception? exception) => exception switch
    {
        LayoutStoreUnavailableException unavailable => unavailable,
        OperationCanceledException => new LayoutStoreUnavailableException($"{what} cancelled"),
        _ => new LayoutStoreUnavailableException($"{what} in module {moduleId} failed ({outcome}): {exception?.Message}", exception),
    };
}

/// <summary>Where the core gets the remote layout store from (tests plug in their own sources).</summary>
public interface IRemoteLayoutSource
{
    /// <summary>Current remote store or null (local only).</summary>
    ILayoutStore? Current { get; }

    /// <summary>Store switched or it reports a change/reconnect – reconcile needed.</summary>
    event EventHandler? Changed;
}
