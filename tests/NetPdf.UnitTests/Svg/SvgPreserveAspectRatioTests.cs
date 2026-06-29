// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 SVG part 5 — <c>preserveAspectRatio</c> (§8.8) full support: <c>none</c> (stretch),
/// <c>meet</c> (fit), <c>slice</c> (cover + clip), and the nine x/y MIN/MID/MAX alignments, on
/// <c>&lt;image&gt;</c> and the nested-viewport path. Replaces the prior xMidYMid-meet/none-only cut.</summary>
public sealed class SvgPreserveAspectRatioTests
{
    private static byte[] Svg(string s) => Encoding.UTF8.GetBytes(s);

    // 8×8 solid steel-blue PNG.
    private const string Blue8 =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAEklEQVR42mNwy1vwHx9mGBkKAGzclMHUslfUAAAAAElFTkSuQmCC";

    private static (byte R, byte G, byte B, byte A) Px(NetPdf.Pdf.Images.RasterImageInfo info, int x, int y)
    {
        var i = (y * info.Width + x) * 4;
        return (info.PixelBytes[i], info.PixelBytes[i + 1], info.PixelBytes[i + 2], info.PixelBytes[i + 3]);
    }

    [Theory]
    [InlineData(null, 0.5, 0.5, false, false)]
    [InlineData("none", 0.0, 0.0, true, false)]
    [InlineData("xMinYMin meet", 0.0, 0.0, false, false)]
    [InlineData("xMaxYMax slice", 1.0, 1.0, false, true)]
    [InlineData("xMidYMax", 0.5, 1.0, false, false)]
    [InlineData("xMaxYMin meet", 1.0, 0.0, false, false)]
    [InlineData("defer xMinYMid meet", 0.0, 0.5, false, false)]
    // PR-243 review [P3] — malformed values fall back to the §8.8 lacuna default (xMidYMid meet).
    [InlineData("garbage", 0.5, 0.5, false, false)]
    [InlineData("xBadYBad meet", 0.5, 0.5, false, false)]
    [InlineData("xMidYMid foo", 0.5, 0.5, false, false)]
    [InlineData("xMidYMid meet extra", 0.5, 0.5, false, false)]
    public void Parse_resolves_align_and_meet_or_slice(string? raw, double ax, double ay, bool none, bool slice)
    {
        var (alignX, alignY, n, s) = SvgPreserveAspectRatio.Parse(raw);
        Assert.Equal(ax, alignX);
        Assert.Equal(ay, alignY);
        Assert.Equal(none, n);
        Assert.Equal(slice, s);
    }

    [Fact]
    public void Compute_none_stretches_each_axis_independently()
    {
        var par = SvgPreserveAspectRatio.Compute("none", 10, 10, 20, 40);
        Assert.Equal(2f, par.ScaleX);
        Assert.Equal(4f, par.ScaleY);
    }

    [Fact]
    public void Compute_meet_fits_and_centers()
    {
        var par = SvgPreserveAspectRatio.Compute("xMidYMid meet", 10, 10, 20, 40);
        Assert.Equal(2f, par.ScaleX);   // min(2, 4)
        Assert.Equal(0f, par.Tx);
        Assert.Equal(10f, par.Ty);      // (40 - 20)/2
        Assert.False(par.Slice);
    }

    [Fact]
    public void Compute_slice_covers_and_flags_overflow()
    {
        var par = SvgPreserveAspectRatio.Compute("xMidYMid slice", 10, 10, 20, 40);
        Assert.Equal(4f, par.ScaleX);   // max(2, 4)
        Assert.True(par.Slice);
        Assert.Equal(-10f, par.Tx);     // (20 - 40)/2
        Assert.Equal(0f, par.Ty);
    }

    [Fact]
    public void Image_xmin_meet_aligns_left_and_is_not_flagged()
    {
        // 8×8 into 40×20 meet → 20×20 scaled; xMin → left half filled, right half empty.
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            $"<image x=\"0\" y=\"0\" width=\"40\" height=\"20\" preserveAspectRatio=\"xMinYMid meet\" href=\"{Blue8}\"/></svg>"),
            out var unsupported);
        Assert.NotNull(info);
        Assert.False(unsupported);          // all PAR values now supported — no flag
        Assert.True(Px(info!, 5, 10).B > 150);
        Assert.Equal(0, Px(info!, 32, 10).A);
    }

    [Fact]
    public void Image_xmax_meet_aligns_right()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            $"<image x=\"0\" y=\"0\" width=\"40\" height=\"20\" preserveAspectRatio=\"xMaxYMid meet\" href=\"{Blue8}\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.Equal(0, Px(info!, 5, 10).A);
        Assert.True(Px(info!, 32, 10).B > 150);
    }

    [Fact]
    public void Root_svg_preserve_aspect_ratio_none_stretches_the_viewbox()
    {
        // PR-243 review [P1] — the ROOT viewBox must honor preserveAspectRatio. A 10×10 viewBox over a
        // 40×20 raster with "none" stretches a full-viewBox rect to fill the whole raster.
        var stretched = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\" viewBox=\"0 0 10 10\" preserveAspectRatio=\"none\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></svg>"), out _);
        var meet = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\" viewBox=\"0 0 10 10\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></svg>"), out _);
        Assert.NotNull(stretched);
        Assert.NotNull(meet);
        Assert.True(Px(stretched!, 2, 10).R > 200 && Px(stretched!, 38, 10).R > 200); // fills the raster
        Assert.Equal(0, Px(meet!, 2, 10).A);                                          // default meet → centered, left empty
    }

    [Fact]
    public void Root_svg_preserve_aspect_ratio_slice_covers_the_raster()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\" viewBox=\"0 0 10 10\" preserveAspectRatio=\"xMidYMid slice\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"red\"/></svg>"), out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 2, 10).R > 200 && Px(info!, 38, 10).R > 200);            // slice → covers fully
    }

    [Fact]
    public void Image_slice_covers_the_whole_rect()
    {
        var info = SvgRasterizer.TryRender(Svg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">" +
            $"<image x=\"0\" y=\"0\" width=\"40\" height=\"20\" preserveAspectRatio=\"xMidYMid slice\" href=\"{Blue8}\"/></svg>"),
            out _);
        Assert.NotNull(info);
        Assert.True(Px(info!, 5, 10).B > 150);
        Assert.True(Px(info!, 32, 10).B > 150);   // both ends covered (sliced)
    }
}
