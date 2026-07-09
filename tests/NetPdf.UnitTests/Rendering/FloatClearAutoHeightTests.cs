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
/// Regression tests for the auto-height-float + <c>clear</c> overlap (surfaced by the visual-regression
/// harness on the <c>01-classic</c> invoice) and its PR #314-review follow-ups. Two bugs conspired: (1) an
/// AUTO-height float was registered with <c>FloatManager</c> at chrome-only height (<c>height:auto</c> read
/// as 0), so its footprint was ~0-tall; (2) an inline-only (text) block with <c>clear</c> never resolved
/// clearance. Together, a <c>clear</c> footer after a float was placed at the float's TOP and OVERLAPPED it.
///
/// <para>Executable end-to-end coverage here (via the facade + PDF text-run positions, relative
/// marker-below-marker assertions): <c>clear:both</c> after a right BLOCK-CHILD float, and <c>clear:left</c>
/// after a left BLOCK-CHILD float. Two related cases are deliberately NOT end-to-end here and are covered /
/// explained elsewhere — see the notes below each: the DIRECT-TEXT float chrome double-count guard (a
/// separate pre-existing emission gap makes it unobservable through the facade), and the break-planning
/// pre-check that DEFERS a too-tall auto-height float (body floats route through the recursion, so it is
/// covered by the lower-level
/// <see cref="NetPdf.UnitTests.Phase3.BlockLayouterTests.Auto_height_float_content_sizes_in_the_break_precheck_and_defers"/>).</para>
/// </summary>
public sealed class FloatClearAutoHeightTests
{
    /// <summary>Parse the PDF content streams into per-page lists of (baseline-y, glyph-count) text runs
    /// (higher y = higher on the page; pages in file order). One text-bearing content stream per page.</summary>
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

    private static List<(double Y, int G)> AllRuns(string html) => Pages(html).SelectMany(p => p).ToList();

    /// <summary>The single run whose glyph count is EXACTLY <paramref name="glyphs"/>. Markers are built with
    /// unique lengths so this is deterministic — <see cref="Enumerable.Single{T}(IEnumerable{T})"/> throws if
    /// a body word accidentally collides (the false-pass the FirstOrDefault-by-length approach risked).</summary>
    private static (double Y, int G) Marker(IEnumerable<(double Y, int G)> runs, int glyphs) =>
        runs.Single(r => r.G == glyphs);

    // ── clear:both after a right float ────────────────────────────────────────────────────────────────
    // Markers have UNIQUE glyph lengths (17 / 19); all body words are short (<= 5) so Single() is exact.
    private const string RightFloatDoc =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
        + "@page{size:A4;margin:20mm}body{font-family:Arial;font-size:14px;margin:0}"
        + ".tot{float:right;width:45%;border:1px solid #888;padding:8px}"
        + ".footer{clear:both;border-top:1px solid #000;padding-top:8px}"
        + "</style></head><body>"
        + "<p>Intro</p>"
        + "<div class=\"tot\"><div>Alpha</div><div>Beta</div><div>RIGHTFLOATLASTAAA</div></div>"   // 17
        + "<div class=\"footer\">CLEARBOTHFOOTERBBBB</div></body></html>";                          // 19

    [Fact]
    public void Clear_both_footer_sits_below_an_auto_height_right_float()
    {
        var runs = AllRuns(RightFloatDoc);
        Assert.NotEmpty(runs);

        // RIGHTFLOATLASTAAA = 17 glyphs (float's LAST line); CLEARBOTHFOOTERBBBB = 19 (the cleared footer).
        var floatLast = Marker(runs, 17);
        var footer = Marker(runs, 19);

        // PDF user space: y DECREASES down the page. The cleared footer must sit strictly BELOW the float's
        // last line. Pre-fix the footer overlapped the float (y at/above the float, i.e. GREATER).
        Assert.True(footer.Y < floatLast.Y - 1.0,
            $"clear:both footer (y={footer.Y:0.#}) is not below the auto-height right-float's last line "
            + $"(y={floatLast.Y:0.#}) — it overlaps the float.");
    }

    // ── clear:left after a left float ─────────────────────────────────────────────────────────────────
    private const string LeftFloatDoc =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
        + "@page{size:A4;margin:20mm}body{font-family:Arial;font-size:14px;margin:0}"
        + ".side{float:left;width:40%;border:1px solid #888;padding:8px}"
        + ".below{clear:left;padding-top:8px}"
        + "</style></head><body>"
        + "<div class=\"side\"><div>One</div><div>Two</div><div>LEFTFLOATLASTCCCCCC</div></div>"    // 19
        + "<div class=\"below\">CLEARLEFTBELOWDDDDDDD</div></body></html>";                          // 21

    [Fact]
    public void Clear_left_block_sits_below_an_auto_height_left_float()
    {
        var runs = AllRuns(LeftFloatDoc);
        var floatLast = Marker(runs, 19);
        var below = Marker(runs, 21);
        Assert.True(below.Y < floatLast.Y - 1.0,
            $"clear:left block (y={below.Y:0.#}) is not below the auto-height left-float's last line "
            + $"(y={floatLast.Y:0.#}) — it overlaps the float.");
    }

    // NOTE on the inline-only-root double-count guard (ContentSizeAutoFloatBlock's ContainsDecorationOwner
    // branch): a float whose content is DIRECT text (no block child) would be the case where the measured
    // buffer folds the float chrome into its extent, so re-adding chrome double-counts. That branch is a
    // future-proof guard mirroring the inline-block auto-height path — it is byte-identical for the block-
    // child float case exercised above. It is NOT covered by an end-to-end test here because direct inline
    // text inside a float is a SEPARATE, pre-existing emission gap (the float's border paints but its direct
    // text is dropped), so the facade can't surface the double-count through a text-run assertion.

    // NOTE on the break-planning pre-check (WouldFloatOverflow content-sizing an auto-height float so a
    // too-tall float DEFERS instead of overflowing): body-level floats route through the RECURSIVE emit
    // path (EmitNestedFloat), which does not defer, so the facade can't reach the top-level deferral. That
    // fix is covered by a LOW-LEVEL BlockLayouter test driving the top-level loop directly —
    // BlockLayouterTests.Auto_height_float_content_sizes_in_the_break_precheck_and_defers.
}
