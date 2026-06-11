// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Unit tests for <see cref="PdfPage.FillRoundedRectangle"/> (border-radius cycle) — the rounded
/// background-band fill. Inspects the raw (uncompressed) content-stream operators.
/// </summary>
public sealed class PdfPageRoundedFillTests
{
    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    [Fact]
    public void FillRoundedRectangle_emits_a_bezier_path_fill()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, 8, 0.2, 0.4, 0.8);

        var content = ContentOf(page);
        Assert.Contains("0.2 0.4 0.8 rg", content);
        Assert.Contains(" m ", content);                       // path start
        Assert.Equal(4, CountOccurrences(content, " c "));     // one Bézier per corner
        Assert.Contains(" f Q", content);                      // filled + state restored
        Assert.DoesNotContain(" re ", content);                // not the square fast path
        // The path starts at the bottom edge's left arc-end: x+radius = 18.
        Assert.Contains("18 20 m", content);
    }

    [Fact]
    public void FillRoundedRectangle_with_non_positive_radius_delegates_to_the_square_fill()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, 0, 0.2, 0.4, 0.8);

        var content = ContentOf(page);
        Assert.Contains("10 20 100 50 re f Q", content);
        Assert.DoesNotContain(" c ", content);
    }

    [Fact]
    public void FillRoundedRectangle_clamps_the_radius_to_half_the_smaller_dimension()
    {
        // radius 999 on a 100×50 rect clamps to 25 — the path's first point is x+25 = 35
        // (a capsule, per the CSS B&B §5.5 overlap rule for the uniform case).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, 999, 0, 0, 0);

        Assert.Contains("35 20 m", ContentOf(page));
    }

    [Fact]
    public void FillRoundedRectangle_zero_area_is_a_no_op()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 0, 50, 8, 0, 0, 0);

        Assert.DoesNotContain(" f", ContentOf(page));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
