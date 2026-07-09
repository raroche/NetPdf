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
/// Regression test for the auto-height-float + <c>clear</c> overlap (surfaced by the visual-regression
/// harness on the <c>01-classic</c> invoice). Two bugs conspired: (1) an AUTO-height float was registered
/// with <c>FloatManager</c> at chrome-only height (<c>height:auto</c> read as 0), so its footprint was
/// ~0-tall; (2) an inline-only (text) block with <c>clear</c> never resolved clearance. Together, a
/// <c>clear:both</c> footer after a <c>float:right</c> totals box was placed at the float's TOP and
/// OVERLAPPED it. The float now content-sizes its block axis, and the inline-only path honors <c>clear</c>,
/// so the footer sits BELOW the float.
/// </summary>
public sealed class FloatClearAutoHeightTests
{
    // A right-floated AUTO-height totals box (its own text: TOTALMARKER…), then a clear:both footer
    // (FOOTERMARKER…). Markers are single unbreakable tokens so each is exactly one glyph run.
    private const string Doc =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
        + "@page{size:A4;margin:20mm}body{font-family:Arial;font-size:14px;margin:0}"
        + ".tot{float:right;width:45%;border:1px solid #888;padding:8px}"
        + ".footer{clear:both;border-top:1px solid #000;padding-top:8px}"
        + "</style></head><body>"
        + "<p>IntroTextOnTheLeft</p>"
        + "<div class=\"tot\"><div>SubtotalTwoHundred</div><div>VatThirtyEight</div><div>TOTALMARKERZZZ</div></div>"
        + "<div class=\"footer\">FOOTERMARKERQQQ</div></body></html>";

    /// <summary>Per-page (baseline-y, glyph-count) text runs parsed from the PDF content streams
    /// (higher y = higher on the page).</summary>
    private static List<(double Y, int G)> Runs(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        var runs = new List<(double, int)>();
        foreach (Match sm in Regex.Matches(pdf, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (!s.Contains(" Tf")) continue;
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
        }
        return runs;
    }

    [Fact]
    public void Clear_both_footer_sits_below_an_auto_height_right_float()
    {
        var runs = Runs(Doc);
        Assert.NotEmpty(runs);

        // The two markers are the longest runs; TOTAL (14 glyphs) and FOOTER (15) — both longer than the
        // shorter body words. Identify each by finding its length class rather than exact text (glyph codes
        // aren't readable). TOTALMARKERZZZ = 14, FOOTERMARKERQQQ = 15.
        var footer = runs.Where(r => r.G == 15).OrderBy(r => r.Y).FirstOrDefault();
        var total = runs.Where(r => r.G == 14).OrderBy(r => r.Y).FirstOrDefault();
        Assert.True(footer.G == 15, "footer marker run not found");
        Assert.True(total.G == 14, "float TOTAL marker run not found");

        // In PDF user space, y DECREASES down the page. The cleared footer must sit BELOW the float's last
        // line (Total) — i.e. its baseline y is strictly LESS than Total's. Pre-fix the footer overlapped
        // the float (its y was at/above the float's top, i.e. GREATER than Total's).
        Assert.True(footer.Y < total.Y - 1.0,
            $"clear:both footer (y={footer.Y:0.#}) is not below the auto-height right-float's last line "
            + $"(Total y={total.Y:0.#}) — it overlaps the float.");
    }
}
