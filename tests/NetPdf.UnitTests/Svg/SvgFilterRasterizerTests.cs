// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Svg;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG — <c>filter="url(#id)"</c> element references: primitives compose into a Skia image
/// filter over the element subtree. SVG part 7 models a filter GRAPH — <c>feFlood</c>/<c>feMerge</c>/
/// <c>feComposite</c>/<c>feBlend</c> with <c>in</c>/<c>in2</c>/named-<c>result</c> routing are SUPPORTED; the
/// composited result is clipped to the default filter region; only the primary (reachable) tree contributes.
/// SVG part 8 adds <c>feMorphology</c> / <c>feComponentTransfer</c> / <c>feDisplacementMap</c> /
/// <c>feConvolveMatrix</c> / <c>feTurbulence</c>. SVG part 9 adds <c>feDiffuseLighting</c> /
/// <c>feSpecularLighting</c> (distant/point/spot lights), <c>feImage</c> (a <c>data:</c> raster placed into the
/// region via <c>preserveAspectRatio</c>), and an EXPLICIT filter region (x/y/width/height + <c>filterUnits</c>
/// objectBoundingBox/userSpaceOnUse, incl. an empty zero-size region). A non-filter target, <c>feTile</c>, a
/// <c>feImage</c> element ref, a primitive subregion / <c>primitiveUnits</c>, lighting <c>kernelUnitLength</c>,
/// an unknown <c>filterUnits</c> value, and unmodeled inputs (<c>BackgroundImage</c>/…) are flagged.</summary>
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
        Assert.True(Px(info!, 29, 40).A > 0);      // blurred halo 1px left of the sharp edge (x=30), inside the region
        Assert.Equal(0, Px(info!, 24, 40).A);      // clipped at the default filter region (left ≈ x=28)
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
        var shadow = Px(info!, 31, 31);            // shadow beyond the source (rect ends at 30), inside the region (≤32)
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
    // An external feImage href and a lighting primitive under primitiveUnits (positions not remapped) still
    // aren't modeled → flagged. (Filter region — part 9; in/result routing — part 7; primitive SUBREGIONS —
    // part 11; primitiveUnits non-subregion remap + FillPaint/BackgroundImage inputs — part 12; are supported.)
    [InlineData("<filter id=\"f\"><feImage href=\"x.png\"/></filter>")]                                                       // external, non-data/non-#id href
    [InlineData("<filter id=\"f\" primitiveUnits=\"objectBoundingBox\"><feDiffuseLighting><feDistantLight azimuth=\"0\" elevation=\"90\"/></feDiffuseLighting></filter>")] // lighting position not remapped
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
        var shadow = Px(info!, 31, 31);            // shadow beyond the source, inside the region (≤32)
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

    [Fact]
    public void Fe_flood_alone_is_clipped_to_the_default_filter_region()
    {
        // PR-246 review [P1]: a bare final feFlood would fill the whole layer; the default filter region (the
        // element bbox inflated 10%) clips it so a far-corner pixel outside the region stays transparent.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).B > 150);    // inside the region (≈8..32): flooded blue
        Assert.Equal(0, Px(info!, 70, 70).A);      // far corner outside the default region: transparent
    }

    [Theory]
    // PR-246 review [P2]: every advertised feComposite operator (plus 'lighter') is supported, not flagged.
    [InlineData("over")]
    [InlineData("in")]
    [InlineData("out")]
    [InlineData("atop")]
    [InlineData("xor")]
    [InlineData("arithmetic")]
    [InlineData("lighter")]
    public void Fe_composite_advertised_operators_are_supported(string op)
    {
        var k = op == "arithmetic" ? " k1=\"0\" k2=\"1\" k3=\"1\" k4=\"0\"" : "";
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" +
            "<feFlood flood-color=\"#00ff00\" result=\"g\"/>" +
            $"<feComposite in=\"SourceGraphic\" in2=\"g\" operator=\"{op}\"{k}/>" +
            "</filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
    }

    [Fact]
    public void Fe_composite_lighter_adds_source_and_destination()
    {
        // 'lighter' = source + destination (additive): red SourceGraphic over a green flood → yellow.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" +
            "<feFlood flood-color=\"#00ff00\" result=\"g\"/>" +
            "<feComposite in=\"SourceGraphic\" in2=\"g\" operator=\"lighter\"/>" +
            "</filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 20, 20);
        Assert.True(p.R > 150 && p.G > 150 && p.B < 100); // red + green = yellow
    }

    [Fact]
    public void Fe_composite_unknown_operator_is_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" +
            "<feFlood flood-color=\"#00ff00\" result=\"g\"/>" +
            "<feComposite in=\"SourceGraphic\" in2=\"g\" operator=\"frobnicate\"/>" +
            "</filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }

    [Theory]
    // PR-246 review [P2]: a forward reference (in="later" before result="later") and a missing custom result
    // name are treated as if no input were specified (Filter Effects §9.2) — NOT an unsupported feature.
    [InlineData("<feOffset dx=\"2\" in=\"later\"/><feGaussianBlur stdDeviation=\"1\" result=\"later\"/>")]
    [InlineData("<feGaussianBlur stdDeviation=\"1\" in=\"missing\"/>")]
    public void Filter_forward_or_missing_result_reference_is_not_flagged(string body)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" + body + "</filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
    }

    [Fact]
    public void Disconnected_unsupported_primitive_tree_does_not_flag()
    {
        // PR-246 review [P2]: an unsupported feImage in a DISCONNECTED earlier tree must not flag — only the
        // primary tree (the final feComposite reachable through in/in2) contributes.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" +
            "<feImage href=\"x.png\" result=\"ignored\"/>" +            // disconnected, unsupported
            "<feFlood flood-color=\"blue\" result=\"c\"/>" +
            "<feComposite in=\"c\" in2=\"SourceAlpha\" operator=\"in\"/>" +
            "</filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).B > 150);    // the blue silhouette of the rect, feImage tree ignored
    }

    [Fact]
    public void Fe_merge_stacks_nodes_bottom_to_top_with_a_shaped_source()
    {
        // feMerge stacks nodes bottom-to-top (§15): bottom = a blue silhouette of the source, top = the red
        // SourceGraphic (a non-full-canvas shape) → red wins over the same shape.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" +
            "<feFlood flood-color=\"blue\" result=\"b\"/>" +
            "<feComposite in=\"b\" in2=\"SourceAlpha\" operator=\"in\" result=\"sil\"/>" +
            "<feMerge><feMergeNode in=\"sil\"/><feMergeNode in=\"SourceGraphic\"/></feMerge>" +
            "</filter>" +
            "<rect x=\"5\" y=\"5\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 15, 15);
        Assert.True(p.R > 150 && p.B < 80);        // red (top node) wins over the blue silhouette (bottom node)
    }

    // ---- SVG part 8: feMorphology / feComponentTransfer / feDisplacementMap / feConvolveMatrix / feTurbulence ----

    [Fact]
    public void Fe_morphology_dilate_grows_the_shape()
    {
        // dilate radius 4 spreads a 20×20 rect's ink outward — a pixel 3px outside the sharp edge (x=30)
        // becomes painted (it was transparent without the filter).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\"><feMorphology operator=\"dilate\" radius=\"4\"/></filter>" +
            "<rect x=\"30\" y=\"30\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 40, 40).A > 150);    // core still painted
        Assert.True(Px(info!, 51, 40).A > 100);    // dilated past the sharp edge (x=50), inside the region (<52)
    }

    [Fact]
    public void Fe_morphology_erode_shrinks_the_shape()
    {
        // erode radius 4 eats the edges — a pixel 2px inside the sharp edge is now transparent.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\"><feMorphology operator=\"erode\" radius=\"4\"/></filter>" +
            "<rect x=\"30\" y=\"30\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 40, 40).A > 150);    // core survives
        Assert.Equal(0, Px(info!, 31, 40).A);      // the 1px-inside edge is eaten away
    }

    [Fact]
    public void Fe_component_transfer_linear_zeroes_a_channel()
    {
        // feFuncR type="linear" slope=0 intercept=0 → the red channel is forced to 0 (a red rect → no red).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feComponentTransfer><feFuncR type=\"linear\" slope=\"0\" intercept=\"0\"/></feComponentTransfer></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 20, 20);
        Assert.True(p.A > 150 && p.R < 40);        // opaque, but the red channel is gone
    }

    [Fact]
    public void Fe_component_transfer_table_inverts_a_channel()
    {
        // feFuncR type="table" tableValues="1 0" maps R=1 → 0 (inverts the channel): a red rect loses its red.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feComponentTransfer><feFuncR type=\"table\" tableValues=\"1 0\"/></feComponentTransfer></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).R < 40);
    }

    [Fact]
    public void Fe_displacement_map_is_supported_and_renders()
    {
        // The classic self-displacement: a turbulence map displaces the source. Just assert it's a SUPPORTED
        // primitive (not flagged) and the source still contributes ink.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\">" +
            "<feTurbulence type=\"turbulence\" baseFrequency=\"0.1\" numOctaves=\"2\" result=\"n\"/>" +
            "<feDisplacementMap in=\"SourceGraphic\" in2=\"n\" scale=\"6\" xChannelSelector=\"R\" yChannelSelector=\"G\"/>" +
            "</filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 30, 30).A > 100);    // the displaced source still has ink near the center
    }

    [Fact]
    public void Fe_convolve_matrix_identity_kernel_is_supported_and_keeps_the_source()
    {
        // A 3×3 identity kernel (center 1, rest 0) leaves the source essentially unchanged — and is SUPPORTED.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feConvolveMatrix order=\"3\" kernelMatrix=\"0 0 0 0 1 0 0 0 0\"/></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var p = Px(info!, 20, 20);
        Assert.True(p.R > 150 && p.A > 150);       // the identity kernel preserves the red source
    }

    [Fact]
    public void Fe_convolve_matrix_with_a_bad_kernel_is_flagged()
    {
        // A kernelMatrix whose length doesn't match the order is invalid → flagged (input passes through).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feConvolveMatrix order=\"3\" kernelMatrix=\"1 2 3\"/></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }

    [Fact]
    public void Fe_turbulence_fills_the_region_with_noise()
    {
        // feTurbulence generates Perlin noise across the filter region (clipped to the default region). Assert
        // it's SUPPORTED and the region has VARIED pixels (not a flat fill).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\"><feTurbulence type=\"fractalNoise\" baseFrequency=\"0.2\" numOctaves=\"3\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        // Two interior pixels differ (noise is not a flat fill).
        var a = Px(info!, 20, 20);
        var b = Px(info!, 38, 34);
        Assert.True(a.R != b.R || a.G != b.G || a.B != b.B || a.A != b.A);
    }

    [Theory]
    // PR-248 review [P2]: feTurbulence is a GENERATOR — a degenerate baseFrequency (≤ 0 on both axes,
    // omitted, or malformed) must NOT pass the source through. It is FLAGGED + an EMPTY (transparent) result.
    [InlineData("<feTurbulence baseFrequency=\"0\"/>")]            // explicit zero
    [InlineData("<feTurbulence/>")]                                // omitted (defaults to 0)
    [InlineData("<feTurbulence baseFrequency=\"0 0\"/>")]          // both axes zero
    [InlineData("<feTurbulence baseFrequency=\"junk\"/>")]         // malformed
    public void Fe_turbulence_degenerate_frequency_is_flagged_not_passed_through(string prim)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" + prim + "</filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);                  // flagged (a generator we can't faithfully produce)
        Assert.Equal(0, Px(info!, 20, 20).A);      // empty result — the source does NOT pass through
    }

    [Fact]
    public void Fe_turbulence_one_axis_zero_frequency_still_generates_noise()
    {
        // baseFrequency="0.25 0" varies in x only — still a valid generator (supported, not flagged).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\"><feTurbulence baseFrequency=\"0.25 0\" numOctaves=\"2\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        var a = Px(info!, 16, 30);
        var b = Px(info!, 44, 30);
        Assert.True(a.R != b.R || a.G != b.G || a.B != b.B || a.A != b.A); // varies along x
    }

    [Theory]
    // PR-248 review [P2]: a zero (or negative, or absent) morphology radius on EITHER axis disables the
    // primitive (§9.6) — the input passes through unchanged.
    [InlineData("radius=\"0 4\"")]
    [InlineData("radius=\"4 0\"")]
    [InlineData("radius=\"-3\"")]
    [InlineData("")]                       // no radius attribute → default 0 → disabled
    public void Fe_morphology_zero_axis_radius_disables_the_effect(string attrs)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            $"<filter id=\"f\"><feMorphology operator=\"erode\" {attrs}/></filter>" +
            "<rect x=\"15\" y=\"15\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        // The rect is unchanged (no erode applied) — its edge pixels survive.
        Assert.True(Px(info!, 30, 30).R > 150);    // core
        Assert.True(Px(info!, 16, 30).A > 150);    // the near-edge pixel is NOT eaten (effect disabled)
    }

    [Theory]
    // PR-248 review [P1]: a hostile / malformed feConvolveMatrix order or kernel must flag BEFORE the native
    // Skia call — a huge order can overflow the int product to 0 and slip an empty kernel through.
    [InlineData("order=\"65536 65536\" kernelMatrix=\"1\"")]       // overflowing product + tiny kernel
    [InlineData("order=\"200\" kernelMatrix=\"1 0 0\"")]           // order past the cap
    [InlineData("order=\"2.5\" kernelMatrix=\"1 0 0 0\"")]         // fractional order
    [InlineData("order=\"-3\" kernelMatrix=\"1\"")]                // negative order
    [InlineData("order=\"3\" kernelMatrix=\"1 2 3\"")]             // kernel length ≠ order²
    [InlineData("order=\"3\" kernelMatrix=\"1 2 3 4 x 6 7 8 9\"")] // malformed kernel token
    public void Fe_convolve_matrix_invalid_order_or_kernel_is_flagged(string attrs)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            $"<filter id=\"f\"><feConvolveMatrix {attrs}/></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.True(Px(info!, 20, 20).R > 150);    // the input passes through unchanged
    }

    [Fact]
    public void Fe_component_transfer_gamma_and_alpha_apply()
    {
        // feFuncA type="gamma" amplitude=0 → alpha forced to 0 (the whole filtered rect becomes transparent).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feComponentTransfer><feFuncA type=\"gamma\" amplitude=\"0\" exponent=\"1\" offset=\"0\"/></feComponentTransfer></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.Equal(0, Px(info!, 20, 20).A);      // gamma amplitude 0 zeroed the alpha
    }

    [Fact]
    public void Fe_component_transfer_discrete_steps_a_channel()
    {
        // feFuncG type="discrete" tableValues="0 1" → G < 0.5 → 0, G ≥ 0.5 → 1. A mid-green (#008000, G≈0.5)
        // steps UP to full green.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feComponentTransfer><feFuncG type=\"discrete\" tableValues=\"0 1\"/></feComponentTransfer></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#00c000\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).G > 200);    // #00c000 (G≈0.75) steps to full green
    }

    [Fact]
    public void Fe_displacement_map_actually_moves_ink()
    {
        // A flat blue flood (B=255 → a constant non-zero displacement) shifts the red source. Compare a
        // displaced render (scale=40) against an identity one (scale=0): they must DIFFER somewhere in the
        // rect (direction-agnostic — the point is the map MOVES ink, not a no-op).
        string Doc(int scale) =>
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\">" +
            "<feFlood flood-color=\"#0000ff\" result=\"d\"/>" +
            $"<feDisplacementMap in=\"SourceGraphic\" in2=\"d\" scale=\"{scale}\" xChannelSelector=\"B\" yChannelSelector=\"B\"/>" +
            "</filter>" +
            "<rect x=\"30\" y=\"30\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>";

        var still = SvgRasterizer.TryRender(Svg(Doc(0)), out _);
        var moved = SvgRasterizer.TryRender(Svg(Doc(40)), out var unsupported);
        Assert.NotNull(moved);
        Assert.False(unsupported);
        var differs = false;
        for (var y = 26; y < 54 && !differs; y++)
            for (var x = 26; x < 54 && !differs; x++)
                if (Px(still!, x, y) != Px(moved!, x, y)) differs = true;
        Assert.True(differs, "the displacement map must move the source ink");
    }

    // ---- SVG part 9: lighting / feImage / explicit filter region / mask-type ----

    [Theory]
    // feDiffuseLighting lights the input alpha (a height field) with each light source — SUPPORTED, the lit
    // (opaque) surface fills the region.
    [InlineData("<feDistantLight azimuth=\"45\" elevation=\"60\"/>")]
    [InlineData("<fePointLight x=\"30\" y=\"30\" z=\"20\"/>")]
    [InlineData("<feSpotLight x=\"30\" y=\"30\" z=\"30\" pointsAtX=\"30\" pointsAtY=\"30\" pointsAtZ=\"0\" specularExponent=\"2\"/>")]
    public void Fe_diffuse_lighting_with_each_light_source_renders(string light)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\"><feDiffuseLighting surfaceScale=\"2\" diffuseConstant=\"1\" lighting-color=\"white\">" +
            light + "</feDiffuseLighting></filter>" +
            "<rect x=\"20\" y=\"20\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 30, 30).A > 100);    // the lit surface fills the region
    }

    [Fact]
    public void Fe_specular_lighting_is_supported()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\"><feSpecularLighting surfaceScale=\"3\" specularConstant=\"1\" specularExponent=\"10\" lighting-color=\"white\">" +
            "<feDistantLight azimuth=\"45\" elevation=\"45\"/></feSpecularLighting></filter>" +
            "<rect x=\"20\" y=\"20\" width=\"20\" height=\"20\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
    }

    [Fact]
    public void Fe_lighting_without_a_light_source_is_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feDiffuseLighting surfaceScale=\"1\"/></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.True(Px(info!, 20, 20).R > 150);    // no light → the input passes through
    }

    [Fact]
    public void Fe_image_with_a_data_uri_raster_renders()
    {
        var png = "data:image/png;base64," + Convert.ToBase64String(SyntheticPng.BuildOpaqueRgb8(16, 16, 0x00, 0x00, 0xFF));
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            $"<filter id=\"f\"><feImage href=\"{png}\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 5).B > 150);      // the blue feImage raster is placed (top-left of the region)
    }

    [Theory]
    // An EXTERNAL href (no fetch) and a DANGLING element reference (the id doesn't resolve) → flagged + an
    // empty (transparent) result, NOT a content pass-through (feImage is a generator). A RESOLVING element
    // reference is supported (see Fe_image_element_reference_renders_the_referenced_subtree).
    [InlineData("<feImage href=\"http://example.com/x.png\"/>")]
    [InlineData("<feImage href=\"#other\"/>")]
    public void Fe_image_external_or_dangling_reference_is_flagged(string prim)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\">" + prim + "</filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.Equal(0, Px(info!, 20, 20).A);      // empty result, NOT the source passed through
    }

    [Fact]
    public void Explicit_filter_region_object_bounding_box_clips_the_result()
    {
        // filterUnits=objectBoundingBox (default), x/y=0% width/height=50% → the region is the TOP-LEFT quadrant
        // of the bbox (rect 10..50 → region 10..30). A feFlood fills only that quadrant.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" x=\"0%\" y=\"0%\" width=\"50%\" height=\"50%\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);                 // an explicit filter region is no longer flagged
        Assert.True(Px(info!, 20, 20).B > 150);    // inside the region (top-left quadrant): flooded blue
        Assert.Equal(0, Px(info!, 45, 45).A);      // outside the region (bottom-right): transparent
    }

    [Fact]
    public void Explicit_filter_region_user_space_clips_the_result()
    {
        // filterUnits=userSpaceOnUse with absolute x/y/width/height → the region is (10,10)-(30,30) in user space.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10\" y=\"10\" width=\"20\" height=\"20\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"50\" height=\"50\" fill=\"red\" filter=\"url(#f)\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 20, 20).B > 150);    // inside (10,10)-(30,30): blue
        Assert.Equal(0, Px(info!, 45, 45).A);      // outside the region: transparent
    }

    // ---- SVG part 10: filter-region edge cases / feImage placement / lighting intensity / attr flags ----

    private static NetPdf.Pdf.Images.RasterImageInfo Render(string svg, out bool unsupported)
    {
        var info = SvgRasterizer.TryRender(Svg(svg), out unsupported);
        Assert.NotNull(info);
        return info!;
    }

    private static string BlueDataPng16 =>
        "data:image/png;base64," + Convert.ToBase64String(SyntheticPng.BuildOpaqueRgb8(16, 16, 0x00, 0x00, 0xFF));

    [Fact]
    public void Fe_image_fits_into_a_wide_region_with_preserve_aspect_ratio_meet()
    {
        // A square 16×16 image placed into a WIDE 40×20 user-space region (xMidYMid meet): scale 1.25 → 20×20,
        // centered horizontally → occupies x∈[10,30], y∈[0,20]. The horizontal letterbox stays transparent.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            $"<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"20\"><feImage href=\"{BlueDataPng16}\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"80\" height=\"80\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 20, 10).B > 150);     // center of the fitted image: blue
        Assert.Equal(0, Px(info, 2, 10).A);        // left letterbox (inside region, outside image): transparent
        Assert.Equal(0, Px(info, 37, 10).A);       // right letterbox: transparent
        Assert.Equal(0, Px(info, 20, 30).A);       // below the region (height 20): clipped away
    }

    [Fact]
    public void Fe_image_preserve_aspect_ratio_none_stretches_to_fill_the_region()
    {
        // preserveAspectRatio="none" stretches the 16×16 image to the full 40×20 region — no letterbox.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            $"<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"20\"><feImage preserveAspectRatio=\"none\" href=\"{BlueDataPng16}\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"80\" height=\"80\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 2, 10).B > 150);      // left edge filled (was letterbox under meet)
        Assert.True(Px(info, 37, 10).B > 150);     // right edge filled
        Assert.Equal(0, Px(info, 20, 30).A);       // still clipped to the region height
    }

    private static string DiffuseDoc(string primAttrs, string light) =>
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
        $"<filter id=\"f\"><feDiffuseLighting surfaceScale=\"1\" {primAttrs}>" + light + "</feDiffuseLighting></filter>" +
        "<rect x=\"15\" y=\"15\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>";

    private const string LightStraightDown = "<feDistantLight azimuth=\"0\" elevation=\"90\"/>";

    [Fact]
    public void Fe_diffuse_lighting_color_tints_the_lit_surface()
    {
        // A flat interior surface lit straight down (N·L = 1, kd = 1) → the lit color IS the lighting-color.
        var white = Render(DiffuseDoc("diffuseConstant=\"1\" lighting-color=\"white\"", LightStraightDown), out _);
        var red = Render(DiffuseDoc("diffuseConstant=\"1\" lighting-color=\"red\"", LightStraightDown), out _);
        var w = Px(white, 30, 30);
        Assert.True(w.R > 150 && System.Math.Abs(w.R - w.G) < 40 && System.Math.Abs(w.R - w.B) < 40); // neutral bright
        var r = Px(red, 30, 30);
        Assert.True(r.R > 120 && r.G < 90 && r.B < 90);    // red-only: the lighting-color tints the surface
    }

    [Fact]
    public void Fe_diffuse_lighting_constant_scales_brightness()
    {
        // diffuseConstant (kd) scales the lit intensity: kd=1 is markedly brighter than kd=0.3.
        var bright = Px(Render(DiffuseDoc("diffuseConstant=\"1\" lighting-color=\"white\"", LightStraightDown), out _), 30, 30);
        var dim = Px(Render(DiffuseDoc("diffuseConstant=\"0.3\" lighting-color=\"white\"", LightStraightDown), out _), 30, 30);
        Assert.True(bright.R > dim.R + 40);
    }

    [Fact]
    public void Fe_diffuse_lighting_elevation_changes_intensity()
    {
        // On a flat surface the diffuse term is N·L = sin(elevation): high elevation lights it far brighter.
        var high = Px(Render(DiffuseDoc("diffuseConstant=\"1\" lighting-color=\"white\"", "<feDistantLight azimuth=\"0\" elevation=\"90\"/>"), out _), 30, 30);
        var low = Px(Render(DiffuseDoc("diffuseConstant=\"1\" lighting-color=\"white\"", "<feDistantLight azimuth=\"0\" elevation=\"10\"/>"), out _), 30, 30);
        Assert.True(high.R > low.R + 40);
    }

    [Fact]
    public void Fe_specular_lighting_produces_a_lit_highlight()
    {
        // Specular lighting adds light → the lit surface has non-zero (opaque-ish) output where it reflects.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<filter id=\"f\"><feSpecularLighting surfaceScale=\"2\" specularConstant=\"1\" specularExponent=\"4\" lighting-color=\"white\">" +
            "<feDistantLight azimuth=\"0\" elevation=\"90\"/></feSpecularLighting></filter>" +
            "<rect x=\"15\" y=\"15\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 30, 30).A > 80);      // a specular highlight is produced
    }

    [Fact]
    public void Fe_lighting_kernel_unit_length_is_flagged()
    {
        // kernelUnitLength changes the surface-normal sampling grid — not reproduced → flagged (still renders).
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feDiffuseLighting surfaceScale=\"1\" diffuseConstant=\"1\" kernelUnitLength=\"2 2\">" +
            "<feDistantLight azimuth=\"0\" elevation=\"90\"/></feDiffuseLighting></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.True(unsupported);
        Assert.True(Px(info, 20, 20).A > 80);      // flagged but still lit
    }

    [Theory]
    // A zero (or negative) filter-region width/height is an EMPTY region (§15.7.4): the element renders NOTHING
    // — distinct from "no region" (which would leave it unfiltered). Not a diagnostic (valid empty geometry).
    [InlineData("filterUnits=\"userSpaceOnUse\" x=\"10\" y=\"10\" width=\"0\" height=\"20\"")]
    [InlineData("x=\"0%\" y=\"0%\" width=\"-10%\" height=\"50%\"")]
    public void Empty_filter_region_renders_nothing(string regionAttrs)
    {
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            $"<filter id=\"f\" {regionAttrs}><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);                 // an empty region is valid geometry, not unsupported
        Assert.Equal(0, Px(info, 20, 20).A);       // nothing renders (neither the flood NOR the red source)
        Assert.Equal(0, Px(info, 40, 40).A);
    }

    [Fact]
    public void User_space_filter_region_with_omitted_width_uses_the_user_space_default()
    {
        // filterUnits=userSpaceOnUse with x/y/height but NO width: the omitted width takes its §15 default
        // (120% of the viewport), staying in user space rather than falling back to bbox math. y/height honored.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10\" y=\"10\" height=\"20\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"50\" height=\"50\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 40, 20).B > 150);     // inside (x≥10, y∈[10,30]): flooded blue
        Assert.True(Px(info, 70, 20).B > 150);     // x=70 < default width (96) → still inside (NOT bbox-limited to ~60)
        Assert.Equal(0, Px(info, 40, 45).A);       // y=45 > 30 (height honored): outside the region
        Assert.Equal(0, Px(info, 5, 20).A);        // x=5 < 10 (x honored): outside the region
    }

    [Fact]
    public void Unknown_filter_units_value_is_flagged_and_falls_back_to_bounding_box()
    {
        // An unrecognized filterUnits → objectBoundingBox behavior, FLAGGED (CLAUDE.md: no silent semantics).
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" filterUnits=\"garbage\" x=\"0%\" y=\"0%\" width=\"50%\" height=\"50%\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.True(unsupported);                  // unknown units flagged
        Assert.True(Px(info, 20, 20).B > 150);     // still clipped to the bbox top-left quadrant (10..30): blue
        Assert.Equal(0, Px(info, 45, 45).A);       // outside that quadrant: transparent
    }

    [Fact]
    public void User_space_filter_region_resolves_percentage_lengths()
    {
        // filterUnits=userSpaceOnUse with PERCENTAGE x/y/width/height → resolved against the viewport (80×80):
        // 10%,10%,50%,50% → region (8,8)-(48,48).
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10%\" y=\"10%\" width=\"50%\" height=\"50%\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"80\" height=\"80\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 20, 20).B > 150);     // inside (8,8)-(48,48): blue
        Assert.Equal(0, Px(info, 60, 60).A);       // outside: transparent
    }

    [Fact]
    public void Fe_image_preserve_aspect_ratio_slice_covers_and_clips_to_the_region()
    {
        // preserveAspectRatio="xMidYMid slice" on a WIDE 32×8 image in a SQUARE 20×20 region: the image scales
        // to COVER the region (scale 2.5 → 80×20, centered) and the horizontal overflow is cropped by the
        // filter-region clip — it must NOT leak outside the region. (Under meet the top/bottom would letterbox.)
        var png = "data:image/png;base64," + Convert.ToBase64String(SyntheticPng.BuildOpaqueRgb8(32, 8, 0x00, 0x00, 0xFF));
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            $"<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"20\" height=\"20\"><feImage preserveAspectRatio=\"xMidYMid slice\" href=\"{png}\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"80\" height=\"80\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 10, 10).B > 150);     // region center covered
        Assert.True(Px(info, 10, 2).B > 150);      // top band covered too (slice cover, not a meet letterbox)
        Assert.True(Px(info, 18, 10).B > 150);     // near the right edge of the region still covered
        Assert.Equal(0, Px(info, 25, 10).A);       // overflow beyond the region clipped away (no leak)
        Assert.Equal(0, Px(info, 10, 30).A);       // below the region clipped
    }

    [Fact]
    public void Object_bounding_box_region_accepts_unitless_fractions()
    {
        // A UNITLESS objectBoundingBox value is a bbox fraction: width="0.5" == 50% → the top-left quadrant of
        // the bbox (rect 10..50 → region 10..30). Valid, not flagged.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" x=\"0\" y=\"0\" width=\"0.5\" height=\"0.5\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 20, 20).B > 150);     // inside the top-left quadrant (10..30): blue
        Assert.Equal(0, Px(info, 45, 45).A);       // outside: transparent
    }

    [Fact]
    public void Object_bounding_box_region_with_unit_suffixed_length_is_flagged_not_a_bbox_multiplier()
    {
        // Under the default filterUnits=objectBoundingBox a value is a bbox FRACTION, not a length. width="20px"
        // must NOT be stripped to 20 and read as 20× the bbox (which would push the region far outside) — it is
        // FLAGGED and falls back to the §15 default (120%), so the region stays near the bbox.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" x=\"0\" y=\"0\" width=\"20px\" height=\"20px\"><feFlood flood-color=\"blue\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.True(unsupported);                  // a unit-suffixed objectBoundingBox value is flagged
        Assert.True(Px(info, 30, 30).B > 150);     // inside the default region (10..58): blue
        Assert.Equal(0, Px(info, 70, 30).A);       // x=70 is outside the default region — NOT the 20×-bbox bug
    }

    [Fact]
    public void Fe_image_element_reference_renders_the_referenced_subtree()
    {
        // feImage href="#id" referencing an EXISTING element renders that subtree into the filter region (like
        // a <use>), no longer flagged. A small blue rect at (5,5)-(15,15) is rendered + positioned inside the
        // region and REPLACES the red source.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<defs><rect id=\"src\" x=\"5\" y=\"5\" width=\"10\" height=\"10\" fill=\"blue\"/></defs>" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\"><feImage href=\"#src\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 10, 10).B > 150);     // the referenced blue rect, rendered + positioned in-region
        Assert.True(Px(info, 10, 10).R < 100);     // not the red source (feImage REPLACES it)
        Assert.Equal(0, Px(info, 30, 30).A);       // region area the small rect doesn't cover: transparent
    }

    [Fact]
    public void Primitive_subregion_clips_a_flood_to_its_own_rect()
    {
        // A feFlood with an explicit subregion (x/y/width/height) fills ONLY that rect, not the whole filter
        // region (userSpaceOnUse default units).
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feFlood flood-color=\"blue\" x=\"0\" y=\"0\" width=\"20\" height=\"20\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 10, 10).B > 150);     // inside the subregion: blue
        Assert.Equal(0, Px(info, 30, 30).A);       // inside the filter region but OUTSIDE the subregion: transparent
    }

    [Fact]
    public void Primitive_subregion_object_bounding_box_units_clips_to_the_bbox_fraction()
    {
        // primitiveUnits=objectBoundingBox maps the subregion as a bbox fraction (0..0.5 → top-left quadrant).
        // A feFlood has no non-subregion length, so this is fully modeled → NOT flagged.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\" primitiveUnits=\"objectBoundingBox\">" +
            "<feFlood flood-color=\"blue\" x=\"0\" y=\"0\" width=\"0.5\" height=\"0.5\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 10, 10).B > 150);     // bbox top-left quadrant (0..20): blue
        Assert.Equal(0, Px(info, 30, 30).A);       // outside the quadrant: transparent
    }

    [Fact]
    public void Fe_tile_replicates_a_subregion_across_the_filter_region()
    {
        // A feFlood confined to a 10×10 subregion, then feTile → the tile fills the whole 40×40 filter region.
        // Without feTile the flood would only cover its own subregion (the far corner would be transparent).
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feFlood flood-color=\"blue\" x=\"0\" y=\"0\" width=\"10\" height=\"10\" result=\"t\"/>" +
            "<feTile in=\"t\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 5, 5).B > 150);       // tile origin: blue
        Assert.True(Px(info, 35, 35).B > 150);     // far corner filled by tiling (transparent without feTile)
    }

    [Fact]
    public void Primitive_subregion_bounds_a_blur_input_so_outside_content_does_not_bleed_in()
    {
        // PR-251 review [P1] — the subregion restricts a sampling primitive's CALCULATION, not just its output.
        // A blur with a subregion covering ONLY the left half must not sample the RED shape sitting just outside
        // the subregion and bleed its halo across the boundary. (A pure output crop would let the red halo in.)
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"80\" height=\"80\">" +
            "<feGaussianBlur stdDeviation=\"6\" x=\"0\" y=\"0\" width=\"35\" height=\"80\"/></filter>" +
            "<g filter=\"url(#f)\">" +
            "<rect x=\"5\" y=\"30\" width=\"20\" height=\"20\" fill=\"blue\"/>" +   // inside the subregion (x < 35)
            "<rect x=\"36\" y=\"30\" width=\"20\" height=\"20\" fill=\"red\"/>" +   // just OUTSIDE the subregion
            "</g></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 20, 40).B > 80);      // the blurred blue rect (inside the subregion) is present
        Assert.True(Px(info, 30, 40).R < 40);      // NO red bleed near the boundary (red was cropped from the input)
        Assert.Equal(0, Px(info, 50, 40).A);       // outside the subregion entirely: nothing
    }

    [Fact]
    public void Fe_image_scales_into_the_primitive_subregion_not_the_whole_region_then_cropped()
    {
        // PR-251 review [P1] — a data feImage with an explicit subregion (x/y/width/height) must be SCALED +
        // ALIGNED into that subregion, not placed into the full filter region and then cropped. A WIDE 20×10
        // image + xMidYMid meet into a 10×10 subregion → letterboxed to y∈[2.5,7.5] INSIDE the subregion (its
        // center paints). Region-placement-then-crop would land the image at y≥10 → nothing in the subregion.
        var png = "data:image/png;base64," + Convert.ToBase64String(SyntheticPng.BuildOpaqueRgb8(20, 10, 0x00, 0x00, 0xFF));
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            $"<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\"><feImage preserveAspectRatio=\"xMidYMid meet\" x=\"0\" y=\"0\" width=\"10\" height=\"10\" href=\"{png}\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 5, 5).B > 150);       // image center, scaled into the 10×10 subregion: blue
        Assert.Equal(0, Px(info, 5, 1).A);         // letterbox within the subregion (above the fitted image)
        Assert.Equal(0, Px(info, 20, 20).A);       // outside the subregion: transparent
    }

    [Fact]
    public void Fe_tile_destination_is_its_own_subregion()
    {
        // PR-251 review [P2] — feTile with its OWN x/y/width/height tiles into THAT subregion (the destination),
        // not the whole filter region. Content fills the feTile subregion and is transparent outside it.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feFlood flood-color=\"blue\" x=\"0\" y=\"0\" width=\"10\" height=\"10\" result=\"t\"/>" +
            "<feTile in=\"t\" x=\"0\" y=\"0\" width=\"20\" height=\"20\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(Px(info, 15, 15).B > 150);     // inside the feTile subregion (0..20): tiled blue
        Assert.Equal(0, Px(info, 30, 30).A);       // inside the filter region but OUTSIDE the feTile subregion
    }

    [Fact]
    public void Primitive_units_object_bounding_box_remaps_offset_to_bbox_units()
    {
        // primitiveUnits=objectBoundingBox: feOffset dx=1 on a 10-wide bbox → a 10px shift (NOT a 1px user-unit
        // shift). The blue rect (x=5..15) shifts right by 10 → x=15..25. Not flagged (the offset remap is modeled).
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\" primitiveUnits=\"objectBoundingBox\">" +
            "<feOffset dx=\"1\" dy=\"0\"/></filter>" +
            "<rect x=\"5\" y=\"15\" width=\"10\" height=\"10\" fill=\"blue\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);
        Assert.Equal(0, Px(info, 8, 20).A);        // original position now empty (shifted away — proves bbox units)
        Assert.True(Px(info, 20, 20).B > 150);     // shifted right ~10px into x=15..25
    }

    [Fact]
    public void Fill_paint_input_floods_the_element_paint()
    {
        // FillPaint is the element's fill as an infinite plane — a modeled input (no longer flagged). Merging
        // FillPaint under SourceGraphic paints the whole filter region blue behind the shape.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feMerge><feMergeNode in=\"FillPaint\"/><feMergeNode in=\"SourceGraphic\"/></feMerge></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"10\" height=\"10\" fill=\"blue\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);                 // FillPaint no longer flagged
        Assert.True(Px(info, 30, 30).B > 120);     // FillPaint floods the whole region blue (beyond the shape)
    }

    [Fact]
    public void Background_image_input_is_transparent_not_flagged()
    {
        // BackgroundImage is the (empty) accumulated background → transparent, and no longer flagged
        // (enable-background is deprecated). The source composited over a transparent background is unchanged.
        var info = Render(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<filter id=\"f\"><feMerge><feMergeNode in=\"BackgroundImage\"/><feMergeNode in=\"SourceGraphic\"/></feMerge></filter>" +
            "<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"blue\" filter=\"url(#f)\"/></svg>", out var unsupported);
        Assert.False(unsupported);                 // BackgroundImage no longer flagged
        Assert.True(Px(info, 20, 20).B > 150);     // the source still renders over the transparent background
    }

    [Fact]
    public void Fe_turbulence_stitch_tiles_renders_and_differs_from_no_stitch()
    {
        // stitchTiles="stitch" seamlessly tiles the noise over the primitive subregion (Skia's stitched Perlin
        // generator rounds baseFrequency to an integer cycle count over the tile). baseFrequency 0.07 × 40px =
        // 2.8 cycles → stitching adjusts it → the output differs from noStitch. Both render noise, neither flags.
        string Doc(string stitch) =>
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            $"<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\"><feTurbulence type=\"fractalNoise\" baseFrequency=\"0.07\" numOctaves=\"2\" seed=\"1\" stitchTiles=\"{stitch}\"/></filter>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" filter=\"url(#f)\"/></svg>";
        var stitched = Render(Doc("stitch"), out var u1);
        var noStitch = Render(Doc("noStitch"), out var u2);
        Assert.False(u1);
        Assert.False(u2);
        // The stitch adjustment changes the noise field → at least one pixel differs from noStitch.
        var differs = false;
        for (var y = 0; y < 40 && !differs; y++)
            for (var x = 0; x < 40; x++)
            {
                var a = Px(stitched, x, y);
                var b = Px(noStitch, x, y);
                if (Math.Abs(a.R - b.R) > 8 || Math.Abs(a.G - b.G) > 8 || Math.Abs(a.B - b.B) > 8) { differs = true; break; }
            }
        Assert.True(differs);
        // Stitched output is still noise (varied), not a flat fill.
        var p = Px(stitched, 10, 10);
        var q = Px(stitched, 30, 30);
        Assert.True(p.R != q.R || p.G != q.G || p.B != q.B);
    }
}
