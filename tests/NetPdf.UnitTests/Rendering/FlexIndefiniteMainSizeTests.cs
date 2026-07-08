// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// A flex item's main size is resolved against the flex container's main size ONLY when that size is
/// DEFINITE (CSS Flexbox L1 §9.7 / CSS 2.2 §10.5). An auto-height COLUMN flex has an INDEFINITE main
/// (block) size — it is content-sized. When such a column flex is a flex ITEM of a row flex, it is
/// MEASURED at the ~1,000,000px unbounded intra-item budget; before the fix a `flex-grow` item (or a
/// `height: 100%` item) resolved against that budget → a ~1M-tall item → thousands of blank pages
/// (10-event-ticket rendered 2003 pages, 02-travel-quote 1001). Now the indefinite main size makes
/// flex-grow contribute no free space and a `%` main size compute to auto, so the column sizes to
/// content.
/// </summary>
public sealed class FlexIndefiniteMainSizeTests
{
    private static int Pages(string body) =>
        HtmlPdf.ConvertDetailed(
            "<!doctype html><html><head><style>@page{size:A4;margin:20px}body{margin:0}</style></head><body>"
            + body + "</body></html>").PageCount;

    [Fact]
    public void FlexGrow_item_in_an_auto_height_column_flex_item_does_not_run_away()
    {
        // row flex > auto-height column flex (its main = block, indefinite) > flex-grow item. The column is
        // measured at the unbounded budget; flex-grow must NOT fill it. Before the fix: 924 pages.
        Assert.True(Pages(
            "<div style=\"display:flex\"><div style=\"display:flex;flex-direction:column\">"
            + "<div style=\"flex:1 1 auto\">content</div></div></div>") <= 2);
    }

    [Fact]
    public void Percent_height_item_in_an_auto_height_column_flex_item_does_not_run_away()
    {
        // Same shape, but the runaway is a `height: 100%` item — a percentage against the indefinite main
        // size must compute to auto (0 / content), not resolve against the 1M budget.
        Assert.True(Pages(
            "<div style=\"display:flex\"><div style=\"display:flex;flex-direction:column\">"
            + "<span style=\"height:100%\">bar</span></div></div>") <= 2);
    }

    [Fact]
    public void FlexGrow_still_fills_a_definite_height_column_flex()
    {
        // Regression guard: when the column flex height IS definite, flex-grow must still distribute the
        // real free space. A 400px column with one flex-grow child + one 100px child grows the child to
        // ~300px — so the two together fill the container on ONE page (no overflow, no extra page).
        Assert.Equal(1, Pages(
            "<div style=\"display:flex;flex-direction:column;height:400px\">"
            + "<div style=\"flex:1 1 auto;background:#eee\">grows</div>"
            + "<div style=\"height:100px;background:#ccc\">fixed</div></div>"));
    }
}
