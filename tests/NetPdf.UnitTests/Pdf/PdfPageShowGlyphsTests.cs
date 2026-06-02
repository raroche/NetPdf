// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Unit tests for <see cref="PdfPage.ShowGlyphs"/> — the Identity-H glyph-run draw helper
/// the layout → PDF text bridge (cycle 5a-2) uses. Inspects the raw (uncompressed)
/// content-stream operators via <c>PdfPage.Finalize</c>.
/// </summary>
public sealed class PdfPageShowGlyphsTests
{
    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    private static PdfPage NewPage() => new PdfDocument().AddPage(MediaBoxSize.A4);

    [Fact]
    public void ShowGlyphs_emits_a_text_object_with_font_position_and_glyph_hex()
    {
        var page = NewPage();
        page.ShowGlyphs(new PdfName("F1"), 12, 72, 700, new ushort[] { 0x0041, 0x0042 }, 0, 0, 0);

        var content = ContentOf(page);
        Assert.StartsWith("q ", content);
        Assert.Contains("0 0 0 rg", content);
        Assert.Contains("BT /F1 12 Tf", content);
        Assert.Contains("72 700 Td", content);
        Assert.Contains("<00410042> Tj", content);   // 2 big-endian bytes per glyph id
        Assert.Contains("ET", content);
        Assert.Contains(" Q", content);
    }

    [Fact]
    public void ShowGlyphs_hex_encodes_glyph_ids_big_endian()
    {
        var page = NewPage();
        page.ShowGlyphs(new PdfName("F1"), 10, 0, 0, new ushort[] { 0x00FF, 0xABCD }, 0, 0, 0);
        Assert.Contains("<00FFABCD> Tj", ContentOf(page));
    }

    [Fact]
    public void ShowGlyphs_sets_and_clamps_the_fill_color()
    {
        var page = NewPage();
        page.ShowGlyphs(new PdfName("F1"), 10, 0, 0, new ushort[] { 1 }, r: 2.0, g: -1.0, b: 0.5);
        Assert.Contains("1 0 0.5 rg", ContentOf(page));
    }

    [Fact]
    public void ShowGlyphs_empty_run_is_a_no_op()
    {
        var page = NewPage();
        page.ShowGlyphs(new PdfName("F1"), 10, 0, 0, ReadOnlySpan<ushort>.Empty, 0, 0, 0);
        Assert.Equal("", ContentOf(page));
    }

    [Fact]
    public void ShowGlyphs_zero_font_size_emits_an_invisible_run()
    {
        // font-size: 0 → a valid (zero-size, invisible) run, not an invalid text state
        // (post-PR-#121 review; the deferrals.md text-paint guard).
        var page = NewPage();
        page.ShowGlyphs(new PdfName("F1"), 0, 10, 10, new ushort[] { 1 }, 0, 0, 0);
        Assert.Contains("/F1 0 Tf", ContentOf(page));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ShowGlyphs_rejects_invalid_font_size(double size)
    {
        var page = NewPage();
        Assert.Throws<ArgumentException>(() =>
            page.ShowGlyphs(new PdfName("F1"), size, 0, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void ShowGlyphs_rejects_non_finite_position()
    {
        var page = NewPage();
        Assert.Throws<ArgumentException>(() =>
            page.ShowGlyphs(new PdfName("F1"), 10, double.NaN, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void ShowGlyphs_after_finalize_throws()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        doc.Save();
        Assert.Throws<InvalidOperationException>(() =>
            page.ShowGlyphs(new PdfName("F1"), 10, 0, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void ShowGlyphs_is_deterministic()
    {
        Assert.Equal(Emit(), Emit());

        static string Emit()
        {
            var page = NewPage();
            page.ShowGlyphs(new PdfName("F1"), 12, 72, 700, new ushort[] { 0x0041, 0x0042 }, 0.1, 0.2, 0.3);
            return ContentOf(page);
        }
    }
}
