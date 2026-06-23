// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Facade-level coverage for the auto-height emit fix
/// (`auto-height-emit-vs-pagination`, CSS 2.1 §10.6.3): an auto-height
/// block-flow container's painted background / border spans its in-flow
/// children, not just its own chrome — capped to the page fragment so a
/// subtree taller than the page paints a page-bounded rectangle. The facade
/// PDF stream is uncompressed, so the fill rectangle operators
/// (<c>R G B rg  x y w h re f</c>) are string-inspectable. 1 CSS px = 0.75 pt.</summary>
public sealed class AutoHeightBackgroundPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // The block-axis HEIGHT (4th operand) of the FIRST blue (`0 0 1 rg`) fill
    // rectangle — the painted auto-height wrapper background.
    private static double FirstBlueRectHeightPt(string pdf)
    {
        var m = Regex.Match(
            pdf, @"0 0 1 rg\s+\S+ \S+ \S+ (?<h>-?[0-9.]+) re f");
        Assert.True(m.Success, "expected a blue (`0 0 1 rg ... re f`) fill rectangle in the PDF");
        return double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Auto_height_block_background_spans_taller_child()
    {
        // #p is auto-height with 20px padding; its only child is 30px tall.
        // §10.6.3 — the painted background must cover 30 + 20 + 20 = 70 px
        // (52.5 pt), NOT the chrome-only 40 px (30 pt) the pre-fix emit drew.
        var html = "<!DOCTYPE html><html><body>"
            + "<div style='background-color:#0000ff;padding:20px'>"
            + "<div style='height:30px'></div></div></body></html>";

        var result = HtmlPdf.ConvertDetailed(html);
        var height = FirstBlueRectHeightPt(Latin1(result.Pdf));

        Assert.Equal(1, result.PageCount);
        Assert.Equal(52.5, height, precision: 2);   // 70 px × 0.75 — spans the child
    }

    [Fact]
    public void Auto_height_block_background_tracks_child_extent()
    {
        // Two padding-free auto-height wrappers differing ONLY in child height:
        // the painted background grows by exactly the child delta (CSS px × 0.75),
        // proving the emit is content-sized, not a fixed chrome value.
        static double BgHeight(int childPx) => FirstBlueRectHeightPt(Latin1(
            HtmlPdf.ConvertDetailed(
                "<!DOCTYPE html><html><body>"
                + "<div style='background-color:#0000ff'>"
                + $"<div style='height:{childPx}px'></div></div></body></html>").Pdf));

        var shortBg = BgHeight(10);
        var tallBg = BgHeight(200);

        Assert.Equal(7.5, shortBg, precision: 2);          // 10 px × 0.75
        Assert.Equal(150.0, tallBg, precision: 2);         // 200 px × 0.75
        Assert.Equal((200 - 10) * 0.75, tallBg - shortBg, precision: 2);  // exact child delta
    }

    [Fact]
    public void Auto_height_block_background_is_page_capped_when_taller_than_page()
    {
        // An auto-height wrapper whose 10×300px (3000 px) content far exceeds an
        // A4 content box (~931 px) paginates; the painted background on the page
        // it starts is CAPPED to the page fragment (~698.25 pt = 931 px), NOT the
        // full 2250 pt subtree extent — the cap is what keeps the rectangle on the
        // page (and keeps multi-page block-flow pagination byte-identical).
        var html = "<!DOCTYPE html><html><body>"
            + "<div style='background-color:#0000ff'>"
            + string.Concat(Enumerable.Repeat("<div style='height:300px'></div>", 10))
            + "</div></body></html>";

        var result = HtmlPdf.ConvertDetailed(html);
        var startPageBgHeight = FirstBlueRectHeightPt(Latin1(result.Pdf));

        Assert.True(result.PageCount >= 2,
            $"the 3000 px content must paginate; got {result.PageCount} page(s).");
        // Capped to the page content box — well under the 2250 pt full extent.
        Assert.InRange(startPageBgHeight, 600.0, 720.0);
        // No content was dropped — the children paginated cleanly.
        Assert.DoesNotContain(result.Warnings,
            d => d.Code == DiagnosticCodes.PdfContentOverflowTruncated001);
    }
}
