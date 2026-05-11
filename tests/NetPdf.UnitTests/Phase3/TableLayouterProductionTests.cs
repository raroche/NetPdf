// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 12 sub-cycle 1 post-PR-49 hardening (Finding 6) —
/// production-pipeline tests for <see cref="TableLayouter"/>. The
/// existing <see cref="TableLayouterTests"/> construct box trees
/// directly (bypassing BoxBuilder); this fixture exercises tables
/// through the FULL pipeline:
///
/// <para>HTML → <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>VarResolver</c> → <c>BoxBuilder</c> → <c>BlockLayouter</c>
/// (which dispatches into <c>TableLayouter</c> for table wrappers).
/// </para>
///
/// <para>Coverage gaps closed by this fixture (relative to
/// <see cref="TableLayouterTests"/>): captions (which BoxBuilder
/// keeps as direct wrapper children, NOT under the grid); row
/// groups synthesized from real <c>&lt;thead&gt;</c> /
/// <c>&lt;tbody&gt;</c>; anonymous wrappers; whitespace handling
/// around tables; real inline text flowing through the cell content
/// path; sibling block flow around a table.</para>
/// </summary>
public sealed class TableLayouterProductionTests
{
    [Fact]
    public async Task Simple_html_table_renders_via_full_pipeline()
    {
        // Per Finding 6 — a real HTML table flows through every stage
        // of the pipeline + emits row + cell fragments. Asserts the
        // paint-safe order (Finding 2 regression) for the production
        // dispatch path.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td>Hello</td></tr></table>
            </body></html>
            """;

        var (sink, _, table) = await RenderViaFullPipelineAsync(html);

        // At least one Table fragment + one TableRow + one TableCell.
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.Table);
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.TableRow);
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.TableCell);

        // Paint-safe order — for each (row, its cell) pair the row
        // index must precede the cell index, and the cell index must
        // precede the cell content (we use the first AnonymousBlock /
        // BlockContainer under the cell as a proxy for cell content).
        int rowIdx = -1, cellIdx = -1;
        for (var i = 0; i < sink.Fragments.Count; i++)
        {
            var b = sink.Fragments[i].Box;
            if (b.Kind == BoxKind.TableRow && rowIdx < 0) rowIdx = i;
            else if (b.Kind == BoxKind.TableCell && cellIdx < 0) cellIdx = i;
        }
        Assert.True(rowIdx >= 0 && cellIdx >= 0);
        Assert.True(rowIdx < cellIdx,
            $"Paint-safe order violated: row at {rowIdx} after cell at {cellIdx}.");
    }

    [Fact]
    public async Task Table_followed_by_paragraph_does_not_overlap()
    {
        // Per Finding 1 regression — the paragraph after a table must
        // land BELOW the table's bottom edge. Pre-fix the wrapper
        // border-box was 0-tall (auto-height) so the paragraph
        // overlapped the table content.
        const string html = """
            <!DOCTYPE html><html><head><style>
                td > div { height: 100px; }
                p { height: 50px; }
            </style></head><body>
            <table><tr><td><div></div></td></tr></table>
            <p></p>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find the table wrapper + the following <p>.
        var tableFragment = sink.Fragments.FirstOrDefault(f =>
            f.Box.Kind == BoxKind.Table);
        Assert.NotEqual(default, tableFragment);

        // The <p> is a BlockContainer outside the table.
        BoxFragment paragraphFragment = default;
        var foundTable = false;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.Table) { foundTable = true; continue; }
            if (foundTable
                && f.Box.Kind == BoxKind.BlockContainer
                && f.Box.SourceElement?.LocalName == "p")
            {
                paragraphFragment = f;
                break;
            }
        }
        Assert.NotEqual(default, paragraphFragment);

        var tableBottom = tableFragment.BlockOffset + tableFragment.BlockSize;
        Assert.True(paragraphFragment.BlockOffset >= tableBottom,
            $"Paragraph at BlockOffset={paragraphFragment.BlockOffset} overlaps "
            + $"table that bottoms at {tableBottom}.");
    }

    [Fact]
    public async Task Production_table_with_top_caption_renders_via_full_pipeline()
    {
        // Phase 3 Task 12 sub-cycle 3 — a real <caption> element
        // lays out as a TableCaption fragment above the rows (default
        // caption-side: top). Replaces the sub-cycle 1
        // Table_with_caption_emits_feature_diagnostic regression
        // which asserted the now-removed deferral diagnostic.
        //
        // The caption text is wrapped in a <div> with an explicit
        // height so the test isn't sensitive to the synthetic font's
        // line metrics — same pattern as
        // <see cref="Table_cell_with_real_inline_text_lays_out_via_inline_only_block"/>.
        const string html = """
            <!DOCTYPE html><html><head><style>
                caption > div { height: 20px; }
            </style></head><body>
            <table>
              <caption><div>Q1 2026 Revenue</div></caption>
              <tr><td>X</td></tr>
            </table>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // No LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 mentioning
        // "caption" — sub-cycle 3 stopped emitting the diagnostic.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));

        // The TableCaption fragment lands ABOVE the TableRow.
        BoxFragment? captionFragment = null;
        BoxFragment? rowFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCaption && captionFragment is null)
                captionFragment = f;
            else if (f.Box.Kind == BoxKind.TableRow && rowFragment is null)
                rowFragment = f;
        }
        Assert.NotNull(captionFragment);
        Assert.NotNull(rowFragment);
        Assert.True(captionFragment!.Value.BlockOffset < rowFragment!.Value.BlockOffset,
            $"Expected caption (BlockOffset={captionFragment.Value.BlockOffset}) to "
            + $"render ABOVE row (BlockOffset={rowFragment.Value.BlockOffset}).");
    }

    [Fact]
    public async Task Production_table_with_bottom_caption_renders_below_rows()
    {
        // Phase 3 Task 12 sub-cycle 3 — caption-side: bottom flips
        // the caption to render AFTER the row stack.
        //
        // Caption text wrapped in a <div> with a known height so
        // the test isn't sensitive to synthetic-font line metrics
        // (same pattern as the cell-text-with-known-height tests).
        const string html = """
            <!DOCTYPE html><html><head><style>
                caption { caption-side: bottom; }
                caption > div { height: 18px; }
                td > div { height: 30px; }
            </style></head><body>
            <table>
              <caption><div>Footer note</div></caption>
              <tr><td><div>X</div></td></tr>
            </table>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // No caption deferral diagnostic — sub-cycle 3 lays it out.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));

        // Caption appears AFTER the last row's bottom edge.
        BoxFragment? captionFragment = null;
        BoxFragment? lastRowFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCaption) captionFragment = f;
            else if (f.Box.Kind == BoxKind.TableRow) lastRowFragment = f;
        }
        Assert.NotNull(captionFragment);
        Assert.NotNull(lastRowFragment);
        var lastRowBottom = lastRowFragment!.Value.BlockOffset + lastRowFragment.Value.BlockSize;
        Assert.True(captionFragment!.Value.BlockOffset >= lastRowBottom,
            $"Expected bottom caption (BlockOffset={captionFragment.Value.BlockOffset}) "
            + $"to render AT OR BELOW last row's bottom ({lastRowBottom}).");
    }

    [Fact]
    public async Task Table_with_thead_tbody_collects_rows_via_row_groups()
    {
        // BoxBuilder synthesizes TableHeaderGroup + TableRowGroup
        // wrappers around <thead> + <tbody>. TableLayouter must
        // recurse into them to find the inner rows.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <thead><tr><th>H</th></tr></thead>
              <tbody>
                <tr><td>1</td></tr>
                <tr><td>2</td></tr>
              </tbody>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var rowCount = sink.Fragments.Count(f => f.Box.Kind == BoxKind.TableRow);
        Assert.Equal(3, rowCount);
    }

    [Fact]
    public async Task Production_colspan_table_renders_correctly()
    {
        // Phase 3 Task 12 sub-cycle 2 — through the full pipeline a
        // colspan="2" cell renders with inline-size = 2 × columnWidth.
        // The table has 2 columns total (max occupied column + 1 = 2);
        // contentInlineSize = 600 ⇒ columnWidth = 300; the header cell
        // is 600 wide.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <tr><td colspan="2">Header</td></tr>
              <tr><td>A</td><td>B</td></tr>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var rowCount = sink.Fragments.Count(f => f.Box.Kind == BoxKind.TableRow);
        var cellCount = sink.Fragments.Count(f => f.Box.Kind == BoxKind.TableCell);
        Assert.Equal(2, rowCount);
        Assert.Equal(3, cellCount); // 1 header cell + 2 plain cells

        // The header cell — the cell whose source element has
        // colspan="2". InlineSize must equal 2 × columnWidth = 600.
        BoxFragment? headerFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell
                && f.Box.SourceElement?.GetAttribute("colspan") == "2")
            {
                headerFragment = f;
                break;
            }
        }
        Assert.NotNull(headerFragment);
        Assert.Equal(600, headerFragment!.Value.InlineSize);
        Assert.Equal(0, headerFragment.Value.InlineOffset);
    }

    [Fact]
    public async Task Production_rowspan_table_renders_correctly()
    {
        // Phase 3 Task 12 sub-cycle 2 — through the full pipeline a
        // rowspan="2" cell renders with BlockSize covering both row
        // heights; the second row's plain cell anchors at col 1.
        // The cell content uses a <div height=30> to give each cell
        // a determinate row height (the synthetic font's line metrics
        // also contribute; we just need positive row heights).
        const string html = """
            <!DOCTYPE html><html><head><style>
                td > div { height: 30px; }
            </style></head><body>
            <table>
              <tr><td rowspan="2"><div></div></td><td><div></div></td></tr>
              <tr><td><div></div></td></tr>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Locate the rowspan cell.
        BoxFragment? spanFragment = null;
        BoxFragment? row1NonSpanFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind != BoxKind.TableCell) continue;
            var rs = f.Box.SourceElement?.GetAttribute("rowspan");
            if (rs == "2")
            {
                spanFragment = f;
            }
            else if (f.BlockOffset > 0)
            {
                // The row-1 plain cell sits AFTER row 0 — its
                // BlockOffset is positive.
                row1NonSpanFragment = f;
            }
        }
        Assert.NotNull(spanFragment);
        Assert.NotNull(row1NonSpanFragment);

        // The rowspan cell's BlockSize covers BOTH row heights. Each
        // row's natural height is at least the 30px <div>.
        Assert.True(spanFragment!.Value.BlockSize >= 60,
            $"Expected rowspan cell BlockSize ≥ 60 (= 2 × 30px row), "
            + $"got {spanFragment.Value.BlockSize}.");
        Assert.Equal(0, spanFragment.Value.BlockOffset);

        // Row 1's plain cell anchors at column 1 (col 0 is occupied
        // by the rowspan continuation). The 2-column table has
        // columnWidth = 300 (contentInlineSize=600), so the row-1
        // cell's inline offset must be 300 — NOT 0.
        Assert.Equal(300, row1NonSpanFragment!.Value.InlineOffset);
        Assert.Equal(300, row1NonSpanFragment.Value.InlineSize);
    }

    [Fact]
    public async Task Production_colspan_rowspan_no_longer_diagnoses()
    {
        // Sub-cycle 2 — through the full pipeline the colspan
        // attribute path must NOT produce
        // LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 because cell merging
        // works. The code stays in the catalog (captions still emit
        // it) so this regression specifically checks the colspan
        // message path.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <tr><td colspan="2">spans two columns</td></tr>
            </table>
            </body></html>
            """;

        var (_, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("colspan"));
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("rowspan"));
    }

    [Fact]
    public async Task Production_caption_side_inherits_from_table_to_caption()
    {
        // Sub-cycle 3 hardening Finding 5 — `caption-side` is an
        // INHERITED CSS property (CSS Tables L3 §11.5.2). Setting it
        // on the <table> element should cascade to the <caption>; the
        // caption then renders at the bottom even though the <caption>
        // itself has no caption-side declaration. Pre-Finding-5 the
        // production tests only covered explicit caption-side on the
        // <caption> element.
        const string html = """
            <!DOCTYPE html><html><head><style>
                table { caption-side: bottom; }
                caption > div { height: 20px; }
                td > div { height: 35px; }
            </style></head><body>
            <table>
              <caption><div>Inherited bottom</div></caption>
              <tr><td><div>X</div></td></tr>
            </table>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // No caption deferral diagnostic — caption renders normally.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));

        // Caption appears BELOW the row (inheritance from <table>'s
        // caption-side: bottom worked).
        BoxFragment? captionFragment = null;
        BoxFragment? rowFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCaption) captionFragment = f;
            else if (f.Box.Kind == BoxKind.TableRow) rowFragment = f;
        }
        Assert.NotNull(captionFragment);
        Assert.NotNull(rowFragment);
        var rowBottom = rowFragment!.Value.BlockOffset + rowFragment.Value.BlockSize;
        Assert.True(captionFragment!.Value.BlockOffset >= rowBottom,
            $"Expected caption (BlockOffset={captionFragment.Value.BlockOffset}) "
            + $"to render AT OR BELOW row bottom ({rowBottom}) via "
            + "caption-side inheritance from <table>.");
    }

    [Fact]
    public async Task Table_cell_with_real_inline_text_lays_out_via_inline_only_block()
    {
        // Per Finding 6 — the cell content path must produce a
        // BoxFragment with shaped inline content (proving the nested
        // BlockLayouter → InlineLayouter chain works inside cells).
        // We wrap the cell text in a <div> so BoxBuilder produces a
        // BlockContainer child whose only descendant is the TextRun
        // — that's exactly the inline-only-block shape the inline
        // dispatch detects.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td><div>Hello world</div></td></tr></table>
            </body></html>
            """;

        var (sink, _, root) = await RenderViaFullPipelineAsync(html);

        // First sanity-check we got a TableCell fragment via the
        // nested-recursion dispatch.
        Assert.Contains(sink.Fragments, f => f.Box.Kind == BoxKind.TableCell);

        // Walk fragments for any BoxFragment whose InlineLayout is
        // populated — that's the cell-content inline-only-block
        // fragment. The cell's div lands either as a direct fragment
        // (if it has inline content + uses inline-only-block dispatch)
        // OR as an AnonymousBlock fragment around the TextRun. Either
        // way the InlineLayout should be populated somewhere.
        var inlineFragments = sink.Fragments
            .Where(f => f.InlineLayout?.Lines is not null && f.InlineLayout.Value.Lines.Length > 0)
            .ToList();
        Assert.NotEmpty(inlineFragments);

        // Verify at least one inline fragment carries real glyphs
        // (the shape pass produced glyph data for "Hello world").
        var anyGlyphs = false;
        foreach (var frag in inlineFragments)
        {
            var inlineLayout = frag.InlineLayout!.Value;
            foreach (var run in inlineLayout.ShapedRuns)
            {
                if (run.Glyphs.Length > 0) { anyGlyphs = true; break; }
            }
            if (anyGlyphs) break;
        }
        Assert.True(anyGlyphs, "Expected at least one shaped glyph in the cell's inline content.");
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 4 — table-layout: fixed + <col> widths
    // ====================================================================

    [Fact]
    public async Task Production_table_with_fixed_layout_and_col_widths_renders_correctly()
    {
        // Sub-cycle 4 — through the full pipeline a
        // table-layout: fixed table with two <col width> declarations
        // renders cells at the declared per-column widths.
        //
        // Sub-cycle 4 hardening (Finding 1) — after Pass A claims cols
        // 0 + 1 with widths 100 + 200 (columnSum=300), Pass D
        // distributes the leftover (600 - 300 = 300) equally (+150
        // each) → 250 + 350.
        const string html = """
            <!DOCTYPE html><html><head><style>
                table { table-layout: fixed; }
            </style></head><body>
            <table>
              <col width="100">
              <col width="200">
              <tr><td>A</td><td>B</td></tr>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(2, cells.Count);
        Assert.Equal(250, cells[0].InlineSize);
        Assert.Equal(350, cells[1].InlineSize);
        Assert.Equal(0, cells[0].InlineOffset);
        Assert.Equal(250, cells[1].InlineOffset);
    }

    [Fact]
    public async Task Production_table_with_auto_layout_ignores_col_widths_for_now()
    {
        // Sub-cycle 4 — table-layout: auto (default) IGNORES <col>
        // widths because the §3 shrink-to-fit algorithm is sub-cycle
        // 5+ work; equal-split applies.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <col width="100">
              <tr><td>A</td><td>B</td></tr>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(2, cells.Count);
        // contentInlineSize=600 / 2 columns ⇒ 300 each. The <col
        // width="100"> is silently ignored by sub-cycle 4 auto mode.
        Assert.Equal(300, cells[0].InlineSize);
        Assert.Equal(300, cells[1].InlineSize);
    }

    // ====================================================================
    //  Pipeline driver
    // ====================================================================

    /// <summary>Drive an HTML string through the full production
    /// pipeline + run <see cref="BlockLayouter"/> on the resulting box
    /// tree. Returns the recording sink + diagnostic sink + the root
    /// box for assertions.</summary>
    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root)>
        RenderViaFullPipelineAsync(string html)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var box = BoxBuilder.Build(document, resolved);

        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        using var layouter = new BlockLayouter(
            rootBox: box,
            sink: sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: shaper);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        return (sink, diagSink, box);
    }

    private static ImmutableArray<CssStylesheet> AdaptAllSheetsViaPreprocessor(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;

        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            string rawText;
            if (styleIdx < styleElements.Length)
            {
                rawText = styleElements[styleIdx].TextContent ?? string.Empty;
                styleIdx++;
            }
            else
            {
                rawText = string.Empty;
            }
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null,
                isDisabled: false,
                order: order++));
        }
        return output.ToImmutable();
    }

    // ====================================================================
    //  Test doubles
    // ====================================================================

    private sealed class RecordingFragmentSink : IBlockFragmentSink
    {
        public List<BoxFragment> Fragments { get; } = new();
        public int Cursor => Fragments.Count;
        public void Emit(BoxFragment fragment) => Fragments.Add(fragment);
        public void RollbackTo(int cursor)
        {
            if (cursor < Fragments.Count)
            {
                Fragments.RemoveRange(cursor, Fragments.Count - cursor);
            }
        }
    }

    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
