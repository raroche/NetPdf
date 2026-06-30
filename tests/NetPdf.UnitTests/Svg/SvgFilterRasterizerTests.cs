// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 5 — <c>filter="url(#id)"</c> element references: a linear chain of
/// <c>feGaussianBlur</c> / <c>feOffset</c> / <c>feColorMatrix</c> primitives composes into a Skia image
/// filter over the element subtree. Graph-routing primitives (feMerge/feComposite/…) and a non-filter
/// target are flagged.</summary>
public sealed class SvgFilterRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Fact]
    public void Gaussian_blur_spreads_ink_beyond_the_sharp_shape()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"b\"><feGaussianBlur stdDeviation=\"3\"/></filter>" +
            "<rect x=\"30\" y=\"30\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#b)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 40, 40).A > 150);    // core still painted
        Assert.True(Px(info!, 26, 40).A > 0);      // blurred halo 4px left of the sharp edge (x=30)
        Assert.Equal(0, Px(info!, 2, 2).A);        // far corner clean
    }

    [Fact]
    public void Color_matrix_saturate_zero_desaturates_to_gray()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"g\"><feColorMatrix type=\"saturate\" values=\"0\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#g)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 20, 20);
        Assert.True(p.A > 150);
        Assert.True(System.Math.Abs(p.R - p.G) < 12 && System.Math.Abs(p.R - p.B) < 12); // R≈G≈B → gray
        Assert.True(p.R is > 20 and < 120);        // luma of red, not full red
    }

    [Fact]
    public void Color_matrix_explicit_values_apply()
    {
        // Swap R↔G via an explicit matrix: a red fill becomes green.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<filter id=\"m\"><feColorMatrix type=\"matrix\" values=\"0 1 0 0 0  1 0 0 0 0  0 0 1 0 0  0 0 0 1 0\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"#ff0000\" filter=\"url(#m)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 10, 10);
        Assert.True(p.G > 200 && p.R < 60);        // R→G swap turned red into green
    }

    [Fact]
    public void Fe_drop_shadow_paints_an_offset_colored_shadow_under_the_source()
    {
        // SVG part 6 — feDropShadow: a sharp blue shadow offset +8,+8 with the red source drawn on top.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"ds\"><feDropShadow dx=\"8\" dy=\"8\" stdDeviation=\"0\" flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#ds)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);                 // feDropShadow is a supported primitive
        Assert.True(Px(info!, 20, 20).R > 150);    // the red source on top
        var shadow = Px(info!, 35, 35);            // shadow region beyond the source (rect ends at 30)
        Assert.True(shadow.B > 150 && shadow.R < 100); // blue shadow, not red
    }

    [Fact]
    public void A_filter_referencing_a_non_filter_is_flagged_and_renders_unfiltered()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<linearGradient id=\"x\"><stop offset=\"0\" stop-color=\"red\"/></linearGradient>" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#x)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.True(Px(info!, 10, 10).R > 200);    // rendered unfiltered
    }

    [Theory]
    // The filter REGION / primitive subregion / *Units still aren't modeled → flagged. (in/result routing is
    // now SUPPORTED via the filter graph — SVG part 7.)
    [InlineData("<filter id=\"f\" x=\"0\" y=\"0\" width=\"100\" height=\"100\"><feGaussianBlur stdDeviation=\"1\"/></filter>")] // filter region
    [InlineData("<filter id=\"f\"><feGaussianBlur stdDeviation=\"1\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></filter>")]   // primitive subregion
    [InlineData("<filter id=\"f\" primitiveUnits=\"objectBoundingBox\"><feGaussianBlur stdDeviation=\"1\"/></filter>")]        // primitiveUnits
    [InlineData("<filter id=\"f\"><feImage href=\"x.png\"/></filter>")]                                                       // unsupported primitive
    [InlineData("<filter id=\"f\"><feOffset dx=\"2\" in=\"BackgroundImage\"/></filter>")]                                     // unsupported input
    public void Filter_region_or_unsupported_input_is_flagged(string filter)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" + filter +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }

    [Theory]
    // SVG part 7 — named-result routing (in/in2/result, SourceGraphic, SourceAlpha) is now supported, not flagged.
    [InlineData("<filter id=\"f\"><feGaussianBlur stdDeviation=\"1\" in=\"SourceGraphic\"/></filter>")]
    [InlineData("<filter id=\"f\"><feOffset dx=\"2\" in=\"SourceAlpha\"/></filter>")]
    [InlineData("<filter id=\"f\"><feGaussianBlur stdDeviation=\"1\" result=\"b\"/><feOffset dx=\"2\" in=\"b\"/></filter>")]
    public void Filter_named_result_routing_is_supported(string filter)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" + filter +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
    }

    [Fact]
    public void Fe_merge_and_flood_build_a_colored_drop_shadow()
    {
        // The classic hand-built drop shadow: SourceAlpha → blur → offset → flood-colored (composite in) →
        // merged under SourceGraphic. A blue shadow offset down-right with the red source on top.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\">" +
            "<feOffset in=\"SourceAlpha\" dx=\"8\" dy=\"8\" result=\"o\"/>" +
            "<feFlood flood-color=\"blue\" result=\"c\"/>" +
            "<feComposite in=\"c\" in2=\"o\" operator=\"in\" result=\"s\"/>" +
            "<feMerge><feMergeNode in=\"s\"/><feMergeNode in=\"SourceGraphic\"/></feMerge>" +
            "</filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).R > 150);    // red source on top
        var shadow = Px(info!, 35, 35);            // shadow region beyond the source
        Assert.True(shadow.B > 150 && shadow.R < 100); // blue shadow
    }

    [Fact]
    public void Fe_blend_multiply_darkens()
    {
        // SourceGraphic (red) blended multiply with a green flood → near-black (R·G ≈ 0).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" +
            "<feFlood flood-color=\"#00ff00\" result=\"g\"/>" +
            "<feBlend in=\"SourceGraphic\" in2=\"g\" mode=\"multiply\"/>" +
            "</filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 20, 20);
        Assert.True(p.R < 80 && p.G < 80 && p.B < 80);   // red × green ≈ black
    }
}
