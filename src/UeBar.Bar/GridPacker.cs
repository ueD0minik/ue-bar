// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Bar;

public readonly record struct GridPlacement(int Column, int Row, int ColumnSpan, int RowSpan);

/// <summary>
/// Packs tiles densely into the grid (like CSS grid "dense"): each tile at the first free spot,
/// read from the top left. Tiles that are too wide are cut to the number of columns.
/// </summary>
public static class GridPacker
{
    public static IReadOnlyList<GridPlacement> Pack(IReadOnlyList<TileSize> sizes, int columns, out int rows)
    {
        columns = Math.Max(1, columns);
        var occupied = new List<bool[]>();
        var result = new List<GridPlacement>(sizes.Count);

        foreach (var size in sizes)
        {
            var w = Math.Clamp(size.Columns, 1, columns);
            var h = Math.Max(1, size.Rows);
            for (var row = 0; ; row++)
            {
                var placed = false;
                for (var col = 0; col <= columns - w; col++)
                {
                    if (Fits(occupied, col, row, w, h, columns))
                    {
                        Mark(occupied, col, row, w, h, columns);
                        result.Add(new GridPlacement(col, row, w, h));
                        placed = true;
                        break;
                    }
                }

                if (placed)
                {
                    break;
                }
            }
        }

        rows = occupied.Count;
        return result;
    }

    private static bool Fits(List<bool[]> grid, int col, int row, int w, int h, int columns)
    {
        for (var r = row; r < row + h; r++)
        {
            if (r >= grid.Count)
            {
                continue;
            }

            for (var c = col; c < col + w; c++)
            {
                if (grid[r][c])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Mark(List<bool[]> grid, int col, int row, int w, int h, int columns)
    {
        while (grid.Count < row + h)
        {
            grid.Add(new bool[columns]);
        }

        for (var r = row; r < row + h; r++)
        {
            for (var c = col; c < col + w; c++)
            {
                grid[r][c] = true;
            }
        }
    }
}
