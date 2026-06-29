// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients — the <c>background-size</c>/<c>-position</c>/<c>-repeat</c> tile-grid
/// geometry for a gradient (no intrinsic size: <c>auto</c>/<c>contain</c>/<c>cover</c> = the area; an
/// explicit length sets the tile, an auto axis = the area dimension). Pure px-in/px-out math.</summary>
public sealed class GradientTileGridTests
{
    private static ImageResourceCache.GradientBgGeometry Geom(
        string? size = null, string? position = null, string? repeat = null) =>
        new(OriginRaw: null, ClipRaw: null, SizeRaw: size, PositionRaw: position, RepeatRaw: repeat);

    // Grid over a 100×60 area whose clip == the area (the simple single-box case).
    private static FragmentPainter.GradientTileGrid Grid(
        ImageResourceCache.GradientBgGeometry geom, double w = 100, double h = 60) =>
        FragmentPainter.ResolveGradientTileGrid(geom, w, h, 0, w, 0, h, out _);

    [Fact]
    public void Auto_size_fills_the_area_as_a_single_tile()
    {
        var g = Grid(Geom());
        Assert.Equal(100, g.TileWidthPx, 6);
        Assert.Equal(60, g.TileHeightPx, 6);
        Assert.Equal(1, g.CountX);
        Assert.Equal(1, g.CountY);
        Assert.Equal(0, g.FirstXPx, 6);
    }

    [Theory]
    [InlineData("contain")]
    [InlineData("cover")]
    [InlineData("auto auto")]
    public void Contain_cover_auto_all_equal_the_area_for_a_gradient(string size)
    {
        var g = Grid(Geom(size: size));
        Assert.Equal(100, g.TileWidthPx, 6);
        Assert.Equal(60, g.TileHeightPx, 6);
    }

    [Fact]
    public void Explicit_two_value_size_sets_the_tile_and_tiles_to_cover()
    {
        // 50×30 tile over a 100×60 area, default repeat → 2×2 tiles flush from the origin.
        var g = Grid(Geom(size: "50px 30px"));
        Assert.Equal(50, g.TileWidthPx, 6);
        Assert.Equal(30, g.TileHeightPx, 6);
        Assert.Equal(2, g.CountX);
        Assert.Equal(2, g.CountY);
        Assert.Equal(50, g.StepXPx, 6);
    }

    [Fact]
    public void Single_value_size_makes_the_other_axis_the_area_dimension()
    {
        // `40px` = `40px auto`; a gradient has no ratio → auto height = the 60px area height.
        var g = Grid(Geom(size: "40px"));
        Assert.Equal(40, g.TileWidthPx, 6);
        Assert.Equal(60, g.TileHeightPx, 6);
        Assert.Equal(1, g.CountY); // height fills the area → one row
    }

    [Fact]
    public void Percentage_size_resolves_against_the_area()
    {
        var g = Grid(Geom(size: "25% 50%"));
        Assert.Equal(25, g.TileWidthPx, 6);  // 25% of 100
        Assert.Equal(30, g.TileHeightPx, 6); // 50% of 60
        Assert.Equal(4, g.CountX);           // 100 / 25
        Assert.Equal(2, g.CountY);           // 60 / 30
    }

    [Fact]
    public void No_repeat_places_a_single_positioned_tile()
    {
        var g = Grid(Geom(size: "50px 30px", repeat: "no-repeat"));
        Assert.Equal(1, g.CountX);
        Assert.Equal(1, g.CountY);
    }

    [Fact]
    public void Repeat_x_tiles_only_the_x_axis()
    {
        var g = Grid(Geom(size: "50px 30px", repeat: "repeat-x"));
        Assert.Equal(2, g.CountX);
        Assert.Equal(1, g.CountY); // y is no-repeat
    }

    [Fact]
    public void Position_phases_the_grid()
    {
        // A 50px tile phased at posX=10 over a 100-wide clip: first tile origin ≤ 0 → 10 - 50 = -40.
        var g = Grid(Geom(size: "50px 30px", position: "10px 0"));
        Assert.Equal(-40, g.FirstXPx, 6);
        Assert.Equal(3, g.CountX); // tiles at -40, 10, 60 cover [0, 100]
    }

    [Fact]
    public void Round_rescales_the_tile_to_a_whole_count()
    {
        // 30px tile, round over a 100px axis → round(100/30)=3 tiles → tile rescaled to 100/3 ≈ 33.33.
        var g = Grid(Geom(size: "30px 30px", repeat: "round"));
        Assert.Equal(100.0 / 3.0, g.TileWidthPx, 4);
        Assert.Equal(3, g.CountX);
    }

    [Fact]
    public void Space_distributes_whole_tiles_with_equal_gaps()
    {
        // 40px tile, space over a 100px axis → floor(100/40)=2 tiles, gap = (100-80)/(2-1)=20 → step 60.
        var g = Grid(Geom(size: "40px 30px", repeat: "space"));
        Assert.Equal(2, g.CountX);
        Assert.Equal(60, g.StepXPx, 6); // 40 tile + 20 gap
        Assert.Equal(0, g.FirstXPx, 6); // flush with the area start
    }

    [Fact]
    public void Unsupported_size_unit_flags_unsupported_and_falls_back_to_the_area()
    {
        FragmentPainter.ResolveGradientTileGrid(Geom(size: "3em 2em"), 100, 60, 0, 100, 0, 60, out var bad);
        Assert.True(bad);
    }

    [Theory]
    [InlineData("0", 0.0, 60.0)]      // zero width, height = area (single value → auto)
    [InlineData("10px 0", 10.0, 0.0)] // zero height
    [InlineData("0%", 0.0, 60.0)]     // zero percentage
    public void Zero_size_yields_a_no_tile_grid_without_dividing_by_zero(string size, double w, double h)
    {
        // A valid zero-sized tile → count 0 on both axes (no AxisTilingPlan, no NaN/Infinity), NOT flagged
        // unsupported (the value parsed fine — it just paints nothing).
        var g = FragmentPainter.ResolveGradientTileGrid(Geom(size: size, repeat: "round"), 100, 60, 0, 100, 0, 60, out var bad);
        Assert.False(bad);
        Assert.Equal(w, g.TileWidthPx, 6);
        Assert.Equal(h, g.TileHeightPx, 6);
        Assert.Equal(0, g.CountX);
        Assert.Equal(0, g.CountY);
    }
}
