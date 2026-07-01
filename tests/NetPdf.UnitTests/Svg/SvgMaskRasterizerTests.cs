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
    public void Object_bounding_box_mask_bbox_uses_the_resolved_font_size_for_em_geometry()
    {
        // PR-241 review [P2] — the masked element's bbox must use its RESOLVED font-size (10), so a 2em
        // child rect is 20px wide. A 0.5-wide objectBoundingBox white mask reveals x in [0,10). With the old
        // SvgStyle.Initial (font-size 16) the bbox would be 32 wide and reveal x in [0,16) → x=13 would show.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" maskContentUnits=\"objectBoundingBox\"><rect x=\"0\" y=\"0\" width=\"0.5\" height=\"1\" fill=\"white\"/></mask>" +
            "<g font-size=\"10\" fill=\"red\" mask=\"url(#m)\"><rect x=\"0\" y=\"0\" width=\"2em\" height=\"2em\"/></g></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 5, 10).A > 200);     // x=5 revealed (inside [0,10))
        Assert.Equal(0, Px(info!, 13, 10).A);      // x=13 masked out → proves bbox width 20, not 32
    }

    [Fact]
    public void Object_bounding_box_mask_on_a_target_without_shape_geometry_is_flagged_and_unmasked()
    {
        // PR-241 review [P2] — a <text> target has no basic-shape bbox, so an objectBoundingBox mask can't
        // map; flag it and leave the element UNMASKED (predictable) rather than masking it in user space.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"30\">" +
            "<mask id=\"m\" maskContentUnits=\"objectBoundingBox\"><rect x=\"0\" y=\"0\" width=\"1\" height=\"1\" fill=\"white\"/></mask>" +
            "<text x=\"5\" y=\"20\" font-size=\"20\" fill=\"black\" mask=\"url(#m)\">Hi</text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        var ink = 0;
        for (var i = 3; i < info!.PixelBytes.Length; i += 4) if (info.PixelBytes[i] > 40) ink++;
        Assert.True(ink > 0);                      // text rendered, unmasked
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

    [Fact]
    public void Alpha_mask_type_uses_the_mask_alpha_not_its_luminance()
    {
        // SVG part 9 — `mask-type="alpha"`: a BLACK rect at 0.5 opacity has LUMINANCE 0 but ALPHA 0.5. A
        // luminance mask would HIDE the element; an alpha mask shows it at ≈ half alpha.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" mask-type=\"alpha\"><rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"black\" fill-opacity=\"0.5\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.InRange(Px(info!, 20, 20).A, 100, 160);   // ≈ 128 (the mask's alpha, not its 0 luminance)
    }

    [Fact]
    public void Alpha_mask_type_from_inline_style_uses_the_mask_alpha()
    {
        // `mask-type` is a presentation property — an inline `style="mask-type:alpha"` must work like the
        // attribute (SVG §6.4, the style declaration wins). Same black-0.5 mask: alpha → the element shows.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" style=\"mask-type:alpha\"><rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"black\" fill-opacity=\"0.5\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.InRange(Px(info!, 20, 20).A, 100, 160);   // ≈ 128 (the mask's alpha via inline style)
    }

    [Fact]
    public void Luminance_mask_type_default_hides_a_black_translucent_mask()
    {
        // The DEFAULT (luminance) on the same black-0.5 mask → luminance 0 → the element is HIDDEN
        // (byte-identical to before the mask-type change).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\"><rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"black\" fill-opacity=\"0.5\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.Equal(0, Px(info!, 20, 20).A);            // luminance 0 → hidden
    }

    [Fact]
    public void Mask_units_region_clips_the_element_to_the_region()
    {
        // The mask REGION (maskUnits x/y/width/height, §14.4) bounds where the mask applies: outside it the
        // mask value is 0 → the element is clipped away. A white mask (fully shows the element) with a region
        // of only (0,0)-(20,20) → the element shows only in that quadrant.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" maskUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"white\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(Px(info!, 10, 10).R > 150);          // inside the region: shown (red)
        Assert.Equal(0, Px(info!, 30, 30).A);            // outside the mask region: clipped away
    }

    [Fact]
    public void Object_bounding_box_mask_region_unit_suffixed_value_is_flagged()
    {
        // A unitless/% objectBoundingBox mask-region value is a bbox fraction; a unit-suffixed one (width="10px")
        // must not be reinterpreted as a 10×-bbox multiplier → flagged + the §14 default region used.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" width=\"10px\"><rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"white\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);                        // unit-suffixed objectBoundingBox value flagged
        Assert.True(Px(info!, 20, 20).R > 150);          // default region (bbox ±10%) → element still shown
    }

    [Fact]
    public void Unknown_mask_units_value_is_flagged()
    {
        // An unknown maskUnits value → objectBoundingBox behavior, FLAGGED (parity with filterUnits).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<mask id=\"m\" maskUnits=\"garbage\"><rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"white\"/></mask>" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" mask=\"url(#m)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        Assert.True(Px(info!, 20, 20).R > 150);          // default (objectBoundingBox) region → element shown
    }
}
