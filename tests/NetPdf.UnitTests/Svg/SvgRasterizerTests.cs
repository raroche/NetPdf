// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Svg;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 1 (PR 5) — unit tests for <see cref="SvgRasterizer"/>: format sniffing,
/// intrinsic sizing, shapes painting pixels, the DoS depth/element guards (PR-230 review [P1]), and the
/// unsupported-feature flag (PR-230 [P2/P3]).</summary>
public sealed class SvgRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void LooksLikeSvg_detects_svg_and_xml_prolog()
    {
        Assert.True(SvgRasterizer.LooksLikeSvg(Svg("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")));
        Assert.True(SvgRasterizer.LooksLikeSvg(Svg("<?xml version=\"1.0\"?><svg></svg>")));
        Assert.False(SvgRasterizer.LooksLikeSvg(Svg("\x89PNG\r\n")));
        Assert.False(SvgRasterizer.LooksLikeSvg(Svg("<html></html>")));
    }

    [Fact]
    public void Intrinsic_size_from_width_height_attrs()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.Equal(40, info!.Width);
        Assert.Equal(30, info.Height);
        Assert.True(info.HasAlpha);
        Assert.False(unsupported);
    }

    [Fact]
    public void Rect_fill_paints_the_fill_color()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\"/></svg>"), out _);
        Assert.NotNull(info);
        var idx = (20 * info!.Width + 20) * 4; // RGBA at (20,20) — inside the full-bleed red rect
        Assert.True(info.PixelBytes[idx] > 200);       // R
        Assert.True(info.PixelBytes[idx + 1] < 60);    // G
        Assert.True(info.PixelBytes[idx + 2] < 60);    // B
        Assert.True(info.PixelBytes[idx + 3] > 200);   // A (opaque inside the rect)
    }

    [Fact]
    public void Transparent_outside_shapes()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<circle cx=\"5\" cy=\"5\" r=\"3\" fill=\"blue\"/></svg>"), out _);
        Assert.NotNull(info);
        var far = (35 * info!.Width + 35) * 4;
        Assert.Equal(0, info.PixelBytes[far + 3]);     // fully transparent away from the circle
    }

    [Fact]
    public void Path_data_renders()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<path d=\"M0 0 L20 0 L20 20 Z\" fill=\"green\"/></svg>"), out _);
        Assert.NotNull(info);
        var idx = (5 * info!.Width + 15) * 4;          // inside the triangle (upper-right)
        Assert.True(info.PixelBytes[idx + 3] > 200);   // painted
    }

    [Fact]
    public void Malformed_input_returns_null()
    {
        Assert.Null(SvgRasterizer.TryRender(Svg("<svg width=\"10\" height=\"10\"><rect"), out _)); // truncated
    }

    [Fact]
    public void Deeply_nested_groups_do_not_crash_and_are_flagged()
    {
        // PR-230 review [P1] — thousands of nested <g> must not StackOverflow (uncatchable). The depth cap
        // truncates + flags; the render still returns (no crash).
        var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">");
        for (var i = 0; i < 5000; i++) sb.Append("<g>");
        sb.Append("<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\"/>");
        for (var i = 0; i < 5000; i++) sb.Append("</g>");
        sb.Append("</svg>");
        var info = SvgRasterizer.TryRender(Svg(sb.ToString()), out var unsupported);
        Assert.NotNull(info);          // no crash
        Assert.True(unsupported);      // depth-truncated → flagged
    }

    [Fact]
    public void Unsupported_element_sets_the_flag()
    {
        // <image> (an external/embedded raster reference) is still out of scope for the renderer; <text> is
        // now supported (PR 7) so it no longer flags.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<image x=\"0\" y=\"0\" width=\"20\" height=\"20\" href=\"x.png\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }

    // ---- SVG part 3 task 1: stroke-dasharray + caps/joins ----

    private static (int Painted, int Gaps) ScanLineAlpha(NetPdf.Pdf.Images.RasterImageInfo info, int y)
    {
        int painted = 0, gaps = 0;
        for (var x = 1; x < info.Width - 1; x++)
        {
            var a = info.PixelBytes[(y * info.Width + x) * 4 + 3];
            if (a > 150) painted++;
            else if (a < 40) gaps++;
        }
        return (painted, gaps);
    }

    [Fact]
    public void Stroke_dasharray_creates_gaps_along_the_line()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            "<line x1=\"0\" y1=\"10\" x2=\"60\" y2=\"10\" stroke=\"black\" stroke-width=\"6\" stroke-dasharray=\"6 6\"/></svg>"),
            out _);
        Assert.NotNull(info);
        var (painted, gaps) = ScanLineAlpha(info!, 10);
        Assert.True(painted > 0, "dash segments painted");
        Assert.True(gaps > 0, "gaps between dashes");
    }

    [Fact]
    public void Solid_stroke_has_no_interior_gaps()
    {
        // The control: the same line WITHOUT a dasharray is continuous (no transparent gaps along it).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            "<line x1=\"0\" y1=\"10\" x2=\"60\" y2=\"10\" stroke=\"black\" stroke-width=\"6\"/></svg>"),
            out _);
        Assert.NotNull(info);
        var (painted, gaps) = ScanLineAlpha(info!, 10);
        Assert.True(painted > 40);
        Assert.Equal(0, gaps);
    }

    [Fact]
    public void Stroke_dasharray_none_is_solid()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"20\">" +
            "<line x1=\"0\" y1=\"10\" x2=\"60\" y2=\"10\" stroke=\"black\" stroke-width=\"6\" stroke-dasharray=\"none\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.Equal(0, ScanLineAlpha(info!, 10).Gaps);
    }

    // ---- SVG part 3 task 2: <image> (data: URI) ----

    private static string PngDataUri(int w, int h) =>
        "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(w, h));

    [Fact]
    public void Image_with_a_data_uri_renders()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            $"<image x=\"0\" y=\"0\" width=\"40\" height=\"40\" preserveAspectRatio=\"none\" href=\"{PngDataUri(16, 16)}\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);                       // a data: image is now supported
        Assert.True(info!.PixelBytes[(20 * info.Width + 20) * 4 + 3] > 200); // the image painted (opaque)
    }

    [Fact]
    public void Image_preserves_aspect_ratio_by_default_letterboxing()
    {
        // A 16×16 image into a 40×20 box (meet) fits to height 20, width 20, centered at x∈[10,30) →
        // the left/right strips stay transparent (letterbox), the center is painted.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            $"<image x=\"0\" y=\"0\" width=\"40\" height=\"20\" href=\"{PngDataUri(16, 16)}\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.Equal(0, info!.PixelBytes[(10 * info.Width + 2) * 4 + 3]);   // left letterbox: transparent
        Assert.True(info.PixelBytes[(10 * info.Width + 20) * 4 + 3] > 200); // center: painted
    }

    [Fact]
    public void Image_with_an_external_href_is_unsupported()
    {
        // The renderer never fetches — an http/relative href is not rendered (flagged).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<image x=\"0\" y=\"0\" width=\"20\" height=\"20\" href=\"https://example.com/x.png\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
    }

    // ---- SVG part 3 task 3: % / em lengths ----

    [Fact]
    public void Percentage_width_resolves_against_the_viewport()
    {
        // width="50%" of a 40-wide viewport → 20px → the rect covers x∈[0,20): painted at 10, empty at 30.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<rect x=\"0\" y=\"0\" width=\"50%\" height=\"100%\" fill=\"red\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(info!.PixelBytes[(20 * info.Width + 10) * 4 + 3] > 200); // inside the left half
        Assert.Equal(0, info.PixelBytes[(20 * info.Width + 30) * 4 + 3]);    // past 50% → transparent
    }

    [Fact]
    public void Em_length_resolves_against_the_font_size()
    {
        // font-size:10 → 3em = 30px wide rect over a 40px box: painted at x=10, transparent at x=35.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\" font-size=\"10\">" +
            "<rect x=\"0\" y=\"0\" width=\"3em\" height=\"40\" fill=\"red\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(info!.PixelBytes[(20 * info.Width + 10) * 4 + 3] > 200); // inside the 30px-wide rect
        Assert.Equal(0, info.PixelBytes[(20 * info.Width + 35) * 4 + 3]);    // past 30px → transparent
    }

    // ---- SVG part 3 task 4: element / group opacity ----

    [Fact]
    public void Element_opacity_makes_the_fill_semi_transparent()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" opacity=\"0.5\"/></svg>"), out _);
        Assert.NotNull(info);
        var a = info!.PixelBytes[(20 * info.Width + 20) * 4 + 3];
        Assert.InRange(a, 110, 145); // ~50% alpha
    }

    [Fact]
    public void Opacity_zero_renders_nothing()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"red\" opacity=\"0\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.Equal(0, info!.PixelBytes[(20 * info.Width + 20) * 4 + 3]);
    }

    [Fact]
    public void Group_opacity_composites_the_subtree_once_not_per_element()
    {
        // Two opaque overlapping rects inside a group at 0.5: the OVERLAP is ~50% (the group composites as
        // one layer), NOT ~75% (which per-element opacity would give). Proves the SaveLayer group.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<g opacity=\"0.5\">" +
            "<rect x=\"0\" y=\"0\" width=\"30\" height=\"30\" fill=\"red\"/>" +
            "<rect x=\"10\" y=\"10\" width=\"30\" height=\"30\" fill=\"red\"/>" +
            "</g></svg>"), out _);
        Assert.NotNull(info);
        var a = info!.PixelBytes[(20 * info.Width + 20) * 4 + 3]; // (20,20) is in BOTH rects
        Assert.InRange(a, 110, 145); // ~50%, not ~75% (≈191)
    }

    // ---- SVG part 3 task 5: nested <svg> viewport ----

    [Fact]
    public void Nested_svg_clips_content_to_its_viewport()
    {
        // A nested <svg> at (10,10) size 15×15 holds an oversized rect; it's CLIPPED to [10,25).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<svg x=\"10\" y=\"10\" width=\"15\" height=\"15\">" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\" fill=\"red\"/></svg></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(info!.PixelBytes[(15 * info.Width + 15) * 4 + 3] > 200); // inside the nested viewport
        Assert.Equal(0, info.PixelBytes[(30 * info.Width + 30) * 4 + 3]);    // clipped away outside it
    }

    [Fact]
    public void Nested_svg_viewbox_scales_content_to_fit()
    {
        // A 10×10 viewBox scaled into a 40×40 nested svg: the 10-unit rect fills the whole 40px box.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<svg x=\"0\" y=\"0\" width=\"40\" height=\"40\" viewBox=\"0 0 10 10\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></svg></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(info!.PixelBytes[(35 * info.Width + 35) * 4 + 3] > 200); // scaled to fill (else empty here)
    }

    [Fact]
    public void Title_desc_metadata_are_not_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<title>t</title><desc>d</desc><rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"red\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);     // metadata elements are legitimately ignorable
    }

    [Fact]
    public void Gradient_paint_server_fill_is_transparent_not_black_and_flagged()
    {
        // PR-230 review [P2/P3] — fill="url(#grad)" we can't resolve must NOT paint inherited/default black;
        // it paints transparent (so a gradient logo doesn't become a black blob) + is flagged.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<rect x=\"0\" y=\"0\" width=\"20\" height=\"20\" fill=\"url(#grad)\"/></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
        var idx = (10 * info!.Width + 10) * 4;
        Assert.Equal(0, info.PixelBytes[idx + 3]);     // transparent, not opaque black
    }
}
