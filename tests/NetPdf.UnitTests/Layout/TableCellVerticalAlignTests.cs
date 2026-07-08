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
/// CSS 2.2 §17.5.3 table-cell <c>vertical-align</c> (F2, RC-F2b). Before this, cell content was pinned
/// to the cell content-box top for every keyword, and there was no WHATWG <c>middle</c> default — so a
/// short cell next to a taller sibling never centered the way browsers do. These render a two-column
/// row whose FIRST (leftmost) cell is short and second is tall, then measure the short cell's text
/// baseline (the glyph run with the smallest x) as the alignment keyword varies. The synthetic font
/// keeps the geometry deterministic.
/// </summary>
public sealed class TableCellVerticalAlignTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver() };

    // A tall right cell (3 lines) forces free space in the short left cell's row.
    private static string Row(string cellCss) =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}"
        + "table{border-collapse:collapse}td{padding:0;font-size:12px;" + cellCss + "}</style></head>"
        + "<body style='margin:0'><table><tr>"
        + "<td class='short'>S</td>"
        + "<td>Line one<br>Line two<br>Line three</td>"
        + "</tr></table></body></html>";

    // The short cell's text baseline y (the run with the smallest x = leftmost column). PDF is y-up,
    // so a LARGER y is HIGHER on the page (closer to the row top).
    private static double ShortCellY(string cellCss)
    {
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(Row(cellCss), Opts()));
        var runs = Regex.Matches(pdf, @"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+Td\s+<[0-9A-Fa-f]*>\s+Tj")
            .Select(m => (
                X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.NotEmpty(runs);
        return runs.OrderBy(r => r.X).First().Y;   // leftmost run = the short cell.
    }

    [Fact]
    public void Keywords_distribute_free_space_top_middle_bottom()
    {
        var top = ShortCellY("vertical-align:top");
        var middle = ShortCellY("vertical-align:middle");
        var bottom = ShortCellY("vertical-align:bottom");

        // y-up: top is highest, bottom is lowest, middle strictly between.
        Assert.True(top > middle + 1, $"top ({top:0.#}) should be higher than middle ({middle:0.#})");
        Assert.True(middle > bottom + 1, $"middle ({middle:0.#}) should be higher than bottom ({bottom:0.#})");
    }

    [Fact]
    public void Explicit_top_does_not_shift_the_content()
    {
        // invoice-12 declares `vertical-align: top` — content must stay at the cell content-box top,
        // i.e. level with the tall cell's FIRST line (the topmost text run overall).
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(Row("vertical-align:top"), Opts()));
        var ys = Regex.Matches(pdf, @"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+Td\s+<[0-9A-Fa-f]*>\s+Tj")
            .Select(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            .ToList();
        var topmost = ys.Max();
        var shortY = ShortCellY("vertical-align:top");
        Assert.True(System.Math.Abs(shortY - topmost) < 0.5,
            $"top-aligned short cell (y={shortY:0.##}) is not level with the topmost line (y={topmost:0.##})");
    }

    [Fact]
    public void Default_with_no_author_value_centers_like_the_whatwg_middle_default()
    {
        // No author vertical-align anywhere → WHATWG UA default is `middle`, so the short cell centers.
        var none = ShortCellY(string.Empty);
        var middle = ShortCellY("vertical-align:middle");
        Assert.True(System.Math.Abs(none - middle) < 0.5,
            $"default short-cell y ({none:0.##}) should equal explicit middle ({middle:0.##}) — the "
            + "WHATWG `middle` default did not apply.");
        // …and it is genuinely lower than top-aligned (i.e. the default is NOT top).
        var top = ShortCellY("vertical-align:top");
        Assert.True(top > none + 1, $"default ({none:0.#}) should sit below top ({top:0.#})");
    }

    [Fact]
    public void Explicit_baseline_is_approximated_as_middle_documented_deferral()
    {
        // review [P3] — locks the documented approximation
        // (docs/deferrals.md#table-cell-vertical-align-baseline): the table content sink has no
        // first-baseline, so an explicit `vertical-align: baseline` cell is NOT baseline-distributed
        // — it folds to the walk default `middle`. Assert it renders identical to explicit middle
        // (and NOT top). When true baseline support lands, this test changes with the deferral.
        var baseline = ShortCellY("vertical-align:baseline");
        var middle = ShortCellY("vertical-align:middle");
        var top = ShortCellY("vertical-align:top");
        Assert.True(System.Math.Abs(baseline - middle) < 0.5,
            $"explicit baseline (y={baseline:0.##}) should currently match middle (y={middle:0.##}) "
            + "per the documented approximation.");
        Assert.True(top > baseline + 1, $"baseline ({baseline:0.#}) must not be top-aligned ({top:0.#})");
    }

    [Fact]
    public void Author_row_vertical_align_top_overrides_the_ua_middle_default()
    {
        // vertical-align on the <tr> must reach the cell (inheritance in the WHATWG model) and put the
        // content back at the top — proving the default is not a hard `td, th { middle }` rule.
        const string html = "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}"
            + "table{border-collapse:collapse}td{padding:0;font-size:12px}tr{vertical-align:top}</style></head>"
            + "<body style='margin:0'><table><tr><td>S</td>"
            + "<td>Line one<br>Line two<br>Line three</td></tr></table></body></html>";
        var pdf = Encoding.Latin1.GetString(HtmlPdf.Convert(html, Opts()));
        var runs = Regex.Matches(pdf, @"(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+Td\s+<[0-9A-Fa-f]*>\s+Tj")
            .Select(m => (
                X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        var shortY = runs.OrderBy(r => r.X).First().Y;
        var topmost = runs.Max(r => r.Y);
        Assert.True(System.Math.Abs(shortY - topmost) < 0.5,
            $"author `tr {{ vertical-align: top }}` did not reach the cell (short y={shortY:0.##}, "
            + $"topmost={topmost:0.##}).");
    }

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
