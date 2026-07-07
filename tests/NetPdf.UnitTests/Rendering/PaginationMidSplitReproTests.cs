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
/// Faithful repro for the pagination "mid-split" (cycle-2d) density defect — a block-flow subtree
/// (the <c>.timeline</c> of auto-height flex <c>.day</c> rows) that doesn't fit the REMAINING page is
/// committed atomically (moved wholly / force-overflowed) instead of starting on the current page and
/// breaking between its <c>.day</c> children. That under-fills pages (real <c>03-itinerary</c> renders
/// 4 pages where ~2 suffice).
///
/// <para>This mirrors the real <c>03-itinerary-day-by-day.html</c> shape (see
/// <c>docs/design/pagination-mid-split.md</c>): a header band (brandbar + hero + glance + section
/// heading) consuming ~⅓ page, then <c>.timeline</c> of N auto-height flex <c>.day</c> rows, then a
/// trailing <c>.note</c>.</para>
///
/// <para>Density is pinned by <see cref="Pages_are_densely_filled_target"/>; content preservation by
/// <see cref="Content_is_preserved_and_note_is_last"/>; the correctness invariants (margin-collapse
/// across the break, a skewed first child, break-inside:avoid) by the facts below.</para>
/// </summary>
public sealed class PaginationMidSplitReproTests
{

    // A4, 16mm margins, @page margin boxes — same frame as real 03.
    private static string Doc(int days)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><style>");
        sb.Append("@page{size:A4;margin:16mm;");
        sb.Append("@bottom-center{content:\"Page \" counter(page) \" of \" counter(pages)}");
        sb.Append("@top-left{content:\"Azure Horizon Travel\";font-size:8pt;color:#7a8699}}");
        sb.Append("*{box-sizing:border-box}body{font-family:Arial,sans-serif;color:#1f2a3a;font-size:10.5pt;line-height:1.55;margin:0}");
        sb.Append(".brandbar{display:flex;justify-content:space-between;align-items:center;padding-bottom:14px;border-bottom:2px solid #0f7a5a}");
        sb.Append(".brand .name{font-size:15pt;font-weight:700;color:#0f7a5a}.brand .sub{font-size:8.5pt;color:#7a8699}");
        sb.Append(".traveler{text-align:right;font-size:9pt;color:#52627a}");
        sb.Append(".hero{margin-top:20px;background:#0f7a5a;color:#fff;border-radius:14px;padding:22px 26px}");
        sb.Append(".hero h1{font-size:21pt;margin:0}.hero .dates{font-size:10.5pt;margin-top:4px}");
        sb.Append(".glance{display:flex;gap:12px;margin-top:16px}");
        sb.Append(".glance .g{flex:1;border:1px solid #d4e7df;border-radius:12px;padding:12px 14px;background:#f4fbf8;text-align:center}");
        sb.Append(".glance .g .lbl{font-size:8pt;color:#7a8699}.glance .g .val{font-size:13pt;font-weight:700;color:#0f7a5a;margin-top:3px}");
        sb.Append("h2.sec{font-size:11pt;color:#0f7a5a;border-bottom:1px solid #d7dee7;padding-bottom:5px;margin:26px 0 8px}");
        sb.Append(".timeline{margin-top:10px}");
        sb.Append(".day{display:flex;gap:16px;padding:12px 0;border-left:3px solid #d4e7df;margin-left:18px;padding-left:22px;position:relative}");
        sb.Append(".day .badge{position:absolute;left:-20px;top:12px;width:34px;height:34px;border-radius:50%;background:#0f7a5a;color:#fff;font-weight:700;font-size:8pt;display:flex;align-items:center;justify-content:center}");
        sb.Append(".day .body{flex:1}.day .toprow{display:flex;justify-content:space-between;align-items:baseline}");
        sb.Append(".day .loc{font-size:12pt;font-weight:700;color:#14503c}.day .date{font-size:9pt;color:#7a8699}");
        sb.Append(".day .acts{margin:6px 0 0;padding:0;list-style:none;font-size:9.5pt}.day .acts li{display:flex;gap:10px;padding:2px 0}");
        sb.Append(".day .acts .t{min-width:62px;color:#0f7a5a;font-weight:600}.day .acts .d{color:#33414f}");
        sb.Append(".note{margin-top:22px;font-size:8.5pt;color:#7a8699;border-top:1px solid #e4e9ef;padding-top:10px}");
        sb.Append("</style></head><body>");
        sb.Append("<div class=\"brandbar\"><div class=\"brand\"><div><div class=\"name\">Azure Horizon Travel</div><div class=\"sub\">Your Personal Voyage Itinerary</div></div></div>");
        sb.Append("<div class=\"traveler\">Traveler: <strong>Daniel &amp; Elena Whitfield</strong><br>Ref: <strong>AZ-2026-084517</strong><br>Advisor: Priya Nandakumar</div></div>");
        sb.Append("<div class=\"hero\"><h1>Mediterranean Jewels</h1><div class=\"dates\">7-Night Voyage &middot; June 6 &ndash; June 13, 2026 &middot; Aboard the Celestial Meridian</div></div>");
        sb.Append("<div class=\"glance\">");
        sb.Append("<div class=\"g\"><div class=\"lbl\">Nights</div><div class=\"val\">7</div></div>");
        sb.Append("<div class=\"g\"><div class=\"lbl\">Ports</div><div class=\"val\">6</div></div>");
        sb.Append("<div class=\"g\"><div class=\"lbl\">Countries</div><div class=\"val\">3</div></div>");
        sb.Append("<div class=\"g\"><div class=\"lbl\">Stateroom</div><div class=\"val\">10218</div></div>");
        sb.Append("<div class=\"g\"><div class=\"lbl\">Embark</div><div class=\"val\">Barcelona</div></div></div>");
        sb.Append("<h2 class=\"sec\">Your Day-by-Day Plan</h2><div class=\"timeline\">");
        for (var d = 1; d <= days; d++)
        {
            sb.Append("<div class=\"day\"><div class=\"badge\">Day ").Append(d).Append("</div><div class=\"body\">");
            sb.Append("<div class=\"toprow\"><span class=\"loc\">Port City ").Append(d).Append(", Country</span><span class=\"date\">Day ").Append(d).Append(", Jun 2026</span></div>");
            sb.Append("<ul class=\"acts\">");
            for (var a = 0; a < 4; a++)
                sb.Append("<li><span class=\"t\">0").Append(a).Append(":00 PM</span><span class=\"d\">Excursion ").Append(a).Append(" and guided walking tour at port ").Append(d).Append("</span></li>");
            sb.Append("</ul></div></div>");
        }
        sb.Append("</div>");
        // A single-line note (8.5pt, wide content box → no wrap) engineered to be the UNIQUE
        // max-glyph run in the whole document (longer than any header line), so it's easy to locate;
        // it must be the LAST content in the document.
        sb.Append("<div class=\"note\">NOTEMARKERZ times are local to each port and subject to change per weather and captains discretion</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // Strips the running header/footer margin boxes so EVERY text run in the render is body content
    // — makes total-run-count a clean, geometry-independent content-preservation signal (no reliance
    // on a y-band filter that a force-overflow below the page bottom would slip past). Body
    // pagination is unaffected: margin boxes live in the page margin, not the content area.
    private static string Strip(string html) => html
        .Replace("@bottom-center{content:\"Page \" counter(page) \" of \" counter(pages)}", "")
        .Replace("@top-left{content:\"Azure Horizon Travel\";font-size:8pt;color:#7a8699}", "");

    private static string StrippedDoc(int days) => Strip(Doc(days));

    // Same document on a single very tall page (no pagination) — the ground-truth run set. Any
    // faithful pagination must preserve exactly this many runs.
    private static string TallDoc(int days) => Strip(Doc(days).Replace("size:A4", "size:210mm 4000mm"));

    // Per-page list of (baseline-y, glyph-count) text runs. Reuses the extraction from
    // AutoHeightFlexTimelineFooterTests: facade streams are uncompressed so operators are searchable.
    private static List<List<(double y, int g)>> Pages(string html)
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

    private static int TotalRuns(List<List<(double y, int g)>> pages)
    {
        var n = 0;
        foreach (var p in pages) n += p.Count;
        return n;
    }

    // A4 content band in PDF points: page height 842pt, 16mm (=45.35pt) margins. The running
    // header (@top-left, y≈816) sits ABOVE this band and the footer (@bottom-center, y≈18) BELOW,
    // so a body-content run has 45 < y < 800. Content area top ≈ 796.6pt, bottom ≈ 45.35pt.
    private const double BodyBandLo = 45.0;
    private const double BodyBandHi = 800.0;
    private const double ContentAreaTop = 796.6;
    private const double ContentAreaHeight = 751.3;

    private static bool IsBody(double y) => y > BodyBandLo && y < BodyBandHi;

    // Topmost body-content baseline on a page (max y in the content band), or NaN if none.
    private static double TopBodyY(List<(double y, int g)> page)
    {
        var top = double.NaN;
        foreach (var (y, _) in page) if (IsBody(y) && (double.IsNaN(top) || y > top)) top = y;
        return top;
    }

    // Fraction of the content area occupied on a page, measured from the top of the content area
    // down to the lowest body baseline. ~1.0 = full page, small = under-filled (wasted).
    private static double BodyFill(List<(double y, int g)> page)
    {
        var yBot = double.MaxValue;
        var any = false;
        foreach (var (y, _) in page) if (IsBody(y)) { any = true; if (y < yBot) yBot = y; }
        if (!any) return 0;
        return (ContentAreaTop - yBot) / ContentAreaHeight;
    }

    /// <summary>
    /// CONTENT PRESERVATION — active now, and the strongest guard through the enter-and-split fix.
    /// The paginated render must contain exactly the body runs of the single-tall-page render (same
    /// content, only redistributed across pages), and the trailing <c>.note</c> must be the last
    /// content exactly once. A wrong resume index / dropped or duplicated child would break this.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    public void Content_is_preserved_and_note_is_last(int days)
    {
        var tall = Pages(TallDoc(days));
        Assert.Single(tall); // sanity: the tall page holds everything on one page
        var expectedRuns = TotalRuns(tall);

        var pages = Pages(StrippedDoc(days));
        Assert.Equal(expectedRuns, TotalRuns(pages));

        // The note carries the unique "NOTEMARKERZ" token → the single longest glyph run.
        var noteG = 0;
        foreach (var p in pages) foreach (var (_, g) in p) if (g > noteG) noteG = g;
        var noteHits = 0; var notePage = -1; var noteY = double.NaN;
        for (var pi = 0; pi < pages.Count; pi++)
            foreach (var (y, g) in pages[pi]) if (g == noteG) { noteHits++; notePage = pi; noteY = y; }
        Assert.Equal(1, noteHits);
        Assert.Equal(pages.Count - 1, notePage); // the note is on the LAST page
        // Nothing (bar the running header above it) sits below the note on its page.
        foreach (var (y, g) in pages[notePage])
            Assert.False(IsBody(y) && y < noteY - 0.5 && g != noteG,
                $"a body run (glyphs={g}) at y={y:0.#} sits below the footer note (y={noteY:0.#}).");
    }

    /// <summary>
    /// DENSITY TARGET — the goal of the enter-and-split ("mid-split") fix. A block-flow subtree that
    /// doesn't fit the remaining page must start on the current page and break BETWEEN its children,
    /// filling the trailing space, instead of moving wholly to the next page. Then no page before the
    /// last is left substantially under-filled while content remains.
    ///
    /// <para>Before mid-split (days=8): 4 pages, fills [0.36, 0.99, 0.22, 0.06] (header alone on
    /// page 0, note alone on the last page). After: 2 pages, both dense. See
    /// docs/design/pagination-mid-split.md.</para>
    /// </summary>
    // A header band (~⅓ page) then a block-flow container (`.card`) of N single-line paragraphs, tuned
    // so the container overflows the space remaining after the header. `avoid` toggles
    // break-inside:avoid on the container.
    private static string CardDoc(int paras, bool avoid)
    {
        // A4 content area ≈ 1001px tall. Header 300px → ~700px remains on page 0. Each paragraph is a
        // deterministic ~61px (60px height + 1px border). So N paragraphs ≈ N×61px.
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{margin:0;box-sizing:border-box}");
        sb.Append("body{font-family:Arial,sans-serif;font-size:11pt;line-height:1.5}");
        sb.Append(".head{height:300px;background:#eee;padding:12px}");
        sb.Append(".card{border:1px solid #888;padding:4px}");
        sb.Append(avoid ? ".card{break-inside:avoid}" : "");
        sb.Append(".card p{height:60px;border-bottom:1px solid #ddd}");
        sb.Append("</style></head><body><div class=\"head\">Header band content here</div><div class=\"card\">");
        for (var i = 0; i < paras; i++)
            sb.Append("<p>PARA").Append(i).Append(" a single line of paragraph text content number ").Append(i).Append("</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Enter-and-split on a BLOCK-FLOW-child container (not flex): a `.card` of paragraphs that
    /// overflows the space left after the header must START on page 0 (filling it) and break between
    /// its paragraphs — not move wholly to page 1. Content is preserved and page 0 is well filled.
    /// </summary>
    [Fact]
    public void Block_flow_child_container_enters_and_splits()
    {
        var pages = Pages(CardDoc(paras: 30, avoid: false));
        // Page 0 holds the header AND a run of paragraphs (enter-and-split), so it is well filled —
        // NOT just the header (which is ~300px ≈ 0.3 of the content area = what move-wholly would give).
        Assert.True(BodyFill(pages[0]) >= 0.6,
            $"page 0 fill {BodyFill(pages[0]):0.00} — the card did not enter-and-split onto page 0.");
        // Content preserved vs a single tall page.
        var tall = Pages(CardDoc(30, avoid: false).Replace("size:A4", "size:210mm 4000mm"));
        Assert.Single(tall);
        Assert.Equal(TotalRuns(tall), TotalRuns(pages));
    }

    /// <summary>
    /// <c>break-inside: avoid</c> on the container suppresses enter-and-split: a card that FITS a
    /// fresh page must move WHOLLY to the next page rather than be split. So page 0 (header only) is
    /// LESS filled than the non-avoid case, and no paragraph shares page 0 with the header.
    /// </summary>
    [Fact]
    public void Break_inside_avoid_container_is_not_entered()
    {
        // A card (~20 paragraphs) that fits a fresh page but not the space left after the header — so
        // break-inside:avoid is honorable (move the card wholly), and its absence enters+splits.
        const int n = 20;
        var avoid = Pages(CardDoc(paras: n, avoid: true));
        var noAvoid = Pages(CardDoc(paras: n, avoid: false));
        // With avoid, the card moves wholly → page 0 holds only the header (few runs). Without avoid,
        // the card enters and page 0 also holds a run of paragraphs → materially more runs on page 0.
        Assert.True(noAvoid[0].Count > avoid[0].Count + 3,
            $"page0 runs: avoid={avoid[0].Count} noAvoid={noAvoid[0].Count} — break-inside:avoid must "
            + "keep the card off page 0 (move wholly), while its absence enters+splits onto page 0.");
        // The avoided card is NOT fragmented: it lands on a single page (page 1), page 0 is the header.
        Assert.Equal(2, avoid.Count);
        // Content preserved in both renders vs a single tall page.
        var tall = Pages(CardDoc(n, avoid: true).Replace("size:A4", "size:210mm 4000mm"));
        Assert.Single(tall);
        Assert.Equal(TotalRuns(tall), TotalRuns(avoid));
        Assert.Equal(TotalRuns(tall), TotalRuns(noAvoid));
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(10, 2)]
    [InlineData(14, 3)]
    public void Pages_are_densely_filled_target(int days, int expectedPages)
    {
        var pages = Pages(Doc(days));
        Assert.Equal(expectedPages, pages.Count);
        // Every page before the last is substantially filled — no page is wasted while content
        // follows on later pages (the LAST page may be legitimately partial: the content simply ran
        // out, e.g. days=8 ends with 4 days + the note on page 1 at ~0.65 fill).
        for (var pi = 0; pi < pages.Count - 1; pi++)
            Assert.True(BodyFill(pages[pi]) >= 0.75,
                $"days={days}: page {pi} is only {BodyFill(pages[pi]):0.00} filled but content follows on later pages.");
    }

    // A header block of text paragraphs (~half page) then a `.stack` of text paragraphs each with a
    // `margin-top:M`, tuned to enter page 0, fill it, and break BETWEEN paragraphs onto page 1.
    // Paragraphs (line-splittable text) paginate reliably (a leaf height-div can't break). `m`
    // toggles the stack paragraphs' top margin.
    private static string StackDoc(int rows, int m)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{margin:0;box-sizing:border-box}");
        sb.Append("body{font-family:Arial,sans-serif;font-size:11pt;line-height:1.5}");
        sb.Append(".head p{padding:5px 0}.item{margin-top:").Append(m).Append("px}.item p{padding:5px 0}");
        sb.Append("</style></head><body><div class=\"head\">");
        for (var i = 0; i < 16; i++) sb.Append("<p>Header paragraph ").Append(i).Append(" filling the top band with a line of text.</p>");
        // Each `.item` is a block-flow CONTAINER (div > p) so it goes through EmitBlockSubtreeRecursive's
        // per-child topShift path — the exact resume boundary the margin-truncation fix targets.
        sb.Append("</div><div class=\"stack\">");
        for (var i = 0; i < rows; i++) sb.Append("<div class=\"item\"><p>Stack item ").Append(i).Append(" body text on a single line.</p></div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// MARGIN-COLLAPSE ACROSS THE MID-SPLIT BREAK (design-doc correctness invariant). When a container
    /// is resumed at a mid-child boundary, the first resumed child's top margin is truncated at the
    /// fragmentainer boundary (CSS Fragmentation L3 §5.1) — it must NOT re-apply a full top margin that
    /// opens a phantom gap on the resume page. Verified by rendering the same split with a big row
    /// top-margin vs no margin: the FIRST row on the resume page lands at the same y either way (its
    /// margin is truncated), and content is preserved.
    /// </summary>
    [Fact]
    public void First_resumed_child_top_margin_is_truncated_at_the_break()
    {
        var withMargin = Pages(StackDoc(rows: 24, m: 40));
        var noMargin = Pages(StackDoc(rows: 24, m: 0));
        Assert.True(withMargin.Count >= 2 && noMargin.Count >= 2, "expected the stack to split across pages");

        // The resume page (page 1) — its topmost row must sit at the same y with and without the row
        // top-margin, because the first resumed row's margin is truncated at the page break. A phantom
        // (untruncated) margin would push the margined variant's first row LOWER (smaller y).
        var yMargin = TopBodyY(withMargin[1]);
        var yNoMargin = TopBodyY(noMargin[1]);
        Assert.False(double.IsNaN(yMargin) || double.IsNaN(yNoMargin), "resume page had no body content");
        Assert.True(Math.Abs(yMargin - yNoMargin) < 2.0,
            $"resume-page first row y differs (margin={yMargin:0.#}, no-margin={yNoMargin:0.#}) — the "
            + "first resumed child's top margin was not truncated at the fragmentation break.");

        // Content preserved (no dropped/duplicated rows) vs a single tall page.
        var tall = Pages(StackDoc(24, 40).Replace("size:A4", "size:210mm 4000mm"));
        Assert.Single(tall);
        Assert.Equal(TotalRuns(tall), TotalRuns(withMargin));
    }

    // A header of text paragraphs (~half page) + a `.doc` container whose FIRST in-flow child is a
    // `break-inside:avoid` block of `bigParas` paragraphs — much TALLER than the small paragraphs that
    // follow — so it cannot be line-split and must move as one unit. `bigParas` sizes the first child.
    private static string SkewDoc(int bigParas)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{margin:0;box-sizing:border-box}");
        sb.Append("body{font-family:Arial,sans-serif;font-size:11pt;line-height:1.5}");
        sb.Append(".head p,.big p,.doc>p{padding:5px 0}.big{break-inside:avoid;border:1px solid #333}");
        sb.Append("</style></head><body><div class=\"head\">");
        for (var i = 0; i < 18; i++) sb.Append("<p>Header paragraph ").Append(i).Append(" filling the top band with one line of text.</p>");
        sb.Append("</div><div class=\"doc\"><div class=\"big\">");
        // Each big paragraph is the UNIQUELY longest single line in the document, so its glyph run is
        // the max-glyph run — a robust page locator independent of layout tuning.
        for (var i = 0; i < bigParas; i++) sb.Append("<p>BIGFIRST unbreakable first-child paragraph ").Append(i).Append(" is deliberately the single longest line anywhere in this whole document for detection.</p>");
        sb.Append("</div>");
        for (var i = 0; i < 4; i++) sb.Append("<p>trailing small ").Append(i).Append("</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// SKEWED FIRST CHILD — the enter-and-split decision must not enter a container when its FIRST
    /// in-flow child cannot fit the remaining page (a naive per-child average could under-report a
    /// first child much taller than its siblings and wrongly enter, force-overflowing it off the page
    /// bottom). Here the unbreakable first child does not fit the space left after the header, so the
    /// whole `.doc` must move wholly to the next page: page 0 holds only the header, and the first
    /// child renders intact on page 1 without being force-overflowed off page 0.
    /// </summary>
    [Fact]
    public void A_first_child_taller_than_the_remaining_page_moves_wholly_not_split()
    {
        var pages = Pages(SkewDoc(bigParas: 10));
        Assert.True(pages.Count >= 2, "expected a paginated render");

        // The `.big` block's paragraphs carry the unique "BIGFIRST" token (glyph count > any header/
        // small run). If `.doc` had wrongly entered, `.big` would force-overflow onto page 0. Assert
        // NO "BIGFIRST" run appears on page 0 — the container moved wholly, so `.big` is on page 1+.
        var bigG = 0;
        foreach (var p in pages) foreach (var (_, g) in p) if (g > bigG) bigG = g;
        Assert.DoesNotContain(pages[0], r => r.g == bigG);
        // Content preserved vs a single tall page (nothing clipped by an errant force-overflow).
        var tall = Pages(SkewDoc(10).Replace("size:A4", "size:210mm 4000mm"));
        Assert.Single(tall);
        Assert.Equal(TotalRuns(tall), TotalRuns(pages));
    }

    /// <summary>
    /// <c>break-before/after: avoid</c> around a flex child at the mid-split boundary is routed through
    /// the resolver (not a hard-coded fit test); content is preserved either way. (The production
    /// greedy resolver is cost-insensitive, so the avoid is advisory — this pins that adding the
    /// metadata does not drop or duplicate content, and keeps the resolver contract intact.)
    /// </summary>
    [Theory]
    [InlineData("break-before:avoid")]
    [InlineData("break-after:avoid")]
    public void Flex_child_boundary_break_with_avoid_metadata_preserves_content(string avoidRule)
    {
        var doc = Doc(9).Replace(".day{display:flex", ".day{" + avoidRule + ";display:flex");
        var pages = Pages(Strip(doc));
        var tall = Pages(TallDoc(9));
        Assert.Single(tall);
        Assert.Equal(TotalRuns(tall), TotalRuns(pages));
    }
}
