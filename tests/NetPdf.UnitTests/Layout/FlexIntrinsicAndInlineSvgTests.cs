// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Text.Fonts;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Layout;

/// <summary>
/// Rendering-fidelity regressions found on the travel-document corpus:
/// <list type="bullet">
///   <item><b>RC1</b> — a flex item whose content is BLOCK-level children was measured at the huge
///   max-content available width (a <c>width:auto</c> block fills it), so its flex base size blew up
///   and the flex line falsely overflowed → the sibling collapsed toward min-content and its text
///   wrapped one word per line (the header "Sailing / with / Meridian / …" disaster).</item>
///   <item><b>RC3</b> — an inline <c>&lt;svg&gt;</c> element in HTML flow was not treated as a replaced
///   element: unhandled, it filled the available width (poisoning the flex measure above) and rendered
///   nothing. It is now a replaced element (intrinsic size from width/height/viewBox) painted via the
///   SVG pipeline.</item>
/// </list>
/// </summary>
public sealed class FlexIntrinsicAndInlineSvgTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver() };

    private static string Doc(string body) =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{box-sizing:border-box}"
        + "body{font-family:sans-serif;font-size:10.5pt;margin:0}"
        + ".bar{display:flex;justify-content:space-between;align-items:center}"
        + ".brand{display:flex;align-items:center;gap:12px}.name{font-size:15pt;font-weight:700}"
        + ".right{text-align:right;font-size:9pt}</style></head><body>" + body + "</body></html>";

    // ── RC1: a nested-block-in-flex item must not collapse its space-between sibling ─────────

    [Fact]
    public void Flex_item_with_block_children_does_not_collapse_the_space_between_sibling()
    {
        // The left flex item is a nested flex whose child is a BLOCK wrapper holding block lines — the
        // exact shape that used to measure as 1e6 and collapse `.right` to one word per line.
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='bar'>"
            + "<div class='brand'><div><div class='name'>Azure Horizon Travel</div>"
            + "<div>Curated Ocean Voyages</div></div></div>"
            + "<div class='right'>Sailing with Meridian Cruise Line</div></div>"), Opts());

        // The right block is one logical line; if it collapsed to min-content it would wrap into MANY
        // rows (one per word). Count the distinct text rows on the right half of the page.
        var rightRows = DistinctTextRowsInRightHalf(pdf);
        Assert.True(rightRows <= 1,
            $"the right block collapsed and wrapped into {rightRows} rows — the flex over-measurement regressed.");
    }

    [Fact]
    public void Deeply_nested_auto_width_blocks_still_measure_to_content_not_available()
    {
        // A control with an even deeper auto-width block nest on the left — still must not collapse the right.
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='bar'>"
            + "<div class='brand'><div><div><div class='name'>Azure Horizon Travel</div></div></div></div>"
            + "<div class='right'>Issued March 12 2026</div></div>"), Opts());
        Assert.True(DistinctTextRowsInRightHalf(pdf) <= 1);
    }

    // ── RC3: inline <svg> renders as a replaced element ─────────────────────────────────────

    [Fact]
    public void Inline_svg_renders_as_a_replaced_element()
    {
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><p>logo:</p>"
            + "<svg width='40' height='40' viewBox='0 0 40 40'>"
            + "<circle cx='20' cy='20' r='18' fill='#e02020'/></svg></body></html>", Opts()));

        // The inline SVG is decoded into an image XObject and painted (a `/Name Do`).
        Assert.Contains("/Subtype /Image", pdf);
        Assert.Matches(@"/[A-Za-z0-9]+ Do", pdf);
    }

    [Fact]
    public void Inline_svg_logo_in_a_flex_header_does_not_collapse_the_sibling()
    {
        // The real header shape: a logo <svg> + text block on the left, info block on the right. The svg
        // used to measure as 1e6 and collapse the right block.
        var pdf = HtmlPdf.Convert(Doc(
            "<div class='bar'>"
            + "<div class='brand'><svg width='46' height='46' viewBox='0 0 46 46'>"
            + "<circle cx='23' cy='23' r='20' fill='#0d5c7a'/></svg>"
            + "<div><div class='name'>Azure Horizon Travel</div></div></div>"
            + "<div class='right'>Sailing with Meridian Cruise Line</div></div>"), Opts());
        Assert.True(DistinctTextRowsInRightHalf(pdf) <= 1);
    }

    /// <summary>Number of distinct text baseline rows whose x-origin is on the RIGHT half of an A4 page
    /// (&gt; 297pt). A collapsed right block wraps one word per line → many rows; a correctly-sized one → 1.</summary>
    private static int DistinctTextRowsInRightHalf(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        var ys = new HashSet<int>();
        foreach (Match m in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
        {
            var blk = m.Groups[1].Value;
            double x, y;
            var tm = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
            var td = Regex.Match(blk, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
            if (tm.Success) { x = D(tm.Groups[5].Value); y = D(tm.Groups[6].Value); }
            else if (td.Success) { x = D(td.Groups[1].Value); y = D(td.Groups[2].Value); }
            else continue;
            if (!Regex.IsMatch(blk, @"<[0-9A-Fa-f]+>")) continue;   // has shown text
            if (x > 297) ys.Add((int)(y + 0.5));
        }

        return ys.Count;
    }

    private static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
