// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// End-to-end "keep with next" pagination (CSS Fragmentation L3 §3.2): <c>break-after: avoid</c> /
/// <c>break-before: avoid</c> between siblings, through the real facade with real fonts. The fixed-box
/// geometry cases live in the W3C conformance suite (<c>frag-break-*-avoid-*</c>); these cover TEXT
/// headings, lists, tables, wrappers, chains, and the limits of the rule.
/// <para>Pages are A4 with 20 mm margins (a 971 px content box). Headings are identified by their font
/// size in the content stream (<c>/F1 &lt;size&gt; Tf</c>), so assertions don't depend on glyph widths.</para>
/// </summary>
public sealed class KeepWithNextPaginationTests
{
    private const double HeadingPt = 15;   // 20px headings
    private const double SubheadingPt = 12; // 16px sub-headings
    private const double BodyPt = 10.5;    // 14px body text

    private static string Doc(string body) =>
        "<!DOCTYPE html><html><head><meta charset='utf-8'><style>@page{size:A4;margin:20mm}"
        + "body{margin:0;font:14px/1.3 sans-serif}"
        + "h2{font-size:20px;margin:10px 0}h3{font-size:16px;margin:6px 0}p,ol,table{margin:0}"
        + "</style></head><body>" + body + "</body></html>";

    private static string Filler(int px) => $"<div style='height:{px}px;background:#eef'></div>";

    private static string Paragraph(int sentences) =>
        "<p>" + string.Concat(System.Linq.Enumerable.Repeat("Lorem ipsum dolor sit amet consectetur. ", sentences)) + "</p>";

    /// <summary>Per page (document order), the font sizes of its text runs from top to bottom. Pages with
    /// no text (a filler-only page) are included as empty lists.</summary>
    private static List<List<double>> PageTextSizes(string html, out int pageCount)
    {
        var result = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        pageCount = result.PageCount;
        var pdf = Encoding.Latin1.GetString(result.Pdf);
        var pages = new List<List<double>>();
        foreach (Match sm in Regex.Matches(pdf, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            // A page content stream paints something (a fill or text); font programs and XMP don't.
            if (!s.Contains(" re") && !s.Contains(" Tf")) continue;
            var runs = new List<(double Size, double Y)>();
            foreach (Match m in Regex.Matches(s, @"/F\d+ ([\d.]+) Tf (-?[\d.]+) (-?[\d.]+) Td"))
                runs.Add((Num(m.Groups[1].Value), Num(m.Groups[3].Value)));
            runs.Sort((a, b) => b.Y.CompareTo(a.Y));
            var sizes = new List<double>(runs.Count);
            foreach (var r in runs) sizes.Add(r.Size);
            pages.Add(sizes);
        }
        return pages;
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static bool Is(double size, double expectedPt) => Math.Abs(size - expectedPt) < 0.01;

    [Fact]
    public void Heading_moves_with_the_paragraph_it_introduces()
    {
        var pages = PageTextSizes(Doc(Filler(880) + "<h2 style='break-after:avoid'>Heading</h2>" + Paragraph(40)), out _);
        Assert.Empty(pages[0]);                       // page 1: only the filler
        Assert.True(Is(pages[1][0], HeadingPt));     // page 2 starts with the heading
    }

    [Fact]
    public void Legacy_page_break_after_avoid_works_the_same()
    {
        var pages = PageTextSizes(Doc(Filler(880) + "<h2 style='page-break-after:avoid'>Heading</h2>" + Paragraph(40)), out _);
        Assert.Empty(pages[0]);
        Assert.True(Is(pages[1][0], HeadingPt));
    }

    [Fact]
    public void Break_before_avoid_on_the_paragraph_keeps_the_heading_with_it()
    {
        var pages = PageTextSizes(Doc(Filler(880) + "<h2>Heading</h2>"
            + "<p style='break-before:avoid'>" + string.Concat(System.Linq.Enumerable.Repeat("Lorem ipsum. ", 200)) + "</p>"), out _);
        Assert.Empty(pages[0]);
        Assert.True(Is(pages[1][0], HeadingPt));
    }

    [Fact]
    public void Heading_and_subheading_chain_moves_together()
    {
        // h2 keeps with h3, h3 keeps with the paragraph: all three start page 2.
        var pages = PageTextSizes(Doc(Filler(860)
            + "<h2 style='break-after:avoid'>Heading</h2><h3 style='break-after:avoid'>Sub</h3>" + Paragraph(40)), out _);
        Assert.Empty(pages[0]);
        Assert.True(Is(pages[1][0], HeadingPt));
        Assert.True(Is(pages[1][1], SubheadingPt));
    }

    [Fact]
    public void Heading_is_not_left_alone_before_a_list()
    {
        // A list can split between (and inside) its items, so the rule is satisfied as soon as some list
        // content shares the page with the heading: page 1 must not END with the heading (the
        // 12-terms-and-conditions "3. Changes & Amendments" bug).
        var items = string.Concat(System.Linq.Enumerable.Repeat(
            "<li>Requests to amend a confirmed booking must be made in writing and are subject to availability.</li>", 6));
        var pages = PageTextSizes(Doc(Filler(900) + "<h2 style='break-after:avoid'>Heading</h2><ol>" + items + "</ol>"), out _);
        var lastOnPage1 = pages[0].Count > 0 ? pages[0][^1] : double.NaN;
        Assert.False(Is(lastOnPage1, HeadingPt), "page 1 ends with the heading");
    }

    [Fact]
    public void Heading_and_first_list_item_move_together_instead_of_leaving_one_line()
    {
        // The list is indented (40px padding), so its items wrap narrower than the page. The measure used
        // the page width, under-measured the first item, entered the list at the page bottom, and the item
        // was line-split leaving ONE line behind (orphans: 2 violated). Measured at its real width, the
        // heading + first item don't fit, so both start page 2.
        var items = string.Concat(System.Linq.Enumerable.Repeat(
            "<li>Requests to amend a confirmed booking must be made in writing and are subject to availability.</li>", 6));
        var pages = PageTextSizes(Doc(Filler(900) + "<h2 style='break-after:avoid'>Heading</h2><ol>" + items + "</ol>"), out _);
        Assert.Empty(pages[0]);
        Assert.True(Is(pages[1][0], HeadingPt));
    }

    [Fact]
    public void Heading_moves_with_a_table()
    {
        // Room for the heading but not for the table's first rows: the heading starts page 2 with the table.
        var rows = string.Concat(System.Linq.Enumerable.Repeat("<tr><td>Item</td><td>42.00</td></tr>", 10));
        var pages = PageTextSizes(Doc(Filler(915) + "<h2 style='break-after:avoid'>Heading</h2><table>" + rows + "</table>"), out _);
        Assert.Empty(pages[0]);
        Assert.True(Is(pages[1][0], HeadingPt));
    }

    [Fact]
    public void Heading_inside_a_wrapper_moves_too()
    {
        // Real documents wrap content in a container; the nested layout path must keep too.
        var pages = PageTextSizes(Doc("<main><section>" + Filler(880)
            + "<h2 style='break-after:avoid'>Heading</h2>" + Paragraph(40) + "</section></main>"), out _);
        Assert.Empty(pages[0]);
        Assert.True(Is(pages[1][0], HeadingPt));
    }

    [Fact]
    public void Heading_stays_when_the_first_part_of_the_next_section_fits()
    {
        // The block after the heading is a section with a short first paragraph: only that first part has to
        // share the page, so the heading and the short paragraph stay on page 1 and the long one moves on.
        var pages = PageTextSizes(Doc("<div>" + Filler(820) + "<h2 style='break-after:avoid'>Heading</h2>"
            + "<div><p>Short first line.</p>" + Paragraph(40) + "</div></div>"), out _);
        Assert.True(Is(pages[0][0], HeadingPt));
        Assert.True(Is(pages[0][^1], BodyPt), "page 1 should end with the short paragraph, not the heading");
    }

    [Fact]
    public void Without_avoid_the_heading_stays_at_the_bottom()
    {
        // Control — no rule, no keep (what a browser does too).
        var pages = PageTextSizes(Doc(Filler(880) + "<h2>Heading</h2>" + Paragraph(40)), out _);
        Assert.True(Is(pages[0][^1], HeadingPt));
    }

    [Fact]
    public void Keep_is_dropped_when_heading_and_content_cannot_share_even_a_fresh_page()
    {
        // The paragraph is taller than a whole page, so moving the heading would not keep them together;
        // it stays, and no blank page is produced.
        var pages = PageTextSizes(Doc(Filler(880) + "<h2 style='break-after:avoid'>Heading</h2>" + Paragraph(400)), out var count);
        Assert.True(Is(pages[0][^1], HeadingPt));
        Assert.All(pages.GetRange(1, count - 1), p => Assert.NotEmpty(p));
    }

    [Fact]
    public void Heading_at_the_top_of_a_page_is_never_moved()
    {
        // Forward progress: a heading that already starts a page stays there even if its content doesn't fit.
        var pages = PageTextSizes(Doc("<h2 style='break-after:avoid'>Heading</h2>" + Paragraph(400)), out _);
        Assert.True(Is(pages[0][0], HeadingPt));
    }

    [Fact]
    public void Anonymous_text_does_not_inherit_its_parents_break_after()
    {
        // `break-after: avoid` on the container is about the container's own next sibling. Its loose text
        // (an anonymous block that shares the container's style object) must not be treated as a kept
        // heading, so the text stays at the bottom of page 1 and the tall block after it moves on.
        var pages = PageTextSizes(Doc("<div>" + Filler(900)
            + "<div style='break-after:avoid'>Loose intro text<div>" + string.Concat(System.Linq.Enumerable.Repeat("Block line. ", 150)) + "</div></div></div>"), out _);
        Assert.NotEmpty(pages[0]);
    }

    [Fact]
    public void Long_document_never_ends_a_page_with_a_kept_heading()
    {
        // 40 sections of different lengths: wherever the breaks fall, no page ends with a heading, and no
        // heading is lost.
        var body = new StringBuilder();
        for (var i = 0; i < 40; i++)
            body.Append("<h2 style='break-after:avoid'>Section</h2>").Append(Paragraph(3 + (i * 7) % 23));
        var pages = PageTextSizes(Doc(body.ToString()), out _);

        var headings = 0;
        foreach (var page in pages)
        {
            if (page.Count == 0) continue;
            foreach (var size in page) if (Is(size, HeadingPt)) headings++;
            Assert.False(Is(page[^1], HeadingPt), "a page ends with a heading");
        }
        Assert.Equal(40, headings);
    }
}
