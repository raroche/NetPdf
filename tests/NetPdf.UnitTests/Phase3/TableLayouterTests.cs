// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
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
/// Phase 3 Task 12 sub-cycles 1 + 2 — TableLayouter tests.
/// Constructs Table → TableGrid → TableRow → TableCell hierarchies
/// directly (bypassing BoxBuilder) so each test can pin a precise
/// structural shape + verify the layouter's row/cell emit math.
///
/// <para>Fixture mirrors <see cref="BlockLayouterTests"/> /
/// <see cref="BlockInlineIntegrationTests"/> — <c>RentForExclusiveTesting</c>
/// styles, a <see cref="RecordingFragmentSink"/>, a
/// <see cref="RecordingDiagnosticsSink"/>. The synthetic-font shaper
/// resolver lets cell content render real glyphs when a test exercises
/// the inline-only-block dispatch inside cells.</para>
///
/// <para><b>Behaviors exercised here:</b>
/// <list type="bullet">
///   <item>Sub-cycle 2 colspan / rowspan via the 2D occupancy-grid
///   algorithm — placement, equal-split column widths, row-height
///   distribution.</item>
///   <item>Multi-page splitting within a table — emits all rows on
///   the current page; <c>PAGINATION-FORCED-OVERFLOW-001</c>
///   diagnostic fires when the row stack exceeds the page bottom
///   (multi-fragmentainer table splitting is deferred — see
///   <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>).</item>
///   <item>Auto / fixed column-width algorithms still use the equal-
///   split approximation; tests assert the equal-split offsets.</item>
///   <item>Caption deferral diagnostic + sanitized caption text
///   snippets in the diagnostic message.</item>
/// </list></para>
/// </summary>
public sealed class TableLayouterTests
{
    [Fact]
    public void Simple_2x2_table_emits_outer_plus_2_rows_plus_4_cells()
    {
        // Build a 2x2 table directly. Each cell carries a single
        // AnonymousBlock wrapping a TextRun so the inner BlockLayouter
        // dispatches the inline-only path (matching what BoxBuilder
        // produces).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var (root, table) = BuildTable(rowCount: 2, columnCount: 2,
            cellTextRun: "X", style: () => MakeStyle());

        using var layouter = new BlockLayouter(
            root, sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: shaper);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Expected emit order (sub-cycle 1):
        //   [0] = Table wrapper (from BlockLayouter)
        //   [1] = cell (0,0) inline-only-block fragment (from nested BlockLayouter)
        //   [2] = cell (0,1) inline-only-block fragment
        //   [3] = row 0 fragment (TableRow)
        //   [4] = cell (0,0) TableCell fragment
        //   [5] = cell (0,1) TableCell fragment
        //   [6] = cell (1,0) inline-only-block fragment
        //   [7] = cell (1,1) inline-only-block fragment
        //   [8] = row 1 fragment (TableRow)
        //   [9] = cell (1,0) TableCell fragment
        //  [10] = cell (1,1) TableCell fragment
        // ⇒ 1 wrapper + 2 rows + 4 cells + 4 inline-only-block content fragments = 11
        Assert.Equal(11, sink.Fragments.Count);

        // The outer wrapper is the first emit.
        Assert.Same(table, sink.Fragments[0].Box);
        Assert.Equal(BoxKind.Table, sink.Fragments[0].Box.Kind);

        // Find rows + cells by Box.Kind.
        var rowFragments = new List<BoxFragment>();
        var cellFragments = new List<BoxFragment>();
        foreach (var frag in sink.Fragments)
        {
            switch (frag.Box.Kind)
            {
                case BoxKind.TableRow:
                    rowFragments.Add(frag);
                    break;
                case BoxKind.TableCell:
                    cellFragments.Add(frag);
                    break;
            }
        }
        Assert.Equal(2, rowFragments.Count);
        Assert.Equal(4, cellFragments.Count);

        // Equal column split — content inline size is 600 (no padding /
        // border on the wrapper in this test); column width = 300.
        Assert.Equal(300, cellFragments[0].InlineSize);
        Assert.Equal(300, cellFragments[1].InlineSize);
        Assert.Equal(0, cellFragments[0].InlineOffset);
        Assert.Equal(300, cellFragments[1].InlineOffset);

        // Row 0 spans full width.
        Assert.Equal(600, rowFragments[0].InlineSize);
        Assert.Equal(0, rowFragments[0].InlineOffset);
    }

    [Fact]
    public void Row_height_matches_max_cell_height()
    {
        // Two cells in one row: the LEFT cell carries an AnonymousBlock
        // with an explicit height; the RIGHT cell carries the default.
        // The row's height should be the max of the two cells' measured
        // extents.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        // Build directly so we can control the explicit height.
        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());

        // Tall cell — explicit height on its inner block.
        var tallCell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var tallInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var tallStyle = MakeStyle();
        SetLengthPx(tallStyle, PropertyId.Height, 150);
        var tallBlock = Box.ForElement(BoxKind.BlockContainer, tallStyle, MakeElement());
        tallInner.AppendChild(tallBlock);
        tallCell.AppendChild(tallInner);

        // Short cell — explicit height on its inner block.
        var shortCell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var shortInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var shortStyle = MakeStyle();
        SetLengthPx(shortStyle, PropertyId.Height, 40);
        var shortBlock = Box.ForElement(BoxKind.BlockContainer, shortStyle, MakeElement());
        shortInner.AppendChild(shortBlock);
        shortCell.AppendChild(shortInner);

        row.AppendChild(tallCell);
        row.AppendChild(shortCell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find the TableRow fragment.
        BoxFragment? rowFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) { rowFragment = f; break; }
        }
        Assert.NotNull(rowFragment);

        // Row height = max(tall=150, short=40) = 150. (The tall block's
        // height carries the explicit 150 — nothing else contributes to
        // cell content extent.) Both cells should also share this row
        // height per the sub-cycle 1 "cells stretch to row" rule.
        Assert.Equal(150, rowFragment!.Value.BlockSize);

        // Both cells emitted with the row's height.
        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(2, cells.Count);
        Assert.Equal(150, cells[0].BlockSize);
        Assert.Equal(150, cells[1].BlockSize);
    }

    [Fact]
    public void Equal_column_split_across_3_columns()
    {
        // 3 columns, contentInlineSize = 900 ⇒ column width = 300.
        // No cell content — empty cells; row height resolves to 0.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        for (var i = 0; i < 3; i++)
        {
            var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
            row.AppendChild(cell);
        }
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 900, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Find cell fragments.
        var cells = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cells.Add(f);
        }
        Assert.Equal(3, cells.Count);
        Assert.Equal(300, cells[0].InlineSize);
        Assert.Equal(300, cells[1].InlineSize);
        Assert.Equal(300, cells[2].InlineSize);
        Assert.Equal(0,   cells[0].InlineOffset);
        Assert.Equal(300, cells[1].InlineOffset);
        Assert.Equal(600, cells[2].InlineOffset);
    }

    [Fact]
    public void Empty_table_with_no_rows_emits_only_outer_placeholder()
    {
        // Table → TableGrid (no rows). Sub-cycle 1 emits only the
        // wrapper fragment + no row/cell fragments.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Exactly one fragment — the outer Table wrapper. No rows, no
        // cells (the TableLayouter's CollectRows returned empty + the
        // method returned AllDone before emitting any row geometry).
        Assert.Single(sink.Fragments);
        Assert.Equal(BoxKind.Table, sink.Fragments[0].Box.Kind);
    }

    [Fact]
    public void Colspan_rowspan_no_longer_emits_LAYOUT_TABLE_FEATURE_UNSUPPORTED_001()
    {
        // Sub-cycle 2 — the colspan / rowspan attribute path no longer
        // emits LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 (the 2D occupancy-
        // grid algorithm now correctly merges cells). This is the
        // regression test for the diagnostic removal: the table from
        // the old "Table_with_colspan_attribute_emits_feature_unsupported_diagnostic"
        // shape must NOT trigger the feature-unsupported code.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cellWithColspan = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "2"));
        row.AppendChild(cellWithColspan);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        layoutCtx.Diagnostics = diagSink;
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // No LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 (the cell-merging
        // path now works). The code itself stays in the catalog —
        // captions + missing-TableGrid still emit it — so this
        // assertion specifically guards against the colspan emit path.
        foreach (var d in diagSink.Diagnostics)
        {
            Assert.False(
                d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                "Sub-cycle 2 no longer emits LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 "
                + "for colspan / rowspan — the 2D occupancy-grid algorithm "
                + "renders span cells correctly. Diagnostic message: "
                + d.Message);
        }
    }

    [Fact]
    public void Table_oversized_for_fragmentainer_emits_forced_overflow()
    {
        // Two tall rows that together exceed the page block-size. Sub-
        // cycle 1 commits both rows + emits PAGINATION-FORCED-OVERFLOW-
        // 001. Multi-page splitting is sub-cycle 2.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // Page is 800. Each row carries one 500-tall cell ⇒ two rows
        // stack to 1000 > 800 page bottom.
        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        for (var r = 0; r < 2; r++)
        {
            var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
            var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
            var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
            var tallStyle = MakeStyle();
            SetLengthPx(tallStyle, PropertyId.Height, 500);
            var block = Box.ForElement(BoxKind.BlockContainer, tallStyle, MakeElement());
            anon.AppendChild(block);
            cell.AppendChild(anon);
            row.AppendChild(cell);
            grid.AppendChild(row);
        }
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        layoutCtx.Diagnostics = diagSink;
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        // Both rows + their cells DID emit (sub-cycle 1 atomic
        // commit).
        var rowCount = 0;
        var cellCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowCount++;
            if (f.Box.Kind == BoxKind.TableCell) cellCount++;
        }
        Assert.Equal(2, rowCount);
        Assert.Equal(2, cellCount);

        // The forced-overflow diagnostic fired (emitted by TableLayouter
        // because the second row's bottom exceeds the page).
        var hasOverflowDiag = false;
        foreach (var d in diagSink.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001)
            {
                hasOverflowDiag = true;
                break;
            }
        }
        Assert.True(hasOverflowDiag,
            "Expected PAGINATION-FORCED-OVERFLOW-001 when the row stack "
            + "exceeds the page block-size (sub-cycle 1 doesn't split "
            + "tables across pages).");
    }

    [Fact]
    public void TableLayouter_rejects_non_table_root_box()
    {
        // The TableLayouter contract rejects any rootBox that isn't a
        // Table or InlineTable wrapper.
        var sink = new RecordingFragmentSink();
        var blockBox = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());

        Assert.Throws<ArgumentException>(() =>
            new TableLayouter(blockBox, sink));
    }

    [Fact]
    public void TableLayouter_rejects_non_null_continuation_in_sub_cycle_1()
    {
        // Sub-cycle 1 doesn't yet support mid-table resume — the
        // constructor rejects a non-null continuation.
        var sink = new RecordingFragmentSink();
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var continuation = new BlockContinuation(ResumeAtChild: 0, ConsumedBlockSize: 0);

        Assert.Throws<ArgumentException>(() =>
            new TableLayouter(table, sink, incomingContinuation: continuation));
    }

    [Fact]
    public void Rows_inside_TableRowGroup_are_collected()
    {
        // Table → TableGrid → TableRowGroup → TableRow → TableCell —
        // the TableLayouter should recurse into the row group when
        // collecting rows.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var rowGroup = Box.ForElement(BoxKind.TableRowGroup, MakeStyle(), MakeElement());
        for (var r = 0; r < 2; r++)
        {
            var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
            row.AppendChild(Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement()));
            rowGroup.AppendChild(row);
        }
        grid.AppendChild(rowGroup);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(
            root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
            LayoutAttemptStrategy.LastResort);

        var rowCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowCount++;
        }
        Assert.Equal(2, rowCount);
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 1 post-PR-49 hardening review tests
    //  (Findings 1-5; Finding 6 lives in TableLayouterProductionTests.cs).
    // ====================================================================

    [Fact]
    public void Finding1_table_height_feeds_wrapper_so_following_sibling_does_not_overlap()
    {
        // Per Finding 1 — pre-PR-49 the wrapper fragment was emitted
        // with BlockSize = 0 (auto-height returns 0 from
        // ReadLengthPxOrZero), so siblings overlapped the table rows.
        // Post-fix the wrapper's border-box is sized to the measured
        // table content height; the following sibling lands BELOW
        // the table.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        // Build: root -> [table (one row, one cell with 100-tall block),
        //                 following paragraph (50-tall block)]
        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var cellAnon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var cellInnerStyle = MakeStyle();
        SetLengthPx(cellInnerStyle, PropertyId.Height, 100);
        var cellInner = Box.ForElement(BoxKind.BlockContainer, cellInnerStyle, MakeElement());
        cellAnon.AppendChild(cellInner);
        cell.AppendChild(cellAnon);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        var followStyle = MakeStyle();
        SetLengthPx(followStyle, PropertyId.Height, 50);
        var follow = Box.ForElement(BoxKind.BlockContainer, followStyle, MakeElement());
        root.AppendChild(follow);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Find the table wrapper + the follow-block fragments.
        BoxFragment? tableFragment = null;
        BoxFragment? followFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, table)) tableFragment = f;
            if (ReferenceEquals(f.Box, follow)) followFragment = f;
        }
        Assert.NotNull(tableFragment);
        Assert.NotNull(followFragment);

        // Table wrapper now has the measured content height in its
        // border-box block extent (pre-fix this was 0).
        Assert.True(tableFragment!.Value.BlockSize >= 100,
            $"Expected wrapper BlockSize ≥ 100 (cell content), got {tableFragment.Value.BlockSize}.");

        // The following paragraph lands BELOW the table's bottom edge —
        // no overlap.
        var tableBottom = tableFragment.Value.BlockOffset + tableFragment.Value.BlockSize;
        Assert.True(followFragment!.Value.BlockOffset >= tableBottom,
            $"Sibling overlap: follow.BlockOffset={followFragment.Value.BlockOffset} < "
            + $"tableBottom={tableBottom}.");
    }

    [Fact]
    public void Finding1_MeasureContentHeight_is_idempotent()
    {
        // Per Finding 1 spec — MeasureContentHeight must be idempotent;
        // calling twice returns the same value from cache (no re-running
        // of cell layouts).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var (_, table) = BuildTable(rowCount: 2, columnCount: 2,
            cellTextRun: "X", style: () => MakeStyle());

        using var tableLayouter = new TableLayouter(
            rootBox: table, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        tableLayouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0, contentInlineSize: 600);

        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);

        var first = tableLayouter.MeasureContentHeight(ctx, ref layoutCtx);
        var second = tableLayouter.MeasureContentHeight(ctx, ref layoutCtx);

        Assert.Equal(first, second);
        Assert.True(first > 0, "Expected a positive measured height for a non-empty table.");
    }

    [Fact]
    public void Finding2_emit_order_is_row_then_cell_then_cell_content()
    {
        // Per Finding 2 — paint-safe order requires row → cell → cell
        // content. Build a 1x1 table with a TextRun in the cell; assert
        // the outer sink saw row before cell before content.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var cellAnon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        cellAnon.AppendChild(Box.TextRun("Hello", MakeStyle()));
        cell.AppendChild(cellAnon);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Find indices of the row + cell + cell-content fragments. The
        // cell content is the AnonymousBlock fragment whose box is the
        // anon under the cell.
        int rowIdx = -1, cellIdx = -1, cellContentIdx = -1;
        for (var i = 0; i < sink.Fragments.Count; i++)
        {
            var b = sink.Fragments[i].Box;
            if (b.Kind == BoxKind.TableRow && rowIdx < 0) rowIdx = i;
            else if (b.Kind == BoxKind.TableCell && cellIdx < 0) cellIdx = i;
            else if (ReferenceEquals(b, cellAnon) && cellContentIdx < 0) cellContentIdx = i;
        }

        Assert.True(rowIdx >= 0, "row fragment not emitted");
        Assert.True(cellIdx >= 0, "cell fragment not emitted");
        Assert.True(cellContentIdx >= 0, "cell-content fragment not emitted");

        // Paint-safe order: row before cell, cell before content.
        Assert.True(rowIdx < cellIdx,
            $"Row at index {rowIdx} must precede cell at index {cellIdx} for paint-safe order.");
        Assert.True(cellIdx < cellContentIdx,
            $"Cell at index {cellIdx} must precede cell content at index {cellContentIdx} "
            + "for paint-safe order (cell background under text).");
    }

    [Fact]
    public void Finding3_cell_layout_does_not_alter_outer_resolver_checkpoint_state()
    {
        // Per Finding 3 — the nested BlockLayouter for cell content
        // must use a FRESH BreakResolver scoped to the cell. The outer
        // resolver's checkpoint state must be IDENTICAL before and
        // after the table dispatch.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var (root, _) = BuildTable(rowCount: 2, columnCount: 2,
            cellTextRun: "X", style: () => MakeStyle());

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);

        // Use a custom resolver that snapshots its registered-checkpoint
        // count + last-registered identity at every Register. After the
        // table layout, neither value should reflect cell-internal
        // checkpoints (only the outer wrapper's checkpoints, which is
        // exactly 1 — the wrapper-level checkpoint registered by
        // BlockLayouter when entering the table child).
        var capturingResolver = new CapturingResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, capturingResolver, LayoutAttemptStrategy.LastResort);

        // The number of checkpoints registered against the outer
        // resolver matches the count of block-level children processed
        // BY THE OUTER LAYOUTER (= 1, the Table wrapper). Pre-fix the
        // cells' inner BlockLayouter would also register against this
        // resolver, inflating the count by 4 (one per cell).
        Assert.Equal(1, capturingResolver.RegisterCount);
        capturingResolver.Dispose();
    }

    [Fact]
    public void Caption_no_longer_emits_LAYOUT_TABLE_FEATURE_UNSUPPORTED_001()
    {
        // Renamed + inverted from sub-cycle 1 Finding 4 test
        // (Finding4_caption_emits_diagnostic_with_text_snippet).
        // Sub-cycle 1 + 2 emitted LAYOUT-TABLE-FEATURE-UNSUPPORTED-001
        // for captions to surface the deferral; sub-cycle 3 lays
        // captions out for real (CSS Tables L3 §11.5), so the
        // diagnostic must NOT fire.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        // Caption is a DIRECT child of the wrapper (per BoxBuilder).
        var caption = Box.ForElement(BoxKind.TableCaption, MakeStyle(), MakeElement());
        caption.AppendChild(Box.TextRun("Annual Report 2026", MakeStyle()));
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // No LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 diagnostic for the
        // caption path. (The code itself stays in the catalog —
        // missing-TableGrid + span=0 still emit it — so we look
        // specifically for the "caption" message text.)
        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));

        // The caption is actually laid out — a TableCaption fragment
        // appears in the sink with a positive InlineSize matching the
        // table content width (600).
        var captionFragment = sink.Fragments.Find(f => f.Box.Kind == BoxKind.TableCaption);
        Assert.True(captionFragment.Box?.Kind == BoxKind.TableCaption,
            "Expected a TableCaption fragment emitted by the layouter.");
        Assert.Equal(600, captionFragment.InlineSize);
    }

    [Fact]
    public void Finding5_missing_TableGrid_emits_feature_unsupported_not_overflow()
    {
        // Per Finding 5 — pre-fix a Table wrapper with no TableGrid
        // child emitted PAGINATION-FORCED-OVERFLOW-001 (a pagination
        // code for what is actually a malformed-box-tree anomaly).
        // Post-fix the code is LAYOUT-TABLE-FEATURE-UNSUPPORTED-001
        // (which carries the table-specific signal that something is
        // wrong with the table's box structure).
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // Construct a Table wrapper WITHOUT a TableGrid child.
        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var hasFeatureDiag = false;
        var hasOverflowDiag = false;
        foreach (var d in diagSink.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
                && d.Message.Contains("no TableGrid"))
            {
                hasFeatureDiag = true;
            }
            if (d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001)
            {
                hasOverflowDiag = true;
            }
        }

        Assert.True(hasFeatureDiag,
            "Expected LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 for missing TableGrid child.");
        Assert.False(hasOverflowDiag,
            "Did NOT expect PAGINATION-FORCED-OVERFLOW-001 for malformed table tree.");
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 2 — colspan / rowspan cell merging
    // ====================================================================

    [Fact]
    public void Colspan_2_cell_spans_two_columns()
    {
        // 2-row, 4-column table where the first cell of row 1 has
        // colspan=2. Expected geometry (contentInlineSize=600,
        // columnCount=4 ⇒ columnWidth=150):
        //   Row 0: cell0(colspan=2, col=0..1, width=300) +
        //          cell1(col=2)            + cell2(col=3)
        //   Row 1: cell0(col=0) + cell1(col=1) + cell2(col=2) + cell3(col=3)
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // Row 0 — first cell carries colspan=2.
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var spanCell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "2"));
        row0.AppendChild(spanCell);
        row0.AppendChild(Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement()));
        row0.AppendChild(Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement()));
        grid.AppendChild(row0);

        // Row 1 — 4 plain cells.
        var row1 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        for (var i = 0; i < 4; i++)
        {
            row1.AppendChild(Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement()));
        }
        grid.AppendChild(row1);

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Find cells in document order — they're emitted row-by-row,
        // so the first 3 TableCell fragments belong to row 0 (the
        // colspan cell + its 2 plain neighbors) and the next 4 to
        // row 1.
        var cellFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cellFragments.Add(f);
        }
        Assert.Equal(7, cellFragments.Count); // 3 in row 0 + 4 in row 1

        // The first row-0 cell is the span cell.
        Assert.Same(spanCell, cellFragments[0].Box);
        // columnWidth = 600 / 4 = 150 → colspan=2 cell width = 300.
        Assert.Equal(300, cellFragments[0].InlineSize);
        Assert.Equal(0, cellFragments[0].InlineOffset);

        // The remaining row-0 cells anchor at columns 2 + 3
        // (offsets 300 + 450).
        Assert.Equal(300, cellFragments[1].InlineOffset);
        Assert.Equal(150, cellFragments[1].InlineSize);
        Assert.Equal(450, cellFragments[2].InlineOffset);
        Assert.Equal(150, cellFragments[2].InlineSize);

        // Row 1's 4 cells anchor at columns 0..3.
        Assert.Equal(0, cellFragments[3].InlineOffset);
        Assert.Equal(150, cellFragments[3].InlineSize);
        Assert.Equal(150, cellFragments[4].InlineOffset);
        Assert.Equal(300, cellFragments[5].InlineOffset);
        Assert.Equal(450, cellFragments[6].InlineOffset);
    }

    [Fact]
    public void Rowspan_2_cell_spans_two_rows()
    {
        // 2-row, 2-column table where the first cell carries rowspan=2.
        // Row layout:
        //   Row 0: spanCell(col=0,rowspan=2) + plainCell(col=1)
        //   Row 1: plainCell(col=1)            <-- col 0 is occupied
        //                                          by the rowspan cell
        //
        // Expected: spanCell.BlockSize = row0.height + row1.height.
        //           Row 1's plain cell anchors at col 1, not col 0.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // Row 0 — span cell + a 60-tall plain cell.
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var spanCell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "2"));
        row0.AppendChild(spanCell);
        var row0PlainCell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var row0Anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var row0Style = MakeStyle();
        SetLengthPx(row0Style, PropertyId.Height, 60);
        var row0Block = Box.ForElement(BoxKind.BlockContainer, row0Style, MakeElement());
        row0Anon.AppendChild(row0Block);
        row0PlainCell.AppendChild(row0Anon);
        row0.AppendChild(row0PlainCell);
        grid.AppendChild(row0);

        // Row 1 — one plain cell with 80-tall content.
        var row1 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var row1Cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var row1Anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var row1Style = MakeStyle();
        SetLengthPx(row1Style, PropertyId.Height, 80);
        var row1Block = Box.ForElement(BoxKind.BlockContainer, row1Style, MakeElement());
        row1Anon.AppendChild(row1Block);
        row1Cell.AppendChild(row1Anon);
        row1.AppendChild(row1Cell);
        grid.AppendChild(row1);

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Find the span cell + the row1 plain cell.
        BoxFragment? spanCellFragment = null;
        BoxFragment? row1CellFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, spanCell)) spanCellFragment = f;
            if (ReferenceEquals(f.Box, row1Cell)) row1CellFragment = f;
        }
        Assert.NotNull(spanCellFragment);
        Assert.NotNull(row1CellFragment);

        // Row heights — row0 = max(rowspan=1 content) = 60 (the plain
        // cell), row1 = 80. Span cell extends 60 → 140 covers
        // content; no extension needed.
        // Span cell block extent = 60 + 80 = 140.
        Assert.Equal(140, spanCellFragment!.Value.BlockSize);
        Assert.Equal(0, spanCellFragment.Value.BlockOffset);

        // Row 1's plain cell sits at column 1 (column 0 is occupied
        // by the rowspan cell). columnWidth = 400 / 2 = 200.
        Assert.Equal(200, row1CellFragment!.Value.InlineOffset);
        Assert.Equal(200, row1CellFragment.Value.InlineSize);
        Assert.Equal(60, row1CellFragment.Value.BlockOffset); // after row 0
    }

    [Fact]
    public void Colspan_and_rowspan_combined()
    {
        // 2-row, 2-column table where the first cell of row 0 has
        // BOTH colspan=2 + rowspan=2. The table grid has only that
        // single anchored cell (no other cells fit in the 2x2 grid
        // because the spanning cell occupies all four slots).
        // Expected: cell width = 2 × columnWidth, cell block extent =
        // row0 + row1 heights, no other cell fragments.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // Row 0 — single colspan=2,rowspan=2 cell with 100-tall content.
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var element = MakeElement();
        element.SetAttribute("colspan", "2");
        element.SetAttribute("rowspan", "2");
        var spanCell = Box.ForElement(BoxKind.TableCell, MakeStyle(), element);
        var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var spanStyle = MakeStyle();
        SetLengthPx(spanStyle, PropertyId.Height, 100);
        var spanBlock = Box.ForElement(BoxKind.BlockContainer, spanStyle, MakeElement());
        anon.AppendChild(spanBlock);
        spanCell.AppendChild(anon);
        row0.AppendChild(spanCell);
        grid.AppendChild(row0);

        // Row 1 — empty (no cells fit; the rowspan continuation
        // covers both columns).
        var row1 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        grid.AppendChild(row1);

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        BoxFragment? spanFragment = null;
        var cellCount = 0;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell)
            {
                cellCount++;
                if (ReferenceEquals(f.Box, spanCell)) spanFragment = f;
            }
        }
        Assert.NotNull(spanFragment);
        Assert.Equal(1, cellCount); // only the colspan+rowspan cell

        // colSpan=2 ⇒ inline-size = 2 × (400/2) = 400.
        Assert.Equal(400, spanFragment!.Value.InlineSize);
        Assert.Equal(0, spanFragment.Value.InlineOffset);

        // rowspan=2 — row heights pass A finds row 0 = 0 (no
        // rowspan=1 cells) + row 1 = 0. Pass B sees the 100-tall
        // span cell exceeds sum(0, 0)=0, extends row 1 to 100.
        // Span cell block size = 0 + 100 = 100.
        Assert.Equal(100, spanFragment.Value.BlockSize);
    }

    [Fact]
    public void Rowspan_cell_content_taller_than_natural_rows_extends_last_row()
    {
        // 3-row, 2-column table where col 0 of row 0 has rowspan=3
        // + 300-tall content. Each of rows 1 + 2 has one 50-tall cell
        // in col 1.
        // Pass A: rowHeights = [0, 50, 50] (row 0 has no rowspan=1
        // cell). Pass B: span cell extent 300 > sum(0,50,50)=100,
        // excess 200 lands on rowHeights[0 + 3 - 1 = 2] → 250.
        // Span cell block size = 0 + 50 + 250 = 300 (covers content).
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // Row 0 — single rowspan=3 cell with 300-tall content.
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var spanCell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "3"));
        var spanAnon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var spanStyle = MakeStyle();
        SetLengthPx(spanStyle, PropertyId.Height, 300);
        var spanBlock = Box.ForElement(BoxKind.BlockContainer, spanStyle, MakeElement());
        spanAnon.AppendChild(spanBlock);
        spanCell.AppendChild(spanAnon);
        row0.AppendChild(spanCell);
        grid.AppendChild(row0);

        // Rows 1 + 2 — each has one 50-tall plain cell at col 1.
        for (var r = 1; r <= 2; r++)
        {
            var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
            var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
            var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
            var style = MakeStyle();
            SetLengthPx(style, PropertyId.Height, 50);
            var block = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
            anon.AppendChild(block);
            cell.AppendChild(anon);
            row.AppendChild(cell);
            grid.AppendChild(row);
        }

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Span cell block size must cover its 300px content.
        BoxFragment? spanFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, spanCell)) { spanFragment = f; break; }
        }
        Assert.NotNull(spanFragment);
        Assert.Equal(300, spanFragment!.Value.BlockSize);

        // The third row's fragment must be 250 tall (50 natural + 200
        // excess from pass B); locate it by being the last TableRow
        // in document order.
        var rowFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableRow) rowFragments.Add(f);
        }
        Assert.Equal(3, rowFragments.Count);
        Assert.Equal(0, rowFragments[0].BlockSize);   // row 0 has no rowspan=1 cell
        Assert.Equal(50, rowFragments[1].BlockSize);  // row 1 natural height
        Assert.Equal(250, rowFragments[2].BlockSize); // row 2 = 50 + 200 excess
    }

    [Fact]
    public void Colspan_overflowing_row_extends_column_count()
    {
        // Row 0 has 2 plain cells. Row 1's first cell has colspan=4.
        // Column count = max occupied column + 1 = 4 (row 1 fills
        // columns 0..3). Row 0 keeps its 2 cells at columns 0 + 1;
        // columns 2 + 3 are empty in row 0.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // Row 0 — 2 cells.
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        row0.AppendChild(Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement()));
        row0.AppendChild(Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement()));
        grid.AppendChild(row0);

        // Row 1 — single colspan=4 cell.
        var row1 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var spanCell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "4"));
        row1.AppendChild(spanCell);
        grid.AppendChild(row1);

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 800, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // columnCount=4 ⇒ columnWidth = 800 / 4 = 200.
        // Row 0 plain cells must be 200 wide each.
        // Row 1 span cell must be 800 wide (4 × 200).
        // Document order: row 0's 2 cells, then row 1's 1 span cell.
        var cellFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) cellFragments.Add(f);
        }
        Assert.Equal(3, cellFragments.Count);

        Assert.Equal(200, cellFragments[0].InlineSize);
        Assert.Equal(0, cellFragments[0].InlineOffset);
        Assert.Equal(200, cellFragments[1].InlineSize);
        Assert.Equal(200, cellFragments[1].InlineOffset);

        // The colspan=4 cell is in document order #3.
        Assert.Same(spanCell, cellFragments[2].Box);
        Assert.Equal(800, cellFragments[2].InlineSize);
        Assert.Equal(0, cellFragments[2].InlineOffset);
    }

    [Fact]
    public void Cells_with_invalid_colspan_attribute_fall_back_to_1()
    {
        // colspan="abc" + colspan="0" both clamp to 1 per the locked
        // design (HTML5's "colspan=0 means rest of row" semantic is
        // sub-cycle 3+ work; sub-cycle 2 simplifies to "any
        // out-of-range value → 1").
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cellAbc = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "abc"));
        var cellZero = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "0"));
        row.AppendChild(cellAbc);
        row.AppendChild(cellZero);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // columnCount = 2 (both cells fell back to colspan=1). Each
        // cell is 200 wide; cellAbc at col 0, cellZero at col 1.
        BoxFragment? abcFrag = null, zeroFrag = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, cellAbc)) abcFrag = f;
            if (ReferenceEquals(f.Box, cellZero)) zeroFrag = f;
        }
        Assert.NotNull(abcFrag);
        Assert.NotNull(zeroFrag);
        Assert.Equal(200, abcFrag!.Value.InlineSize);
        Assert.Equal(0, abcFrag.Value.InlineOffset);
        Assert.Equal(200, zeroFrag!.Value.InlineSize);
        Assert.Equal(200, zeroFrag.Value.InlineOffset);
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 2 hardening review — Findings 1–7
    // ====================================================================

    [Fact]
    public void Hardening_Finding1_nested_table_does_not_emit_forced_overflow_for_tall_cell_blocks()
    {
        // Per Finding 1 — a body > table where one cell contains TWO
        // tall <div> children. Pre-fix the recursion's pre-measure
        // path synthesized a `blockSize: 1` fragmentainer; the cell's
        // 200-px tall blocks tripped BlockLayouter's forced-overflow
        // PageComplete + TableLayouter discarded the continuation,
        // losing content + emitting a false PAGINATION-FORCED-
        // OVERFLOW-001.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // Root → Div → Table → Grid → Row → Cell → 2 tall block divs.
        var root = Box.CreateRoot(MakeStyle());
        var outerDiv = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());

        var tallStyle1 = MakeStyle();
        SetLengthPx(tallStyle1, PropertyId.Height, 100);
        var tallDiv1 = Box.ForElement(BoxKind.BlockContainer, tallStyle1, MakeElement());

        var tallStyle2 = MakeStyle();
        SetLengthPx(tallStyle2, PropertyId.Height, 100);
        var tallDiv2 = Box.ForElement(BoxKind.BlockContainer, tallStyle2, MakeElement());

        cell.AppendChild(tallDiv1);
        cell.AppendChild(tallDiv2);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        outerDiv.AppendChild(table);
        root.AppendChild(outerDiv);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // No forced-overflow diagnostic.
        var hasOverflowDiag = diagSink.Diagnostics.Exists(d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
        Assert.False(hasOverflowDiag,
            "Pre-Finding-1 the synthesized 1px fragmentainer tripped the forced-overflow path.");

        // Both tall divs land in the emitted fragments.
        var hasTallDiv1 = false;
        var hasTallDiv2 = false;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, tallDiv1)) hasTallDiv1 = true;
            if (ReferenceEquals(f.Box, tallDiv2)) hasTallDiv2 = true;
        }
        Assert.True(hasTallDiv1, "First tall div was dropped by the table recursion.");
        Assert.True(hasTallDiv2, "Second tall div was dropped by the table recursion.");
    }

    [Fact]
    public void Hardening_Finding2_recursive_measure_pass_reports_table_content_extent()
    {
        // Per Finding 2 — a div containing a tall table + a following
        // paragraph. The recursive measure pass must include the
        // table's content extent so the div's reported subtree extent
        // covers the table rows (not just the wrapper border-box).
        // Pre-fix: subtree extent reported only the wrapper's own 0-
        // px border-box, so the div under-counted by the full row
        // stack.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());

        // Outer div wraps the table.
        var divStyle = MakeStyle();
        var outerDiv = Box.ForElement(BoxKind.BlockContainer, divStyle, MakeElement());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var inner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var blockStyle = MakeStyle();
        SetLengthPx(blockStyle, PropertyId.Height, 150);
        var block = Box.ForElement(BoxKind.BlockContainer, blockStyle, MakeElement());
        inner.AppendChild(block);
        cell.AppendChild(inner);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        outerDiv.AppendChild(table);
        root.AppendChild(outerDiv);

        // Sibling paragraph after the div.
        var siblingStyle = MakeStyle();
        SetLengthPx(siblingStyle, PropertyId.Height, 50);
        var sibling = Box.ForElement(BoxKind.BlockContainer, siblingStyle, MakeElement());
        root.AppendChild(sibling);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // The sibling block must land below the table's content area
        // (BlockOffset >= 150). Pre-Finding-2 the recursive measure
        // reported 0 for the table, so the sibling overlapped at 0.
        BoxFragment? siblingFrag = null;
        foreach (var f in sink.Fragments)
        {
            if (ReferenceEquals(f.Box, sibling)) { siblingFrag = f; break; }
        }
        Assert.NotNull(siblingFrag);
        Assert.True(siblingFrag!.Value.BlockOffset >= 150,
            $"Sibling at BlockOffset={siblingFrag.Value.BlockOffset} must be below 150 (table content extent).");
    }

    [Fact]
    public void Hardening_Finding3_three_phase_emit_groups_rows_then_cells_then_content()
    {
        // Per Finding 3 — for a multi-row table with rowspan, the outer
        // sink's emit order must be: ALL rows, then ALL cells, then ALL
        // cell content. Pre-fix the loop emitted row → cells-at-row →
        // content per row; a rowspan cell's content from row 0 flushed
        // before row 1's background, allowing row 1 to paint over it.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        // 3-row × 2-column table with a rowspan=3 cell at (0,0).
        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // Row 0 — rowspan=3 first cell + plain second cell.
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var spanCell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "3"));
        // Give the spanning cell some content so it has a real
        // content fragment after FlushTo.
        var spanAnon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        spanAnon.AppendChild(Box.TextRun("S", MakeStyle()));
        spanCell.AppendChild(spanAnon);
        var row0Plain = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row0.AppendChild(spanCell);
        row0.AppendChild(row0Plain);
        grid.AppendChild(row0);

        // Row 1 + Row 2 — single second-column cell each.
        var row1 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var row1Cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row1.AppendChild(row1Cell);
        grid.AppendChild(row1);

        var row2 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var row2Cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row2.AppendChild(row2Cell);
        grid.AppendChild(row2);

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Find last-row + first-cell indices.
        var firstRowIdx = -1;
        var lastRowIdx = -1;
        var firstCellIdx = -1;
        var lastCellIdx = -1;
        var firstContentIdx = -1;
        for (var i = 0; i < sink.Fragments.Count; i++)
        {
            var b = sink.Fragments[i].Box;
            if (b.Kind == BoxKind.TableRow)
            {
                if (firstRowIdx < 0) firstRowIdx = i;
                lastRowIdx = i;
            }
            else if (b.Kind == BoxKind.TableCell)
            {
                if (firstCellIdx < 0) firstCellIdx = i;
                lastCellIdx = i;
            }
            else if (ReferenceEquals(b, spanAnon) && firstContentIdx < 0)
            {
                firstContentIdx = i;
            }
        }
        Assert.True(firstRowIdx >= 0 && lastRowIdx >= 0, "no row fragments");
        Assert.True(firstCellIdx >= 0 && lastCellIdx >= 0, "no cell fragments");
        Assert.True(firstContentIdx >= 0, "spanning cell content not emitted");
        // Per Finding 3 — ALL rows precede ALL cells.
        Assert.True(lastRowIdx < firstCellIdx,
            $"Finding 3 violation: row at {lastRowIdx} must precede first cell at {firstCellIdx}.");
        // ALL cells precede ALL content.
        Assert.True(lastCellIdx < firstContentIdx,
            $"Finding 3 violation: last cell at {lastCellIdx} must precede first content at {firstContentIdx}.");
    }

    [Fact]
    public void Hardening_Finding4_colspan_1000_is_bounded()
    {
        // Per Finding 4 — a single-row table with a single colspan=1000
        // cell must complete in bounded time + memory (no 65M hash
        // insertions). We assert via a tight runtime bound (under 1s
        // on any reasonable machine) + that the column count = 1000.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "1000"));
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 1000, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        stopwatch.Stop();

        // Generous 1000ms bound — the algorithm should clear in
        // microseconds on a modern machine.
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"colspan=1000 took {stopwatch.ElapsedMilliseconds}ms — placement should be O(1) per row.");

        // The cell fragment exists with inline-size = 1000 (one row,
        // contentInlineSize=1000, columnCount=1000, columnWidth=1).
        BoxFragment? cellFrag = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCell) { cellFrag = f; break; }
        }
        Assert.NotNull(cellFrag);
        Assert.Equal(1000, cellFrag!.Value.InlineSize);
    }

    [Fact]
    public void Hardening_Finding4_slot_budget_diagnostic_fires_on_oversize_table()
    {
        // Per Finding 4 — two cells with very large spans cross the
        // 1,000,000-slot budget; the second cell is capped at 1×1 +
        // LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001 fires.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());

        // First cell: 500 × 1000 = 500_000 slots (under budget).
        var row0 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var bigCell1 = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "500"));
        bigCell1.SourceElement!.SetAttribute("colspan", "1000");
        row0.AppendChild(bigCell1);
        grid.AppendChild(row0);

        // Second row's cell: another 500 × 1000 = 500_000 → total
        // 1,000,000 exactly. The third cell forces budget exceeded.
        var row1 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var bigCell2 = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "500"));
        bigCell2.SourceElement!.SetAttribute("colspan", "1000");
        row1.AppendChild(bigCell2);
        grid.AppendChild(row1);

        var row2 = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var bigCell3 = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "500"));
        bigCell3.SourceElement!.SetAttribute("colspan", "1000");
        row2.AppendChild(bigCell3);
        grid.AppendChild(row2);

        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 1000, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var budgetDiag = diagSink.Diagnostics.Find(d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableSlotBudgetExceeded001);
        Assert.True(budgetDiag.Code == PaginateDiagnosticCodes.LayoutTableSlotBudgetExceeded001,
            "Expected LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001 diagnostic.");
    }

    [Fact]
    public void Hardening_Finding6_rowspan_zero_clamps_to_one_and_emits_unsupported()
    {
        // Per Finding 6 — rowspan="0" / colspan="0" carry HTML5 §4.9.11
        // "remainder of row-group / column-group" semantics that are
        // currently deferred. The parser clamps to 1 + emits a
        // LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 with the axis info.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("rowspan", "0"));
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var rowspanZeroDiag = diagSink.Diagnostics.Find(d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("rowspan=\"0\""));
        Assert.True(rowspanZeroDiag.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "Expected LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 with rowspan=\"0\" wording.");
    }

    [Fact]
    public void Hardening_Finding6_colspan_zero_clamps_to_one_and_emits_unsupported()
    {
        // Mirror of the rowspan=0 case for colspan=0.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(),
            MakeElementWithAttribute("colspan", "0"));
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        var colspanZeroDiag = diagSink.Diagnostics.Find(d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("colspan=\"0\""));
        Assert.True(colspanZeroDiag.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "Expected LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 with colspan=\"0\" wording.");
    }

    [Fact]
    public void Caption_with_control_chars_renders_without_diagnostic()
    {
        // Sub-cycle 3 — pre-sub-cycle-3 a caption with control chars
        // would surface a sanitized snippet inside a LAYOUT-TABLE-
        // FEATURE-UNSUPPORTED-001 diagnostic. Sub-cycle 3 lays the
        // caption out for real; the original sub-cycle 1 hardening
        // (Finding 7) sanitization no longer applies because the
        // caption text never flows through a diagnostic message.
        //
        // This test guards against a regression where control-char
        // captions would crash or trigger a stale code path; the
        // caption simply emits a fragment + no diagnostic.
        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());
        var caption = Box.ForElement(BoxKind.TableCaption, MakeStyle(), MakeElement());
        // U+0001 (SOH) + U+001B (ESC, the start of an ANSI sequence)
        // embedded in the caption text. Build it via explicit char
        // codes so the source stays portable through editors /
        // formatters.
        var maliciousText = "Re" + (char)0x01 + "po" + (char)0x1B + "rt";
        caption.AppendChild(Box.TextRun(maliciousText, MakeStyle()));
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, diagSink, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // No caption-message-bearing LAYOUT-TABLE-FEATURE-UNSUPPORTED-
        // 001 diagnostic — the layout path doesn't dump caption text
        // into a diagnostic message anymore.
        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));

        // The caption fragment is emitted with the correct geometry.
        var captionFragment = sink.Fragments.Find(f => f.Box.Kind == BoxKind.TableCaption);
        Assert.True(captionFragment.Box?.Kind == BoxKind.TableCaption,
            "Expected a TableCaption fragment for the control-char caption.");
    }

    [Fact]
    public void Hardening_Finding5_buffering_diagnostic_sink_drains_on_flush()
    {
        // Per Finding 5 — direct unit test of the buffering sink:
        // diagnostics emitted into it are NOT visible to the target
        // until FlushTo runs. Test the contract on the sink itself
        // rather than the table-internal wiring (which is exercised
        // indirectly by all the other Finding 5 + 7 tests above).
        var underlying = new RecordingDiagnosticsSink();
        var buffering = new BufferingDiagnosticsSink();

        buffering.Emit(new PaginateDiagnostic(
            PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "buffered-message-1",
            PaginateDiagnosticSeverity.Warning));
        buffering.Emit(new PaginateDiagnostic(
            PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "buffered-message-2",
            PaginateDiagnosticSeverity.Warning));

        // Pre-flush — underlying sees nothing.
        Assert.Empty(underlying.Diagnostics);
        Assert.Equal(2, buffering.Buffered.Count);

        buffering.FlushTo(underlying);
        Assert.Equal(2, underlying.Diagnostics.Count);
        Assert.Equal("buffered-message-1", underlying.Diagnostics[0].Message);
        Assert.Equal("buffered-message-2", underlying.Diagnostics[1].Message);
        // Buffer cleared on flush.
        Assert.Empty(buffering.Buffered);

        // Discard path — emit some, then discard, then verify target
        // sees nothing.
        var discardable = new BufferingDiagnosticsSink();
        discardable.Emit(new PaginateDiagnostic(
            PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "should-not-leak",
            PaginateDiagnosticSeverity.Warning));
        discardable.Discard();
        discardable.FlushTo(underlying);
        Assert.Equal(2, underlying.Diagnostics.Count); // unchanged
    }

    // ====================================================================
    //  Phase 3 Task 12 sub-cycle 3 — table caption layout
    // ====================================================================

    [Fact]
    public void Caption_top_renders_above_rows()
    {
        // CSS Tables L3 §11.5.2 — caption-side: top (default) lays
        // the caption ABOVE the row stack. Verify the caption's
        // BlockOffset < row 0's BlockOffset + caption spans the
        // wrapper's content-inline-size.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        var captionStyle = MakeStyle();
        // Explicit caption-side: top (also the default; this verifies
        // the keyword pipeline carries the value through to the
        // layout-side reader).
        captionStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(0));
        var caption = Box.ForElement(BoxKind.TableCaption, captionStyle, MakeElement());
        var captionInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var captionInnerStyle = MakeStyle();
        SetLengthPx(captionInnerStyle, PropertyId.Height, 25);
        var captionBlock = Box.ForElement(BoxKind.BlockContainer, captionInnerStyle, MakeElement());
        captionInner.AppendChild(captionBlock);
        caption.AppendChild(captionInner);
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var cellInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var cellInnerStyle = MakeStyle();
        SetLengthPx(cellInnerStyle, PropertyId.Height, 40);
        var cellBlock = Box.ForElement(BoxKind.BlockContainer, cellInnerStyle, MakeElement());
        cellInner.AppendChild(cellBlock);
        cell.AppendChild(cellInner);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

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
        // Caption sits above the row.
        Assert.True(captionFragment!.Value.BlockOffset < rowFragment!.Value.BlockOffset,
            $"Top caption (BlockOffset={captionFragment.Value.BlockOffset}) "
            + $"must precede row (BlockOffset={rowFragment.Value.BlockOffset}).");
        // Caption spans the wrapper's content-inline-size (= 600 with
        // zero padding/border on the wrapper in this test).
        Assert.Equal(600, captionFragment.Value.InlineSize);
        Assert.Equal(0, captionFragment.Value.InlineOffset);
        // Row pushed down by the caption height (25).
        Assert.Equal(25, rowFragment.Value.BlockOffset);
    }

    [Fact]
    public void Caption_bottom_renders_below_rows()
    {
        // CSS Tables L3 §11.5.2 — caption-side: bottom lays the
        // caption AFTER the row stack. Verify the caption's
        // BlockOffset >= last row's BlockOffset + BlockSize.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        var captionStyle = MakeStyle();
        // Keyword index 1 == bottom (per KeywordResolver order: top,
        // bottom, block-start, block-end, inline-start, inline-end).
        captionStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(1));
        var caption = Box.ForElement(BoxKind.TableCaption, captionStyle, MakeElement());
        var captionInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var captionInnerStyle = MakeStyle();
        SetLengthPx(captionInnerStyle, PropertyId.Height, 20);
        var captionBlock = Box.ForElement(BoxKind.BlockContainer, captionInnerStyle, MakeElement());
        captionInner.AppendChild(captionBlock);
        caption.AppendChild(captionInner);
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var cellInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var cellInnerStyle = MakeStyle();
        SetLengthPx(cellInnerStyle, PropertyId.Height, 50);
        var cellBlock = Box.ForElement(BoxKind.BlockContainer, cellInnerStyle, MakeElement());
        cellInner.AppendChild(cellBlock);
        cell.AppendChild(cellInner);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        BoxFragment? captionFragment = null;
        BoxFragment? rowFragment = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCaption) captionFragment = f;
            else if (f.Box.Kind == BoxKind.TableRow) rowFragment = f;
        }
        Assert.NotNull(captionFragment);
        Assert.NotNull(rowFragment);
        // Row anchors at top of content area; caption sits AFTER the
        // last row.
        Assert.Equal(0, rowFragment!.Value.BlockOffset);
        var lastRowBottom = rowFragment.Value.BlockOffset + rowFragment.Value.BlockSize;
        Assert.True(captionFragment!.Value.BlockOffset >= lastRowBottom,
            $"Bottom caption (BlockOffset={captionFragment.Value.BlockOffset}) "
            + $"must render AT OR BELOW last row's bottom ({lastRowBottom}).");
    }

    [Fact]
    public void Multiple_top_captions_stack_vertically()
    {
        // CSS Tables L3 §11.5.1 — multiple captions stack in document
        // order. Verify the second caption's BlockOffset >
        // first's BlockOffset + BlockSize.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        // Two top-side captions, each carrying a known-height inner
        // block.
        for (var i = 0; i < 2; i++)
        {
            var captionStyle = MakeStyle();
            captionStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(0)); // top
            var caption = Box.ForElement(BoxKind.TableCaption, captionStyle, MakeElement());
            var inner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
            var innerStyle = MakeStyle();
            SetLengthPx(innerStyle, PropertyId.Height, 15 + (i * 5)); // 15, 20
            var block = Box.ForElement(BoxKind.BlockContainer, innerStyle, MakeElement());
            inner.AppendChild(block);
            caption.AppendChild(inner);
            table.AppendChild(caption);
        }

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var cellInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var cellInnerStyle = MakeStyle();
        SetLengthPx(cellInnerStyle, PropertyId.Height, 30);
        var cellBlock = Box.ForElement(BoxKind.BlockContainer, cellInnerStyle, MakeElement());
        cellInner.AppendChild(cellBlock);
        cell.AppendChild(cellInner);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Collect caption fragments in document order.
        var captionFragments = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.TableCaption)
            {
                captionFragments.Add(f);
            }
        }
        Assert.Equal(2, captionFragments.Count);

        // The first caption's block extent = 15. Second caption
        // starts at first.BlockOffset + first.BlockSize = 0 + 15 = 15.
        Assert.Equal(0, captionFragments[0].BlockOffset);
        Assert.True(captionFragments[1].BlockOffset
                >= captionFragments[0].BlockOffset + captionFragments[0].BlockSize,
            "Second caption must stack after the first.");
        Assert.Equal(15, captionFragments[1].BlockOffset);
    }

    [Fact]
    public void Caption_content_renders_via_nested_BlockLayouter()
    {
        // Sub-cycle 3 — verify the caption's content layouts through
        // the nested BlockLayouter dispatch. The caption carries an
        // AnonymousBlock > TextRun "Hello" which exercises the inline-
        // only-block detection inside the caption layout. The captured
        // inline fragment is buffered + flushed via the caption's
        // MeasuringFragmentSink.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        var captionStyle = MakeStyle();
        captionStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(0));
        var caption = Box.ForElement(BoxKind.TableCaption, captionStyle, MakeElement());
        // AnonymousBlock > TextRun "Hello" — the inline-only-block
        // shape the nested BlockLayouter dispatches.
        var anon = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        anon.AppendChild(Box.TextRun("Hello", MakeStyle()));
        caption.AppendChild(anon);
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Caption fragment is emitted.
        var captionFragment = sink.Fragments.Find(f => f.Box.Kind == BoxKind.TableCaption);
        Assert.True(captionFragment.Box?.Kind == BoxKind.TableCaption,
            "Expected a TableCaption fragment.");

        // The caption's inner AnonymousBlock content fragment is
        // buffered + flushed via FlushTo — the caption isn't a stub.
        // Search the sink for a fragment whose InlineLayout has shaped
        // glyphs.
        var anyShapedInCaption = false;
        foreach (var f in sink.Fragments)
        {
            if (f.InlineLayout is not { Lines.Length: > 0 } inlineLayout) continue;
            foreach (var run in inlineLayout.ShapedRuns)
            {
                if (run.Glyphs.Length > 0) { anyShapedInCaption = true; break; }
            }
            if (anyShapedInCaption) break;
        }
        Assert.True(anyShapedInCaption,
            "Expected at least one shaped glyph in the caption's content (proves "
            + "the nested BlockLayouter ran inside the caption).");
    }

    [Fact]
    public void Caption_block_extent_feeds_into_table_total_height()
    {
        // Sub-cycle 3 — MeasureContentHeight must return a total
        // that includes top + bottom caption heights + row stack.
        // Build a known-sized example: top-caption=10, row=30,
        // bottom-caption=15 ⇒ total = 55.
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        // Top caption.
        var topStyle = MakeStyle();
        topStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(0));
        var topCaption = Box.ForElement(BoxKind.TableCaption, topStyle, MakeElement());
        var topInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var topInnerStyle = MakeStyle();
        SetLengthPx(topInnerStyle, PropertyId.Height, 10);
        topInner.AppendChild(Box.ForElement(BoxKind.BlockContainer, topInnerStyle, MakeElement()));
        topCaption.AppendChild(topInner);
        table.AppendChild(topCaption);

        // Grid + 1 row × 1 cell carrying a 30-tall block.
        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        var cellInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var cellInnerStyle = MakeStyle();
        SetLengthPx(cellInnerStyle, PropertyId.Height, 30);
        cellInner.AppendChild(Box.ForElement(BoxKind.BlockContainer, cellInnerStyle, MakeElement()));
        cell.AppendChild(cellInner);
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);

        // Bottom caption.
        var botStyle = MakeStyle();
        botStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(1));
        var botCaption = Box.ForElement(BoxKind.TableCaption, botStyle, MakeElement());
        var botInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var botInnerStyle = MakeStyle();
        SetLengthPx(botInnerStyle, PropertyId.Height, 15);
        botInner.AppendChild(Box.ForElement(BoxKind.BlockContainer, botInnerStyle, MakeElement()));
        botCaption.AppendChild(botInner);
        table.AppendChild(botCaption);

        root.AppendChild(table);

        // Construct a TableLayouter directly + call MeasureContentHeight.
        var sink = new RecordingFragmentSink();
        using var tableLayouter = new TableLayouter(
            rootBox: table,
            sink: sink,
            incomingContinuation: null,
            diagnostics: null,
            shaperResolver: shaper);
        tableLayouter.ConfigureEmission(
            contentInlineOffset: 0,
            contentBlockOffset: 0,
            contentInlineSize: 600);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        var total = tableLayouter.MeasureContentHeight(ctx, ref layoutCtx);

        // top (10) + row (30) + bottom (15) = 55.
        Assert.Equal(55, total);
    }

    [Fact]
    public void Caption_with_default_no_caption_side_falls_back_to_top()
    {
        // Sub-cycle 3 — when caption-side isn't set at all, the
        // layout reader defaults to Top. Verify the caption lands
        // above the row even without an explicit caption-side keyword.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        // No caption-side property on the caption — fallback to Top.
        var caption = Box.ForElement(BoxKind.TableCaption, MakeStyle(), MakeElement());
        var captionInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var captionInnerStyle = MakeStyle();
        SetLengthPx(captionInnerStyle, PropertyId.Height, 18);
        captionInner.AppendChild(Box.ForElement(BoxKind.BlockContainer, captionInnerStyle, MakeElement()));
        caption.AppendChild(captionInner);
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

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
            "Default caption-side falls back to Top so the caption sits above the row.");
    }

    [Fact]
    public void Caption_block_start_maps_to_top()
    {
        // Sub-cycle 3 — the writing-mode-relative keyword
        // `block-start` maps to physical `top` under LTR horizontal
        // writing mode. Sub-cycle 4+ will route this through the
        // writing-mode resolver; sub-cycle 3 short-circuits to top.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var root = Box.CreateRoot(MakeStyle());
        var table = Box.ForElement(BoxKind.Table, MakeStyle(), MakeElement());

        var captionStyle = MakeStyle();
        // Keyword index 2 == block-start.
        captionStyle.Set(PropertyId.CaptionSide, ComputedSlot.FromKeyword(2));
        var caption = Box.ForElement(BoxKind.TableCaption, captionStyle, MakeElement());
        var captionInner = Box.Anonymous(BoxKind.AnonymousBlock, MakeStyle());
        var captionInnerStyle = MakeStyle();
        SetLengthPx(captionInnerStyle, PropertyId.Height, 12);
        captionInner.AppendChild(Box.ForElement(BoxKind.BlockContainer, captionInnerStyle, MakeElement()));
        caption.AppendChild(captionInner);
        table.AppendChild(caption);

        var grid = Box.Anonymous(BoxKind.TableGrid, MakeStyle());
        var row = Box.ForElement(BoxKind.TableRow, MakeStyle(), MakeElement());
        var cell = Box.ForElement(BoxKind.TableCell, MakeStyle(), MakeElement());
        row.AppendChild(cell);
        grid.AppendChild(row);
        table.AppendChild(grid);
        root.AppendChild(table);

        using var layouter = new BlockLayouter(root, sink, null, null, shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

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
            "block-start under LTR horizontal mode resolves to top.");
    }

    // ====================================================================
    //  Tree builders + test doubles
    // ====================================================================

    /// <summary>Build a Root → Table → TableGrid → rows × cells tree.
    /// Each cell carries an AnonymousBlock wrapping a TextRun with
    /// <paramref name="cellTextRun"/> (so the inner BlockLayouter
    /// dispatches the inline-only-block path).</summary>
    private static (Box root, Box table) BuildTable(
        int rowCount, int columnCount, string cellTextRun,
        Func<ComputedStyle> style)
    {
        var root = Box.CreateRoot(style());
        var table = Box.ForElement(BoxKind.Table, style(), MakeElement());
        var grid = Box.Anonymous(BoxKind.TableGrid, style());
        for (var r = 0; r < rowCount; r++)
        {
            var row = Box.ForElement(BoxKind.TableRow, style(), MakeElement());
            for (var c = 0; c < columnCount; c++)
            {
                var cell = Box.ForElement(BoxKind.TableCell, style(), MakeElement());
                var anon = Box.Anonymous(BoxKind.AnonymousBlock, style());
                anon.AppendChild(Box.TextRun(cellTextRun, style()));
                cell.AppendChild(anon);
                row.AppendChild(cell);
            }
            grid.AppendChild(row);
        }
        table.AppendChild(grid);
        root.AppendChild(table);
        return (root, table);
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, PropertyId id, double px) =>
        style.Set(id, ComputedSlot.FromLengthPx(px));

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }

    private static AngleSharp.Dom.IElement MakeElementWithAttribute(
        string attrName, string attrValue)
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        var element = doc.CreateElement("td");
        element.SetAttribute(attrName, attrValue);
        return element;
    }

    /// <summary>Recording sink mirroring
    /// <see cref="BlockLayouterTests"/>'s.</summary>
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

    /// <summary>Recording diagnostic sink mirroring
    /// <see cref="BlockInlineIntegrationTests"/>'s.</summary>
    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    /// <summary>Synthetic-font shaper resolver mirroring
    /// <see cref="BlockInlineIntegrationTests"/>'s — returns a single
    /// SyntheticFont-backed HbShaper for every style.</summary>
    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }

    /// <summary>Per Finding 3 — counting resolver that wraps a real
    /// <see cref="BreakResolver"/> + records every RegisterCheckpoint
    /// call. Used to assert the outer resolver isn't polluted by
    /// nested cell-layout checkpoints.</summary>
    private sealed class CapturingResolver : IBreakResolver, System.IDisposable
    {
        private readonly BreakResolver _inner = new();
        public int RegisterCount { get; private set; }

        public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
            => _inner.ConsiderBreakAt(opportunity, ctx);

        public OptimizerResult ResolveBreaks(
            System.Collections.Generic.IReadOnlyList<BreakOpportunity> opportunities,
            FragmentainerContext ctx,
            System.Threading.CancellationToken cancellationToken = default)
            => _inner.ResolveBreaks(opportunities, ctx, cancellationToken);

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            RegisterCount++;
            _inner.RegisterCheckpoint(lease);
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _inner.GetLastCheckpoint();

        public void Dispose() => _inner.Dispose();
    }
}
