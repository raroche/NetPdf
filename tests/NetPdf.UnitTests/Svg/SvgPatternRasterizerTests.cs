// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 4 — <c>&lt;pattern&gt;</c> paint servers: a <c>fill="url(#id)"</c> /
/// <c>stroke="url(#id)"</c> referencing a <c>&lt;pattern&gt;</c> tiles the pattern's content across the
/// shape, honoring <c>patternUnits</c> / <c>patternContentUnits</c>. Self-referential patterns are bounded.</summary>
public sealed class SvgPatternRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Fact]
    public void Object_bounding_box_pattern_tiles_content_across_the_shape()
    {
        // patternUnits default = objectBoundingBox: width/height 0.5 over a 40×40 rect → a 20×20 tile, each
        // holding a 10×10 red square at its top-left. The tile repeats 2×2.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<pattern id=\"p\" width=\"0.5\" height=\"0.5\"><rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></pattern>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#p)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 5).R > 200);      // red square in the first tile
        Assert.Equal(0, Px(info!, 15, 15).A);      // empty corner of the first tile
        Assert.True(Px(info!, 25, 5).R > 200);     // second tile (x repeat at +20)
        Assert.True(Px(info!, 5, 25).R > 200);     // third tile (y repeat at +20)
    }

    [Fact]
    public void Userspace_pattern_tiles_at_its_absolute_size()
    {
        // patternUnits=userSpaceOnUse, 20×20 tile with a centered blue circle, over a 40×40 rect → 2×2 tiles.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"20\" height=\"20\">" +
            "<circle cx=\"10\" cy=\"10\" r=\"5\" fill=\"blue\"/></pattern>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#p)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 10).B > 200);    // circle in tile (0,0)
        Assert.True(Px(info!, 30, 10).B > 200);    // circle in tile (20,0)
        Assert.True(Px(info!, 10, 30).B > 200);    // circle in tile (0,20)
        Assert.Equal(0, Px(info!, 1, 1).A);        // corner between circles is empty
    }

    [Fact]
    public void Pattern_content_units_object_bounding_box_scales_content_by_the_bbox()
    {
        // patternContentUnits=objectBoundingBox: a 0.25×0.25 content rect scales by the bbox (40) → 10×10
        // green at each 20×20 tile's origin.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"20\" height=\"20\" patternContentUnits=\"objectBoundingBox\">" +
            "<rect x=\"0\" y=\"0\" width=\"0.25\" height=\"0.25\" fill=\"#00ff00\"/></pattern>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#p)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 5).G > 200);      // 10×10 green in the first tile
        Assert.Equal(0, Px(info!, 15, 15).A);      // empty corner
        Assert.True(Px(info!, 25, 5).G > 200);     // second tile
    }

    [Fact]
    public void Pattern_without_a_size_is_flagged_and_paints_nothing()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<pattern id=\"p\"><rect width=\"10\" height=\"10\" fill=\"red\"/></pattern>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#p)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.Equal(0, Px(info!, 20, 20).A);      // no tile size → no fill
    }

    [Fact]
    public void Self_referential_pattern_is_bounded_and_does_not_crash()
    {
        // A pattern whose content fills with itself must not recurse forever (depth guard).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"url(#p)\"/></pattern>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#p)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);          // no hang / crash
        Assert.True(unsupported);      // the recursion bottoms out flagged
    }

    [Fact]
    public void Pattern_inherits_content_through_href()
    {
        // A pattern with geometry but no children inherits the tile content from its href target.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<pattern id=\"base\" patternUnits=\"userSpaceOnUse\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></pattern>" +
            "<pattern id=\"p\" href=\"#base\"/>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#p)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 5).R > 200);      // inherited tile content rendered
        Assert.True(Px(info!, 25, 5).R > 200);     // and tiled
    }
}
