// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;
using static UeBar.Modules.Screenshot.ShotText;

namespace UeBar.Modules.Screenshot;

/// <summary>
/// Tile "Screenshot": tap = capture in the set mode. 2x1 also has buttons for area, window and screen.
/// The hint shows the hotkey.
/// </summary>
internal sealed class CaptureTileType(ScreenshotModule module) : ITileType
{
    public const string TypeId = "screenshot.capture";

    public string Id => TypeId;

    public string DisplayName => T["Shot_TileName"];

    public string Icon => ScreenshotModule.ShotIcon;

    public IReadOnlyList<TileSize> AllowedSizes => [TileSize.Small, TileSize.Wide];

    public TileSize DefaultSize => TileSize.Small;

    public IReadOnlyList<TileSlot> Slots => [];

    public TileModel CreateTile(TileConfig config)
    {
        var tile = new TileModel { Title = DisplayName, Icon = Icon };
        tile.Elements.Add(new TapAction { Id = "capture", Label = T["Shot_Capture"] });
        if (config.Size == TileSize.Wide)
        {
            tile.Elements.Add(new ButtonRowElement
            {
                Id = "modes",
                Buttons =
                [
                    new TileButton { Id = "region", Icon = "U+F407", Label = T["Shot_ModeRegionShort"] },
                    new TileButton { Id = "window", Icon = "U+E737", Label = T["Shot_ModeWindowShort"] },
                    new TileButton { Id = "screen", Icon = "U+E7F4", Label = T["Shot_ModeScreenShort"] },
                ],
            });
        }

        tile.Handler = new DelegateInteractionHandler((_, interaction, _) =>
        {
            var mode = interaction.Kind == InteractionKind.Press ? ShotOptions.ParseMode(interaction.Text) : null;
            module.StartCapture(mode, null, "tile");
            return Task.CompletedTask;
        });
        module.Track(tile);
        return tile;
    }
}
