// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using NetPdf.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// <c>PAGINATION-FORCED-OVERFLOW-001</c> must mean that content really was forced past a page edge.
/// Before this fix every page of a document whose content sits in one wrapper element taller than a page
/// (<c>&lt;div class="page"&gt;…everything…&lt;/div&gt;</c>) reported it — the wrapper is committed at the
/// page top only to be ENTERED, and its children then paginate normally — so the warning list of every
/// real multi-page document was noise and real overflows were hidden in it. Also covers absolutely
/// positioned content taller than its box, whose nested layout used to paginate into the box-sized
/// fragmentainer (reporting the diagnostic and DROPPING everything past the first break).
/// </summary>
public sealed class ForcedOverflowDiagnosticTests
{
    private static PdfRenderResult Render(string body, string css = "") =>
        HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><meta charset='utf-8'><style>@page{size:A4;margin:20mm}"
            + "body{margin:0;font:14px/1.4 sans-serif}p{margin:0 0 8px}" + css + "</style></head><body>"
            + body + "</body></html>",
            new HtmlPdfOptions { PrintBackgrounds = true });

    private static int ForcedOverflowCount(PdfRenderResult r) =>
        r.Warnings.Count(w => w.Code == DiagnosticCodes.PaginationForcedOverflow001);

    private static string Paragraphs(int n) =>
        string.Concat(Enumerable.Range(0, n).Select(i =>
            $"<p>Paragraph {i}: Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor.</p>"));

    [Theory]
    [InlineData(1)]   // <div class="page">
    [InlineData(3)]   // three nested wrappers
    public void Wrapped_multi_page_document_reports_no_forced_overflow(int depth)
    {
        var open = string.Concat(Enumerable.Repeat("<div>", depth));
        var close = string.Concat(Enumerable.Repeat("</div>", depth));
        var result = Render(open + Paragraphs(160) + close);

        Assert.True(result.PageCount >= 3, $"expected a multi-page document, got {result.PageCount}");
        // Pre-fix: one warning per page, including the LAST page (the wrapper's measured subtree extent
        // still counted the prior pages' children).
        Assert.Equal(0, ForcedOverflowCount(result));
    }

    [Theory]
    [InlineData("<div style='height:2000px;background:#eef'></div>")]
    [InlineData("<img style='display:block;width:100px;height:2000px' src=\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>\">")]
    public void Box_taller_than_a_page_reports_forced_overflow_exactly_once(string tallBox)
    {
        // A box whose OWN height exceeds a page really is forced past the edge — reported once (pre-fix it
        // was reported on every page, by the body wrapper, rather than for the box itself).
        var result = Render("<p>Before</p>" + tallBox + "<p>After</p>");
        Assert.Equal(1, ForcedOverflowCount(result));
    }

    [Fact]
    public void Break_inside_avoid_region_taller_than_a_page_splits_between_its_children()
    {
        // PR #384 review — an avoid region taller than a page can't be kept whole, so it splits between its
        // children like normal content (docs-site/page-breaks.md). Nothing is forced past a page edge, so no
        // forced-overflow diagnostic — and every paragraph is still rendered.
        var result = Render("<div style='break-inside:avoid'>" + Paragraphs(160) + "</div>");
        Assert.True(result.PageCount >= 3, $"expected a multi-page document, got {result.PageCount}");
        Assert.Equal(0, ForcedOverflowCount(result));
        var pdf = Encoding.Latin1.GetString(result.Pdf);
        // Each paragraph wraps to two lines (two text runs); none is dropped.
        Assert.True(Regex.Matches(pdf, @"<[0-9A-Fa-f]+> *Tj").Count >= 160);
    }

    [Fact]
    public void Flex_one_list_in_a_stretched_column_card_keeps_every_item()
    {
        // 02-travel-quote — cards in a stretched row, each a column flex whose <ul> is `flex: 1`. The list
        // was flexed from a 0 basis to ~0 height, laid out into that ~1px budget, and every item after the
        // first was DROPPED (the only trace was PAGINATION-FORCED-OVERFLOW-001 from the nested pass). By
        // §4.5 the list can't be shorter than its content, and item content never paginates inside the item.
        var cards = string.Concat(Enumerable.Range(0, 3).Select(c =>
            "<div class='opt'><div class='head'>Card</div><ul>"
            + string.Concat(Enumerable.Range(0, 5).Select(i => $"<li>Item{c}x{i}</li>"))
            + "</ul></div>"));
        var result = Render("<div class='options'>" + cards + "</div>",
            ".options{display:flex;gap:14px;align-items:stretch}"
            + ".opt{flex:1;display:flex;flex-direction:column;border:1px solid #ccc}"
            + ".opt ul{flex:1;margin:0;padding:8px 16px;list-style:none}");

        var pdf = Encoding.Latin1.GetString(result.Pdf);
        // 3 headings + 15 list items (list-style:none → no marker runs).
        Assert.Equal(18, Regex.Matches(pdf, @"<[0-9A-Fa-f]+> *Tj").Count);
        Assert.Equal(0, ForcedOverflowCount(result));
    }

    [Fact]
    public void Absolute_box_content_taller_than_the_box_is_all_rendered()
    {
        // A 20px-tall positioned box holding three paragraphs: CSS overflows the box (overflow: visible).
        // Pre-fix the nested layout paginated into the 20px fragmentainer, kept only the first paragraph,
        // and reported PAGINATION-FORCED-OVERFLOW-001.
        var result = Render(
            "<div style='position:relative;height:300px'>"
            + "<div style='position:absolute;top:0;left:0;width:300px;height:20px'>"
            + "<p>FirstAbsParagraph</p><p>SecondAbsParagraph</p><p>ThirdAbsParagraph</p></div></div>");

        var pdf = Encoding.Latin1.GetString(result.Pdf);
        var textRuns = Regex.Matches(pdf, @"<[0-9A-Fa-f]+> *Tj").Count;
        Assert.Equal(3, textRuns);
        Assert.Equal(0, ForcedOverflowCount(result));
    }
}
