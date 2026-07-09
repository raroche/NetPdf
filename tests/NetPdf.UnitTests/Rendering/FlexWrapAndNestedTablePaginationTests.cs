// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Regression tests for the two round-3 corpus fixes:
/// <list type="number">
///   <item><b>flex-wrap multi-line auto-height overlap</b> (06-travel-voucher "What's Included") — a
///   wrapping <c>display:flex</c> row of auto-height items packed every line at cross-size 0
///   (<c>FlexLinePacker</c> reads DECLARED cross only), so all wrapped lines painted at the same cross
///   offset (items overlapping) and the container collapsed to one line so the trailing sibling overlapped
///   too. FlexLayouter now folds each item's content-measured cross into its line.</item>
///   <item><b>post-fragmented nested-table trailing sibling</b> (invoice-10/11 <c>.note</c>/<c>.stamp</c>)
///   — a <c>body &gt; div &gt; table</c> laid out by the RECURSIVE dispatch advanced the flow cursor by the
///   table's row-0 full-page over-measure instead of its last-fragment committed height, marooning the
///   trailing sibling on a mostly-empty next page. The recursive path now clamps the cursor-advance extent
///   to the committed height.</item>
/// </list>
/// Assertions are RELATIVE (marker below content / rows above marker / distinct cross bands / page count),
/// so they hold under platform-dependent real-font metrics — mirroring
/// <see cref="AutoHeightFlexTimelineFooterTests"/>. Marker strings are single unbreakable tokens (no spaces
/// or hyphens) so they never wrap into more than one glyph run regardless of font.
/// </summary>
public sealed class FlexWrapAndNestedTablePaginationTests
{
    /// <summary>Parse the PDF content streams into per-page lists of (baseline-y, glyph-count) text runs.
    /// A run's glyph count is a stable per-element fingerprint (used to locate a uniquely long marker),
    /// and its y is the emission baseline (higher y = higher on the page). Pages are returned in file /
    /// document order. Copied from <see cref="AutoHeightFlexTimelineFooterTests"/> (a future cleanup can
    /// share it).</summary>
    private static List<List<(double Y, int G)>> Pages(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        var pages = new List<List<(double, int)>>();
        foreach (Match sm in Regex.Matches(pdf, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (!s.Contains(" Tf")) continue;
            var runs = new List<(double, int)>();
            foreach (Match m in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
            {
                var b = m.Groups[1].Value;
                var h = Regex.Match(b, @"<([0-9A-Fa-f]+)> *T[jJ]");
                if (!h.Success) continue;
                double y = double.NaN;
                var tm = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
                if (tm.Success) y = double.Parse(tm.Groups[6].Value, CultureInfo.InvariantCulture);
                else { var td = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)"); if (td.Success) y = double.Parse(td.Groups[2].Value, CultureInfo.InvariantCulture); }
                if (!double.IsNaN(y)) runs.Add((y, h.Groups[1].Value.Length / 4));
            }
            if (runs.Count > 0) pages.Add(runs);
        }
        return pages;
    }

    // A wrapping auto-height ROW flex (li width:50% → 2 per line, 3 lines) with row-gap, followed by a
    // sibling carrying a uniquely long marker token. Pre-fix: the 3 lines collapsed to one cross offset
    // (items overlap) and .after overlapped the list.
    private const string FlexWrapDoc =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
        + "@page{size:A4;margin:16mm}body{font-family:Arial;font-size:12px;margin:0}"
        + "ul{list-style:none;padding:0;margin:0;display:flex;flex-wrap:wrap;row-gap:10px}"
        + "li{width:50%;padding:5px 0;font-size:12px}.after{border:1px solid #000;padding:8px}"
        + "</style></head><body><ul>"
        + "<li>AlphaLeftOne</li><li>BravoRightTwo</li>"
        + "<li>CharlieLeftThree</li><li>DeltaRightFour</li>"
        + "<li>EchoLeftFive</li><li>FoxtrotRightSix</li>"
        + "</ul><div class=\"after\">TRAILINGSIBLINGMARKERZZZZZZZZZZZZZZZZZZ</div></body></html>";

    // A NESTED (body>div>table) table long enough to fragment across two A4 pages, whose last fragment ends
    // high on the final page, followed by a sibling stamp carrying a uniquely long marker token. Pre-fix:
    // the recursive dispatch over-advanced the cursor and marooned the stamp on a mostly-empty extra page.
    private static string NestedTableDoc()
    {
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
            + "@page{size:A4;margin:16mm}body{font-family:Arial;font-size:12px;margin:0}"
            + "table{width:100%;border-collapse:collapse}td{padding:6px;border-bottom:1px solid #ccc}"
            + ".stamp{margin-top:16px;border:2px solid #000;padding:8px;display:inline-block}"
            + "</style></head><body><div class=\"wrap\"><table><tbody>");
        for (var i = 1; i <= 45; i++)
            sb.Append("<tr><td>LineNum").Append(i).Append("</td><td>DescRowValue").Append(i).Append("</td></tr>");
        sb.Append("</tbody></table><div class=\"stamp\">STAMPMARKERBALANCEDUEQQQQQQQQQQQQ</div></div></body></html>");
        return sb.ToString();
    }

    // A WRAPPING auto-height row flex far taller than one page (60 lines) — a correct multi-page
    // implementation would paginate it; the current single-page fold does not (see the deferred test below).
    private static string TallWrapDoc()
    {
        var sb = new StringBuilder(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
            + "@page{size:A4;margin:16mm}body{font-family:Arial;font-size:12px;margin:0}"
            + "ul{list-style:none;padding:0;margin:0;display:flex;flex-wrap:wrap}"
            + "li{width:50%;padding:6px 0;font-size:12px}"
            + "</style></head><body><ul>");
        for (var i = 1; i <= 120; i++)
            sb.Append("<li>RowItemNumber").Append(i).Append("WithDescriptiveText</li>");
        sb.Append("</ul></body></html>");
        return sb.ToString();
    }

    [Fact]
    public void FlexWrap_auto_height_lines_stack_and_trailing_sibling_sits_below()
    {
        var pages = Pages(FlexWrapDoc);
        Assert.Single(pages);   // the single-page 06-travel-voucher shape
        var runs = pages[0];

        // The trailing sibling is the single longest run (a unique marker token).
        var markerG = runs.Max(r => r.G);
        var markerY = runs.First(r => r.G == markerG).Y;

        // (a) NOTHING sits below the trailing sibling → it is not overlapped by (does not overlap) the list.
        foreach (var (y, g) in runs)
            Assert.False(g != markerG && y < markerY - 0.5,
                $"a flex item (glyphs={g}) at y={y:0.#} sits below the trailing sibling (y={markerY:0.#}) — "
                + "the sibling overlaps the wrapped list.");

        // (b) The wrapped auto-height items STACK into >= 3 distinct cross bands (the 3 lines did not
        // collapse to a shared cross offset — the pre-fix bug painted all 3 lines at the same y).
        var bands = runs.Where(r => r.G != markerG)
            .Select(r => Math.Round(r.Y)).Distinct().Count();
        Assert.True(bands >= 3,
            $"expected the 3 wrapped auto-height lines to stack at distinct cross offsets; got {bands} "
            + "band(s) — the lines collapsed onto a single offset (items overlap).");
    }

    [Fact]
    public void Nested_table_trailing_sibling_stays_on_the_final_fragment_page()
    {
        var pages = Pages(NestedTableDoc());
        Assert.True(pages.Count >= 2,
            $"the nested table must fragment across pages for this regression to bite; got {pages.Count} page(s).");

        // The stamp is the single longest run across the document; it must appear exactly once.
        var markerG = pages.SelectMany(p => p).Max(r => r.G);
        var hits = 0; var stampPage = -1; var stampY = double.NaN;
        for (var pi = 0; pi < pages.Count; pi++)
            foreach (var (y, g) in pages[pi]) if (g == markerG) { hits++; stampPage = pi; stampY = y; }
        Assert.Equal(1, hits);

        // The stamp is NOT marooned on a fresh page: its page also carries the table's final fragment
        // (multiple table-row runs ABOVE it). Pre-fix the cursor over-advanced and the stamp landed alone
        // on the next page (rowsAbove == 0).
        var rowsAbove = pages[stampPage].Count(r => r.G != markerG && r.Y > stampY + 20);
        Assert.True(rowsAbove >= 3,
            $"the trailing sibling landed on a page with {rowsAbove} table-row run(s) above it — it should "
            + "flow directly below the table's last fragment on the same page, not on a mostly-empty next page.");

        // The stamp is the last content on its page (nothing below it; these docs have no page footer).
        foreach (var (y, g) in pages[stampPage])
            Assert.False(g != markerG && y < stampY - 0.5,
                $"content (glyphs={g}) at y={y:0.#} sits below the trailing stamp (y={stampY:0.#}).");
    }

    [Fact]
    public void Tall_wrapped_auto_height_flex_pagination_remains_a_known_deferral()
    {
        // KNOWN DEFERRAL — see docs/deferrals.md (the wrap fold's "STILL OPEN — the PRE-measure ...
        // still reads DECLARED cross sizes" note). The single-page overlap fix folds the content-measured
        // per-line cross AFTER FlexLayouter's fragment-range decision (which reads the packer's declared
        // 0 cross) and AFTER BlockLayouter's pre-measure (FlexLinePacker.SumCrossExtent, also declared
        // cross). So a WRAPPING auto-height row flex tall enough to need pagination does NOT yet paginate:
        // the pre-measure sees a ~0-height wrapper so the paginatable-flex clamp never fires, and every
        // line stacks onto ONE page (content past the page bottom is emitted off-page). A correct
        // multi-page implementation would produce >= 2 pages.
        //
        // This test PINS the residual so it is KNOWN, not accidentally "fixed". When the content-aware
        // pre-measure / fragment-range lands, PageCount becomes >= 2 and this assertion fails — that
        // failure is the reminder to flip this to the positive (>= 2) check.
        var pageCount = HtmlPdf.ConvertDetailed(TallWrapDoc(), new HtmlPdfOptions()).PageCount;
        Assert.Equal(1, pageCount);   // DEFERRED: should be >= 2 once multi-page wrapped auto-height lands
    }
}
