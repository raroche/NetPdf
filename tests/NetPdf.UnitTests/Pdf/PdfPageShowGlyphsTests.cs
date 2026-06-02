// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf.Pdf;
using NetPdf.Pdf.Fonts;
using NetPdf.Pdf.Objects;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Unit tests for <see cref="PdfPage.ShowGlyphs"/> — the Identity-H glyph-run draw helper
/// the layout → PDF text bridge (cycle 5a-2) uses. ShowGlyphs requires the font to be
/// registered on the page via <see cref="PdfPage.AddFont"/> first (post-PR-#123 review).
/// Raw (uncompressed) content-stream operators are inspected via <c>PdfPage.Finalize</c>.
/// </summary>
public sealed class PdfPageShowGlyphsTests
{
    // A page with a font registered (named "F1"). AddFont only needs a ref to name + register
    // it in /Font; ShowGlyphs validates the NAME, not the ref, so a synthetic ref suffices for
    // operator-shape assertions (the real-font path is exercised by the integration tests below).
    private static (PdfPage Page, PdfName Font) PageWithFont()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        var font = page.AddFont(new PdfIndirectRef(1));
        return (page, font);
    }

    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    private static EmbeddedFont BuildEmbedded(params int[] glyphIds)
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int>(glyphIds));
        var subset = TtfSubsetter.Subset(font, plan);
        var toUnicode = ToUnicodeCMap.FromSubset(font, plan);
        return EmbeddedTtfFont.Build(font, subset, toUnicode);
    }

    [Fact]
    public void ShowGlyphs_emits_a_text_object_with_font_position_and_glyph_hex()
    {
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 12, 72, 700, new ushort[] { 0x0041, 0x0042 }, 0, 0, 0);

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
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 10, 0, 0, new ushort[] { 0x00FF, 0xABCD }, 0, 0, 0);
        Assert.Contains("<00FFABCD> Tj", ContentOf(page));
    }

    [Fact]
    public void ShowGlyphs_partial_alpha_uses_a_constant_alpha_ExtGState()
    {
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 12, 72, 700, new ushort[] { 0x0041 }, 1, 0, 0, alpha: 0.5);

        var (pageDict, contentBytes) = page.Finalize();
        var content = Encoding.ASCII.GetString(contentBytes);

        Assert.Contains(" gs ", content);          // a /GSn gs selects the constant alpha
        Assert.Contains("BT /F1 12 Tf", content);   // ... and the glyph run still emits
        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        var extGState = Assert.IsType<PdfDictionary>(resources.Get(PdfNames.ExtGState));
        Assert.Equal(1, extGState.Count);
        foreach (var entry in extGState)
        {
            var gs = Assert.IsType<PdfDictionary>(entry.Value);
            Assert.Equal(0.5, Assert.IsType<PdfReal>(gs.Get(PdfNames.ca)).Value);
        }
    }

    [Fact]
    public void ShowGlyphs_opaque_emits_no_ExtGState()
    {
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 12, 72, 700, new ushort[] { 0x0041 }, 1, 0, 0);   // default alpha = 1.0

        var (pageDict, contentBytes) = page.Finalize();
        Assert.DoesNotContain(" gs ", Encoding.ASCII.GetString(contentBytes));
        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        Assert.Null(resources.Get(PdfNames.ExtGState));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ShowGlyphs_rejects_non_finite_alpha(double badAlpha)
    {
        var (page, font) = PageWithFont();
        Assert.Throws<ArgumentException>(
            () => page.ShowGlyphs(font, 12, 0, 0, new ushort[] { 0x0041 }, 0, 0, 0, alpha: badAlpha));
    }

    [Fact]
    public void ShowGlyphs_sets_and_clamps_the_fill_color()
    {
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 10, 0, 0, new ushort[] { 1 }, r: 2.0, g: -1.0, b: 0.5);
        Assert.Contains("1 0 0.5 rg", ContentOf(page));
    }

    [Fact]
    public void ShowGlyphs_empty_run_is_a_no_op()
    {
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 10, 0, 0, ReadOnlySpan<ushort>.Empty, 0, 0, 0);
        Assert.Equal("", ContentOf(page));
    }

    [Fact]
    public void ShowGlyphs_zero_font_size_emits_an_invisible_run()
    {
        // font-size: 0 → a valid (zero-size, invisible) run, not an invalid text state
        // (post-PR-#121 review; the deferrals.md text-paint guard).
        var (page, font) = PageWithFont();
        page.ShowGlyphs(font, 0, 10, 10, new ushort[] { 1 }, 0, 0, 0);
        Assert.Contains("/F1 0 Tf", ContentOf(page));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ShowGlyphs_rejects_invalid_font_size(double size)
    {
        var (page, font) = PageWithFont();
        Assert.Throws<ArgumentException>(() =>
            page.ShowGlyphs(font, size, 0, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void ShowGlyphs_rejects_non_finite_position()
    {
        var (page, font) = PageWithFont();
        Assert.Throws<ArgumentException>(() =>
            page.ShowGlyphs(font, 10, double.NaN, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void ShowGlyphs_after_finalize_throws()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        var font = page.AddFont(new PdfIndirectRef(1));
        doc.Save();
        Assert.Throws<InvalidOperationException>(() =>
            page.ShowGlyphs(font, 10, 0, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void ShowGlyphs_is_deterministic()
    {
        Assert.Equal(Emit(), Emit());

        static string Emit()
        {
            var (page, font) = PageWithFont();
            page.ShowGlyphs(font, 12, 72, 700, new ushort[] { 0x0041, 0x0042 }, 0.1, 0.2, 0.3);
            return ContentOf(page);
        }
    }

    // --- post-PR-#123 review: registered-font requirement ----------------

    [Theory]
    [InlineData("F1")]            // a plausible name that simply wasn't added
    [InlineData("F1 /X Do")]      // an injection attempt — can never be an AddFont-generated name
    public void ShowGlyphs_throws_for_an_unregistered_font_resource(string name)
    {
        // (P2) Save does not parse content streams, so emitting `/Fn Tf` without a matching
        // /Resources /Font /Fn would be malformed; requiring registration also makes the name
        // injection-safe (only AddFont's escaping-safe "Fn" names can ever be registered).
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        Assert.Throws<ArgumentException>(() =>
            page.ShowGlyphs(new PdfName(name), 12, 0, 0, new ushort[] { 1 }, 0, 0, 0));
    }

    [Fact]
    public void Registered_font_emits_the_resource_and_the_text_operator_together()
    {
        // (P2) A real registered font: the content stream selects /F1 AND the page's
        // /Resources /Font defines /F1 — proving they're emitted together (no dangling
        // font selection).
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        var page = doc.AddPage(MediaBoxSize.A4);
        var name = page.AddFont(fontRef);
        page.ShowGlyphs(name, 12, 72, 700, new ushort[] { 1, 2 }, 0, 0, 0);

        var (pageDict, content) = page.Finalize();

        var text = Encoding.ASCII.GetString(content);
        Assert.Contains($"BT /{name.Value} ", text);
        Assert.Contains("Tf", text);
        Assert.Contains("Tj", text);

        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        var fonts = Assert.IsType<PdfDictionary>(resources.Get(PdfNames.Font));
        Assert.True(fonts.ContainsKey(name));
    }

    [Fact]
    public void Saved_pdf_wires_the_page_font_resource_for_a_shown_run()
    {
        var doc = new PdfDocument();
        var fontRef = doc.RegisterFont(BuildEmbedded(1, 2));
        var page = doc.AddPage(MediaBoxSize.A4);
        var name = page.AddFont(fontRef);
        page.ShowGlyphs(name, 12, 72, 700, new ushort[] { 1, 2 }, 0, 0, 0);

        var pdf = Encoding.Latin1.GetString(doc.Save());

        Assert.Contains("/Font", pdf);
        Assert.Matches(new Regex($@"/{name.Value}\s+\d+ 0 R"), pdf);   // /Font << /F1 N 0 R >>
    }
}
