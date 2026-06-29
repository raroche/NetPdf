// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 4 — <c>clip-path="url(#id)"</c> element references: the element (and its
/// subtree) is clipped to the union of the referenced <c>&lt;clipPath&gt;</c>'s child geometry, honoring
/// <c>clipPathUnits</c> (userSpaceOnUse default + objectBoundingBox). A url() to a non-clipPath leaves the
/// element unclipped and is flagged.</summary>
public sealed class SvgClipPathRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Fact]
    public void Userspace_clip_path_restricts_painting_to_the_clip_geometry()
    {
        // A full-canvas red rect clipped by a circle clipPath → only the circle region is painted.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<clipPath id=\"c\"><circle cx=\"20\" cy=\"20\" r=\"10\"/></clipPath>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" clip-path=\"url(#c)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).R > 200);    // inside the circle → painted
        Assert.Equal(0, Px(info!, 2, 2).A);        // a corner is outside the circle → clipped away
    }

    [Fact]
    public void Object_bounding_box_clip_maps_the_unit_square_onto_the_element_bbox()
    {
        // clipPathUnits=objectBoundingBox: a rect 0 0 0.5 1 clips the element to its LEFT half.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect x=\"0\" y=\"0\" width=\"0.5\" height=\"1\"/></clipPath>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" clip-path=\"url(#c)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 20).R > 200);    // left half painted
        Assert.Equal(0, Px(info!, 30, 20).A);      // right half clipped away
    }

    [Fact]
    public void Clip_path_none_does_not_clip()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\" clip-path=\"none\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 2, 2).R > 200);      // fully painted, no clip
    }

    [Fact]
    public void Clip_path_referencing_a_non_clippath_is_flagged_and_leaves_the_element_unclipped()
    {
        // url(#g) names a gradient, not a clipPath → flag + paint the element unclipped (never clip it all away).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\" clip-path=\"url(#g)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.True(Px(info!, 10, 10).R > 200);    // unclipped, fully painted
    }

    [Fact]
    public void Clip_path_on_a_group_clips_all_children()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<clipPath id=\"c\"><rect x=\"0\" y=\"0\" width=\"20\" height=\"40\"/></clipPath>" +
            "<g clip-path=\"url(#c)\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"20\" fill=\"red\"/>" +
            "<rect x=\"0\" y=\"20\" width=\"40\" height=\"20\" fill=\"blue\"/></g></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 10).R > 200);    // left half of the red rect
        Assert.Equal(0, Px(info!, 30, 10).A);      // right half clipped
        Assert.True(Px(info!, 10, 30).B > 200);    // left half of the blue rect
        Assert.Equal(0, Px(info!, 30, 30).A);      // right half clipped
    }
}
