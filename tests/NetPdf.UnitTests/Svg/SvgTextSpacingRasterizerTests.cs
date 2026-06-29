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

    private static double InkCentroidX(NetPdf.Pdf.Images.RasterImageInfo info)
    {
        long sum = 0, n = 0;
        for (var y = 0; y < info.Height; y++)
            for (var x = 0; x < info.Width; x++)
                if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40) { sum += x; n++; }
        return n == 0 ? 0 : (double)sum / n;
    }

    [Fact]
    public void Letter_spacing_does_not_add_a_trailing_advance_for_anchored_text()
    {
        // PR-243 review [P2] — letter-spacing applies BETWEEN glyphs only. A single-char anchored run has no
        // gaps, so letter-spacing must NOT shift its centered position (the old trailing-spacing bug moved it left).
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\">" +
            "<text x=\"50\" y=\"28\" font-size=\"20\" text-anchor=\"middle\">X</text></svg>"), out _);
        var spaced = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\">" +
            "<text x=\"50\" y=\"28\" font-size=\"20\" text-anchor=\"middle\" letter-spacing=\"30\">X</text></svg>"), out _);
        Assert.NotNull(plain);
        Assert.NotNull(spaced);
        Assert.True(System.Math.Abs(InkCentroidX(spaced!) - InkCentroidX(plain!)) < 4); // same center, not shifted
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
