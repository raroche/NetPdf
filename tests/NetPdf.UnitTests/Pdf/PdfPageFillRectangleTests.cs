// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        var extGState = Assert.IsType<PdfDictionary>(resources.Get(PdfNames.ExtGState));
        Assert.Equal(1, extGState.Count);    // one /ca 0.5 ExtGState
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
        var resources = Assert.IsType<PdfDictionary>(pageDict.Get(PdfNames.Resources));
        var extGState = Assert.IsType<PdfDictionary>(resources.Get(PdfNames.ExtGState));
        Assert.Equal(1, extGState.Count);
    }
}
