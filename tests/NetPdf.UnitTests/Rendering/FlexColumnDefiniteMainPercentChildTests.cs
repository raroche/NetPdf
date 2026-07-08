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
/// Corpus-fidelity (10-event-ticket barcode, F4). A <c>height: 100%</c> child of a flex item that
/// is a <b>column</b> flex item with a DEFINITE main size must resolve against the ITEM's height, not
/// the page. PR #283 fixed this for ROW flex items (definite cross); the COLUMN branch — where the
/// block axis IS the main axis — was left handing the page/fragmentainer budget to the nested content
/// measure, so a <c>height:100%</c> bar resolved against the A4 content box (751pt) instead of the
/// 58px barcode strip. These assert the barcode bars are their real height and the definite/indefinite
/// distinction (css-sizing-3 §5.1.1) is preserved.
/// </summary>
public sealed class FlexColumnDefiniteMainPercentChildTests
{
    // The barcode fill color #1e1b2e → device-RGB `0.11765 0.10588 0.18039` (matches the corpus).
    private const string BarFill = "0.11765 0.10588 0.18039";

    private static List<double> BarHeights(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        // `<r> <g> <b> rg <x> <y> <w> <h> re` — the filled bar's rect follows its fill color.
        return Regex.Matches(pdf,
                Regex.Escape(BarFill) + @" rg\s+-?[\d.]+ -?[\d.]+ -?[\d.]+ (-?[\d.]+) re")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static string Doc(string body, string css) =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:16mm}*{box-sizing:border-box}"
        + "body{margin:0}" + css + "</style></head><body>" + body + "</body></html>";

    [Fact]
    public void Barcode_bars_in_an_auto_height_column_flex_are_the_strip_height_not_the_page()
    {
        // repro3 — auto-height column flex → item .barcode { height:58px } (row flex) → height:100% bars.
        var heights = BarHeights(Doc(
            "<div class='stub'><p>top</p>"
            + "<div class='barcode'><span></span><span></span><span></span></div>"
            + "<p>bottom</p></div>",
            ".stub{display:flex;flex-direction:column;width:210px}"
            + ".barcode{display:flex;align-items:flex-end;height:58px;gap:2px}"
            + ".barcode span{display:block;background:#1e1b2e;height:100%;width:4px}"));

        Assert.NotEmpty(heights);
        // 58px = 43.5pt. Pre-fix these were 751.18pt (the A4 content-box height).
        Assert.All(heights, h => Assert.True(h is > 40 and < 50,
            $"barcode bar height {h:0.##}pt — a height:100% bar in a definite-height column-flex item "
            + "did not resolve against the 58px strip (page-budget leak)."));
    }

    [Fact]
    public void Definite_height_column_container_still_resolves_bars_against_the_inner_strip()
    {
        // repro5 — the column container itself has a definite height (300px); the bars must still
        // resolve against the 58px .barcode item, NOT the 300px container.
        var heights = BarHeights(Doc(
            "<div class='stub'><p>top</p>"
            + "<div class='barcode'><span></span><span></span></div></div>",
            ".stub{display:flex;flex-direction:column;width:210px;height:300px}"
            + ".barcode{display:flex;align-items:flex-end;height:58px;gap:2px}"
            + ".barcode span{display:block;background:#1e1b2e;height:100%;width:4px}"));

        Assert.NotEmpty(heights);
        Assert.All(heights, h => Assert.True(h is > 40 and < 50,
            $"bar height {h:0.##}pt — resolved against the 300px container, not the 58px strip."));
    }

    [Fact]
    public void Plain_block_item_with_definite_height_also_confines_percent_child()
    {
        // repro7 — NOT specific to a nested flex root: a plain BLOCK item height:58px with a
        // height:100% BLOCK child must confine the child to 58px too.
        var heights = BarHeights(Doc(
            "<div class='stub'><div class='item'><div class='fill'></div></div></div>",
            ".stub{display:flex;flex-direction:column;width:210px}"
            + ".item{height:58px}"
            + ".fill{background:#1e1b2e;height:100%;width:40px}"));

        Assert.NotEmpty(heights);
        Assert.All(heights, h => Assert.True(h is > 40 and < 50,
            $"block child height {h:0.##}pt — a definite-height plain block item did not confine its "
            + "height:100% child."));
    }

    [Fact]
    public void Flexed_definite_item_uses_its_grown_height_as_the_percent_base()
    {
        // Definite-height column container (400px), single item height:50px flex-grow:1 → the item
        // GROWS to fill 400px; a height:100% child resolves against the GROWN used size (not 50px,
        // not the page). Locks in "read resolvedItemMainSizes, not the declared slot".
        var heights = BarHeights(Doc(
            "<div class='stub'><div class='item'><div class='fill'></div></div></div>",
            ".stub{display:flex;flex-direction:column;width:210px;height:400px}"
            + ".item{height:50px;flex-grow:1}"
            + ".fill{background:#1e1b2e;height:100%;width:40px}"));

        Assert.NotEmpty(heights);
        // 400px = 300pt (the whole container is the single grown item). Not 50px (37.5pt), not page.
        Assert.All(heights, h => Assert.True(h is > 250 and < 320,
            $"fill height {h:0.##}pt — the child did not resolve against the item's GROWN (400px) height."));
    }

    [Fact]
    public void Content_determined_auto_height_item_keeps_the_page_budget_for_content()
    {
        // Guard the IsMainSizeContentDetermined gate: an AUTO-height column item content-sizes; a
        // height:50% child resolves as auto (indefinite base → 0). The item's TEXT must still render
        // (the content measure must NOT be clipped to a stale 0 budget) — the paragraph is present.
        var pdf = Encoding.Latin1.GetString(HtmlPdf.ConvertDetailed(Doc(
            "<div class='stub'><div class='item'>visible text<div class='fill'></div></div></div>",
            ".stub{display:flex;flex-direction:column;width:210px}"
            + ".item{}"
            + ".fill{background:#1e1b2e;height:50%;width:40px}"),
            new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        // The text renders (a Tj glyph-show op is present) — content was not clipped to 0.
        Assert.Matches(@"<[0-9A-Fa-f]+> *Tj", pdf);
    }
}
