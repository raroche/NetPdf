// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// F3 used-table-width — auto-table SHRINK-TO-FIT (CSS 2.2 §17.5.2 / CSS Tables L3 §3) and the
/// percentage-column distribution (Step E, §3.5.3). A <c>width: auto</c> table sizes its columns to
/// their max-content instead of filling; a percentage column (the <c>&lt;td class="w-full"&gt;</c>
/// spacer idiom) absorbs the surplus so a following content cell shrinks and hugs the right edge; a
/// percentage width on a NESTED table behaves as auto under the intrinsic probe (Step D) so it does
/// not poison its containing cell's max-content.
/// </summary>
public sealed class TableShrinkToFitTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver(), PrintBackgrounds = true };

    // Filled rects of a given device-RGB fill color → (x, width) pt.
    private static List<(double X, double W)> Rects(string bodyInner, string colorRg = "1 0 0", string extraCss = "") =>
        Regex.Matches(
                Encoding.Latin1.GetString(HtmlPdf.Convert(
                    "<!DOCTYPE html><html><head><style>@page{size:A4;margin:0}*{box-sizing:border-box}"
                    + "body{margin:0}table{border-collapse:collapse}" + extraCss + "</style></head>"
                    + "<body>" + bodyInner + "</body></html>", Opts())),
                Regex.Escape(colorRg) + @" rg\s+(-?[\d.]+) -?[\d.]+ (-?[\d.]+) -?[\d.]+ re")
            .Select(m => (
                X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                W: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();

    [Fact]
    public void Auto_table_shrinks_to_max_content_not_fill()
    {
        // reproH — a single-cell auto table collapses to the cell's max-content, not the ~595pt fill.
        var rects = Rects("<table><tbody><tr><td>X</td></tr></tbody></table>", "1 0 0", "td{background:#f00}");
        Assert.NotEmpty(rects);
        Assert.All(rects, r => Assert.True(r.W < 40 && r.W > 0,
            $"auto cell {r.W:0.##}pt should be max-content, not fill."));
    }

    [Fact]
    public void Percent_spacer_absorbs_extra_and_content_cell_hugs_the_right()
    {
        // Step E — `<td class="w-full">` (width:100%) spacer absorbs the surplus; the content cell
        // shrinks to its max-content and sits at the right edge. A4 content width = 595.28pt.
        var spacer = Rects(
            "<table style='width:100%'><tbody><tr><td class='sp'></td><td class='c'>Total</td></tr></tbody></table>",
            "0 1 0", ".sp{width:100%;background:#0f0}.c{background:#f00}");
        var content = Rects(
            "<table style='width:100%'><tbody><tr><td class='sp'></td><td class='c'>Total</td></tr></tbody></table>",
            "1 0 0", ".sp{width:100%;background:#0f0}.c{background:#f00}");

        Assert.Single(spacer);
        Assert.Single(content);
        // The spacer takes the lion's share; the content cell is narrow and at the right edge.
        Assert.True(spacer[0].W > 500, $"spacer {spacer[0].W:0.##}pt should absorb the extra (~569).");
        Assert.True(content[0].W < 40, $"content cell {content[0].W:0.##}pt should be max-content (~26).");
        Assert.True(content[0].X > 500 && System.Math.Abs(content[0].X + content[0].W - 595.28) < 1.0,
            $"content cell [{content[0].X:0.##}..{content[0].X + content[0].W:0.##}] should hug the right edge.");
    }

    [Fact]
    public void Nested_percent_table_does_not_poison_the_cell_max_content()
    {
        // Step D — a `width:100%` NESTED table behaves as auto under the intrinsic max-content probe
        // (css-sizing-3 §5.2.4), so it shrinks-to-fit instead of resolving 100% against the ~1e6 probe.
        // The outer 2-column auto table: col A "left", col B holds the nested table. Col B must be the
        // nested table's real max-content ("Y"), NOT ~1e6 → the outer table stays narrow, not page-wide.
        var rects = Rects(
            "<table><tbody><tr>"
            + "<td class='a'>left</td>"
            + "<td class='b'><table style='width:100%'><tbody><tr><td>Y</td></tr></tbody></table></td>"
            + "</tr></tbody></table>",
            "1 0 0", ".a{background:#f00}");
        Assert.NotEmpty(rects);
        // Col A ("left") is a handful of pt — nowhere near a poisoned ~page/1e6 width.
        Assert.All(rects, r => Assert.True(r.W < 60,
            $"col A {r.W:0.##}pt — a nested %-table poisoned the cell max-content (col grew huge)."));
    }

    [Theory]
    [InlineData("<colgroup><col style='width:100%'><col></colgroup>")]   // CSS percent on <col>
    [InlineData("<colgroup><col width='100%'><col></colgroup>")]          // HTML width attr percent
    public void Col_percent_spacer_absorbs_extra(string colgroup)
    {
        // review [P2] — Step E must also honor a percentage declared on `<col>` / `<colgroup>`, not
        // only on a cell. A `<col width:100%>` spacer column absorbs the surplus so the content column
        // ("Total") shrinks to max-content and hugs the right edge.
        var content = Rects(
            "<table style='width:100%'>" + colgroup
            + "<tbody><tr><td></td><td class='c'>Total</td></tr></tbody></table>",
            "1 0 0", ".c{background:#f00}");
        Assert.Single(content);
        Assert.True(content[0].W < 40, $"content cell {content[0].W:0.##}pt should be max-content (~26).");
        Assert.True(content[0].X > 500 && System.Math.Abs(content[0].X + content[0].W - 595.28) < 1.0,
            $"content cell [{content[0].X:0.##}..{content[0].X + content[0].W:0.##}] should hug the right "
            + "edge — the <col> percent spacer did not absorb the surplus.");
    }

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
