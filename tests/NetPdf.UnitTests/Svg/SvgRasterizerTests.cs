// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
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
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<text x=\"0\" y=\"10\">hi</text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.True(unsupported);
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
