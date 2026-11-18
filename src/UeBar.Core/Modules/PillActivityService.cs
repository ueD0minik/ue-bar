// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Logging;
using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>The activity the pill shows (P2-3): of all running <see cref="IPillActivitySource"/> the one due first.</summary>
public sealed record PillActivityEntry(string ModuleId, PillActivity Activity);

/// <summary>
/// Collects the pill activities of all modules (like <see cref="NowPlayingService"/>). <see cref="Changed"/>
/// comes from any thread.
/// </summary>
public sealed class PillActivityService
{
    private readonly ModuleRegistry _registry;
    private readonly HashSet<IModule> _subscribed = [];
    private readonly Lock _lock = new();
    private PillActivityEntry? _current;

    public PillActivityService(ModuleRegistry registry, IUeLogger log)
    {
        _registry = registry;
        _ = log;
        _registry.ModuleStatusChanged += (_, _) => Refresh();
        Refresh();
    }

    public event EventHandler? Changed;

    public PillActivityEntry? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Due first; without a due time after those with one (public for tests).</summary>
    public static PillActivityEntry? Choose(IEnumerable<PillActivityEntry> candidates) =>
        candidates.OrderBy(c => c.Activity.Due ?? DateTimeOffset.MaxValue).FirstOrDefault();

    public void Refresh()
    {
        var candidates = new List<PillActivityEntry>();
        foreach (var entry in _registry.Modules.Where(m => m.Module is IPillActivitySource))
        {
            Subscribe(entry);
            if (entry.IsAvailable
                && _registry.Invoke(entry.Id, "Pill activity", m => ((IPillActivitySource)m).CurrentPillActivity) is { Succeeded: true, Value: { } activity })
            {
                candidates.Add(new PillActivityEntry(entry.Id, activity));
            }
        }

        var best = Choose(candidates);
        bool changed;
        lock (_lock)
        {
            changed = !Equals(_current, best);
            _current = best;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Subscribe(ModuleEntry entry)
    {
        lock (_lock)
        {
            if (!_subscribed.Add(entry.Module))
            {
                return;
            }
        }

        _registry.Invoke(entry.Id, "Subscribe to pill activity", m =>
        {
            ((IPillActivitySource)m).PillActivityChanged += (_, _) => Refresh();
            return true;
        });
    }
}
