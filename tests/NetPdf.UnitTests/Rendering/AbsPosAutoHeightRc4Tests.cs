// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-4 — an auto-height single-anchored fixed/absolute box (the ubiquitous `position:fixed;
/// bottom:0; height:auto` footer) resolved its height to the FULL available extent instead of its
/// content height, so its background painted as a full-page rect over everything (index.pdf's
/// illegible total row + blank tinted page). Two further bugs stacked on the solver approximation:
/// (a) the fixed pass emitted the box's own TextRun (which spuriously inherits `fixed` by style
/// reference) as a SECOND full-page box; (b) measuring an out-of-flow box's own content returned 0
/// (the inline text run was dropped as "out of flow", and the subject fragment was dropped by the
/// measure sink). Fixed by pre-measuring content height + guarding both.
/// </summary>
public sealed class AbsPosAutoHeightRc4Tests
{
    private static string Doc(string position, string anchor) =>
        "<!doctype html><html><head><style>@page{size:A4;margin:0} body{margin:0;font-size:12px}"
        + ".content{height:200px}"
        + $".footer{{position:{position};left:0;{anchor};width:300px;height:auto;background:#eef;padding:10px}}"
        + "</style></head><body><div class=\"content\">Body content</div>"
        + "<div class=\"footer\">Footer total here</div></body></html>";

    // Heights of the footer-colored (#eef) background rectangles.
    private static List<double> FooterRectHeights(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        return Regex.Matches(s, @"[\d.-]+ [\d.-]+ [\d.-]+ ([\d.]+) re")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(h => h > 5)   // ignore hairline separators
            .ToList();
    }

    private static int FooterBgFills(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"0\.93\d* 0\.93\d* 1(?:\.0+)? rg").Count;

    [Theory]
    [InlineData("fixed", "bottom:0")]
    [InlineData("absolute", "bottom:0")]
    [InlineData("fixed", "top:0")]
    public void Auto_height_positioned_box_is_content_sized_not_page_sized(string position, string anchor)
    {
        var pdf = HtmlPdf.ConvertDetailed(Doc(position, anchor), new HtmlPdfOptions { PrintBackgrounds = true }).Pdf;
        var heights = FooterRectHeights(pdf);
        Assert.NotEmpty(heights);
        // A4 content height is ~842pt; a content-sized footer (one line + 20px padding) is well under
        // 100pt. Before the fix the background rect was the full page (~842pt).
        Assert.All(heights, h => Assert.True(h < 100,
            $"footer background height {h:F1}pt should be content-sized (< 100pt), not page-sized"));
    }

    [Fact]
    public void Fixed_footer_background_is_painted_exactly_once()
    {
        // The box's own TextRun spuriously inherits `position:fixed` by style reference; the fixed pass
        // must not emit it as a second full-page box. Exactly one footer background fill.
        var pdf = HtmlPdf.ConvertDetailed(Doc("fixed", "bottom:0"), new HtmlPdfOptions { PrintBackgrounds = true }).Pdf;
        Assert.Equal(1, FooterBgFills(pdf));
    }
}
