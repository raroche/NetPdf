// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Linq;
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
        var pdf = Encoding.Latin1.GetString(
            HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true }).Pdf);

        // Layout-level check (robust to paint-operator form): the `<p>` sits directly below the 18px
        // bar, so its text baseline is near the TOP of the A4 content area (~768pt = 796 top − 18px
        // bar − a line). Before the fix, the fill's height:100% resolved against the page (~750pt), so
        // the bar box was ~750px tall and pushed the `<p>` to the BOTTOM of the page (small y).
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
        Assert.False(double.IsNaN(pY), "no text found");
        Assert.True(pY > 700,
            $"the paragraph below the bar is at y={pY:0.#}pt — it was pushed toward the page bottom "
            + "because the height:100% bar-fill resolved against the page instead of the 18px track.");
    }
}
