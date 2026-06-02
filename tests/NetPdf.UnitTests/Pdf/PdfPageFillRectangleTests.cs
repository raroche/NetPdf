// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Unit tests for <see cref="PdfPage.FillRectangle"/> — the solid-rectangle draw
/// helper the layout → PDF paint bridge uses for backgrounds + border edges.
/// Inspects the raw (uncompressed) content-stream operators via
/// <c>PdfPage.Finalize</c>.
/// </summary>
public sealed class PdfPageFillRectangleTests
{
    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    /// <summary>Parse the resource name out of the content stream's <c>/&lt;name&gt; gs</c>
    /// operator — the ExtGState the fill actually selects.</summary>
    private static string ExtGStateNameSelectedInContent(string content)
    {
        var gs = content.IndexOf(" gs", StringComparison.Ordinal);
        Assert.True(gs > 0, "expected a ' gs' operator selecting an ExtGState in the content stream");
        var slash = content.LastIndexOf('/', gs);
        Assert.True(slash >= 0 && slash < gs, "expected a '/name' immediately before the gs operator");
        return content.Substring(slash + 1, gs - slash - 1);
    }

    /// <summary>The page's <c>/Resources /ExtGState</c> sub-dictionary.</summary>
    private static PdfDictionary ExtGStateResource(PdfDictionary pageDict)
    {
        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        return Assert.IsType<PdfDictionary>(resources.Get(PdfNames.ExtGState));
    }

    /// <summary>The stored <c>/ca</c> value of a single ExtGState entry.</summary>
    private static double CaValue(PdfObject extGStateEntry)
    {
        var gsDict = Assert.IsType<PdfDictionary>(extGStateEntry);
        return Assert.IsType<PdfReal>(gsDict.Get(PdfNames.ca)).Value;
    }

    [Fact]
    public void FillRectangle_emits_fill_color_then_rectangle_then_fill_in_a_qQ_pair()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRectangle(10, 20, 30, 40, 0.5, 0.25, 0.75);

        var content = ContentOf(page);
        Assert.Contains("0.5 0.25 0.75 rg", content);
        Assert.Contains("10 20 30 40 re f", content);
        Assert.StartsWith("q ", content);
        Assert.Contains(" Q", content);
    }

    [Fact]
    public void FillRectangle_clamps_rgb_channels_to_unit_range()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRectangle(0, 0, 10, 10, r: 2.0, g: -0.5, b: 0.5);

        Assert.Contains("1 0 0.5 rg", ContentOf(page));
    }

    [Theory]
    [InlineData(0, 10)]   // zero width
    [InlineData(10, 0)]   // zero height
    [InlineData(-5, 10)]  // negative width
    public void FillRectangle_is_a_no_op_for_a_degenerate_rectangle(double width, double height)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRectangle(0, 0, width, height, 0.1, 0.2, 0.3);

        var (_, content) = page.Finalize();
        Assert.Empty(content);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FillRectangle_rejects_non_finite_coordinates(double bad)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        Assert.Throws<ArgumentException>(() => page.FillRectangle(bad, 0, 10, 10, 0, 0, 0));
    }

    [Fact]
    public void FillRectangle_produces_a_valid_saveable_document()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        page.FillRectangle(72, 72, 144, 96, 0.2, 0.4, 0.6);

        var bytes = doc.Save();

        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public void FillRectangle_partial_alpha_uses_a_constant_alpha_ExtGState()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        page.FillRectangle(10, 20, 30, 40, 1, 0, 0, alpha: 0.5);

        var (pageDict, contentBytes) = page.Finalize();
        var content = Encoding.ASCII.GetString(contentBytes);

        Assert.Contains("1 0 0 rg", content);
        Assert.Contains(" gs ", content);   // a /GSn gs selects the constant alpha

        var extGState = ExtGStateResource(pageDict);
        Assert.Equal(1, extGState.Count);

        // The content stream must select the SAME ExtGState resource the page declares: the
        // name in "/<name> gs" has to be a key in /Resources /ExtGState (review P3).
        var selected = new PdfName(ExtGStateNameSelectedInContent(content));
        Assert.True(extGState.ContainsKey(selected),
            $"content selects /{selected.Value} gs but /Resources /ExtGState has no such key");

        // Inspect the stored /ca value DIRECTLY — a bare "/ca 0.5" string match would also
        // accept "/ca 0.501961". Here alpha is passed as the exact double 0.5 (review P3).
        var gsDict = Assert.IsType<PdfDictionary>(extGState.Get(selected));
        Assert.Equal("ExtGState", Assert.IsType<PdfName>(gsDict.Get(PdfNames.Type)).Value);
        Assert.Equal(0.5, Assert.IsType<PdfReal>(gsDict.Get(PdfNames.ca)).Value);
    }

    [Fact]
    public void FillRectangle_opaque_emits_no_ExtGState()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        page.FillRectangle(10, 20, 30, 40, 1, 0, 0);   // default alpha = 1.0 (opaque)

        var (pageDict, contentBytes) = page.Finalize();
        Assert.DoesNotContain(" gs ", Encoding.ASCII.GetString(contentBytes));
        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        Assert.Null(resources.Get(PdfNames.ExtGState));
    }

    [Fact]
    public void FillRectangle_dedups_the_constant_alpha_ExtGState_by_value()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        page.FillRectangle(0, 0, 10, 10, 1, 0, 0, alpha: 0.5);
        page.FillRectangle(0, 0, 10, 10, 0, 1, 0, alpha: 0.5);   // same alpha → shares the ExtGState

        var (pageDict, _) = page.Finalize();
        Assert.Equal(1, ExtGStateResource(pageDict).Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FillRectangle_rejects_non_finite_alpha(double badAlpha)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        // A non-finite alpha must throw, not silently paint opaque: Math.Clamp(NaN,0,1) is NaN,
        // the `alpha < 1.0` transparency test is then false, and the fill loses its alpha
        // entirely — a silent-corruption hole this guard closes (review P2).
        Assert.Throws<ArgumentException>(
            () => page.FillRectangle(0, 0, 10, 10, 1, 0, 0, alpha: badAlpha));
    }

    [Fact]
    public void FillRectangle_does_not_collide_distinct_alphas_differing_below_6_decimals()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        // 0.123456 and 0.123457 differ at the 6th fraction digit, so PdfReal serializes them
        // distinctly ("0.123456" vs "0.123457") and they MUST get distinct ExtGStates. The old
        // 5-digit dedup name rounded both to "GSca0_12346", silently reusing one /ca (review P2).
        page.FillRectangle(0, 0, 10, 10, 1, 0, 0, alpha: 0.123456);
        page.FillRectangle(0, 0, 10, 10, 0, 1, 0, alpha: 0.123457);

        var (pageDict, _) = page.Finalize();
        var extGState = ExtGStateResource(pageDict);
        Assert.Equal(2, extGState.Count);

        var caValues = new List<double>();
        foreach (var entry in extGState) caValues.Add(CaValue(entry.Value));
        Assert.Contains(0.123456, caValues);
        Assert.Contains(0.123457, caValues);
    }

    [Fact]
    public void FillRectangle_shares_one_ExtGState_for_alphas_that_serialize_identically()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        // 0.5 and 0.5000001 both serialize to "0.5" at PDF real precision (6 fraction digits),
        // producing byte-identical /ca output — so they share ONE ExtGState. Dedup keys on the
        // serialized value, not raw double bits, which guards against over-fragmentation.
        page.FillRectangle(0, 0, 10, 10, 1, 0, 0, alpha: 0.5);
        page.FillRectangle(0, 0, 10, 10, 0, 1, 0, alpha: 0.5000001);

        var (pageDict, _) = page.Finalize();
        Assert.Equal(1, ExtGStateResource(pageDict).Count);
    }
}
