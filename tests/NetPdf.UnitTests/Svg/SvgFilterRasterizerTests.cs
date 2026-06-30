// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG — <c>filter="url(#id)"</c> element references: primitives compose into a Skia image
/// filter over the element subtree. SVG part 7 models a filter GRAPH — <c>feFlood</c>/<c>feMerge</c>/
/// <c>feComposite</c>/<c>feBlend</c> with <c>in</c>/<c>in2</c>/named-<c>result</c> routing are SUPPORTED; the
/// composited result is clipped to the default filter region; only the primary (reachable) tree contributes.
/// SVG part 8 adds <c>feMorphology</c> / <c>feComponentTransfer</c> / <c>feDisplacementMap</c> /
/// <c>feConvolveMatrix</c> / <c>feTurbulence</c>. A non-filter target, an EXPLICIT filter region / primitive
/// subregion / <c>*Units</c>, the remaining primitives (<c>feImage</c>/<c>feTile</c>/lighting), and unmodeled
/// inputs (<c>BackgroundImage</c>/…) are flagged.</summary>
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
}
