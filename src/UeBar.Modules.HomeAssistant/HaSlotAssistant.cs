// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Modules.HomeAssistant;

/// <summary>
/// Suggestions for the slots of a tile in edit mode (M5a): matching domain from the
/// slot filter, then sorted by closeness – same device, common start of the entity ID
/// (e.g. <c>office_ac_…</c>), same area. Only what is unambiguous is pre-filled.
/// </summary>
internal sealed class HaSlotAssistant(EntityIndex index, EntityStore store, Func<string, ITileType?> findType)
{
    private const int MinCommonPrefix = 8;

    public IReadOnlyList<EntityInfo> Suggest(TileConfig draft, string slot, string query, int maxResults)
    {
        var domains = Domains(draft.Type, slot);
        var anchors = Anchors(draft, slot);
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return
        [
            .. index.All
                .Where(e => domains is null || domains.Contains(e.Domain))
                .Where(e => words.All(w =>
                    e.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                    || e.EntityId.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || (e.Area?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)))
                .Select(e => (Entry: e, Rank: Rank(e, anchors)))
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(Math.Max(1, maxResults))
                .Select(x => ToInfo(x.Entry)),
        ];
    }

    public TileConfig Complete(TileConfig draft)
    {
        var result = UeBarJson.Clone(draft);
        if (findType(draft.Type) is not { } type)
        {
            return result;
        }

        // 1. Move set things that do not fit their slot into a matching free slot.
        foreach (var (slotName, entityId) in result.Entities.ToList())
        {
            var domains = Domains(type, slotName);
            var domain = entityId.Split('.', 2)[0];
            if (domains is null || domains.Contains(domain))
            {
                continue;
            }

            var target = type.Slots.FirstOrDefault(s => !result.Entities.ContainsKey(s.Name) && (Domains(type, s.Name)?.Contains(domain) ?? true));
            if (target is not null)
            {
                result.Entities.Remove(slotName);
                result.Entities[target.Name] = entityId;
            }
        }

        // 2. Empty slots: same device, otherwise unambiguous in the same area or with the same name start.
        foreach (var slot in type.Slots)
        {
            if (result.Entities.ContainsKey(slot.Name) || result.Entities.Count == 0)
            {
                continue;
            }

            var domains = Domains(type, slot.Name);
            var anchors = Anchors(result, slot.Name);
            var taken = new HashSet<string>(result.Entities.Values, StringComparer.OrdinalIgnoreCase);
            var candidates = index.All
                .Where(e => (domains is null || domains.Contains(e.Domain)) && !taken.Contains(e.EntityId))
                .Select(e => (Entry: e, Rank: Rank(e, anchors)))
                .Where(x => x.Rank < 3)
                .ToList();
            var best = candidates.Count == 0 ? (int?)null : candidates.Min(x => x.Rank);
            var top = candidates.Where(x => x.Rank == best).ToList();
            if (top.Count == 1 || (best == 0 && top.Count > 0))
            {
                result.Entities[slot.Name] = top.OrderBy(x => x.Entry.EntityId, StringComparer.Ordinal).First().Entry.EntityId;
            }
        }

        return result;
    }

    private HashSet<string>? Domains(string typeId, string slot) => findType(typeId) is { } type ? Domains(type, slot) : null;

    private static HashSet<string>? Domains(ITileType type, string slot) =>
        type.Slots.FirstOrDefault(s => s.Name == slot)?.Filter is { Length: > 0 } filter
            ? new HashSet<string>(filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            : null;

    private List<HaEntityEntry> Anchors(TileConfig draft, string exceptSlot) =>
        [.. draft.Entities.Where(e => e.Key != exceptSlot).Select(e => index.Get(e.Value)).OfType<HaEntityEntry>()];

    /// <summary>0 = same device, 1 = same name start of the ID, 2 = same area, 3 = otherwise.</summary>
    private static int Rank(HaEntityEntry entry, List<HaEntityEntry> anchors)
    {
        var rank = 3;
        foreach (var anchor in anchors)
        {
            if (anchor.DeviceId is not null && anchor.DeviceId == entry.DeviceId)
            {
                return 0;
            }

            if (CommonPrefix(ObjectId(anchor.EntityId), ObjectId(entry.EntityId)) >= MinCommonPrefix)
            {
                rank = Math.Min(rank, 1);
            }

            if (anchor.Area is not null && anchor.Area == entry.Area)
            {
                rank = Math.Min(rank, 2);
            }
        }

        return rank;
    }

    private static string ObjectId(string entityId) => entityId.Split('.', 2) is [_, var objectId] ? objectId : entityId;

    private static int CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n])
        {
            n++;
        }

        return n;
    }

    private EntityInfo ToInfo(HaEntityEntry entry)
    {
        var state = store.Get(entry.EntityId);
        return new EntityInfo(entry.EntityId, entry.Name, HaIcons.For(entry.EntityId, state, entry), entry.Area,
            state is null ? null : StateFormatter.Format(state, entry), entry.Domain);
    }
}
