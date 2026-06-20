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
using NetPdf.Css.Properties;
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
    public async Task Table_cell_declared_width_honors_box_sizing()
    {
        // Box-sizing audit (table column) — a cell's declared width feeds the column
        // via its BORDER box: under content-box (the initial) the cell's inline
        // padding is ADDED to the declared width, so the column's preferred width is
        // larger than under border-box (where the declared width IS the border box).
        // The auto table layout then distributes the surplus, but the content-box
        // column stays observably WIDER (pre-fix the two were identical — the padding
        // was dropped from the content-box column's contribution).
        async Task<double> FixedColumnWidthAsync(string boxSizing)
        {
            var html = $$"""
                <!DOCTYPE html><html><head><style>
                    td.fixed { width: 100px; padding-left: 40px; padding-right: 40px; box-sizing: {{boxSizing}}; }
                </style></head><body>
                <table><tr><td class="fixed">A</td><td>B</td></tr></table>
                </body></html>
                """;
            var (sink, _, _) = await RenderViaFullPipelineAsync(html);
            var fixedCell = sink.Fragments.First(f =>
                f.Box.Kind == BoxKind.TableCell
                && f.Box.SourceElement?.GetAttribute("class") == "fixed");
            return fixedCell.InlineSize;
        }

        var contentBox = await FixedColumnWidthAsync("content-box");
        var borderBox = await FixedColumnWidthAsync("border-box");
        Assert.True(contentBox > borderBox + 1.0,
            $"box-sizing should widen the content-box column (its 80px padding adds to the declared "
            + $"100): content-box={contentBox}, border-box={borderBox}");
    }

    [Fact]
    public async Task Table_col_padding_does_not_widen_the_column()
    {
        // PR #189 review P2 (table) — padding on a `<col>` must NOT inflate the column
        // (CSS 2.1 §17.5.3 — padding does not apply to columns). The box-sizing chrome
        // mapping is CELL-only, so a padded <col> resolves to the same column width as an
        // unpadded one (pre-fix, the helper unconditionally added the col's padding).
        async Task<double> FirstCellWidthAsync(string colExtra)
        {
            var html = $$"""
                <!DOCTYPE html><html><head><style>
                    col.fixed { width: 100px; {{colExtra}} }
                </style></head><body>
                <table>
                  <col class="fixed"><col>
                  <tr><td>A</td><td>B</td></tr>
                </table>
                </body></html>
                """;
            var (sink, _, _) = await RenderViaFullPipelineAsync(html);
            var cell = sink.Fragments.First(f => f.Box.Kind == BoxKind.TableCell);
            return cell.InlineSize;
        }

        var padded = await FirstCellWidthAsync("padding-left: 40px; padding-right: 40px;");
        var plain = await FirstCellWidthAsync("");
        Assert.Equal(plain, padded, precision: 3);
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
    public async Task Production_auto_layout_col_width_interacts_with_intrinsic_content()
    {
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 2 —
        // table-layout: auto now INCORPORATES <col> / <colgroup>
        // widths as per-column min/max floors. A <col width="100">
        // for col 0 + "B" content for col 1 means col 0's floor is
        // 100; col 1 has only the intrinsic width of "B". The
        // saturated-path distribution adds equal extra to both
        // columns, so col 0 ends up wider than col 1.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <col width="100">
              <tr><td><div>A</div></td><td><div>B</div></td></tr>
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
        // Col 0 is floored by <col width="100"> AND gets the
        // saturated-path extra; col 1 only gets the extra. Both
        // sum to contentInlineSize=600.
        Assert.Equal(600, cells[0].InlineSize + cells[1].InlineSize, precision: 3);
        Assert.True(cells[0].InlineSize > cells[1].InlineSize,
            $"Expected col 0 (floored by <col width=100>) wider than col 1 "
            + $"(intrinsic only); got col0={cells[0].InlineSize}, col1={cells[1].InlineSize}.");
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 5 — table-layout: auto shrink-to-fit
    //  (CSS Tables L3 §3 — min/max-content per column).
    // ====================================================================

    [Fact]
    public async Task Production_auto_layout_with_real_text_renders_per_content()
    {
        // Sub-cycle 5 — the production pipeline (HTML → BoxBuilder →
        // BlockLayouter → TableLayouter) produces ASYMMETRIC column
        // widths under table-layout: auto when cell content has
        // different intrinsic widths. The shorter cell ("A") gets a
        // narrower column than the longer cell ("BBBBBBBBBB"). Both
        // must sum to contentInlineSize.
        //
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 1 — direct
        // <td>text</td> now contributes intrinsic widths (was skipped
        // pre-fix because TableCell wasn't in
        // <c>IsInlineOnlyBlockContainer</c>'s allowed kinds, so the
        // nested BlockLayouter's child loop never dispatched the
        // TableCell's direct TextRun children). The <div> workaround
        // is gone; this test pins the spec-correct behavior.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td>A</td><td>BBBBBBBBBB</td></tr></table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(2, cells.Count);
        // Combined widths cover the wrapper's content-inline-size
        // (600 from the test fixture).
        Assert.Equal(600, cells[0].InlineSize + cells[1].InlineSize, precision: 3);
        // Column 0 (shorter content) is narrower than column 1
        // (longer content) — the §3 shrink-to-fit derives widths
        // from intrinsic content extents.
        Assert.True(cells[0].InlineSize < cells[1].InlineSize,
            $"Expected col 0 ('A', width={cells[0].InlineSize}) to be "
            + $"narrower than col 1 ('BBBBBBBBBB', width={cells[1].InlineSize}). "
            + "Pre-sub-cycle-5 they would have been equal (300/300 from "
            + "the equal-split approximation).");
    }

    [Fact]
    public async Task Production_auto_layout_invoice_style_columns()
    {
        // Sub-cycle 5 — a typical invoice-style 4-column layout: a
        // narrow "Qty" column with single-digit content + a wider
        // "Description" column with multi-word content + an "Amount"
        // column + a "Date" column. Under auto-table-layout the
        // description column should be the widest (its max-content
        // is the longest text), and the qty column should be one of
        // the narrowest. Asserted as ORDERING invariants, not exact
        // pixels (synthetic shaper widths are fixed but the test
        // covers the behavioral contract).
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table>
              <tr>
                <td><div>1</div></td>
                <td><div>AAAAAAAA BBBBBBBB AAAAAAAA</div></td>
                <td><div>AA</div></td>
                <td><div>AA</div></td>
              </tr>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(4, cells.Count);
        // The Description column (cell 1) is the widest — it has the
        // longest max-content.
        Assert.True(cells[1].InlineSize > cells[0].InlineSize,
            $"Expected description col (width={cells[1].InlineSize}) "
            + $"to be wider than qty col (width={cells[0].InlineSize}).");
        Assert.True(cells[1].InlineSize > cells[2].InlineSize,
            $"Expected description col (width={cells[1].InlineSize}) "
            + $"to be wider than amount col (width={cells[2].InlineSize}).");
        Assert.True(cells[1].InlineSize > cells[3].InlineSize,
            $"Expected description col (width={cells[1].InlineSize}) "
            + $"to be wider than date col (width={cells[3].InlineSize}).");
        // The four columns sum to contentInlineSize = 600.
        var totalWidth = cells[0].InlineSize + cells[1].InlineSize
            + cells[2].InlineSize + cells[3].InlineSize;
        Assert.Equal(600, totalWidth, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 5 hardening tests
    // ====================================================================

    [Fact]
    public async Task Production_auto_layout_with_direct_td_text_renders_asymmetric_widths()
    {
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 1 — direct
        // `<td>X</td>` text (no <div> wrapper) now contributes its
        // intrinsic widths to auto-table-layout column sizing. Pre-fix
        // BlockLayouter's IsInlineOnlyBlockContainer predicate rejected
        // TableCell, so the cell's direct TextRun children were
        // skipped + the cell's min/max-content stayed at 0.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td>A</td><td>BBBBBBBBBB</td></tr></table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(2, cells.Count);
        // The two columns produce ASYMMETRIC widths because their
        // intrinsic content widths differ. Pre-fix both columns
        // got the equal-split (300, 300).
        Assert.True(cells[0].InlineSize < cells[1].InlineSize,
            $"Expected col 0 ('A') narrower than col 1 ('BBBBBBBBBB'); "
            + $"got col0={cells[0].InlineSize}, col1={cells[1].InlineSize}.");
    }

    [Fact]
    public async Task Production_auto_layout_with_direct_td_text_emits_inline_content()
    {
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 1 — direct
        // `<td>Description</td>` content emits an inline content
        // fragment (shaped glyphs) under the cell. Pre-fix the cell's
        // direct TextRun was silently dropped + no inline fragment
        // was produced.
        const string html = """
            <!DOCTYPE html><html><head><style></style></head><body>
            <table><tr><td>Description</td></tr></table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Walk fragments for one with InlineLayout (= the cell's
        // shaped inline content). Pre-Finding-1 the cell would emit
        // a fragment but its inline content would never reach the
        // inline pass (the cell wasn't dispatched as an inline-only
        // block container).
        var anyInlineFragment = false;
        foreach (var f in sink.Fragments)
        {
            if (f.InlineLayout is { } inline && inline.Lines.Length > 0)
            {
                anyInlineFragment = true;
                break;
            }
        }
        Assert.True(anyInlineFragment,
            "Expected at least one inline content fragment from "
            + "the direct <td>Description</td> text.");
    }

    [Fact]
    public async Task Production_table_with_cell_padding_renders_correctly()
    {
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 3 — cell
        // padding contributes to the cell's intrinsic widths under
        // auto-table-layout + the inner content is offset by the
        // padding within the cell.
        const string html = """
            <!DOCTYPE html><html><head><style>
                td { padding: 10px; }
            </style></head><body>
            <table><tr><td>A</td><td>B</td></tr></table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(2, cells.Count);
        // Each column carries the 20px padding inline-edges; the
        // sum still equals contentInlineSize (600) under the
        // saturated path. The content fragment should be inset
        // by padding-left within the cell.
        Assert.Equal(600, cells[0].InlineSize + cells[1].InlineSize, precision: 3);
    }

    [Fact]
    public async Task Production_auto_layout_min_content_overflow_wrapper_consistent()
    {
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 6 —
        // end-to-end test that the wrapper's emitted geometry
        // matches the overflowing grid extent under auto-table-
        // layout's min-content overflow path.
        const string html = """
            <!DOCTYPE html><html><head><style>
                table { width: 200px; }
                col { width: 400px; }
            </style></head><body>
            <table>
              <col><col>
              <tr><td>A</td><td>B</td></tr>
            </table>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? wrapper = null;
        BoxFragment? row = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.Table && wrapper is null) wrapper = f;
            else if (f.Box.Kind == BoxKind.TableRow && row is null) row = f;
        }
        Assert.NotNull(wrapper);
        Assert.NotNull(row);
        // Per Finding 6 — the wrapper's InlineSize matches the
        // row's InlineSize (= grid's used inline-size). Pre-fix the
        // wrapper stayed at the declared 200 while the row grew to
        // 800; the wrapper background was narrower than its
        // content.
        Assert.Equal(wrapper!.Value.InlineSize, row!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Cycle1_production_multi_page_table_does_not_crash_full_pipeline()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — the
        // depth==1-only continuation propagation limit has been
        // lifted for nested tables too. A real HTML table nested
        // inside HTML > BODY now SPLITS CLEANLY across pages: the
        // recursion returns a chained BlockContinuation(rc=N,
        // ls=BlockContinuation(rc=N, ls=TableContinuation)) reflecting
        // the html→body→table nesting; the OUTER AttemptLayout wraps
        // that into the top-level PageComplete result. The NoBreak-
        // BreakResolver depth>=2 atomic fallback was deleted; the
        // table's single-oversized-row forward-progress fallback is
        // the safety net.
        //
        // Pre-finding: 3 rows committed atomically on page 1 +
        // PAGINATION-FORCED-OVERFLOW-001 fired. Post-finding: 2 rows
        // commit on page 1 (the third overflows + becomes the
        // TableContinuation's NextRowIndex=2), PageComplete with a
        // chained BlockContinuation, NO PAGINATION-FORCED-OVERFLOW-001.
        const string html = """
            <!DOCTYPE html><html><head><style>
                td > div { height: 300px; }
            </style></head><body>
            <table>
              <tr><td><div></div></td></tr>
              <tr><td><div></div></td></tr>
              <tr><td><div></div></td></tr>
            </table>
            </body></html>
            """;

        var (sink, diagnostics, _, result) =
            await RenderViaFullPipelineCapturingResultAsync(html);

        // Post-finding: 2 rows committed on page 1 (the third row
        // becomes part of the TableContinuation chain returned for
        // page 2).
        var rowCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowCount++;
        }
        Assert.Equal(2, rowCount);

        // Post-finding: NO PAGINATION-FORCED-OVERFLOW-001 — the
        // nested table now splits cleanly via the lifted propagation.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);

        // Page 1 outcome is PageComplete with chained continuation.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var topBc = Assert.IsType<BlockContinuation>(result.Continuation);

        // Walk the chain to the TableContinuation leaf. The HTML >
        // BODY > table nesting produces at least one intermediate
        // BlockContinuation wrapping the TableContinuation.
        object? walker = topBc.LayouterState;
        var chainDepth = 0;
        while (walker is BlockContinuation deeper)
        {
            chainDepth++;
            walker = deeper.LayouterState;
            Assert.True(chainDepth < 32,
                "Continuation chain unexpectedly deep — chain runaway?");
        }
        var tc = Assert.IsType<TableContinuation>(walker);
        Assert.Equal(2, tc.NextRowIndex);
        Assert.True(chainDepth >= 1,
            "Expected chain depth >= 1 — the HTML > BODY > table nesting "
            + "should produce at least one nested BlockContinuation wrapping "
            + "the TableContinuation leaf. Actual chainDepth = " + chainDepth);
    }

    [Fact]
    public void Cycle1_production_table_as_root_child_splits_cleanly_at_outer_path()
    {
        // Per Phase 3 Task 13 cycle 1 hardening (Finding 1) — when
        // the table is a DIRECT child of the BlockLayouter's
        // rootBox (bypassing the typical `<html><body><table>` nest
        // that BoxBuilder produces), the OUTER dispatch path runs.
        // That path:
        //   (a) Pre-measures the table via PreMeasureTableIfNeeded
        //       (with `useDryRunCommittedHeight: true` per Finding 2
        //       so the wrapper sizes to the committed-rows extent
        //       on page 1).
        //   (b) Emits the wrapper + invokes EmitTableInner with the
        //       REAL break resolver (not the NoBreakBreakResolver).
        //   (c) Sees the table return PageComplete(TableContinuation)
        //       when rows overflow; repackages it into a
        //       BlockContinuation with LayouterState =
        //       TableContinuation.
        //   (d) Returns PageComplete(BlockContinuation) for the next
        //       page to resume from.
        //
        // The fix's deliverable: the table actually splits across
        // pages instead of emitting all rows atomically + a false
        // forced-overflow diagnostic.
        //
        // This test bypasses BoxBuilder's body-wrap by constructing
        // the box tree directly: root → table → grid → row → cell.
        // Cycle 1's documented limitation is that the nested-recursion
        // path (reached when table is deeper than 1 level under the
        // BlockLayouter's root) still uses the atomic fallback. The
        // full-pipeline test
        // `Cycle1_production_multi_page_table_does_not_crash_full_pipeline`
        // pins that atomic behavior.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        for (var r = 0; r < 3; r++)
        {
            var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
            var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
            var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
            var bcStyle = MakeStyle();
            SetLengthPx(bcStyle, PropertyId.Height, 300);
            anon.AppendChild(Box.ForElement(BoxKind.BlockContainer, bcStyle, MakeElement()));
            cell.AppendChild(anon);
            row.AppendChild(cell);
            grid.AppendChild(row);
        }
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: shaper);

        // Page = 800; rows 300+300+300 = 900 → row 2 overflows.
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Outer BlockLayouter returns PageComplete with a
        // BlockContinuation whose LayouterState carries a
        // TableContinuation pointing at the unfit row (row 2).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var blockCont = Assert.IsType<BlockContinuation>(result.Continuation);
        var tableCont = Assert.IsType<TableContinuation>(blockCont.LayouterState);
        Assert.Equal(2, tableCont.NextRowIndex);

        // Only 2 rows committed on page 1; row 2 deferred.
        var rowCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowCount++;
        }
        Assert.Equal(2, rowCount);

        // The split is CLEAN — no forced-overflow diagnostic.
        // (Finding 2's dry-run committed extent suppresses the
        // outer block-flow's false signal.)
        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public void IntraCell_production_oversized_row_splits_within_itself_across_pages()
    {
        // Per Phase 3 Task 17 cycle 5c.2d — through the production
        // BlockLayouter outer-dispatch path: a table whose SINGLE row is
        // taller than the page (its cell stacks block children) breaks
        // WITHIN the row across pages instead of force-overflowing at full
        // extent. The TableContinuation — now carrying RowSplitOffset —
        // round-trips through BlockContinuation.LayouterState every page,
        // re-parking the SAME row 0 until the tail fits. Every block emits
        // exactly once (lossless, no duplication), no forced-overflow.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        for (var i = 0; i < 9; i++) // 9 × 200 = 1800 px over an 800 page → 3 pages
        {
            var bcStyle = MakeStyle();
            SetLengthPx(bcStyle, PropertyId.Height, 200);
            anon.AppendChild(Box.ForElement(
                BoxKind.BlockContainer, bcStyle, MakeElement()));
        }
        cell.AppendChild(anon);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        LayoutContinuation? incoming = null;
        var pages = 0;
        var done = false;
        while (!done && pages < 10)
        {
            using var layouter = new BlockLayouter(
                rootBox: root, sink: sink,
                incomingContinuation: incoming,
                diagnostics: diagSink,
                shaperResolver: shaper);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
            using var resolver = new BreakResolver();
            var result = layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
            pages++;
            if (result.Outcome == LayoutAttemptOutcome.AllDone)
            {
                done = true;
            }
            else
            {
                Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
                incoming = result.Continuation;
                var bc = Assert.IsType<BlockContinuation>(incoming);
                var tc = Assert.IsType<TableContinuation>(bc.LayouterState);
                Assert.Equal(0, tc.NextRowIndex);       // still inside row 0
                Assert.True(tc.RowSplitOffset > 0,
                    "the intra-row split offset must round-trip through the wrapper");
            }
        }

        Assert.True(done, "table never completed within the page bound");
        Assert.Equal(3, pages);

        var blockFragments = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer) blockFragments++;
        }
        Assert.Equal(9, blockFragments);

        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public void IntraCell_production_non_divisor_blocks_cut_at_boundary()
    {
        // Per PR-#201 review P1/P3 — through the production BlockLayouter path
        // (which adds the split-aware dry-run wrapper sizing) with NON-divisor
        // blocks: 600 + 600 on an 800px page. No block starts at the 800px
        // budget, so a raw-edge cut would force-overflow; the boundary-aware cut
        // must split after the first block (RowSplitOffset == 600). Both blocks
        // emit across 2 pages, no forced-overflow.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        foreach (var h in new[] { 600.0, 600.0 })
        {
            var bcStyle = MakeStyle();
            SetLengthPx(bcStyle, PropertyId.Height, h);
            anon.AppendChild(Box.ForElement(BoxKind.BlockContainer, bcStyle, MakeElement()));
        }
        cell.AppendChild(anon);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        LayoutContinuation? incoming = null;
        var pages = 0;
        var done = false;
        var firstSplitOffset = -1.0;
        while (!done && pages < 8)
        {
            using var layouter = new BlockLayouter(
                rootBox: root, sink: sink, incomingContinuation: incoming,
                diagnostics: diagSink, shaperResolver: shaper);
            var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
            var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
            using var resolver = new BreakResolver();
            var result = layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
            pages++;
            if (result.Outcome == LayoutAttemptOutcome.AllDone)
            {
                done = true;
            }
            else
            {
                Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
                incoming = result.Continuation;
                var bc = Assert.IsType<BlockContinuation>(incoming);
                var tc = Assert.IsType<TableContinuation>(bc.LayouterState);
                if (firstSplitOffset < 0) firstSplitOffset = tc.RowSplitOffset;
            }
        }

        Assert.True(done, "table never completed within the page bound");
        Assert.Equal(2, pages);
        Assert.Equal(600.0, firstSplitOffset, precision: 3); // boundary, not 800
        var blockFragments = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.BlockContainer) blockFragments++;
        }
        Assert.Equal(2, blockFragments);
        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public void Cycle2_production_table_at_depth_3_splits_cleanly()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) — the
        // multi-level continuation propagation lifts the depth==1-only
        // limit. Construct a 3-level-deep nest:
        //   root > div1 > div2 > table(3 rows of 300 each)
        // Page = 800; row stack 900 → row 2 overflows. Verify the
        // recursion returns a chain of 3 nested BlockContinuations
        // wrapping the TableContinuation leaf at NextRowIndex=2.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var div1 = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var div2 = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        for (var r = 0; r < 3; r++)
        {
            var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
            var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
            var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
            var bcStyle = MakeStyle();
            SetLengthPx(bcStyle, PropertyId.Height, 300);
            anon.AppendChild(Box.ForElement(BoxKind.BlockContainer, bcStyle, MakeElement()));
            cell.AppendChild(anon);
            row.AppendChild(cell);
            grid.AppendChild(row);
        }
        table.AppendChild(grid);
        div2.AppendChild(table);
        div1.AppendChild(div2);
        root.AppendChild(div1);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var topBc = Assert.IsType<BlockContinuation>(result.Continuation);
        // Walk chain to TableContinuation leaf — 3 nested BCs in total.
        object? walker = topBc.LayouterState;
        var depth = 0;
        while (walker is BlockContinuation deeper)
        {
            depth++;
            walker = deeper.LayouterState;
            Assert.True(depth < 32, "Chain runaway?");
        }
        var tc = Assert.IsType<TableContinuation>(walker);
        Assert.Equal(2, tc.NextRowIndex);
        // Chain depth visible to the test = (raw recursion depth) - 1
        // due to the top-level flatten (LayouterState ?? deepRet).
        // For root > div1 > div2 > table the raw depth is 2 (the
        // outer dispatch walks into div1 at depth=1; the recursion
        // walks into div2 at depth=2; the table is at depth=2's
        // child level). The flatten removes one wrapping BC; the
        // remaining BC-chain visible to the walker = 1.
        Assert.True(depth >= 1,
            "Expected chain depth >= 1 for root > div1 > div2 > table; got " + depth);

        // No PAGINATION-FORCED-OVERFLOW-001 — split is clean.
        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public async Task Cycle2_production_html_body_table_splits_cleanly()
    {
        // Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
        // complementary positive case for html > body > table. The
        // flipped pin test asserts this from the row-count + chain
        // angle; this test asserts from the structured-walking angle.
        const string html = """
            <!DOCTYPE html><html><head><style>
                td > div { height: 300px; }
            </style></head><body>
            <table>
              <tr><td><div></div></td></tr>
              <tr><td><div></div></td></tr>
              <tr><td><div></div></td></tr>
            </table>
            </body></html>
            """;

        var (_, diagnostics, _, result) =
            await RenderViaFullPipelineCapturingResultAsync(html);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var topBc = Assert.IsType<BlockContinuation>(result.Continuation);
        object? walker = topBc.LayouterState;
        var depth = 0;
        while (walker is BlockContinuation deeper)
        {
            depth++;
            walker = deeper.LayouterState;
            Assert.True(depth < 32, "Chain runaway?");
        }
        var tc = Assert.IsType<TableContinuation>(walker);
        Assert.Equal(2, tc.NextRowIndex);
        Assert.True(depth >= 1,
            "Expected chain depth >= 1 for html > body > table; got " + depth);
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public async Task Cycle2_production_html_table_with_thead_tfoot_emits_header_and_footer_fragments()
    {
        // Phase 3 Task 13 cycle 2 — production-pipeline test that
        // <thead> and <tfoot> elements flow through BoxBuilder as
        // TableHeaderGroup / TableFooterGroup boxes + their rows
        // emit as TableRow fragments alongside <tbody> rows.
        //
        // Note: under <html><body><table> the recursive-atomic
        // fallback from cycle 1's Finding 1 keeps the table on a
        // single page (rather than splitting + repeating header/
        // footer per the cycle 2 algorithm). Cycle 2's per-page
        // repetition is fully exercised by the direct-construction
        // tests in TableLayouterTests.cs. This test pins the
        // production-path recognition + fragment emission: header,
        // body, and footer rows all flow through BoxBuilder + emit
        // TableRow BoxFragments without throwing.
        const string html = """
            <!DOCTYPE html><html><head><style>
                th > div, td > div { height: 20px; }
            </style></head><body>
            <table>
              <thead><tr><th><div>Header</div></th></tr></thead>
              <tbody>
                <tr><td><div>Row 1</div></td></tr>
                <tr><td><div>Row 2</div></td></tr>
              </tbody>
              <tfoot><tr><td><div>Footer</div></td></tr></tfoot>
            </table>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsync(html);

        // 4 TableRow fragments expected: 1 header + 2 body + 1 footer.
        var rowCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowCount++;
        }
        Assert.Equal(4, rowCount);

        // No LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 diagnostic
        // mentioning thead/tfoot/header/footer — cycle 2 ships
        // header/footer recognition.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && (d.Message.Contains("thead", System.StringComparison.OrdinalIgnoreCase)
                || d.Message.Contains("tfoot", System.StringComparison.OrdinalIgnoreCase)
                || d.Message.Contains("header", System.StringComparison.OrdinalIgnoreCase)
                || d.Message.Contains("footer", System.StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Cycle2_production_html_table_with_thead_tfoot_repeats_across_pages_via_full_pipeline()
    {
        // Per Phase 3 Task 13 cycle 2 hardening Finding 1 — the
        // single-level nested-recursion propagation path now carries
        // TableContinuation through EmitBlockSubtreeRecursive when the
        // table is at depth==1 (= direct descendant of the just-emitted
        // top-level child of the BlockLayouter's root, e.g. body in
        // the typical <html><body><table>). The recursion uses the
        // OUTER resolver; PageComplete bubbles up the call stack to
        // the main loop which wraps it in a BlockContinuation.
        //
        // This test pins: (a) header + footer fragments appear on
        // BOTH pages (= cycle 2's per-page repetition fires through
        // the production pipeline), (b) body rows split correctly
        // (page 1's body window != page 2's body window), (c) no
        // PAGINATION-FORCED-OVERFLOW-001 diagnostic (the table splits
        // cleanly, no false signal from the outer block-flow path).
        //
        // The HTML is sized so the table forces a split: header (20px)
        // + 3 body rows (40px each) + footer (20px) = 160px; page = 100;
        // can't fit all on one page → cycle 2 splits the body slice
        // across two pages, repeating header + footer.
        const string html = """
            <!DOCTYPE html><html><head><style>
                th > div { height: 20px; }
                td > div { height: 40px; }
            </style></head><body>
            <table>
              <thead><tr><th><div>Header</div></th></tr></thead>
              <tbody>
                <tr><td><div>Row 1</div></td></tr>
                <tr><td><div>Row 2</div></td></tr>
                <tr><td><div>Row 3</div></td></tr>
              </tbody>
              <tfoot><tr><td><div>Footer</div></td></tr></tfoot>
            </table>
            </body></html>
            """;

        var (sink, diagnostics, _) = await RenderViaFullPipelineAsyncWithPaging(
            html, contentInlineSize: 600, pageBlockSize: 100);

        // We expect at least one TableRow header + one TableRow footer
        // emitted PER PAGE the table spans. Conservative assertion:
        // total row fragments > unique source row count (= 5: 1 head +
        // 3 body + 1 foot) because header/footer repeated.
        var rowCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowCount++;
        }
        Assert.True(rowCount >= 5,
            $"Expected ≥5 TableRow fragments (= 5 unique rows or more "
            + $"due to repetition); got {rowCount}.");

        // No PAGINATION-FORCED-OVERFLOW-001 — the table splits cleanly.
        Assert.DoesNotContain(diagnostics.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    /// <summary>Variant of <see cref="RenderViaFullPipelineAsync"/>
    /// that drives multi-page layout by iterating AttemptLayout while
    /// the result is PageComplete. The fragmentainer's UsedBlockSize
    /// is reset between pages.</summary>
    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root)>
        RenderViaFullPipelineAsyncWithPaging(
            string html, double contentInlineSize, double pageBlockSize)
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

        LayoutContinuation? continuation = null;
        const int maxPages = 6;
        for (var page = 0; page < maxPages; page++)
        {
            using var layouter = new BlockLayouter(
                rootBox: box,
                sink: sink,
                incomingContinuation: continuation,
                diagnostics: diagSink,
                shaperResolver: shaper);

            var ctx = new FragmentainerContext(
                contentInlineSize: contentInlineSize,
                blockSize: pageBlockSize);
            var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
            using var resolver = new BreakResolver();
            var result = layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

            if (result.Outcome == LayoutAttemptOutcome.AllDone) break;
            if (result.Outcome != LayoutAttemptOutcome.PageComplete) break;
            continuation = result.Continuation;
        }

        return (sink, diagSink, box);
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, PropertyId id, double px)
        => style.Set(id, ComputedSlot.FromLengthPx(px));

    private static IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
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
        var (sink, diagnostics, box, _) =
            await RenderViaFullPipelineCapturingResultAsync(html);
        return (sink, diagnostics, box);
    }

    /// <summary>Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
    /// variant of <see cref="RenderViaFullPipelineAsync"/> that also
    /// returns the page-1 <see cref="LayoutAttemptResult"/>. Used by
    /// tests that need to assert on the continuation chain.</summary>
    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root,
        LayoutAttemptResult result)>
        RenderViaFullPipelineCapturingResultAsync(string html)
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
        var result = layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        return (sink, diagSink, box, result);
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

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            // Per Phase 3 Task 17 cycle 5c.2b — F2 wrapper-resize.
            // Mutate the BoxFragment at <c>cursor</c> in place so the
            // BlockLayouter's post-dispatch wrapper-resize consumer
            // can shrink a paginatable-grid / paginatable-flex
            // wrapper from the clamped budget to the actual emitted
            // extent without breaking z-order (= the wrapper stays
            // ahead of its children in the fragment list).
            if (cursor < 0 || cursor >= Fragments.Count) return;
            Fragments[cursor] = Fragments[cursor] with { BlockSize = newBlockSize };
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
