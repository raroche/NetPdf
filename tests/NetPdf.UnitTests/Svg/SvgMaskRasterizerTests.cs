// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 4 — <c>mask="url(#id)"</c> element references: the element's subtree is
/// composited against the LUMINANCE of a <c>&lt;mask&gt;</c>'s content (white = opaque, black/absent =
/// transparent, gray = partial), honoring <c>maskContentUnits</c>. A url() to a non-mask leaves the element
/// unmasked and is flagged.</summary>
public sealed class SvgMaskRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Fact]
    public void Mask_shows_the_element_only_where_the_mask_is_opaque()
    {
        // A white mask rect covers the left half (user space) → the element shows there and is hidden right.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\"><rect x=\"0\" y=\"0\" width=\"20\" height=\"40\" fill=\"white\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 20).A > 200);    // under the white mask → visible
        Assert.Equal(0, Px(info!, 30, 20).A);      // outside the mask content → fully masked out
    }

    [Fact]
    public void Gray_mask_makes_the_element_partially_transparent()
    {
        // A mid-gray (#808080) mask → luminance ≈ 0.5 → the element renders at ≈ half alpha.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\"><rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#808080\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out _);
        Assert.NotNull(info);
        var a = Px(info!, 20, 20).A;
        Assert.InRange(a, 100, 160);               // ≈ 128
    }

    [Fact]
    public void Mask_none_does_not_mask()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\" mask=\"none\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 10).A > 200);    // fully visible
    }

    [Fact]
    public void Mask_referencing_a_non_mask_is_flagged_and_leaves_the_element_unmasked()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"white\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\" mask=\"url(#g)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.True(Px(info!, 10, 10).A > 200);    // unmasked, fully visible
    }

    [Fact]
    public void Object_bounding_box_mask_content_maps_to_the_element_bbox()
    {
        // maskContentUnits=objectBoundingBox: a white rect 0 0 0.5 1 reveals the element's LEFT half.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" maskContentUnits=\"objectBoundingBox\"><rect x=\"0\" y=\"0\" width=\"0.5\" height=\"1\" fill=\"white\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 20).A > 200);    // left half revealed
        Assert.Equal(0, Px(info!, 30, 20).A);      // right half masked out
    }
}
