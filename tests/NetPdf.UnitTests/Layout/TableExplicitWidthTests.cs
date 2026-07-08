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
/// F3 used-table-width, Step B (CSS 2.2 §17.5.2): an explicit <c>width</c> on a table wrapper is now
/// honored — previously the <c>display: table</c> wrapper kind was excluded from the explicit-width
/// resolution in <c>BlockLayouter.ResolveInFlowBorderBoxInlineSize</c>, so a table ALWAYS filled its
/// containing block
/// and a declared <c>width</c> (px or %) had no effect (repro J of the F3 finding). A <c>width: auto</c>
/// table still fills (shrink-to-fit is the deferred remainder of F3 — see the PR / deferrals).
/// </summary>
public sealed class TableExplicitWidthTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver(), PrintBackgrounds = true };

    // Filled cell background rects (#ff0000 → `1 0 0 rg <x> <y> <w> <h> re`) as (x, width) pt pairs.
    private static List<(double X, double W)> CellRects(string bodyInner, string extraTableCss = "") =>
        Regex.Matches(
                Encoding.Latin1.GetString(HtmlPdf.Convert(
                    "<!DOCTYPE html><html><head><style>@page{size:A4;margin:0}*{box-sizing:border-box}"
                    + "body{margin:0}table{border-collapse:collapse;" + extraTableCss + "}td{background:#ff0000}</style></head>"
                    + "<body>" + bodyInner + "</body></html>", Opts())),
                @"\b1 0 0 rg\s+(-?[\d.]+) -?[\d.]+ (-?[\d.]+) -?[\d.]+ re")
            .Select(m => (
                X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                W: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();

    private static List<double> CellRectWidths(string tableAttrsAndStyle) =>
        CellRects(tableAttrsAndStyle).Select(r => r.W).ToList();

    [Fact]
    public void Explicit_px_width_on_a_table_is_honored()
    {
        // 150px = 112.5pt. Pre-fix the cell filled the full A4 content width (595.28pt).
        var widths = CellRectWidths("<table style='width:150px'><tbody><tr><td>X</td></tr></tbody></table>");
        Assert.NotEmpty(widths);
        Assert.All(widths, w => Assert.True(System.Math.Abs(w - 112.5) < 1.0,
            $"cell width {w:0.##}pt — expected 112.5pt (150px); explicit table width was ignored."));
    }

    [Fact]
    public void Explicit_percent_width_on_a_table_is_honored()
    {
        // A4 content width = 595.28pt (margin:0); 50% = 297.64pt.
        var widths = CellRectWidths("<table style='width:50%'><tbody><tr><td>X</td></tr></tbody></table>");
        Assert.NotEmpty(widths);
        Assert.All(widths, w => Assert.True(System.Math.Abs(w - 297.64) < 2.0,
            $"cell width {w:0.##}pt — expected ~297.6pt (50% of A4); explicit % table width was ignored."));
    }

    [Fact]
    public void Auto_width_table_still_fills_the_available_width()
    {
        // Regression guard: shrink-to-fit for auto tables is the DEFERRED remainder of F3, so a
        // `width: auto` table must keep filling its containing block (current behavior, unchanged).
        var widths = CellRectWidths("<table><tbody><tr><td>X</td></tr></tbody></table>");
        Assert.NotEmpty(widths);
        Assert.All(widths, w => Assert.True(w > 500,
            $"auto-width cell {w:0.##}pt — an auto table should still fill (~595pt) until shrink-to-fit lands."));
    }

    [Fact]
    public void Explicit_width_table_with_auto_margins_is_centered()
    {
        // review [P2] — now that a table has a definite used width, `margin: 0 auto` must center it.
        // A4 content width = 595.28pt; a 150px (112.5pt) table centers at x = (595.28−112.5)/2 ≈ 241.4pt.
        var rects = CellRects("<table><tbody><tr><td>X</td></tr></tbody></table>", "width:150px;margin:0 auto");
        Assert.NotEmpty(rects);
        Assert.All(rects, r =>
        {
            Assert.True(System.Math.Abs(r.W - 112.5) < 1.0, $"width {r.W:0.##}pt (expected 112.5)");
            Assert.True(System.Math.Abs(r.X - 241.39) < 2.0,
                $"cell x {r.X:0.##}pt — a `margin: 0 auto` explicit-width table did not center (expected ≈241.4).");
        });
    }

    [Fact]
    public void Auto_margin_min_content_overflow_stays_on_page()
    {
        // review [P2] ordering / min-content case — a tiny declared width whose content can't fit
        // (a long unbreakable word) overflows to the column min-content. `ResolveAutoInlineMargins`
        // is re-run after the table-driven inline widening (both the outer + recursion dispatch
        // paths) so the margins distribute against the FINAL used width when the pre-measure reports
        // the overflow. RESIDUAL: for a single cell whose min-content only materializes at EMIT (not
        // in the pre-measure), the wrapper isn't widened at the block level, so the table keeps its
        // declared-width auto-margin — it is NOT recentered on the wider used width. Either way the
        // safety invariant holds: the content renders and stays WITHIN the page (no off-right-edge
        // clip). A4 content width (margin:0) = 595.28pt.
        var rects = CellRects(
            "<table><tbody><tr><td>Supercalifragilisticexpialidocious</td></tr></tbody></table>",
            "width:20px;margin:0 auto");
        Assert.NotEmpty(rects);
        Assert.All(rects, r =>
        {
            Assert.True(r.W > 20 * 0.75 + 1,
                $"cell width {r.W:0.##}pt — the content should have overflowed the 20px declared width.");
            Assert.True(r.X >= -0.5 && r.X + r.W <= 595.28 + 0.5,
                $"cell [{r.X:0.##}..{r.X + r.W:0.##}]pt escaped the page — an overflowing auto-margin table "
                + "must stay within the page width.");
        });
    }

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
