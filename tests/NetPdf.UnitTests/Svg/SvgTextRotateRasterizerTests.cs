// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 6 — the per-glyph <c>rotate</c> attribute on <c>&lt;text&gt;</c>/<c>&lt;tspan&gt;</c>.
/// Font-agnostic: a 90° rotation swaps a glyph's ink bounding-box width and height.</summary>
public sealed class SvgTextRotateRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (int W, int H, int Count) InkBox(NetPdf.Pdf.Images.RasterImageInfo info)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, count = 0;
        for (var y = 0; y < info.Height; y++)
            for (var x = 0; x < info.Width; x++)
                if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40)
                {
                    count++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        return (maxX - minX, maxY - minY, count);
    }

    [Fact]
    public void Rotate_ninety_degrees_swaps_a_glyphs_ink_box_dimensions()
    {
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<text x=\"30\" y=\"40\" font-size=\"30\" fill=\"black\">L</text></svg>"), out _);
        var rotated = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
            "<text x=\"30\" y=\"40\" font-size=\"30\" fill=\"black\" rotate=\"90\">L</text></svg>"), out var unsupported);
        Assert.NotNull(plain);
        Assert.NotNull(rotated);
        Assert.False(unsupported);
        var p = InkBox(plain!);
        var r = InkBox(rotated!);
        Assert.True(p.Count > 5 && r.Count > 5);
        // A 90° glyph rotation swaps width↔height (within a few px of antialiasing).
        Assert.True(System.Math.Abs(r.W - p.H) <= 5);
        Assert.True(System.Math.Abs(r.H - p.W) <= 5);
    }

    [Fact]
    public void Rotate_list_applies_per_glyph()
    {
        // "rotate=0 90" rotates only the second glyph → ink is taller than a non-rotated two-glyph run.
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"80\">" +
            "<text x=\"20\" y=\"40\" font-size=\"28\" fill=\"black\">LL</text></svg>"), out _);
        var listRot = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"80\">" +
            "<text x=\"20\" y=\"40\" font-size=\"28\" fill=\"black\" rotate=\"0 90\">LL</text></svg>"), out _);
        Assert.NotNull(plain);
        Assert.NotNull(listRot);
        Assert.True(InkBox(listRot!).H > InkBox(plain!).H + 4);   // the rotated 'L' adds vertical extent
    }

    [Fact]
    public void Rotate_list_indexes_glyphs_globally_across_tspans()
    {
        // rotate on <text> addresses ALL glyphs GLOBALLY (§10.5): "0 0 90 90" over "LL"+<tspan>"LL" rotates the
        // two TSPAN glyphs (global indices 2,3). A run-LOCAL index would give the tspan run indices 0,1 → 0°/0°
        // (no rotation), so the tspan text gaining vertical extent proves the index is global.
        var plain = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"90\">" +
            "<text x=\"15\" y=\"50\" font-size=\"26\" fill=\"black\">LL<tspan>LL</tspan></text></svg>"), out _);
        var rot = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"160\" height=\"90\">" +
            "<text x=\"15\" y=\"50\" font-size=\"26\" fill=\"black\" rotate=\"0 0 90 90\">LL<tspan>LL</tspan></text></svg>"), out var unsupported);
        Assert.NotNull(plain);
        Assert.NotNull(rot);
        Assert.False(unsupported);
        Assert.True(InkBox(rot!).H > InkBox(plain!).H + 4);       // the tspan glyphs (global idx 2,3) rotated 90°
    }
}
