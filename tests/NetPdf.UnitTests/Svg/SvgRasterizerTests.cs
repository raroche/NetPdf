// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 1 (PR 5) — unit tests for <see cref="SvgRasterizer"/>: format sniffing,
/// intrinsic sizing, and that shapes actually paint pixels (rect / circle / path) onto the RGBA raster.</summary>
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
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\"></svg>"));
        Assert.NotNull(info);
        Assert.Equal(40, info!.Width);
        Assert.Equal(30, info.Height);
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public void Rect_fill_paints_the_fill_color()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#ff0000\"/></svg>"));
        Assert.NotNull(info);
        // Pixel at (20,20): RGBA. The full-bleed red rect → r≈255, g/b≈0, opaque.
        var idx = (20 * info!.Width + 20) * 4;
        Assert.True(info.PixelBytes[idx] > 200);       // R
        Assert.True(info.PixelBytes[idx + 1] < 60);    // G
        Assert.True(info.PixelBytes[idx + 2] < 60);    // B
        Assert.True(info.PixelBytes[idx + 3] > 200);   // A (opaque inside the rect)
    }

    [Fact]
    public void Transparent_outside_shapes()
    {
        // A small circle in the corner leaves the opposite corner transparent (the SMask carries it).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<circle cx=\"5\" cy=\"5\" r=\"3\" fill=\"blue\"/></svg>"));
        Assert.NotNull(info);
        var far = (35 * info!.Width + 35) * 4;
        Assert.Equal(0, info.PixelBytes[far + 3]);     // fully transparent away from the circle
    }

    [Fact]
    public void Path_data_renders()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\">" +
            "<path d=\"M0 0 L20 0 L20 20 Z\" fill=\"green\"/></svg>"));
        Assert.NotNull(info);
        var idx = (5 * info!.Width + 15) * 4;          // inside the triangle (upper-right)
        Assert.True(info.PixelBytes[idx + 3] > 200);   // painted
    }

    [Fact]
    public void Malformed_input_returns_null()
    {
        Assert.Null(SvgRasterizer.TryRender(Svg("<svg width=\"10\" height=\"10\"><rect"))); // truncated XML
    }
}
