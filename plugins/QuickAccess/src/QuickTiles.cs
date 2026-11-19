// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Globalization;
using UeBar.Sdk;
using static UeBar.Modules.QuickAccess.QuickText;

namespace UeBar.Modules.QuickAccess;

/// <summary>Shared base: tiles are built from SDK building blocks and refreshed from the targets.</summary>
internal abstract class QuickTileType(QuickAccessModule module) : ITileType
{
    protected QuickAccessModule Module { get; } = module;

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract string Icon { get; }

    public abstract IReadOnlyList<TileSize> AllowedSizes { get; }

    public abstract TileSize DefaultSize { get; }

    public abstract IReadOnlyList<TileSlot> Slots { get; }

    public TileModel CreateTile(TileConfig config)
    {
        var tile = new TileModel { Title = DisplayName, Icon = Icon };
        Build(tile);
        tile.Handler = new DelegateInteractionHandler((t, i, _) => HandleAsync(t, config, i));
        lock (Module.ViewLock)
        {
            // Tracked first, so no change is missed – the lock keeps a refresh round off it meanwhile.
            Module.Track(tile, this, config);
            Refresh(tile, config);
        }

        return tile;
    }

    protected abstract void Build(TileModel tile);

    public abstract void Refresh(TileModel tile, TileConfig config);

    protected abstract Task HandleAsync(TileModel tile, TileConfig config, TileInteraction interaction);

    /// <summary>Start a target from a tile; a problem shows as the tile's short hint.</summary>
    protected Task OpenAsync(TileModel tile, QuickTarget? target)
    {
        var error = target is null ? T["Qa_NotFound"] : Module.Launch(target);
        tile.Alert = error;
        return Task.CompletedTask;
    }

    protected static string Count(int n) => n == 1 ? T["Qa_OneEntry"] : T.Format("Qa_Entries_Count", n.ToString(CultureInfo.CurrentCulture));
}

/// <summary>One app, file, folder, web page or entry: tap = open. 1x1 icon + name, 2x1/2x2 larger.</summary>
internal sealed class ItemTileType(QuickAccessModule module) : QuickTileType(module)
{
    public const string TypeId = "quickaccess.item";

    public override string Id => TypeId;

    public override string DisplayName => T["Qa_TileItem"];

    public override string Icon => QuickNames.AppIcon;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide, TileSize.Large];

    public override TileSize DefaultSize => TileSize.Small;

    public override IReadOnlyList<TileSlot> Slots => [new TileSlot("main", T["Qa_SlotTarget"])];

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new TapAction { Id = "open", Label = T["Qa_Open"] });
    }

    public override void Refresh(TileModel tile, TileConfig config)
    {
        var target = QuickTarget.Parse(config.GetEntity("main"));
        if (target?.Kind == TargetKind.App)
        {
            Module.EnsureAppsLoading();
        }

        var resolved = target is null ? null : Module.Resolve(target);
        if (string.IsNullOrWhiteSpace(config.Label))
        {
            tile.Title = resolved?.Name ?? T["Qa_TileItem"];
        }

        if (string.IsNullOrWhiteSpace(config.Icon))
        {
            tile.Icon = resolved is null ? QuickNames.AppIcon : QuickNames.Icon(resolved.Kind);
        }

        var exists = resolved?.Exists == true;
        tile.ImageUrl = exists && resolved!.IconSource is not null && string.IsNullOrWhiteSpace(config.Icon) ? QuickAccessModule.IconId(target!) : null;
        tile.IsUnavailable = !exists;
        tile.StateText = !exists ? T["Qa_NotFound"]
            : resolved!.Kind == EntryKind.File && target!.IsTemplate ? T["Qa_Template"]
            : resolved.Admin ? T["Qa_AsAdmin"]
            : QuickNames.KindText(resolved.Kind);
        tile.Alert = null;
    }

    protected override Task HandleAsync(TileModel tile, TileConfig config, TileInteraction interaction) =>
        interaction.Kind == InteractionKind.Tap ? OpenAsync(tile, QuickTarget.Parse(config.GetEntity("main"))) : Task.CompletedTask;
}

/// <summary>Collection (2x2): up to six entries in one tile, e.g. "School" with the folders of the subjects.</summary>
internal sealed class GroupTileType(QuickAccessModule module) : QuickTileType(module)
{
    public const string TypeId = "quickaccess.group";
    public const int MaxItems = 6;

    public override string Id => TypeId;

    public override string DisplayName => T["Qa_Group"];

    public override string Icon => QuickNames.GroupIcon;

    public override IReadOnlyList<TileSize> AllowedSizes => [TileSize.Large];

    public override TileSize DefaultSize => TileSize.Large;

    /// <summary>No slots to choose when adding: the entries are edited in the bar (<see cref="ITileItemEditor"/>, P2-4-Fix 1).</summary>
    public override IReadOnlyList<TileSlot> Slots => [];

    protected override void Build(TileModel tile)
    {
        tile.Elements.Add(new IconGridElement { Id = "items", Columns = 3 });
    }

    public override void Refresh(TileModel tile, TileConfig config)
    {
        var wanted = new List<IconGridItem>();
        var items = CollectionItems.Read(config);
        for (var i = 0; i < items.Count; i++)
        {
            if (QuickTarget.Parse(items[i].Target) is not { } target)
            {
                continue;
            }

            if (target.Kind == TargetKind.App)
            {
                Module.EnsureAppsLoading();
            }

            var resolved = Module.Resolve(target);
            var exists = resolved?.Exists == true;
            wanted.Add(new IconGridItem
            {
                Id = CollectionItems.Slot(i),
                Label = items[i].Name ?? resolved?.Name ?? T["Qa_NotFound"],
                Icon = resolved is null ? QuickNames.AppIcon : QuickNames.Icon(resolved.Kind),
                ImageUrl = exists && resolved!.IconSource is not null ? QuickAccessModule.IconId(target) : null,
                StateText = exists ? resolved!.Name : T["Qa_NotFound"],
                IsEnabled = exists,
            });
        }

        if (string.IsNullOrWhiteSpace(config.Label))
        {
            tile.Title = T["Qa_Group"];
        }

        if (string.IsNullOrWhiteSpace(config.Icon))
        {
            tile.Icon = QuickNames.GroupIcon;
        }

        tile.StateText = wanted.Count == 0 ? T["Qa_GroupEmpty"] : Count(wanted.Count);
        tile.Alert = null;
        if (tile.FirstElement<IconGridElement>() is { } grid)
        {
            if (grid.Items.Select(x => (x.Id, x.Label, x.ImageUrl, x.Icon, x.IsEnabled, x.StateText))
                .SequenceEqual(wanted.Select(x => (x.Id, x.Label, x.ImageUrl, x.Icon, x.IsEnabled, x.StateText))))
            {
                return;
            }

            grid.Items = [.. wanted]; // a new list in one step – the app shows a consistent state
        }
    }

    protected override Task HandleAsync(TileModel tile, TileConfig config, TileInteraction interaction)
    {
        if (interaction.Kind != InteractionKind.Press || interaction.Text is not { } slot)
        {
            return Task.CompletedTask;
        }

        // Entries are counted without gaps (an old layout may have item5 without item4).
        var items = CollectionItems.Read(config);
        var index = Enumerable.Range(0, items.Count).FirstOrDefault(i => CollectionItems.Slot(i) == slot, -1);
        return OpenAsync(tile, index < 0 ? null : QuickTarget.Parse(items[index].Target));
    }
}
