// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

namespace UeBar.Bar.Tests;

/// <summary>P2-5-Fix 2: every pin can be resized at all edges and corners, never below its minimum size.</summary>
public sealed class PinResizeTests
{
    private static readonly PixelRect Pin = new(100, 100, 300, 170);

    [Theory]
    [InlineData(101, 180, PinEdge.Left)]
    [InlineData(398, 180, PinEdge.Right)]
    [InlineData(250, 101, PinEdge.Top)]
    [InlineData(250, 268, PinEdge.Bottom)]
    [InlineData(101, 101, PinEdge.Left | PinEdge.Top)]
    [InlineData(398, 268, PinEdge.Right | PinEdge.Bottom)]
    [InlineData(112, 268, PinEdge.Left | PinEdge.Bottom)] // corners are a little larger along the edge
    [InlineData(398, 112, PinEdge.Right | PinEdge.Top)]
    [InlineData(250, 180, PinEdge.None)]
    [InlineData(50, 50, PinEdge.None)]
    public void The_edge_under_the_mouse(int x, int y, PinEdge expected) =>
        Assert.Equal(expected, PinResize.EdgeAt(Pin, new PixelPoint(x, y), border: 8));

    [Fact]
    public void Dragging_an_edge_moves_only_that_edge()
    {
        Assert.Equal(new PixelRect(100, 100, 350, 170), PinResize.Resize(Pin, PinEdge.Right, 50, 999, 120, 56));
        Assert.Equal(new PixelRect(60, 100, 340, 170), PinResize.Resize(Pin, PinEdge.Left, -40, 0, 120, 56));
        Assert.Equal(new PixelRect(100, 80, 300, 190), PinResize.Resize(Pin, PinEdge.Top, 0, -20, 120, 56));
        Assert.Equal(new PixelRect(100, 100, 240, 230), PinResize.Resize(Pin, PinEdge.Right | PinEdge.Bottom, -60, 60, 120, 56));
    }

    [Fact]
    public void The_size_never_goes_below_the_minimum_and_the_opposite_edge_stays()
    {
        Assert.Equal(new PixelRect(100, 100, 120, 56), PinResize.Resize(Pin, PinEdge.Right | PinEdge.Bottom, -1000, -1000, 120, 56));
        Assert.Equal(new PixelRect(280, 214, 120, 56), PinResize.Resize(Pin, PinEdge.Left | PinEdge.Top, 1000, 1000, 120, 56));
    }
}
