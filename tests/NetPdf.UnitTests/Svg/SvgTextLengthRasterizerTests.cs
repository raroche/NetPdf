// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 11 — SVG text <c>textLength</c> / <c>lengthAdjust</c> (§10.5): the text chunk's
/// advance is fitted to a target, either by distributing extra inter-glyph spacing (<c>spacing</c>, default) or
/// by scaling the glyphs horizontally (<c>spacingAndGlyphs</c>). Font-agnostic: compares the painted ink's
/// horizontal extent.</summary>
public sealed class SvgTextLengthRasterizerTests
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

    private static NetPdf.Pdf.Images.RasterImageInfo Render(string body, out bool unsupported)
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" + body + "</svg>"), out unsupported);
        Assert.NotNull(info);
        return info!;
    }

    [Fact]
    public void Text_length_spacing_stretches_the_advance_to_the_target()
    {
        var natural = Render("<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\">Hi</text>", out _);
        var stretched = Render("<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\" textLength=\"90\" lengthAdjust=\"spacing\">Hi</text>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(MaxInkX(stretched) > MaxInkX(natural) + 15);   // markedly wider than the natural advance
        Assert.InRange(MaxInkX(stretched), 78, 97);                // last ink near x = 5 + 90 (minus right bearing)
    }

    [Fact]
    public void Text_length_spacing_and_glyphs_reaches_the_target_too()
    {
        var natural = Render("<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\">Hi</text>", out _);
        var scaled = Render("<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\" textLength=\"90\" lengthAdjust=\"spacingAndGlyphs\">Hi</text>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(MaxInkX(scaled) > MaxInkX(natural) + 15);
        Assert.InRange(MaxInkX(scaled), 78, 97);
    }

    [Fact]
    public void Text_length_shorter_than_natural_compresses_the_chunk()
    {
        var natural = Render("<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\">Width</text>", out _);
        var compressed = Render("<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\" textLength=\"24\">Width</text>", out var unsupported);
        Assert.False(unsupported);
        Assert.True(MaxInkX(compressed) < MaxInkX(natural) - 10); // squeezed into a shorter advance
        Assert.InRange(MaxInkX(compressed), 20, 38);              // last ink near x = 5 + 24
    }

    [Fact]
    public void Text_length_across_multiple_chunks_scales_the_whole_text_to_the_target()
    {
        // textLength over a text with an absolute-x tspan (a second chunk) is fitted by a whole-text horizontal
        // scale about the start x. A target of 60 (narrower than the natural span reaching x≈100) pulls the
        // rightmost ink in toward x = 5 + 60 = 65. Not flagged.
        var info = Render(
            "<text x=\"5\" y=\"30\" font-size=\"20\" fill=\"black\" textLength=\"60\">Hi<tspan x=\"80\">Yo</tspan></text>",
            out var unsupported);
        Assert.False(unsupported);
        Assert.InRange(MaxInkX(info), 40, 72);   // rightmost ink near x = 5 + 60 (was ≈ 80+ naturally)
    }
}
