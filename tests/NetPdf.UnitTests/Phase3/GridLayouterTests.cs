// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
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
/// Phase 3 Task 17 cycle 1 (Hello World) — direct-construction tests
/// for <see cref="GridLayouter"/>. Constructs the grid container
/// (a <see cref="BoxKind.GridContainer"/> Box) directly via test
/// helpers + asserts the layouter's per-item placement math.
///
/// <para>Fixture mirrors <see cref="FlexLayouterTests"/>'s helper
/// shape — <c>RentForExclusiveTesting</c> styles +
/// <see cref="RecordingFragmentSink"/> + the synthetic shaper resolver
/// for parity with other layouter constructors. Cycle 1 doesn't yet
/// route inline content through the shaper.</para>
///
/// <para><b>Cycle 1 (Hello World) behaviors exercised:</b></para>
/// <list type="bullet">
///   <item>2x2 grid with all-explicit placement (4 items at (1,1),
///   (1,2), (2,1), (2,2)) — items emit at expected (offset, size)
///   per cumulative track positions.</item>
///   <item>2x2 grid with all-auto placement (4 items in source order)
///   — sparse auto-placement fills cells row-major.</item>
///   <item>Mixed explicit + auto placement — explicit items placed
///   first, auto items fill remaining cells.</item>
///   <item>Single-row grid (1×N) + single-column grid (N×1) + 1×1
///   degenerate case.</item>
///   <item>Negative line numbers (= count from end per §8.3).</item>
///   <item>Unsupported track kinds (fr / auto / minmax / repeat)
///   contribute 0 px + emit diagnostic.</item>
///   <item>Unsupported placement (span / named-line) degrades to
///   auto-placement + emits diagnostic.</item>
///   <item>Items placed outside explicit grid drop silently +
///   emit diagnostic.</item>
///   <item>Constructor rejects non-grid root BoxKind.</item>
///   <item>Constructor rejects non-null incomingContinuation
///   (= cycle-5 multi-page scope).</item>
///   <item>AttemptLayout without ConfigureEmission throws.</item>
/// </list>
/// </summary>
public sealed class GridLayouterTests
{
    // =====================================================================
    //  Explicit placement (all items explicit row + column)
    // =====================================================================

    [Fact]
    public void Two_by_two_explicit_placement_emits_at_correct_cells()
    {
        // Grid: rows = 100px 200px, columns = 50px 150px.
        // 4 items at (1,1), (1,2), (2,1), (2,2).
        // Expected per-item (InlineOffset, BlockOffset, InlineSize, BlockSize):
        //   (1,1) = (0,   0,   50,  100)
        //   (1,2) = (50,  0,   150, 100)
        //   (2,1) = (0,   100, 50,  200)
        //   (2,2) = (50,  100, 150, 200)
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item11 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item12 = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var item21 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item22 = BuildItemWithExplicitPlacement(row: 2, col: 2);
        grid.AppendChild(item11);
        grid.AppendChild(item12);
        grid.AppendChild(item21);
        grid.AppendChild(item22);

        RunGridLayouter(grid, sink, diag, shaper);

        // Sink should contain exactly 4 fragments (no wrapper here —
        // BlockLayouter emits that; this fixture drives GridLayouter
        // directly so only item fragments appear).
        Assert.Equal(4, sink.Fragments.Count);
        AssertFragmentEquals(sink, item11, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, item12, inlineOffset: 50, blockOffset: 0, inlineSize: 150, blockSize: 100);
        AssertFragmentEquals(sink, item21, inlineOffset: 0, blockOffset: 100, inlineSize: 50, blockSize: 200);
        AssertFragmentEquals(sink, item22, inlineOffset: 50, blockOffset: 100, inlineSize: 150, blockSize: 200);
    }

    [Fact]
    public void Negative_line_number_counts_from_end_per_spec()
    {
        // Per §8.3 — negative line numbers count from the end.
        // -1 → last line → last track (0-based trackCount-1).
        // grid-row-start: -1 in a 2-row grid → row 1 (= track index 1).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: -1, col: -1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // -1 row → track index 1 → blockOffset = 100 (start of row 2).
        // -1 col → track index 1 → inlineOffset = 50 (start of col 2).
        AssertFragmentEquals(sink, item, inlineOffset: 50, blockOffset: 100, inlineSize: 150, blockSize: 200);
    }

    // =====================================================================
    //  Sparse auto-placement (all items auto)
    // =====================================================================

    [Fact]
    public void Two_by_two_sparse_auto_placement_fills_row_major()
    {
        // 4 auto-placed items in a 2×2 grid → fill (1,1), (1,2), (2,1), (2,2)
        // in source order per §8.5 sparse mode.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            items[i] = BuildAutoPlacedItem();
            grid.AppendChild(items[i]);
        }

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(4, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0], inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, items[1], inlineOffset: 50, blockOffset: 0, inlineSize: 150, blockSize: 100);
        AssertFragmentEquals(sink, items[2], inlineOffset: 0, blockOffset: 100, inlineSize: 50, blockSize: 200);
        AssertFragmentEquals(sink, items[3], inlineOffset: 50, blockOffset: 100, inlineSize: 150, blockSize: 200);
    }

    [Fact]
    public void Auto_placement_skips_cells_already_claimed_by_explicit()
    {
        // Items in source order: (auto, auto), (1, 2)-explicit, (auto, auto).
        // Pass 1 places the explicit item at (1, 2). Pass 4 walks the cursor
        // from (1, 1): finds (1, 1) free → places item 1. Cursor → (1, 2)
        // which is occupied. Sparse advances cursor to (1, 2)+1 → (2, 1).
        // (2, 1) is free → places item 3.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var autoA = BuildAutoPlacedItem();
        var explicitB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var autoC = BuildAutoPlacedItem();
        grid.AppendChild(autoA);
        grid.AppendChild(explicitB);
        grid.AppendChild(autoC);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        // explicit at (1,2) → (50, 0, 150, 100)
        AssertFragmentEquals(sink, explicitB, inlineOffset: 50, blockOffset: 0, inlineSize: 150, blockSize: 100);
        // auto A places at (1,1) since explicit is at (1,2)
        AssertFragmentEquals(sink, autoA, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        // auto C: cursor advances past (1,1) → (1,2) occupied → wrap to (2,1)
        AssertFragmentEquals(sink, autoC, inlineOffset: 0, blockOffset: 100, inlineSize: 50, blockSize: 200);
    }

    [Fact]
    public void Row_locked_item_finds_first_free_column_in_declared_row()
    {
        // Item with row=1, col=auto in a 2x3 grid where (1,1) is
        // pre-occupied by an explicit item. The row-locked item should
        // walk row 1 and land at (1, 2).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 60.0, 70.0 });
        var explicitA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var rowLockedB = BuildItemWithRowOnlyPlacement(row: 1);
        grid.AppendChild(explicitA);
        grid.AppendChild(rowLockedB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, explicitA, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        // B walks row 1 from col 1 (occupied) → col 2 (free) → places.
        // colPositions: [0, 50, 110]. col 2 (= index 1) starts at 50.
        AssertFragmentEquals(sink, rowLockedB, inlineOffset: 50, blockOffset: 0, inlineSize: 60, blockSize: 100);
    }

    [Fact]
    public void Column_locked_item_finds_first_free_row_in_declared_column()
    {
        // Symmetric: row=auto, col=2 in a grid where (1,2) is pre-occupied.
        // Walk col 2 starting from row 1 → land at (2, 2).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var explicitA = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var colLockedB = BuildItemWithColOnlyPlacement(col: 2);
        grid.AppendChild(explicitA);
        grid.AppendChild(colLockedB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, explicitA, inlineOffset: 50, blockOffset: 0, inlineSize: 150, blockSize: 100);
        AssertFragmentEquals(sink, colLockedB, inlineOffset: 50, blockOffset: 100, inlineSize: 150, blockSize: 200);
    }

    [Fact]
    public void Single_row_grid_places_items_horizontally()
    {
        // 1×3 grid — all items in row 1, columns 1/2/3.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 30.0, 60.0, 90.0 });
        var items = new[] { BuildAutoPlacedItem(), BuildAutoPlacedItem(), BuildAutoPlacedItem() };
        foreach (var item in items) grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0], inlineOffset: 0, blockOffset: 0, inlineSize: 30, blockSize: 100);
        AssertFragmentEquals(sink, items[1], inlineOffset: 30, blockOffset: 0, inlineSize: 60, blockSize: 100);
        AssertFragmentEquals(sink, items[2], inlineOffset: 90, blockOffset: 0, inlineSize: 90, blockSize: 100);
    }

    [Fact]
    public void Single_column_grid_places_items_vertically()
    {
        // 3×1 grid — all items in column 1, rows 1/2/3.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 30.0, 60.0, 90.0 }, colsPx: new[] { 100.0 });
        var items = new[] { BuildAutoPlacedItem(), BuildAutoPlacedItem(), BuildAutoPlacedItem() };
        foreach (var item in items) grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0], inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 30);
        AssertFragmentEquals(sink, items[1], inlineOffset: 0, blockOffset: 30, inlineSize: 100, blockSize: 60);
        AssertFragmentEquals(sink, items[2], inlineOffset: 0, blockOffset: 90, inlineSize: 100, blockSize: 90);
    }

    [Fact]
    public void Single_cell_grid_with_more_items_drops_overflow()
    {
        // 1×1 grid — 3 items. First lands; remaining 2 overflow + drop
        // with implicit-track diagnostic.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });
        var items = new[] { BuildAutoPlacedItem(), BuildAutoPlacedItem(), BuildAutoPlacedItem() };
        foreach (var item in items) grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, items[0], inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 100);
        // 2 dropped items → 2 implicit-track diagnostics.
        var implicitDiagnostics = diag.Diagnostics.FindAll(d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
        Assert.Equal(2, implicitDiagnostics.Count);
    }

    [Fact]
    public void Empty_grid_template_emits_no_items_and_no_throw()
    {
        // grid-template-rows: none + grid-template-columns: none →
        // 0 tracks → no cells → no item fragments emitted; no throw.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildEmptyGridContainer();
        var item = BuildAutoPlacedItem();
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Empty(sink.Fragments);
    }

    // =====================================================================
    //  Unsupported features → diagnostic + degrade
    // =====================================================================

    [Fact]
    public void Fr_track_kind_emits_diagnostic_and_contributes_zero()
    {
        // cycle 1: only Length tracks are sized. fr → 0 px.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // grid-template-columns: 100px 1fr
        var trackList = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(100)))),
            cols: trackList);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // Col 2 (fr) starts at 100 px (= cumulative sum after the 100px
        // track). Cycle 1 sizes fr as 0 → item is 0 wide.
        AssertFragmentEquals(sink, item, inlineOffset: 100, blockOffset: 0, inlineSize: 0, blockSize: 100);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    [Fact]
    public void Span_placement_falls_back_to_auto_with_diagnostic()
    {
        // Cycle 1: span is not supported. Falls back to auto-placement.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 50.0, 150.0 });
        // Item with grid-row-start: span 2 — cycle 1 ignores span,
        // treats as auto-placement.
        var item = BuildItemWithSpanRowStart(spanCount: 2);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Item_outside_explicit_grid_drops_with_diagnostic()
    {
        // grid-row-start: 5 in a 2-row grid → row index 4 → outside [0,2).
        // Cycle 1 doesn't generate implicit tracks → item drops.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: 5, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Empty(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    // =====================================================================
    //  Constructor + lifecycle guards
    // =====================================================================

    [Fact]
    public void Constructor_rejects_non_grid_box_kind()
    {
        var sink = new RecordingFragmentSink();
        var nonGrid = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        Assert.Throws<System.ArgumentException>(() => new GridLayouter(
            rootBox: nonGrid, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: null));
    }

    [Fact]
    public void Constructor_rejects_non_null_incoming_continuation()
    {
        // Cycle 1 ships atomic emission only — any non-null continuation throws.
        var sink = new RecordingFragmentSink();
        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });
        Assert.Throws<System.ArgumentException>(() => new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: new GridContinuation(RowIndex: 1),
            diagnostics: null, shaperResolver: null));
    }

    [Fact]
    public void AttemptLayout_without_ConfigureEmission_throws()
    {
        var sink = new RecordingFragmentSink();
        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });
        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: null);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        // Can't use Assert.Throws with a lambda because the method takes
        // `ref LayoutContext`; use try/catch.
        System.InvalidOperationException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        }
        catch (System.InvalidOperationException ex) { thrown = ex; }
        Assert.NotNull(thrown);
        Assert.Contains("ConfigureEmission", thrown!.Message);
    }

    [Fact]
    public void ConfigureEmission_with_allowPagination_true_throws_at_AttemptLayout()
    {
        // Cycle 1 doesn't paginate — allowPagination=true is reserved
        // for cycle 5.
        var sink = new RecordingFragmentSink();
        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });
        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: null);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 200, contentBlockSize: 200,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        System.InvalidOperationException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        }
        catch (System.InvalidOperationException ex) { thrown = ex; }
        Assert.NotNull(thrown);
        Assert.Contains("allowPagination", thrown!.Message);
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static void RunGridLayouter(
        Box grid, RecordingFragmentSink sink,
        RecordingDiagnosticsSink diag, SyntheticShaperResolver shaper)
    {
        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 400, contentBlockSize: 400,
            allowPagination: false);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
    }

    private static void AssertFragmentEquals(
        RecordingFragmentSink sink, Box box,
        double inlineOffset, double blockOffset,
        double inlineSize, double blockSize)
    {
        BoxFragment? found = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == box) { found = f; break; }
        }
        Assert.NotNull(found);
        Assert.Equal(inlineOffset, found!.Value.InlineOffset, precision: 3);
        Assert.Equal(blockOffset, found.Value.BlockOffset, precision: 3);
        Assert.Equal(inlineSize, found.Value.InlineSize, precision: 3);
        Assert.Equal(blockSize, found.Value.BlockSize, precision: 3);
    }

    private static Box BuildGridContainer(double[] rowsPx, double[] colsPx)
    {
        var rows = BuildLengthTrackList(rowsPx);
        var cols = BuildLengthTrackList(colsPx);
        return BuildGridContainerWithTemplates(rows, cols);
    }

    private static Box BuildGridContainerWithTemplates(TrackList rows, TrackList cols)
    {
        var style = MakeStyle();
        style.SetSideTablePayload(PropertyId.GridTemplateRows, rows);
        style.Set(PropertyId.GridTemplateRows, ComputedSlot.FromSideTableIndex(0));
        style.SetSideTablePayload(PropertyId.GridTemplateColumns, cols);
        style.Set(PropertyId.GridTemplateColumns, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.GridContainer, style, MakeElement());
    }

    private static Box BuildEmptyGridContainer()
    {
        // No grid-template-rows / -columns set → readers return
        // TrackList.None → 0 tracks.
        var style = MakeStyle();
        return Box.ForElement(BoxKind.GridContainer, style, MakeElement());
    }

    private static TrackList BuildLengthTrackList(double[] sizesPx)
    {
        var items = ImmutableArray.CreateBuilder<TrackListItem>(sizesPx.Length);
        foreach (var size in sizesPx)
        {
            items.Add(new TrackListEntry(TrackEntry.ForLength(size)));
        }
        return new TrackList(items.ToImmutable());
    }

    private static Box BuildItemWithExplicitPlacement(int row, int col)
    {
        var style = MakeStyle();
        var rowValue = GridLineValue.ForLineNumber(row);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var colValue = GridLineValue.ForLineNumber(col);
        style.SetSideTablePayload(PropertyId.GridColumnStart, (object)colValue);
        style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    private static Box BuildItemWithRowOnlyPlacement(int row)
    {
        var style = MakeStyle();
        var rowValue = GridLineValue.ForLineNumber(row);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        // grid-column-start unset → reader returns Auto
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    private static Box BuildItemWithColOnlyPlacement(int col)
    {
        var style = MakeStyle();
        var colValue = GridLineValue.ForLineNumber(col);
        style.SetSideTablePayload(PropertyId.GridColumnStart, (object)colValue);
        style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    private static Box BuildItemWithSpanRowStart(int spanCount)
    {
        var style = MakeStyle();
        var rowValue = GridLineValue.ForSpan(spanCount);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    private static Box BuildAutoPlacedItem()
    {
        // No grid-row-start / grid-column-start set → readers return
        // GridLineValue.Auto.
        return Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }

    /// <summary>Recording sink mirroring
    /// <see cref="FlexLayouterTests"/>'s.</summary>
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

    private sealed class SyntheticShaperResolver : IShaperResolver, System.IDisposable
    {
        private readonly NetPdf.Text.Shaping.HbShaper _shaper =
            new(SyntheticFont.Build(), fontSizePx: 12);
        public NetPdf.Text.Shaping.HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
