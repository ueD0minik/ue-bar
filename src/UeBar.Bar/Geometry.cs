// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar;

/// <summary>Point in physical screen pixels (like GetCursorPos).</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>Rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public PixelRect Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + (2 * dx), Height + (2 * dy));

    public PixelRect Union(PixelRect other) =>
        FromEdges(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
}

/// <summary>
/// A monitor. <see cref="Id"/> is stable across re-plugging (device path), <see cref="Name"/> is
/// the display name, <see cref="Scale"/> the DPI scaling (1.0 = 100 %).
/// </summary>
public sealed record MonitorInfo(
    string Id,
    string Name,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary,
    double Scale);
