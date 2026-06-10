// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Unit tests for <see cref="PdfPage.BeginRectangleClip"/> / <see cref="PdfPage.RestoreGraphicsState"/>
/// — the rectangle clip-path primitives the margin-box overflow clip uses (clip-path cycle). Inspects
/// the raw (uncompressed) content-stream operators via <c>PdfPage.Finalize</c>.
/// </summary>
public sealed class PdfPageClipTests
{
    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    [Fact]
    public void BeginRectangleClip_emits_q_rect_W_n_and_RestoreGraphicsState_balances_it()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.BeginRectangleClip(10, 20, 30, 40);
        page.RestoreGraphicsState();

        var content = ContentOf(page);
        Assert.Contains("q 10 20 30 40 re W n", content);
        Assert.Contains("Q", content);
    }

    [Fact]
    public void BeginRectangleClip_clamps_non_positive_dimensions_to_a_zero_area_clip()
    {
        // A negative `re` dimension is NOT an empty rectangle (it flips across the origin), so a
        // non-positive width/height is clamped to 0 — a degenerate clip that paints nothing. The clip
        // must never silently widen (post-PR-#156 review P3 — the doc and behavior now agree).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.BeginRectangleClip(10, 20, -30, -1);
        page.RestoreGraphicsState();

        Assert.Contains("q 10 20 0 0 re W n", ContentOf(page));
    }

    [Fact]
    public void BeginRectangleClip_rejects_non_finite_coordinates()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        Assert.Throws<ArgumentException>(() => page.BeginRectangleClip(double.NaN, 0, 10, 10));
        Assert.Throws<ArgumentException>(() => page.BeginRectangleClip(0, 0, double.PositiveInfinity, 10));
    }
}
