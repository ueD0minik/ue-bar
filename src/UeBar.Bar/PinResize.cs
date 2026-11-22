// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>Which edges of a pin the mouse holds while resizing (combined for corners).</summary>
[Flags]
public enum PinEdge
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>
/// Resizing a pin with the mouse (P2-5-Fix 2): the app does it itself instead of relying on Windows' sizing
/// of a borderless window – that did not work for text pins. Which edge is under the mouse, and the new
/// rectangle for a mouse movement, with a minimum size. Physical pixels; WPF-free so it can be tested.
/// </summary>
public static class PinResize
{
    /// <summary>Grab zone at the edges (device-independent pixels).</summary>
    public const double Border = 8;

    /// <summary>Smallest text pin (device-independent pixels): room for a few words.</summary>
    public const double TextMinWidth = 120;

    public const double TextMinHeight = 56;

    /// <summary>The edge(s) at a point of the window; corners are a bit larger to be easy to hit.</summary>
    public static PinEdge EdgeAt(PixelRect window, PixelPoint point, int border)
    {
        if (!window.Contains(point) || border <= 0)
        {
            return PinEdge.None;
        }

        var corner = border * 2;
        var nearLeft = point.X < window.X + border;
        var nearRight = point.X >= window.Right - border;
        var nearTop = point.Y < window.Y + border;
        var nearBottom = point.Y >= window.Bottom - border;
        var inLeftCorner = point.X < window.X + corner;
        var inRightCorner = point.X >= window.Right - corner;
        var inTopCorner = point.Y < window.Y + corner;
        var inBottomCorner = point.Y >= window.Bottom - corner;

        var edge = PinEdge.None;
        if (nearLeft || (inLeftCorner && (nearTop || nearBottom)))
        {
            edge |= PinEdge.Left;
        }
        else if (nearRight || (inRightCorner && (nearTop || nearBottom)))
        {
            edge |= PinEdge.Right;
        }

        if (nearTop || (inTopCorner && (nearLeft || nearRight)))
        {
            edge |= PinEdge.Top;
        }
        else if (nearBottom || (inBottomCorner && (nearLeft || nearRight)))
        {
            edge |= PinEdge.Bottom;
        }

        return edge;
    }

    /// <summary>
    /// The rectangle after moving the held edge(s) by (dx, dy). The opposite edge stays where it is; the size
    /// never goes below the minimum (the moving edge stops there).
    /// </summary>
    public static PixelRect Resize(PixelRect start, PinEdge edge, int dx, int dy, int minWidth, int minHeight)
    {
        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;
        if (edge.HasFlag(PinEdge.Left))
        {
            left = Math.Min(left + dx, right - minWidth);
        }
        else if (edge.HasFlag(PinEdge.Right))
        {
            right = Math.Max(right + dx, left + minWidth);
        }

        if (edge.HasFlag(PinEdge.Top))
        {
            top = Math.Min(top + dy, bottom - minHeight);
        }
        else if (edge.HasFlag(PinEdge.Bottom))
        {
            bottom = Math.Max(bottom + dy, top + minHeight);
        }

        return PixelRect.FromEdges(left, top, right, bottom);
    }
}
