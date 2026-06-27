// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 2 (PR 7) — gradient paint servers: a <c>fill="url(#id)"</c> referencing a
/// <c>&lt;linearGradient&gt;</c> / <c>&lt;radialGradient&gt;</c> paints the gradient (not transparent / not
/// black), honoring objectBoundingBox geometry, the stop list, href stop inheritance, and userSpaceOnUse.</summary>
public sealed class SvgGradientRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Fact]
    public void Linear_gradient_objectBoundingBox_is_horizontal_red_to_blue()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">" +
            "<defs><linearGradient id=\"g\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/>" +
            "</linearGradient></defs>" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"20\" fill=\"url(#g)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);                 // defs skipped, gradient resolved → nothing flagged
        var left = Px(info!, 4, 10);
        var right = Px(info, 95, 10);
        Assert.True(left.R > left.B);              // near x=0 → red dominates
        Assert.True(right.B > right.R);            // near x=1 → blue dominates
        Assert.True(left.A > 200 && right.A > 200); // opaque
    }

    [Fact]
    public void Radial_gradient_is_bright_at_center_dark_at_edge()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<radialGradient id=\"r\">" +
            "<stop offset=\"0\" stop-color=\"white\"/><stop offset=\"1\" stop-color=\"black\"/>" +
            "</radialGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#r)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);                 // a bare gradient element does not flag, nor does the resolve
        var center = Px(info!, 20, 20);
        var corner = Px(info, 2, 2);
        Assert.True(center.R > 200);               // white-ish center
        Assert.True(corner.R < 80);                // black-ish edge
    }

    [Fact]
    public void Gradient_stops_inherit_through_href()
    {
        // The geometry gradient has no stops; it borrows them from #base via href (SVG §13.2.4).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">" +
            "<defs>" +
            "<linearGradient id=\"base\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<linearGradient id=\"g\" href=\"#base\"/>" +
            "</defs>" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"20\" fill=\"url(#g)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 4, 10).R > Px(info!, 4, 10).B);    // stops resolved → red on the left
        Assert.True(Px(info!, 95, 10).B > Px(info!, 95, 10).R);  // blue on the right
    }

    [Fact]
    public void UserSpaceOnUse_linear_gradient_paints()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">" +
            "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"100\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"20\" fill=\"url(#g)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 4, 10).R > Px(info!, 4, 10).B);
        Assert.True(Px(info!, 95, 10).B > Px(info!, 95, 10).R);
    }

    [Fact]
    public void Gradient_stop_opacity_is_respected()
    {
        // A fully transparent second stop → the right edge is (near) transparent.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\" stop-opacity=\"1\"/>" +
            "<stop offset=\"1\" stop-color=\"red\" stop-opacity=\"0\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"20\" fill=\"url(#g)\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 4, 10).A > 200);     // opaque on the left
        Assert.True(Px(info!, 98, 10).A < 60);     // transparent on the right
    }

    [Fact]
    public void Unresolved_paint_server_is_flagged_and_unpainted()
    {
        // No gradient with id "missing" exists → no fill, flagged (regression guard for the part-1 behavior).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"url(#missing)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.Equal(0, Px(info!, 10, 10).A);      // transparent, not black
    }

    [Fact]
    public void Stroke_gradient_paints_the_stroke()
    {
        // PR-231 review — pin stroke gradients: a thick gradient stroke around a rect paints non-default ink.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"none\" stroke=\"url(#g)\" stroke-width=\"6\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 20).A > 150);     // left edge of the stroke is painted
        Assert.Equal(0, Px(info!, 20, 20).A);      // interior (fill:none) is empty
    }

    [Fact]
    public void Repeat_spread_method_tiles_the_gradient()
    {
        // spreadMethod=repeat over a userSpaceOnUse vector covering only the left quarter → the red→blue
        // ramp repeats, so red recurs near x=0 and near x=25 (the start of the second tile).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">" +
            "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" spreadMethod=\"repeat\" x1=\"0\" y1=\"0\" x2=\"25\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"20\" fill=\"url(#g)\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 1, 10).R > Px(info!, 1, 10).B);     // red at tile 0 start
        Assert.True(Px(info!, 26, 10).R > Px(info!, 26, 10).B);   // red again at tile 1 start (repeat)
    }

    [Fact]
    public void Gradient_transform_rotates_the_gradient_axis()
    {
        // A horizontal userSpaceOnUse gradient rotated 90° becomes vertical → top redder than bottom.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"40\" y2=\"0\" gradientTransform=\"rotate(90, 20, 20)\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#g)\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 20, 3).R > Px(info!, 20, 3).B);     // top → red end
        Assert.True(Px(info!, 20, 37).B > Px(info!, 20, 37).R);   // bottom → blue end
    }

    [Fact]
    public void Radial_focal_point_offsets_the_bright_center()
    {
        // A focal point in the top-left pushes the white core toward (cx≈0.2,cy≈0.2) — that quadrant is
        // brighter than the bottom-right.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<radialGradient id=\"r\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.2\" fy=\"0.2\">" +
            "<stop offset=\"0\" stop-color=\"white\"/><stop offset=\"1\" stop-color=\"black\"/></radialGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"url(#r)\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 8, 8).R > Px(info!, 32, 32).R);     // focal quadrant brighter
    }

    [Fact]
    public void Gradient_geometry_attributes_inherit_through_href()
    {
        // The referencing gradient borrows the base's userSpaceOnUse + x1/x2 geometry AND stops via href.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">" +
            "<defs>" +
            "<linearGradient id=\"base\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"100\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<linearGradient id=\"g\" href=\"#base\"/></defs>" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"20\" fill=\"url(#g)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 4, 10).R > Px(info!, 4, 10).B);
        Assert.True(Px(info!, 95, 10).B > Px(info!, 95, 10).R);
    }

    [Fact]
    public void Stop_color_currentColor_resolves_to_the_color_property()
    {
        // stop-color="currentColor" must resolve to the element's `color`, not silently paint black.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"currentColor\"/><stop offset=\"1\" stop-color=\"currentColor\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"20\" fill=\"url(#g)\" color=\"#00ff00\"/></svg>"), out _);
        Assert.NotNull(info);
        var p = Px(info!, 20, 10);
        Assert.True(p.G > 200 && p.R < 80 && p.B < 80);    // green, not black
    }
}
