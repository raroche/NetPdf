// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 2 (PR 7) — <c>&lt;text&gt;</c> rendering: a text run paints pixels at its
/// baseline, is no longer flagged unsupported, honors <c>text-anchor</c>, and supports a gradient fill.
/// Assertions are font-agnostic (count painted pixels / compare regions) so they hold regardless of the
/// system default font.</summary>
public sealed class SvgTextRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static int PaintedPixels(NetPdf.Pdf.Images.RasterImageInfo info)
    {
        var n = 0;
        for (var i = 3; i < info.PixelBytes.Length; i += 4)
            if (info.PixelBytes[i] > 40) n++;
        return n;
    }

    /// <summary>Painted pixels whose horizontal position is in [x0, x1).</summary>
    private static int PaintedInBand(NetPdf.Pdf.Images.RasterImageInfo info, int x0, int x1)
    {
        var n = 0;
        for (var y = 0; y < info.Height; y++)
            for (var x = x0; x < x1 && x < info.Width; x++)
                if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40) n++;
        return n;
    }

    [Fact]
    public void Text_paints_pixels_and_is_not_flagged()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\">" +
            "<text x=\"5\" y=\"28\" font-size=\"24\" fill=\"black\">Hello</text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);                 // text is supported now
        Assert.True(PaintedPixels(info!) > 40);    // glyphs drew ink
    }

    [Fact]
    public void Empty_text_draws_nothing()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            "<text x=\"5\" y=\"15\">   </text></svg>"), out _);
        Assert.NotNull(info);
        Assert.Equal(0, PaintedPixels(info!));     // whitespace-only collapses to nothing
    }

    [Fact]
    public void Text_anchor_end_shifts_left_of_the_anchor()
    {
        // With text-anchor:end at x=110, all ink must sit LEFT of x=110 (in the left ~⅘ of a 120px canvas).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\">" +
            "<text x=\"110\" y=\"28\" font-size=\"24\" text-anchor=\"end\" fill=\"black\">Hello</text></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(PaintedPixels(info!) > 40);
        Assert.Equal(0, PaintedInBand(info!, 111, 120));    // nothing to the right of the anchor
        Assert.True(PaintedInBand(info!, 0, 110) > 40);     // ink to the left
    }

    [Fact]
    public void Text_with_gradient_fill_paints_a_gradient()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"160\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<text x=\"5\" y=\"30\" font-size=\"30\" fill=\"url(#g)\">WWWWWW</text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        // Average the painted ink's red on the left third vs the right third — left should be redder.
        long lR = 0, lN = 0, rR = 0, rN = 0, lB = 0, rB = 0;
        for (var y = 0; y < info!.Height; y++)
            for (var x = 0; x < info.Width; x++)
            {
                var i = (y * info.Width + x) * 4;
                if (info.PixelBytes[i + 3] <= 40) continue;
                if (x < info.Width / 3) { lR += info.PixelBytes[i]; lB += info.PixelBytes[i + 2]; lN++; }
                else if (x > info.Width * 2 / 3) { rR += info.PixelBytes[i]; rB += info.PixelBytes[i + 2]; rN++; }
            }
        Assert.True(lN > 0 && rN > 0);
        Assert.True(lR / lN > rR / rN);            // left ink redder than right
        Assert.True(rB / rN > lB / lN);            // right ink bluer than left
    }

    [Fact]
    public void Tspan_runs_are_laid_out_after_the_text()
    {
        // Two tspans in sequence both render → more ink than a single short run.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"40\">" +
            "<text x=\"5\" y=\"28\" font-size=\"24\" fill=\"black\">" +
            "<tspan>AB</tspan><tspan fill=\"black\">CD</tspan></text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(PaintedPixels(info!) > 60);
        Assert.True(PaintedInBand(info!, 60, 200) > 0);    // the second run extends to the right
    }
}
