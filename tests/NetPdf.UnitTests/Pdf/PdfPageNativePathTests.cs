// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>Phase 4 native vector SVG — unit tests for <see cref="PdfPage.FillPath"/> /
/// <see cref="PdfPage.StrokePath"/>, the arbitrary-path painting primitives the native SVG emitter draws with.
/// Inspects the raw (uncompressed) content-stream operators via <c>PdfPage.Finalize</c>.</summary>
public sealed class PdfPageNativePathTests
{
    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    private static IReadOnlyList<PdfPathSegment> Triangle() => new[]
    {
        PdfPathSegment.Move(10, 10),
        PdfPathSegment.Line(50, 10),
        PdfPathSegment.Line(30, 40),
        PdfPathSegment.Close,
    };

    [Fact]
    public void FillPath_emits_path_construction_and_a_nonzero_fill()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.FillPath(Triangle(), 1, 0, 0);
        var content = ContentOf(page);
        Assert.Contains("1 0 0 rg", content);
        Assert.Contains("10 10 m", content);
        Assert.Contains("50 10 l", content);
        Assert.Contains("30 40 l", content);
        Assert.Contains("h f Q", content); // close + nonzero fill
    }

    [Fact]
    public void FillPath_evenOdd_uses_f_star()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.FillPath(Triangle(), 0, 0, 1, evenOdd: true);
        Assert.Contains("f* Q", ContentOf(page));
    }

    [Fact]
    public void FillPath_sub_one_alpha_selects_a_fill_ca_extgstate()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.FillPath(Triangle(), 0, 0, 0, alpha: 0.5);
        Assert.Contains("/GSca0_5 gs", ContentOf(page));
    }

    [Fact]
    public void FillPath_transparent_or_empty_paints_nothing()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.FillPath(Triangle(), 1, 0, 0, alpha: 0);
        page.FillPath(Array.Empty<PdfPathSegment>(), 1, 0, 0);
        Assert.DoesNotContain(" f ", ContentOf(page) + " ");
    }

    [Fact]
    public void StrokePath_emits_width_color_and_S()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.StrokePath(Triangle(), 2.5, 0, 0.5, 0);
        var content = ContentOf(page);
        Assert.Contains("2.5 w", content);
        Assert.Contains("0 0.5 0 RG", content);
        Assert.Contains("S Q", content);
    }

    [Fact]
    public void StrokePath_dash_cap_join_are_emitted()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.StrokePath(Triangle(), 1, 0, 0, 0, alpha: 1.0,
            dash: new double[] { 3, 2 }, dashPhase: 1, lineCap: 1, lineJoin: 1);
        var content = ContentOf(page);
        Assert.Contains("1 J", content);        // round cap
        Assert.Contains("1 j", content);        // round join
        Assert.Contains("[3 2] 1 d", content);  // dash
    }

    [Fact]
    public void StrokePath_zero_width_paints_nothing()
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        page.StrokePath(Triangle(), 0, 0, 0, 0);
        Assert.DoesNotContain(" S ", ContentOf(page) + " ");
    }
}
