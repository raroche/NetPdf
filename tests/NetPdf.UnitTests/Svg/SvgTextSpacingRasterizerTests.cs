// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 5 — SVG text <c>letter-spacing</c> / <c>word-spacing</c> advance adjustments
/// (font-agnostic: compares the painted ink's horizontal extent).</summary>
public sealed class SvgTextSpacingRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static int MaxInkX(NetPdf.Pdf.Images.RasterImageInfo info)
    {
        var maxX = -1;
        for (var y = 0; y < info.Height; y++)
            for (var x = 0; x < info.Width; x++)
                if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40 && x > maxX) maxX = x;
        return maxX;
    }

    [Fact]
    public void Letter_spacing_pushes_later_glyphs_right()
    {
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\">Hill</text></svg>"), out _);
        var spaced = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\" letter-spacing=\"10\">Hill</text></svg>"), out var unsupported);
        Assert.NotNull(plain);
        Assert.NotNull(spaced);
        Assert.False(unsupported);
        Assert.True(MaxInkX(spaced!) > MaxInkX(plain!) + 20);   // 3 gaps × 10px push the tail right
    }

    [Fact]
    public void Word_spacing_widens_across_a_space()
    {
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\">A B</text></svg>"), out _);
        var spaced = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\" word-spacing=\"30\">A B</text></svg>"), out _);
        Assert.NotNull(plain);
        Assert.NotNull(spaced);
        Assert.True(MaxInkX(spaced!) > MaxInkX(plain!) + 20);   // the space gained ~30px
    }

    [Fact]
    public void Tspan_letter_spacing_overrides_the_text()
    {
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\"><tspan>Hill</tspan></text></svg>"), out _);
        var spaced = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\"><tspan letter-spacing=\"8\">Hill</tspan></text></svg>"), out _);
        Assert.NotNull(plain);
        Assert.NotNull(spaced);
        Assert.True(MaxInkX(spaced!) > MaxInkX(plain!) + 14);
    }
}
