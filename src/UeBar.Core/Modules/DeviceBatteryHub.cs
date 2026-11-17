// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>
/// Collects device batteries of all running modules with <see cref="IDeviceBatterySource"/> (P2-2)
/// and offers them to the other modules through their context (<see cref="IDeviceBatteryCatalog"/>).
/// Every call into a module goes through the guard.
/// </summary>
internal sealed class DeviceBatteryHub
{
    private readonly ModuleRegistry _registry;
    private readonly HashSet<IModule> _subscribed = [];
    private readonly Lock _lock = new();

    public DeviceBatteryHub(ModuleRegistry registry)
    {
        _registry = registry;
        _registry.ModuleStatusChanged += (_, entry) =>
        {
            if (entry.Module is IDeviceBatterySource)
            {
                Attach(entry);
                Changed?.Invoke(this, entry.Id);
            }
        };
    }

    /// <summary>Batteries changed; argument = the module that reported them.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Batteries of all available sources except <paramref name="exceptModuleId"/>.</summary>
    public IReadOnlyList<DeviceBatteryInfo> Get(string? exceptModuleId)
    {
        var result = new List<DeviceBatteryInfo>();
        foreach (var entry in _registry.Modules.Where(m => m.Module is IDeviceBatterySource))
        {
            if (!entry.IsAvailable || string.Equals(entry.Id, exceptModuleId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Attach(entry);
            var id = entry.Id;
            var batteries = _registry.Invoke(id, "Device batteries", m => ((IDeviceBatterySource)m).DeviceBatteries);
            if (batteries is { Succeeded: true, Value: { } list })
            {
                result.AddRange(list.Where(b => b is not null).Select(b => b with { Source = id }));
            }
        }

        return result;
    }

    private void Attach(ModuleEntry entry)
    {
        lock (_lock)
        {
            if (!_subscribed.Add(entry.Module))
            {
                return;
            }
        }

        var id = entry.Id;
        _registry.Invoke(id, "Subscribe to device batteries", m =>
        {
            ((IDeviceBatterySource)m).DeviceBatteriesChanged += (_, _) => Changed?.Invoke(this, id);
            return true;
        });
    }
}
