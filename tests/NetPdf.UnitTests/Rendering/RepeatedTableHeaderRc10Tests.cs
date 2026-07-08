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
/// RC-10 — a repeated <c>&lt;thead&gt;</c> cell got a NEGATIVE BlockSize on continuation pages, so
/// FragmentPainter silently skipped its background + borders: the header band was invisible on every
/// page after the first (white-on-white where the templates used white header text). Root cause: the
/// resume-page offset normalization shifted the header/body boundary entry of the rowEnd prefix array,
/// so <c>rowEnd[origin+span] - rowEnd[origin]</c> went negative. Fixed by (A) not shifting that
/// boundary and (B) sizing repeated header cells from the sum of their row heights.
/// </summary>
public sealed class RepeatedTableHeaderRc10Tests
{
    private static string MultiPageTable(int rows) =>
        "<!doctype html><html><head><style>"
        + "@page{size:A4;margin:20mm} body{margin:0;font-size:12px}"
        + "table{width:100%;border-collapse:collapse}"
        + "thead th{background:#333333;color:#ffffff;padding:6px}"
        + "td{padding:4px;border-bottom:1px solid #ccc}"
        + "</style></head><body><table><thead><tr><th>Item</th><th>Qty</th><th>Price</th></tr></thead><tbody>"
        + string.Concat(Enumerable.Range(0, rows).Select(i =>
            $"<tr><td>Item {i}</td><td>{i % 9 + 1}</td><td>${i * 3}</td></tr>"))
        + "</tbody></table></body></html>";

    // Per-page content streams (those carrying text or rects).
    private static List<string> PageStreams(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        return Regex.Matches(s, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(x => x.Contains(" Tf") || x.Contains(" re"))
            .ToList();
    }

    [Fact]
    public void Repeated_header_background_paints_on_every_page_not_just_the_first()
    {
        var res = HtmlPdf.ConvertDetailed(MultiPageTable(120), new HtmlPdfOptions { PrintBackgrounds = true });
        Assert.True(res.PageCount >= 3, $"expected a multi-page table; got {res.PageCount} pages");

        var darkFill = new Regex(@"0\.2(?:\d*)? 0\.2(?:\d*)? 0\.2(?:\d*)? rg");
        var pagesWithHeaderBg = PageStreams(res.Pdf).Count(st => darkFill.IsMatch(st));

        // The three header cells' dark background must appear on EVERY page the table spans, not only
        // page 1. (Before the fix only page 1 painted it.)
        Assert.True(pagesWithHeaderBg >= res.PageCount,
            $"header background painted on {pagesWithHeaderBg} page-streams but the table spans {res.PageCount} pages");
    }

    [Fact]
    public void No_negative_size_header_fragments_are_emitted_on_continuation_pages()
    {
        // A negative-SIZE "x y w h re" rectangle in any content stream signals the RC-10 coordinate
        // bug. Match the full 4-operand form and flag a negative WIDTH (3rd) or HEIGHT (4th) operand
        // — an earlier version only looked at the number immediately before `re` (height), so a
        // regression producing a negative width would have slipped through.
        var res = HtmlPdf.ConvertDetailed(MultiPageTable(120), new HtmlPdfOptions { PrintBackgrounds = true });
        var s = Encoding.Latin1.GetString(res.Pdf);
        var rect = new Regex(@"(-?\d+\.?\d*)\s+(-?\d+\.?\d*)\s+(-?\d+\.?\d*)\s+(-?\d+\.?\d*)\s+re");
        var negativeRects = 0;
        foreach (Match m in rect.Matches(s))
        {
            var w = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var h = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            if (w < 0 || h < 0) negativeRects++;
        }
        Assert.Equal(0, negativeRects);
    }
}
