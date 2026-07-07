// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Corpus-fidelity (08 sales-report bar bleed). A block child with <c>height: 100%</c> inside a ROW
/// flex item that has a DEFINITE cross size (an explicit <c>height</c>) must fill the ITEM's height,
/// not the whole page. The nested content measure uses its block budget as the percentage-height base
/// for the item's block children; before the fix it passed the page height, so a gradient
/// <c>.bar-fill</c> painted (and clipped) down the entire page instead of a fixed-height <c>.bar-track</c>.
/// </summary>
public sealed class FlexDefiniteHeightPercentChildTests
{
    [Fact]
    public void Percent_height_child_of_a_fixed_height_row_flex_item_fills_the_item_not_the_page()
    {
        const string html = "<!DOCTYPE html><html><head><style>*{box-sizing:border-box}"
            + ".row{display:flex;align-items:center}"
            + ".track{flex:1;height:18px;overflow:hidden;border-radius:6px}"
            + ".fill{height:100%;border-radius:6px;background:linear-gradient(90deg,#4338ca,#7c3aed)}</style></head><body>"
            + "<div class=\"row\"><div class=\"track\"><div class=\"fill\" style=\"width:100%\"></div></div></div>"
            + "<p>content below the bar</p></body></html>";
        var pY = ParagraphBaselineY(html);
        Assert.False(double.IsNaN(pY), "no text found");
        Assert.True(pY > 700,
            $"the paragraph below the bar is at y={pY:0.#}pt — it was pushed toward the page bottom "
            + "because the height:100% bar-fill resolved against the page instead of the 18px track.");
    }

    [Fact]
    public void Percent_height_item_in_a_definite_height_row_container_fills_the_item_not_the_page()
    {
        // The ITEM's own cross size is a PERCENTAGE (`height:100%`), definite only because the row
        // container has an explicit height. The percentage must resolve against the container's
        // definite cross (60px) — NOT the page — so the item's own `height:100%` grandchild fill is a
        // 60px bar, and the `<p>` stays near the top. Before the container-base overload, the item's
        // percentage cross resolved to 0/page and the fill leaked down the page.
        const string html = "<!DOCTYPE html><html><head><style>*{box-sizing:border-box}"
            + ".row{display:flex;align-items:stretch;height:60px}"
            + ".track{flex:1;height:100%;overflow:hidden}"
            + ".fill{height:100%;background:linear-gradient(90deg,#4338ca,#7c3aed)}</style></head><body>"
            + "<div class=\"row\"><div class=\"track\"><div class=\"fill\" style=\"width:100%\"></div></div></div>"
            + "<p>content below the bar</p></body></html>";
        var pY = ParagraphBaselineY(html);
        Assert.False(double.IsNaN(pY), "no text found");
        Assert.True(pY > 680,
            $"the paragraph below the bar is at y={pY:0.#}pt — the item's percentage cross did not "
            + "resolve against the 60px definite container height.");
    }

    [Fact]
    public void Definite_zero_content_height_is_preserved_not_replaced_by_the_page_budget()
    {
        // box-sizing:border-box; height:18px; padding:9px 0 → content height is exactly 0. That DEFINITE
        // ZERO must be the percentage-height base (a `height:100%` child gets 0) — the measure must NOT
        // fall back to the page budget. The `<p>` therefore stays near the top (the bar is ~18px tall,
        // its content 0). A page-budget fallback would make the fill ~750px and push the `<p>` down.
        const string html = "<!DOCTYPE html><html><head><style>*{box-sizing:border-box}"
            + ".row{display:flex;align-items:center}"
            + ".track{flex:1;height:18px;padding:9px 0;overflow:hidden}"
            + ".fill{height:100%;background:linear-gradient(90deg,#4338ca,#7c3aed)}</style></head><body>"
            + "<div class=\"row\"><div class=\"track\"><div class=\"fill\" style=\"width:100%\"></div></div></div>"
            + "<p>content below the bar</p></body></html>";
        var pY = ParagraphBaselineY(html);
        Assert.False(double.IsNaN(pY), "no text found");
        Assert.True(pY > 700,
            $"the paragraph below the bar is at y={pY:0.#}pt — a definite-zero content height was "
            + "replaced by the page budget, so the fill leaked down the page.");
    }

    // The last text baseline's y (the `<p>` below the bar), robust to Tm/Td/TD operator form.
    private static double ParagraphBaselineY(string html)
    {
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);
        double pY = double.NaN;
        foreach (Match sm in Regex.Matches(pdf, @"stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var s = sm.Groups[1].Value;
            if (!s.Contains(" Tf")) continue;
            foreach (Match bt in Regex.Matches(s, @"BT(.*?)ET", RegexOptions.Singleline))
            {
                var b = bt.Groups[1].Value;
                if (!Regex.IsMatch(b, @"<[0-9A-Fa-f]+> *T[jJ]")) continue;
                var tm = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
                if (tm.Success) { pY = double.Parse(tm.Groups[6].Value, CultureInfo.InvariantCulture); continue; }
                var td = Regex.Match(b, @"(-?[\d.]+) (-?[\d.]+) (?:Td|TD)");
                if (td.Success) pY = double.Parse(td.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }
        return pY;
    }
}
