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
    public void Text_x_percentage_resolves_against_the_viewport_width()
    {
        // SVG part 4 — x="50%" in a 100-wide viewport → the run starts at x=50, so (text-anchor:start)
        // all ink sits in the right half.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\">" +
            "<text x=\"50%\" y=\"28\" font-size=\"20\">Hi</text></svg>"), out _);
        Assert.NotNull(info);
        Assert.Equal(0, PaintedInBand(info!, 0, 45));      // nothing left of ~50% (allow AA slack)
        Assert.True(PaintedInBand(info!, 50, 100) > 0);    // ink to the right of the 50% start
    }

    [Fact]
    public void Text_x_em_resolves_against_the_font_size()
    {
        // x="2em" at font-size 10 → x=20; nothing renders to the left of it.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"30\">" +
            "<text x=\"2em\" y=\"20\" font-size=\"10\">Hi</text></svg>"), out _);
        Assert.NotNull(info);
        Assert.Equal(0, PaintedInBand(info!, 0, 18));      // nothing left of x≈20 (2em)
        Assert.True(PaintedInBand(info!, 18, 100) > 0);
    }

    [Fact]
    public void Tspan_dx_em_shifts_by_the_font_size()
    {
        // A tspan with dx="3em" at font-size 10 shifts the run 30px right of where it would otherwise sit.
        var with = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"30\">" +
            "<text x=\"5\" y=\"20\" font-size=\"10\"><tspan dx=\"3em\">Hi</tspan></text></svg>"), out _);
        var without = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"30\">" +
            "<text x=\"5\" y=\"20\" font-size=\"10\"><tspan>Hi</tspan></text></svg>"), out _);
        Assert.NotNull(with);
        Assert.NotNull(without);
        // The shifted run's leftmost ink is well right of the unshifted run's.
        int FirstInkX(NetPdf.Pdf.Images.RasterImageInfo info)
        {
            for (var x = 0; x < info.Width; x++)
                if (PaintedInBand(info, x, x + 1) > 0) return x;
            return info.Width;
        }
        Assert.True(FirstInkX(with!) >= FirstInkX(without!) + 25);   // ≈ +30px (3em)
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

    private static bool HasColored(NetPdf.Pdf.Images.RasterImageInfo info, System.Func<byte, byte, byte, bool> pred)
    {
        for (var i = 0; i < info.PixelBytes.Length; i += 4)
            if (info.PixelBytes[i + 3] > 120 && pred(info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2])) return true;
        return false;
    }

    [Fact]
    public void Tspan_stroke_only_run_is_painted()
    {
        // PR-231 review [P2] — a fill:none + stroke:red tspan must still paint (stroke was unimplemented).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"30\">" +
            "<tspan fill=\"none\" stroke=\"red\" stroke-width=\"2\">OOO</tspan></text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(PaintedPixels(info!) > 20);
        Assert.True(HasColored(info!, (r, g, b) => r > 150 && g < 90 && b < 90));   // red stroke ink
    }

    [Fact]
    public void Tspan_gradient_stroke_is_painted()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"40\">" +
            "<linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient>" +
            "<text x=\"5\" y=\"30\" font-size=\"30\">" +
            "<tspan fill=\"none\" stroke=\"url(#g)\" stroke-width=\"2\">OOO</tspan></text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(PaintedPixels(info!) > 20);
    }

    [Fact]
    public void Currentcolor_fill_resolves_to_the_color_property()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\">" +
            "<text x=\"5\" y=\"30\" font-size=\"30\" color=\"#00ff00\" fill=\"currentColor\">Hi</text></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(HasColored(info!, (r, g, b) => g > 150 && r < 90 && b < 90));   // green, not black
    }

    [Fact]
    public void Text_anchor_is_resolved_per_absolute_positioned_chunk()
    {
        // PR-231 review [P2/P3] — two centered tspans with their own x each center on THAT x, not on the
        // flattened sequence. With per-chunk middle anchoring there is ink on BOTH sides of each center
        // (30 and 90); the old single-sequence behavior left-aligned each tspan at its x (no ink to the left).
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"130\" height=\"30\">" +
            "<text y=\"22\" font-size=\"16\" text-anchor=\"middle\" fill=\"black\">" +
            "<tspan x=\"30\">AA</tspan><tspan x=\"90\">BB</tspan></text></svg>"), out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);
        Assert.True(PaintedInBand(info!, 10, 30) > 0);    // left half of the first centered label
        Assert.True(PaintedInBand(info!, 30, 50) > 0);    // right half of the first centered label
        Assert.True(PaintedInBand(info!, 70, 90) > 0);    // left half of the second centered label
        Assert.True(PaintedInBand(info!, 90, 110) > 0);   // right half of the second centered label
    }
}
