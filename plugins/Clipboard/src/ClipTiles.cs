// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using static UeBar.Modules.Clipboard.ClipText;

namespace UeBar.Modules.Clipboard;

/// <summary>
/// Tile "Zwischenablage": the latest entries – tapping one copies it, "All" opens the full history, "Pause" pauses
/// recording (the hint shows it). 1x1 = tap opens the history; 2x1 = 3 entries + All + Pause; 2x2 = 5 entries + All
/// and a pause button. Images show their preview picture.
/// </summary>
internal sealed class HistoryTileType(ClipboardModule module) : ITileType
{
    public const string TypeId = "clipboard.history";
    private const string AllId = "all";
    private const string PauseId = "pause";

    public string Id => TypeId;

    public string DisplayName => T["Clip_TileName"];

    public string Icon => ClipboardModule.ClipIcon;

    public IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide, TileSize.Large];

    public TileSize DefaultSize => TileSize.Wide;

    public IReadOnlyList<TileSlot> Slots => [];

    public TileModel CreateTile(TileConfig config)
    {
        var tile = new TileModel { Title = DisplayName, Icon = Icon };
        if (config.Size == TileSize.Small)
        {
            tile.Elements.Add(new TapAction { Id = AllId, Label = T["Clip_CommandOpen"] });
        }
        else
        {
            tile.Elements.Add(new IconGridElement { Id = "entries", Columns = config.Size == TileSize.Large ? 3 : 5 });
            if (config.Size == TileSize.Large)
            {
                tile.Elements.Add(new ButtonRowElement { Id = "controls", Buttons = [new TileButton { Id = PauseId }] });
            }
        }

        tile.Handler = new DelegateInteractionHandler(async (_, interaction, ct) =>
        {
            var id = interaction.Kind == InteractionKind.Press ? interaction.Text : AllId;
            switch (id)
            {
                case AllId or null:
                    module.OpenPicker("tile");
                    break;
                case PauseId:
                    module.SetPaused(!module.IsPaused, "tile");
                    break;
                default:
                    await module.CopyEntryAsync(id, ct).ConfigureAwait(false);
                    break;
            }
        });
        module.Track(tile, config);
        return tile;
    }

    /// <summary>Brings a tile up to date (any thread; lists are replaced in one step).</summary>
    public static void Refresh(ClipboardModule module, TileModel tile, TileSize? size)
    {
        tile.Title = T["Clip_TileName"];
        tile.StateText = module.IsPaused ? T["Clip_PausedShort"] : module.CountText();
        tile.IsActive = false;
        if (tile.FirstElement<ButtonRowElement>() is { } controls && controls.Buttons.FirstOrDefault() is { } pause)
        {
            pause.Icon = module.IsPaused ? "U+E768" : "U+E769";
            pause.Label = module.IsPaused ? T["Clip_TileResume"] : T["Clip_TilePause"];
            pause.IsActive = module.IsPaused;
        }

        if (tile.FirstElement<IconGridElement>() is not { } grid)
        {
            return;
        }

        var large = size == TileSize.Large || grid.Columns == 3;
        var entries = (module.Store?.Entries ?? []).Take(large ? 5 : 3).ToList();
        var now = module.Now;
        List<IconGridItem> wanted =
        [
            .. entries.Select(e => new IconGridItem
            {
                Id = e.Id,
                Label = e.Kind == ClipKind.Image ? T["Clip_ImageShort"] : ClipStore.Fold(ClipboardModule.Title(e), 18),
                Icon = ClipboardModule.KindIcon(e.Kind),
                ImageUrl = e.HasThumbnail ? ClipboardModule.ThumbPrefix + e.Id : null,
                StateText = $"{ClipStore.Fold(ClipboardModule.Title(e), 120)}\n{module.Detail(e, now)}",
            }),
            new IconGridItem { Id = AllId, Label = T["Clip_TileAll"], Icon = "U+E8FD", StateText = T["Clip_CommandOpen"] },
        ];
        if (!large)
        {
            wanted.Add(new IconGridItem
            {
                Id = PauseId,
                Label = module.IsPaused ? T["Clip_TileResume"] : T["Clip_TilePause"],
                Icon = module.IsPaused ? "U+E768" : "U+E769",
                StateText = T["Clip_TrayPause"],
            });
        }

        if (grid.Items.Select(x => (x.Id, x.Label, x.ImageUrl, x.Icon, x.StateText)).SequenceEqual(wanted.Select(x => (x.Id, x.Label, x.ImageUrl, x.Icon, x.StateText))))
        {
            return;
        }

        grid.Items = [.. wanted];
    }
}
