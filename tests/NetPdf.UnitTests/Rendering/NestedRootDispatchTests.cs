// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// RC-6 / RC-8 — nested root-display dispatch. A nested content root (a flex / grid item's content,
/// abspos content, …) that is ITSELF a flex / grid / table container was measured by a plain
/// BlockLayouter, which only dispatches its CHILDREN — so a flex root stacked its items as block flow
/// (RC-6) and a table root, whose rows are neither block-flow nor inline, had every child skipped and
/// was silently DROPPED (RC-8, a rule-7 violation — 07-tax's totals table). Fixed by routing each
/// container root to its own layouter in NestedContentMeasurer.
/// </summary>
public sealed class NestedRootDispatchTests
{
    private static int TextRunCount(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @" Td\b").Count;

    private const string TableRows =
        "<table><tr><td>Subtotal</td><td>$100</td></tr>"
        + "<tr><td>GST</td><td>$10</td></tr><tr><td>Total</td><td>$110</td></tr></table>";

    [Fact]
    public void Table_as_a_direct_flex_item_is_not_dropped()
    {
        // RC-8: `<table>` as a direct flex item must render every row, identical to the same table in
        // a plain block (the control). Before the fix the flex version emitted ZERO text.
        var flex = HtmlPdf.ConvertDetailed(
            "<style>.w{display:flex}td{border:1px solid #000;padding:4px}</style>"
            + "<div class=\"w\">" + TableRows + "</div>");
        var block = HtmlPdf.ConvertDetailed(
            "<style>.w{display:block}td{border:1px solid #000;padding:4px}</style>"
            + "<div class=\"w\">" + TableRows + "</div>");

        Assert.True(TextRunCount(block.Pdf) > 0, "control (block) table should render text");
        Assert.Equal(TextRunCount(block.Pdf), TextRunCount(flex.Pdf));
    }

    [Fact]
    public void Table_as_a_direct_grid_item_is_not_silently_dropped()
    {
        // RC-8 for grid: a `<table>` grid item was silently dropped (0 text, 0 warnings). It now
        // renders. (A grid auto-row track that under-sizes a large table item and truncates it is a
        // separate grid track-sizing residual — but it emits PDF-CONTENT-OVERFLOW-TRUNCATED-001, so
        // it is no longer SILENT: rule 7 — never drop content silently — is satisfied either way.)
        var grid = HtmlPdf.ConvertDetailed(
            "<style>.w{display:grid}td{border:1px solid #000;padding:4px}</style>"
            + "<div class=\"w\">" + TableRows + "</div>");
        var block = HtmlPdf.ConvertDetailed(
            "<style>.w{display:block}td{border:1px solid #000;padding:4px}</style>"
            + "<div class=\"w\">" + TableRows + "</div>");

        Assert.True(TextRunCount(block.Pdf) > 0);
        Assert.True(TextRunCount(grid.Pdf) > 0, "table grid item must render content, not be silently dropped");
        var fullyRendered = TextRunCount(grid.Pdf) == TextRunCount(block.Pdf);
        Assert.True(fullyRendered || grid.Warnings.Count > 0,
            "a partially-rendered table grid item must surface a diagnostic (never a silent drop)");
    }

    [Fact]
    public void Flex_item_that_is_itself_flex_lays_out_horizontally_not_stacked()
    {
        // RC-6: a flex item that is `display: flex` (icon + name lockup). Before the fix it fell to
        // block flow so the name stacked BELOW the logo at the left edge. Now it lays out as flex, so
        // the name's text is horizontally offset past the logo box.
        var res = HtmlPdf.ConvertDetailed(
            "<style>body{margin:0}.brand{display:flex;align-items:center}"
            + ".logo{display:flex;width:40px;height:40px;background:#333}.name{margin-left:10px}</style>"
            + "<div class=\"brand\"><div class=\"logo\"></div><div class=\"name\">Acme</div></div>");
        var s = Encoding.Latin1.GetString(res.Pdf);
        var m = Regex.Match(s, @"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+Td");
        Assert.True(m.Success, "expected a text run for the name");
        var nameX = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        // The logo item is 40px ≈ 30pt wide; the name must start clearly to its right, not at x≈0.
        Assert.True(nameX > 25, $"name x-position {nameX} should be past the logo (flex layout), not stacked at the left");
    }
}
