// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
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

    private static bool IsClip(Diagnostic d) => d.Code == "PDF-CONTENT-OVERFLOW-TRUNCATED-001";
    private static bool IsForcedOverflow(Diagnostic d) => d.Code == "PAGINATION-FORCED-OVERFLOW-001";

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
