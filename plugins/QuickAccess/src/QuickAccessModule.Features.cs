// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using static UeBar.Modules.QuickAccess.QuickText;

namespace UeBar.Modules.QuickAccess;

public sealed partial class QuickAccessModule
{
    // ------------------------------------------------------------------ search (edit mode and quick search)

    /// <summary>
    /// Entries of the settings and apps of the Start menu whose name contains the query (names starting
    /// with it first), plus the collection tile in edit mode. Empty query: the entries only.
    /// </summary>
    public async Task<IReadOnlyList<EntityInfo>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        query = query.Trim();
        var results = new List<(int Rank, EntityInfo Info)>();
        foreach (var entry in Entries)
        {
            if (Rank(entry.Name, query) is { } rank)
            {
                results.Add((rank, new EntityInfo("entry:" + entry.Id, entry.Name, QuickNames.Icon(entry.Kind), StateText: QuickNames.KindText(entry.Kind), Category: T["Qa_Entries"])));
            }
        }

        if (GroupMatches(query))
        {
            results.Add((query.Length == 0 ? 3 : 1, new EntityInfo(GroupEntity, T["Qa_Group"], QuickNames.GroupIcon, StateText: T["Qa_Group_Hint"], Category: T["Qa_ModuleName"])));
        }

        if (query.Length > 0)
        {
            var apps = await AppsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var app in apps)
            {
                if (Rank(app.Name, query) is { } rank)
                {
                    results.Add((rank + 1, new EntityInfo(new QuickTarget(TargetKind.App, app.Id).ToString(), app.Name, QuickNames.AppIcon, StateText: T["Qa_KindApp"], Category: T["Qa_Apps"])));
                }
            }
        }

        return [.. results.OrderBy(r => r.Rank).ThenBy(r => r.Info.Name, StringComparer.CurrentCultureIgnoreCase).Take(maxResults).Select(r => r.Info)];
    }

    /// <summary>0 = the name starts with the query, 2 = a word starts with it, 4 = somewhere inside; null = no match.</summary>
    private static int? Rank(string name, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var at = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        return at switch
        {
            < 0 => null,
            0 => 0,
            _ when !char.IsLetterOrDigit(name[at - 1]) => 2,
            _ => 4,
        };
    }

    private static bool GroupMatches(string query) =>
        query.Length == 0
        || BothLanguages("Qa_Group").Concat(BothLanguages("Qa_GroupWords").SelectMany(w => w.Split(',', StringSplitOptions.TrimEntries)))
            .Any(word => word.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) || query.StartsWith(word, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Enter in the quick search: start it.</summary>
    public Task ExecuteDefaultActionAsync(string entityId, CancellationToken cancellationToken)
    {
        if (TargetOf(entityId) is { } target && Launch(target) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        return Task.CompletedTask;
    }

    /// <summary>A search hit as a tile target: entries become their target (commands stay a reference).</summary>
    internal QuickTarget? TargetOf(string entityId)
    {
        if (entityId.StartsWith("entry:", StringComparison.Ordinal))
        {
            return FindEntry(entityId[6..]) is { } entry ? TileTargetOf(entry) : null;
        }

        return QuickTarget.Parse(entityId);
    }

    public TileConfig? SuggestTile(EntityInfo entity)
    {
        if (entity.Id == GroupEntity)
        {
            return new TileConfig { Module = ModuleId, Type = GroupTileType.TypeId, Size = TileSize.Large };
        }

        if (TargetOf(entity.Id) is not { } target)
        {
            return null;
        }

        var fromEntry = entity.Id.StartsWith("entry:", StringComparison.Ordinal);
        if (target.Kind == TargetKind.App)
        {
            EnsureAppsLoading();
        }

        return new TileConfig
        {
            Module = ModuleId,
            Type = ItemTileType.TypeId,
            Size = TileSize.Small,
            Entities = new Dictionary<string, string> { ["main"] = target.ToString() },
            Label = fromEntry ? entity.Name : null,
        };
    }

    // ------------------------------------------------------------------ slots of the collection tile

    public async Task<IReadOnlyList<EntityInfo>> SuggestForSlotAsync(TileConfig draft, string slot, string query, int maxResults, CancellationToken cancellationToken)
    {
        // Candidates carry the target itself, so the collection tile works on every PC of the layout.
        var hits = await SearchAsync(query, maxResults + 1, cancellationToken).ConfigureAwait(false);
        return [.. hits
            .Where(h => h.Id != GroupEntity)
            .Select(h => TargetOf(h.Id) is { } target ? h with { Id = target.ToString() } : null)
            .OfType<EntityInfo>()
            .Take(maxResults)];
    }

    public TileConfig CompleteSlots(TileConfig draft) => draft;

    // ------------------------------------------------------------------ drag and drop (edit mode)

    public Task<TileConfig?> CreateTileForDropAsync(DroppedItem item, CancellationToken cancellationToken)
    {
        var (target, label) = FromDropped(item);
        if (target is null)
        {
            return Task.FromResult<TileConfig?>(null);
        }

        Log?.Info($"Dropped {item.Kind} → {target.Kind} tile");
        return Task.FromResult<TileConfig?>(new TileConfig
        {
            Module = ModuleId,
            Type = ItemTileType.TypeId,
            Size = TileSize.Small,
            Entities = new Dictionary<string, string> { ["main"] = target.ToString() },
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
        });
    }

    /// <summary>
    /// Something dropped, chosen or typed → the target (and a name if it brings one). A typed web page
    /// without "https://" gets it ("example.org" → "https://example.org").
    /// </summary>
    internal (QuickTarget? Target, string? Label) FromDropped(DroppedItem item)
    {
        switch (item.Kind)
        {
            case DropKind.Folder:
                return (new QuickTarget(TargetKind.Folder, QuickPaths.Collapse(item.Value, UserProfile)), null);

            case DropKind.Link:
            {
                var url = item.Value.Trim();
                if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                return QuickTarget.IsAllowedUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeMailto || uri.Host.Contains('.'))
                    ? (new QuickTarget(TargetKind.Url, url), item.Title)
                    : (null, null);
            }

            case DropKind.File:
                return FromDroppedFile(item.Value);

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// A dropped file: an Internet shortcut (.url) becomes the web page, a shortcut (.lnk) to a folder the
    /// folder; any other shortcut stays the shortcut (it keeps its arguments and icon), named like it.
    /// </summary>
    private (QuickTarget? Target, string? Label) FromDroppedFile(string path)
    {
        var extension = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
        {
            return _shell.ReadUrlShortcut(path) is { } url && QuickTarget.IsAllowedUrl(url)
                ? (new QuickTarget(TargetKind.Url, url), name)
                : (null, null);
        }

        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
            && _shell.ReadShortcut(path) is { TargetPath: { } linked, Arguments: null } && _shell.FolderExists(linked))
        {
            return (new QuickTarget(TargetKind.Folder, QuickPaths.Collapse(linked, UserProfile)), name);
        }

        var label = string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ? name : null;
        return (new QuickTarget(TargetKind.File, QuickPaths.Collapse(path, UserProfile)), label);
    }
}
