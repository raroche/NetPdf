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
/// Phase 3 Task 12 sub-cycle 1 — TableLayouter Hello-World tests.
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
/// <para><b>Sub-cycle 1 deferrals exercised here</b> (each test pins
/// the deferred behavior or its diagnostic):
/// <list type="bullet">
///   <item>colspan / rowspan attributes — silently ignored;
///   <c>LAYOUT-TABLE-FEATURE-UNSUPPORTED-001</c> diagnostic fires.</item>
///   <item>Multi-page splitting within a table — sub-cycle 1 emits
///   all rows on the current page;
///   <c>PAGINATION-FORCED-OVERFLOW-001</c> diagnostic fires when the
///   row stack exceeds the page bottom.</item>
///   <item>Auto / fixed column-width algorithms — sub-cycle 1 uses
///   equal-split; tests assert the equal-split offsets.</item>
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
    public void Table_with_colspan_attribute_emits_feature_unsupported_diagnostic()
    {
        // A cell carries colspan="2"; sub-cycle 1 silently ignores it
        // (treats it as a 1-column cell) + emits the deferred-feature
        // diagnostic. The render still produces row/cell fragments.
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

        // The deferred-feature diagnostic fired.
        var hasFeatureDiag = false;
        foreach (var d in diagSink.Diagnostics)
        {
            if (d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001)
            {
                hasFeatureDiag = true;
                Assert.Equal(PaginateDiagnosticSeverity.Warning, d.Severity);
                break;
            }
        }
        Assert.True(hasFeatureDiag,
            "Expected LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 diagnostic when a "
            + "cell carries a colspan attribute (sub-cycle 1 deferral).");
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
    public void Finding4_caption_emits_diagnostic_with_text_snippet()
    {
        // Per Finding 4 — captions are silently dropped pre-fix; post-
        // fix the layouter emits LAYOUT-TABLE-FEATURE-UNSUPPORTED-001
        // with the caption text in the message.
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

        var captionDiag = diagSink.Diagnostics.Find(d =>
            d.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001
            && d.Message.Contains("caption"));
        Assert.True(captionDiag.Code == PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
            "Expected a LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 diagnostic mentioning 'caption'.");
        Assert.Contains("Annual Report 2026", captionDiag.Message);
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
