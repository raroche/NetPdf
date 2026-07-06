// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// Regression tests for the offset-table pagination clip: a tall table that starts BELOW the page top —
/// because plain content (a heading, a paragraph) precedes it — must fragment across pages WITHOUT clipping
/// (dropping) any rows. Pre-fix, the nested-table dry-run in the subtree measure ignored the table's block
/// offset within the subtree, over-committed by ~one row, overshot the page, and tripped a false
/// forced-overflow that committed the table whole and clipped every row past the first page.
///
/// <para>Determinism: the synthetic font gives fixed glyph metrics so row heights — and therefore the
/// pagination decisions — are reproducible without a system font.</para>
/// </summary>
public sealed class OffsetTablePaginationTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver() };

    private static string Rows(int n) =>
        string.Concat(Enumerable.Range(1, n).Select(i =>
            $"<tr><td>Item {i}</td><td>Description of line item {i}</td><td>${i * 10}.00</td></tr>"));

    private static string Doc(string body) =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}"
        + "table{width:100%;border-collapse:collapse}td{border:1px solid #ccc;padding:6px}</style></head>"
        + "<body>" + body + "</body></html>";

    [Fact]
    public void Tall_table_at_page_top_paginates_without_clipping()
    {
        // Control — the always-working case (offset 0). Guards it against the offset fix.
        var r = HtmlPdf.ConvertDetailed(Doc($"<table>{Rows(40)}</table>"), Opts());
        Assert.True(r.PageCount >= 2, $"a 40-row table should paginate; got {r.PageCount} page(s).");
        Assert.DoesNotContain(r.Warnings, IsClip);
    }

    [Theory]
    [InlineData("<div style='height:50px'></div>")]                      // a spacer pushes the table down
    [InlineData("<h1>Invoice 0042</h1><p>Bill to: Acme Corp</p>")]       // real preceding content
    [InlineData("<div style='height:200px'></div>")]                     // a larger offset
    public void Tall_table_offset_from_page_top_paginates_without_clipping(string preceding)
    {
        // The bug: any of these offsets used to clip the table's overflowing rows (data loss).
        var r = HtmlPdf.ConvertDetailed(Doc($"{preceding}<table>{Rows(40)}</table>"), Opts());

        Assert.True(r.PageCount >= 2, $"the table should still paginate; got {r.PageCount} page(s).");
        Assert.DoesNotContain(r.Warnings, IsClip);                       // NEVER drop content
        Assert.DoesNotContain(r.Warnings, IsForcedOverflow);            // and it fragments cleanly, not by force
    }

    // ── Content-preservation: prove every row survives pagination, not just "no warning" ────
    // The bug is DATA LOSS, so absence of a clip diagnostic isn't enough — assert the cells are
    // actually painted. Each cell paints exactly one text-show (`<hex> Tj`), so the number of Tj
    // operators across all pages is a faithful count of cells that reached the page.

    private static int CellTextShows(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"[>)]\s*Tj").Count;

    [Fact]
    public void Offset_table_preserves_every_cell_exactly_like_the_page_top_render()
    {
        const int rows = 40;
        var atTop = HtmlPdf.Convert(Doc($"<table>{Rows(rows)}</table>"), Opts());
        var offset = HtmlPdf.Convert(
            Doc($"<div style='height:200px'></div><table>{Rows(rows)}</table>"), Opts());

        // The offset only shifts the table down a page — it must not drop a single cell, so the
        // total cell count is identical to the (always-correct) page-top render, and covers every
        // row × column (the spacer div paints no text of its own).
        Assert.Equal(CellTextShows(atTop), CellTextShows(offset));
        Assert.True(CellTextShows(offset) >= rows * 3,
            $"expected ≥ {rows * 3} cell shows (40 rows × 3 cols); got {CellTextShows(offset)} — rows were dropped.");
    }

    [Fact]
    public void Table_in_a_padded_bordered_wrapper_preserves_every_row()
    {
        // The fix threads a page-start offset through the wrapper's border + padding — exercise that
        // path directly: a padded, bordered wrapper pushes the table down and shrinks its content box.
        // The CRITICAL guarantee is content preservation: no clip diagnostic, and every cell painted.
        // NOTE: a padded/bordered wrapper still trips a (non-lossy) forced-overflow — the documented
        // `padded-wrapper` residual (an extra page, NOT data loss) — so this test asserts no data is
        // lost rather than absence of forced-overflow.
        const int rows = 36;
        var r = HtmlPdf.ConvertDetailed(
            Doc($"<div style='padding:40px;border:12px solid #333'><table>{Rows(rows)}</table></div>"), Opts());

        Assert.True(r.PageCount >= 2, $"the wrapped table should paginate; got {r.PageCount} page(s).");
        Assert.DoesNotContain(r.Warnings, IsClip);          // NEVER drop content
        Assert.True(CellTextShows(r.Pdf) >= rows * 3,
            $"expected ≥ {rows * 3} cell shows; got {CellTextShows(r.Pdf)} — rows lost inside the wrapper.");
    }

    [Fact]
    public void Table_with_a_caption_offset_preserves_every_row()
    {
        // A <caption> adds block extent above the row grid — another source of a non-zero start offset.
        const int rows = 38;
        var r = HtmlPdf.ConvertDetailed(
            Doc($"<div style='height:120px'></div><table><caption>Line items</caption>{Rows(rows)}</table>"), Opts());

        Assert.True(r.PageCount >= 2, $"the captioned table should paginate; got {r.PageCount} page(s).");
        Assert.DoesNotContain(r.Warnings, IsClip);
        Assert.True(CellTextShows(r.Pdf) >= rows * 3,
            $"expected ≥ {rows * 3} cell shows; got {CellTextShows(r.Pdf)} — captioned rows were dropped.");
    }

    [Fact]
    public void Offset_table_with_repeating_header_and_footer_preserves_every_body_row()
    {
        // thead/tfoot repeat per page; the body rows must still all appear once when the table starts
        // below the page top. Header + footer only ADD text-shows, so the ≥ body-cell floor still holds.
        const int bodyRows = 36;
        var r = HtmlPdf.ConvertDetailed(Doc(
            "<div style='height:150px'></div><table>"
            + "<thead><tr><td>#</td><td>Item</td><td>Amount</td></tr></thead>"
            + "<tfoot><tr><td></td><td>Total</td><td>$999</td></tr></tfoot>"
            + $"<tbody>{Rows(bodyRows)}</tbody></table>"), Opts());

        Assert.True(r.PageCount >= 2, $"should paginate; got {r.PageCount} page(s).");
        Assert.DoesNotContain(r.Warnings, IsClip);
        Assert.True(CellTextShows(r.Pdf) >= bodyRows * 3,
            $"expected ≥ {bodyRows * 3} body cell shows; got {CellTextShows(r.Pdf)} — body rows lost.");
    }

    private static bool IsClip(Diagnostic d) => d.Code == "PDF-CONTENT-OVERFLOW-TRUNCATED-001";
    private static bool IsForcedOverflow(Diagnostic d) => d.Code == "PAGINATION-FORCED-OVERFLOW-001";

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
