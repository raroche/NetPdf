// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity (02 travel-quote) — RC2 residual (1): an abspos box whose containing block is a
/// <c>position:relative</c> BLOCK DESCENDANT nested inside a flex item (e.g. a bullet inside a
/// <c>&lt;li&gt;</c> in a flex-item <c>&lt;ul&gt;</c>). The <c>&lt;li&gt;</c>'s geometry is emitted
/// through the flex item's CONTENT BUFFER, which did not record positioned-box geometry, so the
/// descendant CB was unresolved and the box was dropped. The buffer flush now records geometry for
/// positioned block descendants, and the redundant nested abspos pass (FlexLayouter is not a delegation
/// boundary) is suppressed so no spurious "dropped" diagnostic leaks.
/// </summary>
public sealed class AbsPosInFlexItemDescendantTests
{
    private const string Css =
        ".row{display:flex;gap:12px}.opt{flex:1}"
        + ".opt ul{list-style:none;margin:0;padding:0}"
        + ".opt li{position:relative;padding-left:18px;margin:4px 0}"
        + ".bullet{position:absolute;left:0;top:2px;width:10px;height:10px;background:#ff0000}";

    private static PdfRenderResult Render(string body) =>
        HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><style>" + Css + "</style></head><body>" + body + "</body></html>",
            new HtmlPdfOptions { PrintBackgrounds = true });

    [Fact]
    public void Abspos_bullet_anchored_to_positioned_li_in_flex_item_renders()
    {
        var res = Render(
            "<div class=\"row\">"
            + "<div class=\"opt\"><ul>"
            + "<li><span class=\"bullet\"></span>Ocean view cabin</li>"
            + "<li><span class=\"bullet\"></span>All meals included</li></ul></div>"
            + "<div class=\"opt\"><ul><li><span class=\"bullet\"></span>Balcony suite</li></ul></div>"
            + "</div>");
        var pdf = Encoding.Latin1.GetString(res.Pdf);

        // Exactly the three bullets paint (0 before the fix) — no drop, no duplicate.
        Assert.Equal(3, Regex.Matches(pdf, @"1 0 0 rg").Count);
        // No spurious absolute-unsupported diagnostic (the nested item-content pass no longer runs it).
        foreach (var w in res.Warnings)
            Assert.DoesNotContain("ABSOLUTE", w.Code);
    }

    [Fact]
    public void Abspos_bullets_anchor_per_li_column_not_the_page()
    {
        // Two flex columns → the second column's bullets sit at a larger inline offset than the first's.
        // A page/ICB fallback would collapse both columns' bullets to the same x. Proves per-li anchoring.
        var res = Render(
            "<div class=\"row\">"
            + "<div class=\"opt\"><ul><li><span class=\"bullet\"></span>A</li></ul></div>"
            + "<div class=\"opt\"><ul><li><span class=\"bullet\"></span>B</li></ul></div></div>");
        var pdf = Encoding.Latin1.GetString(res.Pdf);
        var xs = new System.Collections.Generic.List<double>();
        foreach (Match m in Regex.Matches(pdf, @"([\d.]+) [\d.]+ [\d.]+ [\d.]+ re\s*f"))
            xs.Add(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2, xs.Count);
        Assert.True(System.Math.Abs(xs[0] - xs[1]) > 50,
            $"the two columns' bullets should sit at distinct inline offsets, got {xs[0]} and {xs[1]}");
    }
}
