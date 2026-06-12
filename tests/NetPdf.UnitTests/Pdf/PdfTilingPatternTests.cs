// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Tiling-patterns cycle — the PDF-layer primitives: <see cref="PdfDocument.RegisterTilingPattern"/>
/// (the ISO 32000-2 §8.7.3 pattern stream) + <see cref="PdfPage.FillRectangleWithPattern"/>
/// (the <c>/Pattern cs … scn … re f</c> fill + the page's <c>/Pattern</c> resource).
/// </summary>
public sealed class PdfTilingPatternTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static (PdfDocument Doc, PdfIndirectRef ImageRef) NewDocWithImage()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(PngImageXObject.Build(SyntheticPng.BuildOpaqueRgb8(4, 4)));
        return (doc, imageRef);
    }

    [Fact]
    public void Pattern_stream_carries_the_8_7_3_dictionary_and_cell()
    {
        var (doc, imageRef) = NewDocWithImage();
        var patternRef = doc.RegisterTilingPattern(imageRef, 12, 12, anchorXPt: 30, anchorYPt: 700);
        var page = doc.AddPage(MediaBoxSize.A4);
        page.FillRectangleWithPattern(patternRef, 30, 600, 200, 112);
        var pdf = Latin1(doc.Save());

        Assert.Contains("/PatternType 1", pdf);
        Assert.Contains("/PaintType 1", pdf);
        Assert.Contains("/TilingType 1", pdf);
        Assert.Contains("/BBox [0 0 12 12]", pdf);
        Assert.Contains("/XStep 12", pdf);
        Assert.Contains("/YStep 12", pdf);
        Assert.Contains("/Matrix [1 0 0 1 30 700]", pdf);
        Assert.Contains("q 12 0 0 12 0 0 cm /ImP Do Q", pdf);   // the cell paints the image
    }

    [Fact]
    public void Pattern_fill_selects_the_pattern_color_space_and_resource()
    {
        var (doc, imageRef) = NewDocWithImage();
        var patternRef = doc.RegisterTilingPattern(imageRef, 12, 12, 0, 0);
        var page = doc.AddPage(MediaBoxSize.A4);
        var name = page.FillRectangleWithPattern(patternRef, 10, 20, 100, 50);
        var pdf = Latin1(doc.Save());

        Assert.Equal("P1", name);
        Assert.Contains("q /Pattern cs /P1 scn 10 20 100 50 re f Q", pdf);
        Assert.Contains("/Pattern <<", pdf);   // the page's /Pattern resource dictionary
    }

    [Fact]
    public void Pattern_registration_dedupes_by_image_size_and_anchor()
    {
        var (doc, imageRef) = NewDocWithImage();
        var a = doc.RegisterTilingPattern(imageRef, 12, 12, 5, 5);
        var b = doc.RegisterTilingPattern(imageRef, 12, 12, 5, 5);     // identical → same ref
        var c = doc.RegisterTilingPattern(imageRef, 12, 12, 5, 17);    // different anchor → new
        Assert.Equal(a.ObjectNumber, b.ObjectNumber);
        Assert.NotEqual(a.ObjectNumber, c.ObjectNumber);

        // The page-level resource dedups by referenced object too: two fills with one
        // pattern share one /Pattern entry + name.
        var page = doc.AddPage(MediaBoxSize.A4);
        var n1 = page.FillRectangleWithPattern(a, 0, 0, 10, 10);
        var n2 = page.FillRectangleWithPattern(b, 20, 20, 10, 10);
        var n3 = page.FillRectangleWithPattern(c, 40, 40, 10, 10);
        Assert.Equal(n1, n2);
        Assert.NotEqual(n1, n3);
    }

    [Fact]
    public void Pattern_fill_validates_inputs()
    {
        var (doc, imageRef) = NewDocWithImage();
        Assert.Throws<ArgumentException>(() => doc.RegisterTilingPattern(imageRef, 0, 12, 0, 0));
        Assert.Throws<ArgumentException>(() => doc.RegisterTilingPattern(imageRef, 12, double.NaN, 0, 0));
        Assert.Throws<ArgumentException>(() => doc.RegisterTilingPattern(imageRef, 12, 12, double.PositiveInfinity, 0));

        var patternRef = doc.RegisterTilingPattern(imageRef, 12, 12, 0, 0);
        var page = doc.AddPage(MediaBoxSize.A4);
        Assert.Throws<ArgumentException>(
            () => page.FillRectangleWithPattern(patternRef, double.NaN, 0, 10, 10));
        // Non-positive dimensions no-op (the FillRectangle contract).
        Assert.Equal(string.Empty, page.FillRectangleWithPattern(patternRef, 0, 0, 0, 10));
    }
}
