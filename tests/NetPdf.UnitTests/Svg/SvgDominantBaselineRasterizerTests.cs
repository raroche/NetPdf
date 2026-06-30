// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 6 — SVG text <c>dominant-baseline</c> shifts the baseline relative to the pen Y
/// (hanging/before-edge put the top at y, after-edge the bottom, middle/central center). Font-agnostic:
/// compares the painted ink's vertical position.</summary>
public sealed class SvgDominantBaselineRasterizerTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    private static (int MinY, int MaxY, int Count) InkY(NetPdf.Pdf.Images.RasterImageInfo info)
    {
        int minY = int.MaxValue, maxY = -1, count = 0;
        for (var y = 0; y < info.Height; y++)
            for (var x = 0; x < info.Width; x++)
                if (info.PixelBytes[(y * info.Width + x) * 4 + 3] > 40)
                {
                    count++;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        return (minY, maxY, count);
    }

    [Fact]
    public void Hanging_baseline_drops_the_text_below_the_pen_y()
    {
        var alphabetic = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<text x=\"5\" y=\"30\" font-size=\"24\" fill=\"black\">H</text></svg>"), out _);
        var hanging = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<text x=\"5\" y=\"30\" font-size=\"24\" fill=\"black\" dominant-baseline=\"hanging\">H</text></svg>"), out _);
        Assert.NotNull(alphabetic);
        Assert.NotNull(hanging);
        Assert.True(InkY(alphabetic!).MaxY <= 31);                 // default: ink sits at/above the baseline y=30
        Assert.True(InkY(hanging!).MinY >= 29);                    // hanging: ink starts at/below y=30
        Assert.True(InkY(hanging!).MinY > InkY(alphabetic!).MinY + 10); // clearly lower than alphabetic
    }

    [Fact]
    public void Middle_baseline_centers_the_text_on_the_pen_y()
    {
        var alphabetic = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<text x=\"5\" y=\"30\" font-size=\"24\" fill=\"black\">H</text></svg>"), out _);
        var middle = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"60\">" +
            "<text x=\"5\" y=\"30\" font-size=\"24\" fill=\"black\" dominant-baseline=\"middle\">H</text></svg>"), out var unsupported);
        Assert.NotNull(middle);
        Assert.False(unsupported);
        // The glyph's vertical center sits near y=30 → its ink straddles the pen line more than alphabetic.
        var m = InkY(middle!);
        Assert.True(m.MinY < 30 && m.MaxY > 30);
        Assert.True(m.MaxY > InkY(alphabetic!).MaxY + 4);          // shifted down vs alphabetic
    }
}
