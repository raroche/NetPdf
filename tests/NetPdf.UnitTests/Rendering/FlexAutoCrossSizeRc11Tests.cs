// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-2 / RC-2b / RC-11 — auto cross-size flex items. In a ROW container, an item with auto cross
/// size (the universal card / chip / pill pattern) was placed and EMITTED with a cross size of 0:
/// FlexLinePacker sizes lines from declared cross only, so the container / line cross came out 0
/// (RC-2b), `align-items: center`/`flex-end` centered the item as if zero-height (RC-2), and — worst —
/// the item's geometry fragment had BlockSize 0, so FragmentPainter culled its ENTIRE background /
/// border / radius (RC-11, the dropped card decorations across 10+ corpus templates). Fixed by folding
/// the content-measured cross (itemBaselineCrossSizes / autoRowContentCross) into the container cross
/// and the per-item align cross size.
/// </summary>
public sealed class FlexAutoCrossSizeRc11Tests
{
    // Count the card background (#e0e7ff ≈ 0.878 0.905 1) and border (#4f46e5 ≈ 0.31 0.27 0.90) fills.
    private static (int bg, int border) CardFills(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        return (
            Regex.Matches(s, @"0\.87\d* 0\.90\d* 1(?:\.0+)? rg").Count,
            Regex.Matches(s, @"0\.3\d* 0\.2\d* 0\.89\d* rg").Count);
    }

    private static string Row(string extraItemCss = "", string containerCss = "") =>
        "<!doctype html><html><head><style>body{margin:0}"
        + ".row{display:flex;gap:10px;" + containerCss + "}"
        + ".card{flex:1;background:#e0e7ff;border:2px solid #4f46e5;border-radius:8px;padding:12px;" + extraItemCss + "}"
        + "</style></head><body><div class=\"row\">"
        + "<div class=\"card\">Alpha</div><div class=\"card\">Bravo</div><div class=\"card\">Charlie</div>"
        + "</div></body></html>";

    [Fact]
    public void Auto_height_row_flex_cards_paint_their_background_and_border()
    {
        // RC-11: three auto-height cards in a row must each paint bg + border, identical to the same
        // cards given an explicit height (the control). Before the fix the auto version painted NEITHER.
        var auto = CardFills(HtmlPdf.ConvertDetailed(Row(), new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        var fixedH = CardFills(HtmlPdf.ConvertDetailed(Row("height:60px"), new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);

        Assert.Equal(3, fixedH.bg);      // control: 3 cards, 3 backgrounds
        Assert.Equal(3, fixedH.border);
        Assert.Equal(fixedH.bg, auto.bg);
        Assert.Equal(fixedH.border, auto.border);
    }

    [Fact]
    public void Align_items_center_auto_item_still_paints_its_decoration()
    {
        // RC-2 + RC-11: with align-items:center the auto items were centered as if 0-height AND their
        // decoration was culled. They must still paint their background/border under center alignment.
        var centered = CardFills(HtmlPdf.ConvertDetailed(
            Row(containerCss: "align-items:center"), new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        Assert.Equal(3, centered.bg);
        Assert.Equal(3, centered.border);
    }

    [Fact]
    public void Align_items_flex_end_auto_item_still_paints_its_decoration()
    {
        var end = CardFills(HtmlPdf.ConvertDetailed(
            Row(containerCss: "align-items:flex-end"), new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        Assert.Equal(3, end.bg);
        Assert.Equal(3, end.border);
    }
}
