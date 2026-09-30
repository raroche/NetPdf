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
/// End-to-end regressions for the 2026-09-30 visual review of the NetPdf-tester corpus, through the real
/// facade (real fonts, real pagination, real PDF bytes):
/// <list type="number">
///   <item><b>12-terms-and-conditions</b> — an <c>h2 { break-after: avoid }</c> was left alone at the page
///   bottom because the greedy resolver ignores the avoid flag; the keep-with-next lookahead now moves it
///   with its section.</item>
///   <item><b>11-course-completion-certificate</b> — a non-stretched auto-width item in a COLUMN flex was
///   placed as if 0 wide, so <c>align-items: center</c> put its left edge on the center line.</item>
///   <item><b>06-travel-voucher</b> — <c>li { width: 50% }</c> in a row flex wrapped its text at 25% (the
///   percentage was applied twice).</item>
///   <item><b>01-cruise-booking-confirmation</b> — flex items shrank below their longest word
///   (<c>min-width: auto</c> was treated as 0), so a long value overlapped its label.</item>
/// </list>
/// Assertions are RELATIVE (same page / same line / centers / edges), so they hold under platform font
/// metrics. The PDF content streams are uncompressed, so operators are string-searchable.
/// </summary>
public sealed class CorpusVisualReviewTests
{
    private static byte[] Render(string html) =>
        HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf;

    private static IEnumerable<string> ContentStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        foreach (Match sm in Regex.Matches(text, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (s.Contains(" re") || s.Contains(" Tf")) yield return s;
        }
    }

    /// <summary>The number of content streams (pages) that show any text.</summary>
    private static int PagesWithText(byte[] pdf)
    {
        var n = 0;
        foreach (var s in ContentStreams(pdf))
            if (Regex.IsMatch(s, @"<[0-9A-Fa-f]+> *T[jJ]")) n++;
        return n;
    }

    /// <summary>Text runs as (x, baseline-y) from each <c>BT … Tm|Td … Tj ET</c> block.</summary>
    private static List<(double X, double Y)> TextRuns(byte[] pdf)
    {
        var runs = new List<(double, double)>();
        foreach (var s in ContentStreams(pdf))
        {
            foreach (Match m in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
            {
                var b = m.Groups[1].Value;
                if (!Regex.IsMatch(b, @"<[0-9A-Fa-f]+> *T[jJ]")) continue;
                var tm = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
                if (tm.Success)
                {
                    runs.Add((Num(tm.Groups[5].Value), Num(tm.Groups[6].Value)));
                    continue;
                }
                var td = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
                if (td.Success)
                    runs.Add((Num(td.Groups[1].Value), Num(td.Groups[2].Value)));
            }
        }
        return runs;
    }

    /// <summary>Filled rectangles painted in the exact device-RGB fill (<c>r g b rg … x y w h re</c>).</summary>
    private static List<(double X, double Y, double W, double H)> Rects(byte[] pdf, double r, double g, double b)
    {
        var rects = new List<(double, double, double, double)>();
        foreach (var s in ContentStreams(pdf))
        {
            foreach (Match m in Regex.Matches(s,
                @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re"))
            {
                if (Math.Abs(Num(m.Groups[1].Value) - r) > 0.002
                    || Math.Abs(Num(m.Groups[2].Value) - g) > 0.002
                    || Math.Abs(Num(m.Groups[3].Value) - b) > 0.002) continue;
                rects.Add((Num(m.Groups[4].Value), Num(m.Groups[5].Value),
                    Num(m.Groups[6].Value), Num(m.Groups[7].Value)));
            }
        }
        return rects;
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // ── 12-terms-and-conditions: keep-with-next ────────────────────────────────────────────────

    /// <summary>A text-free filler that leaves room for the heading but not for the paragraph after it,
    /// then the heading and a ~25-line paragraph. Only the heading and paragraph carry text, so "pages
    /// that show text" tells where the heading landed.</summary>
    private static string KeepDoc(string headingCss, bool wrapped)
    {
        var body = "<div style='height:880px;background:#eef'></div>"
            + "<h2 style='margin:10px 0;" + headingCss + "'>Heading</h2>"
            + "<p style='margin:0'>" + string.Concat(System.Linq.Enumerable.Repeat(
                "Lorem ipsum dolor sit amet consectetur. ", 60)) + "</p>";
        if (wrapped) body = "<div class='wrap'>" + body + "</div>";
        return "<!DOCTYPE html><html><head><meta charset='utf-8'><style>@page{size:A4;margin:20mm}"
            + "body{margin:0;font:14px sans-serif}</style></head><body>" + body + "</body></html>";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]  // inside a wrapper (the nested recursion — where the corpus puts its headings)
    public void Heading_with_break_after_avoid_moves_to_the_page_of_its_content(bool wrapped)
    {
        var pdf = Render(KeepDoc("break-after:avoid", wrapped));
        // The heading and the paragraph share page 2; page 1 holds only the text-free filler.
        Assert.Equal(1, PagesWithText(pdf));
    }

    [Fact]
    public void Heading_without_break_after_avoid_stays_at_the_page_bottom()
    {
        // Control: with no avoid the heading fits page 1 and stays there (browser behavior).
        var pdf = Render(KeepDoc("", wrapped: false));
        Assert.Equal(2, PagesWithText(pdf));
    }

    // ── 11-course-completion-certificate: column flex fit-content ──────────────────────────────

    [Theory]
    [InlineData("center")]
    [InlineData("flex-end")]
    public void Column_flex_item_is_fit_content_and_aligned(string align)
    {
        var pdf = Render(
            "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}body{margin:0;font:12px sans-serif}"
            + ".seal{display:flex;flex-direction:column;align-items:" + align + ";width:160px;background:#ff0000}"
            + ".id{margin:0;background:#0000ff;color:#fff}</style></head><body>"
            + "<div class='seal'><p class='id'>Short</p></div></body></html>");

        var seal = Assert.Single(Rects(pdf, 1, 0, 0));
        // Pre-fix the item was 0 wide, so its background was culled entirely.
        var id = Assert.Single(Rects(pdf, 0, 0, 1));
        Assert.True(id.W > 1 && id.W < seal.W - 1, $"fit-content width expected; got {id.W:0.##} of {seal.W:0.##}");
        if (align == "center")
            Assert.Equal(seal.X + seal.W / 2, id.X + id.W / 2, precision: 1);
        else
            Assert.Equal(seal.X + seal.W, id.X + id.W, precision: 1);
    }

    // ── 06-travel-voucher: percentage width applied once ───────────────────────────────────────

    [Fact]
    public void Percentage_width_flex_item_text_uses_the_whole_item_width()
    {
        var pdf = Render(
            "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}body{margin:0;font:12px sans-serif}"
            + "ul{display:flex;flex-wrap:wrap;width:500px;margin:0;padding:0;list-style:none}"
            + "li{width:50%}</style></head><body>"
            + "<ul><li>Left</li><li>Complimentary Wi-Fi throughout</li></ul></body></html>");

        // One line per item on one row: 2 runs on the same baseline. Pre-fix the right item wrapped at a
        // quarter of the list (its 50% applied again), adding a second line.
        var runs = TextRuns(pdf);
        Assert.Equal(2, runs.Count);
        Assert.Equal(runs[0].Y, runs[1].Y, precision: 2);
    }

    // ── 01-cruise-booking-confirmation: flex automatic minimum size ────────────────────────────

    [Fact]
    public void Flex_value_wider_than_its_row_overflows_instead_of_overlapping_the_label()
    {
        var pdf = Render(
            "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}body{margin:0;font:12px sans-serif}"
            + ".kv{display:flex;justify-content:space-between;width:200px;background:#00ff00}"
            + ".k{background:#ff0000}.v{background:#0000ff;color:#fff}</style></head><body>"
            + "<div class='kv'><span class='k'>Email</span>"
            + "<span class='v'>d.whitfield@example.com.longer</span></div></body></html>");

        var row = Assert.Single(Rects(pdf, 0, 1, 0));
        var label = Assert.Single(Rects(pdf, 1, 0, 0));
        var value = Assert.Single(Rects(pdf, 0, 0, 1));
        Assert.True(value.X >= label.X + label.W - 0.01, "the value box must start after the label box");
        // min-width:auto floors each item at its longest word, so the unbreakable value overflows the row
        // (pre-fix it shrank to the row's end and its text spilled back over the label).
        Assert.True(value.X + value.W > row.X + row.W + 1,
            $"value should overflow the row: value right {value.X + value.W:0.##}, row right {row.X + row.W:0.##}");
    }

    [Fact]
    public void Overflow_hidden_value_still_shrinks_to_the_row()
    {
        // `overflow: hidden` (a scroll container) has no automatic minimum, so the value shrinks and ends
        // at the row's end — which also proves the `overflow` shorthand now reaches the cascade.
        var pdf = Render(
            "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}body{margin:0;font:12px sans-serif}"
            + ".kv{display:flex;justify-content:space-between;width:200px;background:#00ff00}"
            + ".v{background:#0000ff;color:#fff;overflow:hidden}</style></head><body>"
            + "<div class='kv'><span>Email</span>"
            + "<span class='v'>d.whitfield@example.com.longer</span></div></body></html>");

        var row = Assert.Single(Rects(pdf, 0, 1, 0));
        var value = Assert.Single(Rects(pdf, 0, 0, 1));
        Assert.Equal(row.X + row.W, value.X + value.W, precision: 1);
    }
}
