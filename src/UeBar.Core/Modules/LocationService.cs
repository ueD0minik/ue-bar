// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Abstractions;
using UeBar.Core.Logging;

namespace UeBar.Core.Modules;

/// <summary>
/// Location of the device for tab visibility (SPEC 9): the value of the first running module with
/// <see cref="ILocationProvider"/> (registration order). No provider → <see cref="DeviceLocation.Unknown"/>.
/// All calls into the module go through the fault protection. <see cref="Changed"/> comes from any thread.
/// </summary>
public sealed class LocationService
{
    private readonly ModuleRegistry _registry;
    private readonly IUeLogger _log;
    private readonly HashSet<IModule> _subscribed = [];
    private readonly Lock _lock = new();
    private DeviceLocation _current = DeviceLocation.Unknown;

    public LocationService(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _log = log;
        _registry.ModuleStatusChanged += (_, _) => Refresh();
        Refresh();
    }

    public event EventHandler<DeviceLocation>? Changed;

    public DeviceLocation Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Determine again (on module status or location change).</summary>
    public void Refresh()
    {
        var location = DeviceLocation.Unknown;
        foreach (var entry in _registry.Modules)
        {
            if (entry.Module is not ILocationProvider)
            {
                continue;
            }

            Subscribe(entry);
            if (!entry.IsAvailable)
            {
                continue;
            }

            var result = _registry.Invoke(entry.Id, "Query location", m => ((ILocationProvider)m).CurrentLocation);
            location = result.Succeeded ? result.Value : DeviceLocation.Unknown;
            break;
        }

        bool changed;
        lock (_lock)
        {
            changed = _current != location;
            _current = location;
        }

        if (changed)
        {
            _log.Info(null, $"Location: {Describe(location)}");
            Changed?.Invoke(this, location);
        }
    }

    /// <summary>For the log (English).</summary>
    public static string Describe(DeviceLocation location) => location switch
    {
        DeviceLocation.Home => "home",
        DeviceLocation.Away => "away",
        _ => "unknown",
    };

    private void Subscribe(ModuleEntry entry)
    {
        lock (_lock)
        {
            if (!_subscribed.Add(entry.Module))
            {
                return;
            }
        }

        _registry.Invoke(entry.Id, "Subscribe to location", m =>
        {
            ((ILocationProvider)m).LocationChanged += (_, _) => Refresh();
            return true;
        });
    }
}
