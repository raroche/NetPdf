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
/// CSS Flexbox §4.5 automatic minimum height of COLUMN flex items, through the real facade (PR #384 review):
/// the content-based floor is capped by <c>max-height</c>, and a definite <c>height</c> caps the floor at
/// min(content, height) instead of switching it off. Heights are read from the item's background rectangle
/// (PDF points = 0.75 × CSS px).
/// </summary>
public sealed class FlexColumnAutomaticMinimumTests
{
    private const double PxToPt = 0.75;

    private static double ItemHeightPx(string itemCss, string content, string containerCss = "")
    {
        var pdf = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><head><meta charset='utf-8'><style>@page{size:A4;margin:20mm}"
            + "body{margin:0;font:12px/1.5 sans-serif}"
            + ".col{display:flex;flex-direction:column;width:300px;" + containerCss + "}"
            + ".item{background:#0000ff;" + itemCss + "}.item div{height:40px}</style></head><body>"
            + "<div class='col'><div class='item'>" + content + "</div></div></body></html>",
            new HtmlPdfOptions { PrintBackgrounds = true }).Pdf;
        var text = Encoding.Latin1.GetString(pdf);
        var m = Regex.Match(text, @"0 0 1 rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");
        Assert.True(m.Success, "item background not painted");
        return double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / PxToPt;
    }

    private static string Blocks(int n) => string.Concat(Enumerable.Repeat("<div></div>", n));

    [Fact]
    public void Content_floor_of_a_flex_one_item_is_capped_by_max_height()
    {
        // Three 40px children (120px of content) in a `flex: 1` item with max-height: 50px → 50px, not 120px.
        Assert.Equal(50, ItemHeightPx("flex:1;max-height:50px", Blocks(3)), precision: 0);
    }

    [Fact]
    public void Content_sized_item_is_capped_by_max_height()
    {
        Assert.Equal(50, ItemHeightPx("max-height:50px", Blocks(3)), precision: 0);
    }

    [Fact]
    public void Definite_height_item_with_taller_content_is_not_shrunk_below_its_height()
    {
        // A 50px column; the item asks for 100px and holds 120px of content. Its automatic minimum is
        // min(120, 100) = 100, so flex-shrink can't take it to 50px. (Column items with a definite height
        // don't shrink at all yet — a separate gap — so this pins the floor for when they do.)
        Assert.Equal(100, ItemHeightPx("height:100px;flex-shrink:1", Blocks(3), "height:50px"), precision: 0);
    }
}
