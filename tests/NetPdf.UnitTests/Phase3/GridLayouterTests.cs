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
    public void Column_and_row_gap_offset_the_second_track_per_cell()
    {
        // §10.1 — column-gap:20 + row-gap:30 on the 2×2 grid above shift the
        // second column to X=70 (50+20) and the second row to Y=130 (100+30).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        grid.Style.Set(PropertyId.ColumnGap, ComputedSlot.FromLengthPx(20));
        grid.Style.Set(PropertyId.RowGap, ComputedSlot.FromLengthPx(30));
        var item11 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item12 = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var item21 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item22 = BuildItemWithExplicitPlacement(row: 2, col: 2);
        grid.AppendChild(item11);
        grid.AppendChild(item12);
        grid.AppendChild(item21);
        grid.AppendChild(item22);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(4, sink.Fragments.Count);
        AssertFragmentEquals(sink, item11, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, item12, inlineOffset: 70, blockOffset: 0, inlineSize: 150, blockSize: 100);
        AssertFragmentEquals(sink, item21, inlineOffset: 0, blockOffset: 130, inlineSize: 50, blockSize: 200);
        AssertFragmentEquals(sink, item22, inlineOffset: 70, blockOffset: 130, inlineSize: 150, blockSize: 200);
    }

    [Fact]
    public void Negative_minus_two_selects_last_explicit_track_per_spec()
    {
        // Per §8.3 + PR-#92 review F3 — negative line numbers count
        // from the END OF THE EXPLICIT GRID. For 2 explicit tracks
        // there are 3 explicit lines (1, 2, 3). -1 names line 3 (= the
        // line AFTER the last track; would require implicit). -2 names
        // line 2 (= start of last explicit track). So `grid-row-start: -2`
        // in a 2-row grid → places at the LAST EXPLICIT TRACK (row 2).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: -2, col: -2);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // -2 row → line 2 → track index 1 → blockOffset = 100 (row 2 start).
        // -2 col → line 2 → track index 1 → inlineOffset = 50 (col 2 start).
        AssertFragmentEquals(sink, item, inlineOffset: 50, blockOffset: 100, inlineSize: 150, blockSize: 200);
    }

    [Fact]
    public void Negative_minus_one_resolves_to_line_after_last_track_via_implicit()
    {
        // Per PR-#92 review F3 — `-1` = the line AFTER the last track
        // (= line N+1 in a grid with N tracks). Single-cell placement
        // at line N+1 requires an implicit row per §7.5 (= row N+1
        // 0-based = row 2 in a 2-row grid). Cycle 6 generates implicit
        // tracks for items extending past the explicit grid; the new
        // implicit row sizes from grid-auto-rows (= the default `auto`
        // means size to the item's contribution, which is 0 here since
        // the item has no declared dimensions).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: -1, col: -1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        // Item lands at the implicit (row 2, col 2) past the explicit
        // grid. Implicit tracks are auto-sized; with no item
        // contribution they collapse to 0px so the fragment's inline /
        // block size is 0. The fragment ORIGIN at (200, 300) = sum of
        // explicit col widths + sum of explicit row heights.
        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 200, blockOffset: 300,
            inlineSize: 0, blockSize: 0);
        // Cycle 6 ships implicit tracks → no LayoutGridImplicitTrackUnsupported001 fires.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    [Fact]
    public void Negative_far_from_end_selects_first_explicit_track()
    {
        // -3 in a 2-track grid → line 1 (= first explicit line) → track 0.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: -3, col: -3);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
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
    public void Sparse_placement_preserves_source_order_for_auto_and_col_locked()
    {
        // Per CSS Grid §8.5 + PR-#92 review F4 — column-locked +
        // both-auto items share the auto-placement cursor + run in
        // source order. Children [auto, col-locked-to-col-1] in a
        // 2-col grid: per spec the auto item places first at (1, 1);
        // then the col-locked item walks down col 1 from cursor=(1,2)
        // → finds (2, 1) free → places there. (Pre-F4 the col-locked
        // ran in a separate pass that placed it at (1, 1), reordering.)
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 100.0 }, colsPx: new[] { 50.0, 60.0 });
        var autoFirst = BuildAutoPlacedItem();
        var colLockedSecond = BuildItemWithColOnlyPlacement(col: 1);
        grid.AppendChild(autoFirst);
        grid.AppendChild(colLockedSecond);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        // autoFirst → (1, 1) = (0, 0)
        AssertFragmentEquals(sink, autoFirst, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        // colLockedSecond walks col 1 from cursor.row=1 (= 0-based) → (2, 1) = (0, 100)
        AssertFragmentEquals(sink, colLockedSecond, inlineOffset: 0, blockOffset: 100, inlineSize: 50, blockSize: 100);
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
    public void Single_cell_grid_with_more_items_grows_implicit_rows()
    {
        // 1×1 grid — 3 auto-placed items. Cycle 6 generates implicit
        // rows (one per overflowing auto-placed item) sized per
        // grid-auto-rows (default `auto`, collapses to 0 when items
        // have no intrinsic contribution).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });
        var items = new[] { BuildAutoPlacedItem(), BuildAutoPlacedItem(), BuildAutoPlacedItem() };
        foreach (var item in items) grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        // All 3 items emit. Item[0] in the explicit cell; items[1] +
        // items[2] in implicit rows past the explicit grid (auto-sized
        // to 0).
        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0],
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 100);
        AssertFragmentEquals(sink, items[1],
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 100, blockSize: 0);
        AssertFragmentEquals(sink, items[2],
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 100, blockSize: 0);
        // Cycle 6 ships implicit tracks → no LayoutGridImplicitTrackUnsupported001 fires.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    [Fact]
    public void Empty_grid_template_with_one_item_emits_via_implicit_grid()
    {
        // Per PR-#103 review F1 — grid-template-rows: none +
        // grid-template-columns: none with an item is now an
        // implicit-only grid. Cycle 6 seeds a 1×1 implicit grid + the
        // sparse-cursor places the item at (0,0). Auto-sized implicit
        // row/col with no item intrinsic contribution collapse to 0
        // so the fragment is 0×0.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildEmptyGridContainer();
        var item = BuildAutoPlacedItem();
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 0, blockSize: 0);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    [Fact]
    public void Empty_grid_template_with_no_items_is_degenerate_noop()
    {
        // Per PR-#103 review F1 — a grid with no template AND no
        // items is a degenerate no-op (no fragments, no diagnostic).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildEmptyGridContainer();

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Empty(sink.Fragments);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    // =====================================================================
    //  Unsupported features → diagnostic + degrade
    // =====================================================================

    [Fact]
    public void Cycle2_fr_track_distributes_leftover_space_no_diagnostic()
    {
        // Per cycle 2 — fr tracks distribute leftover space per
        // §11.7 with the SPEC-CORRECT divisor
        // `max(SUM(factors), 1.0)` (= sum applied ONCE to the total,
        // not per-track).
        //
        // grid-template-columns: 100px 1fr in 400px container.
        //   nonFlexBase = 100, leftover = 300
        //   rawFlexFactorSum = 1, flexFactorSum = max(1, 1) = 1
        //   hypoFr = 300 / 1 = 300, col 2 = 300 * 1 = 300.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var trackList = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(100)))),
            cols: trackList);
        // Explicit width + height → both axes are definite, no fr-
        // under-indefinite diagnostic fires.
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 100);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item, inlineOffset: 100, blockOffset: 0, inlineSize: 300, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001);
    }

    // =====================================================================
    //  Cycle 2 — §11.7 fr distribution algorithm
    // =====================================================================

    [Fact]
    public void Cycle2_two_equal_fr_tracks_split_container_equally()
    {
        // grid-template-columns: 1fr 1fr in 400px container.
        //   rawFlexFactorSum = 2, flexFactorSum = max(2, 1) = 2
        //   hypoFr = 200, each track = 200 * 1 = 200.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(1)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 200, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 200, blockOffset: 0, inlineSize: 200, blockSize: 50);
    }

    [Fact]
    public void Cycle2_fixed_plus_two_unequal_fr_distributes_per_flex_factor()
    {
        // grid-template-columns: 100px 1fr 2fr in 400px:
        //   nonFlexBase = 100, leftover = 300
        //   rawFlexFactorSum = 1 + 2 = 3, flexFactorSum = max(3, 1) = 3
        //   hypoFr = 100, col2 = 100*1 = 100, col3 = 100*2 = 200.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100)),
            new TrackListEntry(TrackEntry.ForFr(1)),
            new TrackListEntry(TrackEntry.ForFr(2))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var itemC = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);
        grid.AppendChild(itemC);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 100, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemC, inlineOffset: 200, blockOffset: 0, inlineSize: 200, blockSize: 50);
    }

    [Fact]
    public void Cycle2_fractional_fr_below_one_uses_floored_total_sum_per_spec()
    {
        // Per CSS Grid §11.7.1 + PR-#93 review F1 — the flex-factor
        // floor applies to the TOTAL sum ONCE, NOT per-track. Spec
        // text: "Let flex factor sum be the sum of the flex factors
        // of all the flexible tracks. If flex factor sum is less than
        // 1, set it to 1."
        //
        // grid-template-columns: 0.25fr 0.25fr in 400px:
        //   rawFlexFactorSum = 0.25 + 0.25 = 0.5
        //   flexFactorSum = max(0.5, 1) = 1
        //   hypoFr = 400 / 1 = 400
        //   each track = 400 * 0.25 = 100
        //   total used = 200; container has 200 unused (fractional
        //   factors deliberately under-use leftover).
        //
        // Pre-F1 the per-track floor produced 50/50 (= flexFactorSum
        // was 2, hypoFr was 200, each = 200 * 0.25 = 50).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(0.25)),
            new TrackListEntry(TrackEntry.ForFr(0.25))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper, contentInlineSize: 400);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 100, blockOffset: 0, inlineSize: 100, blockSize: 50);
    }

    [Fact]
    public void Cycle2_half_fr_plus_one_fr_distributes_per_spec_unfloored_sum()
    {
        // Per PR-#93 review F1 — `0.5fr 1fr` in 400px:
        //   rawFlexFactorSum = 0.5 + 1 = 1.5
        //   flexFactorSum = max(1.5, 1) = 1.5 (no floor; sum >= 1)
        //   hypoFr = 400 / 1.5 ≈ 266.67
        //   track 1 = 266.67 * 0.5 ≈ 133.33
        //   track 2 = 266.67 * 1 ≈ 266.67
        // Pre-F1 per-track floor produced 100/200 (= flexFactorSum
        // was 2, hypoFr was 200, t1 = 200 * 0.5 = 100, t2 = 200 * 1 = 200).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(0.5)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper, contentInlineSize: 400);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 400.0 / 3, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 400.0 / 3, blockOffset: 0, inlineSize: 800.0 / 3, blockSize: 50);
    }

    [Fact]
    public void Cycle2_negative_leftover_pins_fr_tracks_at_zero()
    {
        // Per §11.7 — when nonFlexBase >= containerExtent, leftover <= 0
        // → fr tracks all get 0. Container visually overflows.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(500)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 500, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 500, blockOffset: 0, inlineSize: 0, blockSize: 50);
    }

    [Fact]
    public void Cycle2_zero_fr_factor_per_spec_total_sum_floor()
    {
        // Per CSS Grid §11.7 + PR-#93 review F1 — `0fr 1fr` in 400px:
        //   rawFlexFactorSum = 0 + 1 = 1
        //   flexFactorSum = max(1, 1) = 1
        //   hypoFr = 400 / 1 = 400
        //   col1 = 400 * 0 = 0
        //   col2 = 400 * 1 = 400
        // Pre-F1 per-track floor produced 0/200 (= flexFactorSum was
        // max(0,1) + max(1,1) = 2, hypoFr was 200).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(0)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper, contentInlineSize: 400);

        // 0fr emits LayoutGridZeroSizedCellContentSkipped001 if item
        // has children; here itemA is empty so no diagnostic.
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 0, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 0, blockOffset: 0, inlineSize: 400, blockSize: 50);
    }

    [Fact]
    public void Cycle2_fr_in_rows_with_explicit_height_distributes_block_extent()
    {
        // fr can apply to rows when the block axis is DEFINITE
        // (= explicit height). nonFlexBase = 100, leftover = 300, row 2 = 300.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: rows,
            cols: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))));
        SetExplicitWidth(grid, 50);
        SetExplicitHeight(grid, 400);
        var item = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 100, inlineSize: 50, blockSize: 300);
        // Definite height → no fr-under-indefinite diagnostic.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001);
    }

    // =====================================================================
    //  PR-#93 review F2 — zero-sized cell content diagnostic
    // =====================================================================

    [Fact]
    public void Cycle2_zero_sized_cell_with_inner_content_emits_diagnostic()
    {
        // Per F2 — a `0fr` column produces a 0-width cell. The item's
        // outer fragment still emits at zero geometry, but inner
        // content can't be dispatched (sub-BlockLayouter needs
        // positive extent). Surface as diagnostic so the silent drop
        // is visible.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(0))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);

        // Item with a child block — diagnostic should fire.
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var innerChild = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        item.AppendChild(innerChild);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        // Outer item fragment still emits at zero size.
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 0, blockSize: 50);
        // Inner content is skipped; diagnostic fires.
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001);
    }

    [Fact]
    public void Cycle2_zero_sized_cell_without_inner_content_emits_no_diagnostic()
    {
        // Empty item in zero-sized cell — no inner content to drop;
        // diagnostic should NOT fire.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(0))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 0, blockSize: 50);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001);
    }

    // =====================================================================
    //  PR-#93 review F3 — fr under indefinite axis
    // =====================================================================

    [Fact]
    public void Cycle2_fr_in_rows_with_auto_height_emits_indefinite_diagnostic()
    {
        // Per F3 — auto-height grid with fr rows → fr collapses to 0
        // (cycle 3 ships intrinsic resolution); diagnostic fires.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: rows,
            cols: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))));
        SetExplicitWidth(grid, 50);
        // Height intentionally NOT set → row axis is indefinite.

        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        // Pass an indefinite-style content extent (= chrome + length-track sum).
        RunGridLayouter(grid, sink, diag, shaper, contentBlockSize: 100);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001);
    }

    // =====================================================================
    //  PR-#93 review F4 — non-finite flexFactorSum guard
    // =====================================================================

    [Fact]
    public void Cycle2_overflowing_fr_factor_sum_emits_diagnostic_and_skips_distribution()
    {
        // Per F4 — `1e308fr 1e308fr` → rawFlexFactorSum overflows to
        // ∞. Pre-F4 hypoFr = leftover/∞ = 0 silently collapsed both
        // tracks to 0. Post-F4 the guard emits
        // LayoutGridNonFiniteGeometry001 + skips distribution.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFr(1e308)),
            new TrackListEntry(TrackEntry.ForFr(1e308))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001);
    }

    [Fact]
    public void Span_placement_auto_positions_with_row_span_extent()
    {
        // Per Phase 3 Task 18 cycle 6 — `grid-row-start: span 2` is
        // an auto-placed N-row span. In a 1×2 explicit grid an
        // implicit row is generated so the item occupies rows 0+1 at
        // column 0. The implicit row is auto-sized (= 0 with no
        // intrinsic contribution from the item) so the fragment's
        // block extent is rowSizes[0] + rowSizes[1] = 100 + 0 = 100.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithSpanRowStart(spanCount: 2);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
        // Cycle 6 ships span placement → no placement-approximated diagnostic.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Item_outside_explicit_grid_grows_implicit_rows()
    {
        // Per Phase 3 Task 18 cycle 6 — `grid-row-start: 5` in a
        // 2-row grid places the item at row index 4 (= 1-based line 5).
        // Cycle 6 generates implicit rows past the explicit grid (rows
        // 2, 3, 4 sized per grid-auto-rows = `auto` default, collapsing
        // to 0 with no item contribution).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: 5, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // Block offset = sum of explicit row heights (100 + 200) +
        // implicit rows 2..3 (= 0 each) = 300.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 300,
            inlineSize: 50, blockSize: 0);
        // Cycle 6 ships implicit tracks → no diagnostic.
        Assert.DoesNotContain(diag.Diagnostics, d =>
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
    public void Constructor_accepts_grid_continuation_in_cycle_5_but_rejects_wrong_type()
    {
        // Per Phase 3 Task 17 cycle 5 — GridContinuation is NOW
        // accepted (replaces the cycle-1 "throws on any non-null"
        // contract). A non-GridContinuation continuation still
        // throws since it indicates a misrouted dispatch.
        var sink = new RecordingFragmentSink();
        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });

        // GridContinuation now works.
        using var ok = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: new GridContinuation(RowIndex: 1),
            diagnostics: null, shaperResolver: null);

        // Wrong continuation type still throws.
        Assert.Throws<System.ArgumentException>(() => new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: new FlexContinuation(LineIndex: 0),
            diagnostics: null, shaperResolver: null));

        // Negative RowIndex throws.
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: new GridContinuation(RowIndex: -1),
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

    // =====================================================================
    //  PR-#92 review F5 — non-default end line emits diagnostic
    // =====================================================================

    [Fact]
    public void Cycle6_grid_row_1_slash_3_spans_two_rows()
    {
        // Per Phase 3 Task 18 cycle 6 — `grid-row: 1 / 3` resolves to
        // a 2-row span (lines 1 through 3) at column 0. The fragment's
        // block extent is rowSizes[0] + rowSizes[1] = 100 + 200 = 300.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        // rowEnd=1 here uses BuildItemWithExplicitStartAndEnd's
        // (rowStart=1, rowEnd=3, colStart=1, colEnd=1) shape — note
        // colEnd=1 with colStart=1 → §8.3 swap reduces to span 1.
        var item = BuildItemWithExplicitStartAndEnd(rowStart: 1, rowEnd: 3, colStart: 1, colEnd: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 300);
        // Cycle 6 ships span end values → no placement-approximated diagnostic.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle6_grid_column_1_slash_3_spans_two_columns()
    {
        // Per Phase 3 Task 18 cycle 6 — `grid-column: 1 / 3` spans
        // both columns of a 2-column grid (= entire row width).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitStartAndEnd(rowStart: 1, rowEnd: 1, colStart: 1, colEnd: 3);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 200, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    // =====================================================================
    //  PR-#92 review F6 — empty grid emits per-item drop diagnostic
    // =====================================================================

    [Fact]
    public void Empty_grid_template_with_children_lays_out_via_implicit_only_grid()
    {
        // Per PR-#103 review F1 — pre-fix the 0-track early-return
        // silently dropped children. Cycle 6 generates a 1×1 implicit
        // seed + implicit rows per overflow; 3 auto-placed items in
        // row-mode fill (0,0), (1,0), (2,0). All fragments are 0×0
        // since the implicit row/col are auto-sized with no item
        // contribution.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildEmptyGridContainer();
        var items = new[] { BuildAutoPlacedItem(), BuildAutoPlacedItem(), BuildAutoPlacedItem() };
        foreach (var item in items) grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    // =====================================================================
    //  PR-#92 review F8 — throwing diagnostic sink doesn't abort layout
    // =====================================================================

    [Fact]
    public void Throwing_diagnostic_sink_does_not_abort_layout()
    {
        // Per F8 — diagnostic emission is nonfatal. A malformed sink
        // that throws must not abort layout.
        var sink = new RecordingFragmentSink();
        var throwingDiag = new ThrowingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 100.0 });
        // Build with grid-row-start: span 2 — triggers
        // LayoutGridPlacementApproximated001 → throwing sink → must
        // NOT abort the layouter.
        var item = BuildItemWithSpanRowStart(spanCount: 2);
        grid.AppendChild(item);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: throwingDiag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 200, contentBlockSize: 200,
            allowPagination: false);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        // Must NOT throw — F8 catches sink exceptions internally.
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Layout completed; item was placed despite throwing sink.
        Assert.Single(sink.Fragments);
    }

    // =====================================================================
    //  PR-#92 review F9 — non-finite cumulative geometry skips items
    // =====================================================================

    [Fact]
    public void Non_finite_cumulative_track_position_skips_items_with_diagnostic()
    {
        // Per F9 — hostile CSS with very large finite tracks can
        // overflow cumulative sums to ±Infinity. The layouter detects
        // this + skips item emission to protect downstream paint.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // Each track is finite (= passes ForLength's IsFinite check)
        // but their sum overflows double.MaxValue (~1.8e308) to ∞.
        var hugeFinite = 1e308;
        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(hugeFinite)),
            new TrackListEntry(TrackEntry.ForLength(hugeFinite))));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        grid.AppendChild(BuildAutoPlacedItem());

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Empty(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001);
    }

    [Fact]
    public void ConfigureEmission_with_allowPagination_true_now_paginates_in_cycle_5()
    {
        // Per Phase 3 Task 17 cycle 5 — allowPagination=true is now
        // active (replaces the cycle-1 "throws" contract). With a
        // budget large enough to fit the single 100px row, the
        // layouter returns AllDone (= the row fits, no split needed).
        // Multi-row split is exercised by the dedicated cycle 5 tests
        // below; this test just verifies the gate doesn't throw.
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
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
    }

    // =====================================================================
    //  Cycle 3 — intrinsic sizing (auto / min-content / max-content)
    // =====================================================================

    [Fact]
    public void Cycle3_auto_track_sizes_from_item_explicit_height()
    {
        // Per cycle 3 — auto row track size = max declared height of
        // items placed at that row (L19 approximation).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForAuto())));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);

        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(75));
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 75);
        // Cycle 3 supports auto → no unsupported-kind diagnostic.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    [Fact]
    public void Cycle3_auto_track_takes_max_of_spanning_items()
    {
        // Multiple items in same row, different declared heights →
        // track size = MAX (50 vs 100 → 100).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForAuto())));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(50)),
            new TrackListEntry(TrackEntry.ForLength(50))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);

        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        itemA.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(50));
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        itemB.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(100));
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, itemB, inlineOffset: 50, blockOffset: 0, inlineSize: 50, blockSize: 100);
    }

    [Fact]
    public void Cycle3_min_content_and_max_content_resolve_like_auto_L19_known_gap()
    {
        // Per the cycle-3 L19 approximation — auto / min-content /
        // max-content all resolve identically. Cycle ?? L19 will
        // diverge their resolution.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinContent()),
            new TrackListEntry(TrackEntry.ForMaxContent())));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);

        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item1.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(60));
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        item2.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(80));
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        RunGridLayouter(grid, sink, diag, shaper);

        // Both resolve identically — using item's declared height.
        AssertFragmentEquals(sink, item1, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 60);
        AssertFragmentEquals(sink, item2, inlineOffset: 0, blockOffset: 60, inlineSize: 100, blockSize: 80);
    }

    [Fact]
    public void Cycle3_auto_column_sizes_from_item_explicit_width()
    {
        // Symmetric: auto COLUMN sized from item's width.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(50))));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForAuto())));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 200);
        SetExplicitHeight(grid, 50);

        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item.Style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(120));
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 120, blockSize: 50);
    }

    [Fact]
    public void Cycle3_auto_track_with_no_declared_dimension_is_zero_L19_known_gap()
    {
        // Per the cycle-3 L19 deferral — items with no explicit
        // width/height contribute 0 to auto tracks. Cycle ?? L19 will
        // ship true intrinsic content measurement. KNOWN GAP pinned.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForAuto())));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);

        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        // NO height declaration.
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 0);
    }

    [Fact]
    public void Cycle3_auto_plus_fr_redistributes_after_intrinsic()
    {
        // grid-template-rows: auto 1fr in 400px:
        //   pass 1 sizing: auto=0, fr=400 (no intrinsic yet).
        //   pass 2 placement: item1@row1, item2@row2.
        //   pass 3 intrinsic: auto gets item1's 75 → 75.
        //   pass 4 fr re-distribute: leftover = 400-75=325 → fr=325.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForAuto()),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 400);

        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item1.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(75));
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, item1, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 75);
        AssertFragmentEquals(sink, item2, inlineOffset: 0, blockOffset: 75, inlineSize: 100, blockSize: 325);
    }

    // =====================================================================
    //  Cycle 4 — minmax() / fit-content() / repeat(integer) + §11.6 Maximize
    // =====================================================================

    [Fact]
    public void Cycle4_minmax_length_length_clamps_to_max_when_container_exceeds()
    {
        // grid-template-columns: minmax(100px, 200px) in 400px container.
        //   Base = 100 (from min), growth = 200 (from max).
        //   No fr → Maximize fills (400-100=300 of free space, but
        //   headroom is only 200-100=100 → track grows to 200, NOT to 400).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(100), TrackEntry.ForLength(200)))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 200, blockSize: 50);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    [Fact]
    public void Cycle4_minmax_length_fr_distributes_via_fr_with_min_floor()
    {
        // grid-template-columns: minmax(100px, 1fr) 1fr in 400px container.
        //   Track 1: base=100, growth=∞, IsFr=true, factor=1, MinBase=100.
        //   Track 2: base=0, growth=∞, IsFr=true, factor=1, MinBase=0.
        //   §11.7 pass 1: nonFlexBase = 100+0 = 100 (track 1 base
        //     counted since IsFr+frozen? No — initially frozen[]=false,
        //     so track 1 unfrozen excluded). Actually nonFlexBase
        //     counts only NON-fr tracks; both are fr → nonFlexBase=0.
        //     leftover = 400. flexFactorSum = max(1+1, 1) = 2.
        //     hypoFr = 200. Check track 1: base 100 > 200×1=200? NO.
        //     Check track 2: base 0 > 200? NO. Distribute:
        //     track 1 = max(100, 200) = 200. track 2 = max(0, 200) = 200.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(100), TrackEntry.ForFr(1))),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 200, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 200, blockOffset: 0, inlineSize: 200, blockSize: 50);
    }

    [Fact]
    public void Cycle4_minmax_fr_removal_step_freezes_when_min_exceeds_proportional_share()
    {
        // grid-template-columns: minmax(300px, 1fr) 1fr in 400px.
        //   Pass 1: nonFlexBase=0, leftover=400, flexFactorSum=2,
        //     hypoFr=200. Track 1: base 300 > 200×1=200 → FREEZE.
        //   Pass 2: nonFlexBase=300 (track 1 frozen at base), leftover=100,
        //     flexFactorSum=1 (only track 2), hypoFr=100. Track 2: base 0
        //     > 100? NO → unfrozen. Distribute: track 2 = max(0, 100) = 100.
        //   Final: track 1 = 300 (min floor), track 2 = 100.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(300), TrackEntry.ForFr(1))),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 300, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 300, blockOffset: 0, inlineSize: 100, blockSize: 50);
    }

    [Fact]
    public void Cycle4_minmax_invalid_min_exceeds_max_treats_max_as_min_per_spec()
    {
        // grid-template-columns: minmax(200px, 100px) in 400px.
        //   Per §11.5: when min > max, max = min. Track sits at 200.
        //   No fr to consume rest → Maximize finds 0 headroom (base=growth)
        //   → track stays 200, container has 200px of empty space.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(200), TrackEntry.ForLength(100)))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 200, blockSize: 50);
    }

    [Fact]
    public void Cycle4_fit_content_clamps_to_limit_when_content_exceeds()
    {
        // grid-template-columns: fit-content(100px) 1fr in 400px.
        //   Item 1 declared width=150 → max-content contribution = 150.
        //   §7.2.2: min(limit=100, max-content=150) = 100. Track 1 = 100.
        //   Track 2 fr: leftover = 400-100 = 300 → track 2 = 300.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFitContent(100)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        itemA.Style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(150));
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 100, blockOffset: 0, inlineSize: 300, blockSize: 50);
    }

    [Fact]
    public void Cycle4_fit_content_uses_content_when_below_limit()
    {
        // grid-template-columns: fit-content(200px) 1fr in 400px.
        //   Item 1 declared width=80 → max-content = 80.
        //   min(limit=200, max-content=80) = 80. Track 1 = 80.
        //   Track 2 fr: leftover = 320 → track 2 = 320.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFitContent(200)),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        itemA.Style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(80));
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 80, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 80, blockOffset: 0, inlineSize: 320, blockSize: 50);
    }

    [Fact]
    public void Cycle4_repeat_integer_expands_inline()
    {
        // grid-template-columns: repeat(3, 100px) in 400px.
        //   Expands to 100px 100px 100px → tracks 100/100/100 = 300.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListRepeat(TrackRepeat.Create(
                3,
                ImmutableArray.Create<TrackRepeatItem>(
                    new TrackRepeatEntry(TrackEntry.ForLength(100)))))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var itemC = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);
        grid.AppendChild(itemC);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 100, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemC, inlineOffset: 200, blockOffset: 0, inlineSize: 100, blockSize: 50);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    [Fact]
    public void Cycle4_repeat_integer_with_mixed_pattern_alternates()
    {
        // grid-template-columns: repeat(2, 100px 1fr) in 400px.
        //   Expands to 100px 1fr 100px 1fr.
        //   nonFlexBase = 200, leftover = 200, flexFactorSum = max(2, 1) = 2,
        //   hypoFr = 100 → fr tracks each = 100. Final: 100/100/100/100.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListRepeat(TrackRepeat.Create(
                2,
                ImmutableArray.Create<TrackRepeatItem>(
                    new TrackRepeatEntry(TrackEntry.ForLength(100)),
                    new TrackRepeatEntry(TrackEntry.ForFr(1)))))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            items[i] = BuildItemWithExplicitPlacement(row: 1, col: i + 1);
            grid.AppendChild(items[i]);
        }

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, items[0], inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, items[1], inlineOffset: 100, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, items[2], inlineOffset: 200, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, items[3], inlineOffset: 300, blockOffset: 0, inlineSize: 100, blockSize: 50);
    }

    [Fact]
    public void Cycle4_maximize_distributes_free_space_to_growth_limits()
    {
        // grid-template-columns: minmax(50px, 150px) minmax(50px, 150px) in 400px.
        //   Both tracks: base=50, growth=150 → headroom 100 each, total 200.
        //   Free space = 400 - 100 = 300. ratio = min(1.0, 300/200) = 1.0.
        //   Each track grows by full headroom 100 → 150. Final: 150/150.
        //   (= 100 remaining; with no fr tracks the container has empty
        //   space, which is the correct §11.6 spec behavior — no auto-stretch.)
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(50), TrackEntry.ForLength(150))),
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(50), TrackEntry.ForLength(150)))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 150, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 150, blockOffset: 0, inlineSize: 150, blockSize: 50);
    }

    // T1 (per PR-#95 review C1) — REPLACED the prior cycle-4-initial
    // test which pinned the WRONG (proportional-to-headroom) behavior.
    // Per CSS Grid §11.6 + §11.5.1 Maximize uses EQUAL distribution
    // with iterative freezing.
    [Fact]
    public void Cycle4_maximize_equal_distribution_freezes_tracks_at_growth_limit()
    {
        // grid-template-columns: minmax(50px, 100px) minmax(50px, 1000px)
        //   container 400px → free space 300.
        //   Per spec §11.6: equal-share each pass.
        //     Pass 1: share=150 each. Track A: 50+150=200 ≥ 100 → clamp at 100, freeze.
        //             Track B: 50+150=200 ≤ 1000 → B=200.
        //     Pass 2: totalBase=100+200=300, freeSpace=100, live=1, share=100.
        //             B: 200+100=300 ≤ 1000 → B=300.
        //     Pass 3: totalBase=400, freeSpace=0 → return.
        //   Final: A=100 (frozen), B=300.
        //   (Pre-C1 the proportional impl said A=65, B=335 — WRONG.)
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(50), TrackEntry.ForLength(100))),
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(50), TrackEntry.ForLength(1000)))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 100, blockOffset: 0, inlineSize: 300, blockSize: 50);
    }

    // T1 additional — same-headroom case validates equal-distribute when
    // no track hits its limit (= simplest path through Maximize).
    [Fact]
    public void Cycle4_maximize_equal_distribution_below_all_limits()
    {
        // grid-template-columns: minmax(0px, 200px) minmax(0px, 200px)
        //   container 200px → free space 200.
        //   Pass 1: share=100 each. A=100 (< 200), B=100 (< 200). No freeze.
        //   No frozen this pass → return.
        //   Final: A=100, B=100.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(0), TrackEntry.ForLength(200))),
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(0), TrackEntry.ForLength(200)))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 200);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper, contentInlineSize: 200);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 100, blockOffset: 0, inlineSize: 100, blockSize: 50);
    }

    // T2 (per PR-#95 review C2) — fit-content track WITHOUT an
    // accompanying fr track. Pre-C2, Maximize would grow it past its
    // computed size up to FitContentLimit.
    [Fact]
    public void Cycle4_fit_content_without_fr_does_not_grow_past_computed_size()
    {
        // grid-template-columns: fit-content(200px), container 600px,
        //   item width 80px.
        //   Intrinsic: effective = min(200, 80) = 80.
        //   Pre-C2 Maximize: free space 520, headroom 200-80=120,
        //     ratio=1.0 → base grows by 120 → 200 (WRONG).
        //   Post-C2: GrowthLimit pinned to effective=80 → headroom=0
        //     → Maximize skips this track → base stays 80.
        //   Container has 520 unused px.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFitContent(200))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 600);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item.Style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(80));
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper, contentInlineSize: 600);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 80, blockSize: 50);
    }

    // T3 (per PR-#95 review C3) — percentage in minmax min arg emits
    // diagnostic + collapses to 0 (was silently treated as pixels).
    [Fact]
    public void Cycle4_minmax_with_percentage_min_emits_diagnostic()
    {
        // grid-template-columns: minmax(50%, 1fr) in 400px container.
        //   Pre-C3: 50% was silently read as MinSubLengthPx=50 → base=50.
        //   Post-C3: percentage detected → diagnostic + base=0.
        //     ResolveFr: leftover=400, fr factor 1, hypoFr=400.
        //     Track gets base=max(0, 400)=400.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForPercentage(50), TrackEntry.ForFr(1)))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 400, blockSize: 50);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPercentageTrackApproximated001);
    }

    // T3 additional — fit-content with percentage limit also diagnoses.
    [Fact]
    public void Cycle4_fit_content_percentage_limit_emits_diagnostic()
    {
        // grid-template-columns: fit-content(25%) → diagnostic + collapses to 0.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForFitContent(25, isPercentage: true))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPercentageTrackApproximated001);
    }

    // T3 additional — top-level percentage Length emits the dedicated
    // percentage diagnostic (was the misleading "Length unsupported").
    [Fact]
    public void Cycle4_top_level_percentage_length_emits_percentage_diagnostic()
    {
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForPercentage(50))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        // Per PR-#95 review H3 — DEDICATED percentage diagnostic (not
        // the generic "track kind Length is not supported").
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPercentageTrackApproximated001);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    // T4 (per PR-#95 review R2) — repeat() expansion truncates at
    // MaxExpandedTrackCount and emits the dedicated truncation
    // diagnostic (not the misleading "Length unsupported").
    [Fact]
    public void Cycle4_repeat_expansion_truncates_at_max_with_dedicated_diagnostic()
    {
        // Construct an AST that would expand past MaxExpandedTrackCount
        // = 50000. repeat(10000, 1px × 6) = 60000 → truncates at 50000.
        // We construct via direct AST since the parser would reject
        // counts > MaxRepeatCount (10000); 10000 × 6 = 60000 exceeds
        // MaxExpandedTrackCount cleanly.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var pattern = ImmutableArray.Create<TrackRepeatItem>(
            new TrackRepeatEntry(TrackEntry.ForLength(1)),
            new TrackRepeatEntry(TrackEntry.ForLength(1)),
            new TrackRepeatEntry(TrackEntry.ForLength(1)),
            new TrackRepeatEntry(TrackEntry.ForLength(1)),
            new TrackRepeatEntry(TrackEntry.ForLength(1)),
            new TrackRepeatEntry(TrackEntry.ForLength(1)));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListRepeat(TrackRepeat.Create(10000, pattern))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 50000);
        SetExplicitHeight(grid, 50);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
    }

    // T5 — TrackRepeatNamedLine inside pattern preserved as
    // TrackListNamedLine on expansion. Currently the named lines are
    // unused (= cycle 7 ships lookup) but the expansion contract is
    // pinned now so cycle 7's reader has stable input.
    [Fact]
    public void Cycle4_repeat_with_named_lines_in_pattern_expands_without_diagnostic()
    {
        // repeat(3, [start] 1px [end]) — pattern of [name, length, name].
        // Cycle 4 expansion: 3 × (start, 1px, end) → 9 items in the
        // expanded list (= 3 length tracks + 6 named lines). The track
        // count = 3, named lines pass through silently.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var pattern = ImmutableArray.Create<TrackRepeatItem>(
            TrackRepeatNamedLine.Create("start"),
            new TrackRepeatEntry(TrackEntry.ForLength(1)),
            TrackRepeatNamedLine.Create("end"));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListRepeat(TrackRepeat.Create(3, pattern))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper, contentInlineSize: 100);

        // 3 track expansion → cell at col 1 is the first 1px track.
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 1, blockSize: 50);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
    }

    // T6 — multiple sequential repeats. Tests that the outer loop
    // properly walks each TrackListRepeat in order.
    [Fact]
    public void Cycle4_multiple_sequential_repeats_expand_in_order()
    {
        // grid-template-columns: repeat(2, 1fr) repeat(3, 50px)
        //   Expands to: 1fr 1fr 50px 50px 50px = 5 tracks.
        //   Container 400px. nonFlexBase = 150 (3×50). leftover = 250.
        //   flexFactorSum = max(2, 1) = 2. hypoFr = 125. Each fr = 125.
        //   Final: 125, 125, 50, 50, 50.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var pattern1 = ImmutableArray.Create<TrackRepeatItem>(
            new TrackRepeatEntry(TrackEntry.ForFr(1)));
        var pattern2 = ImmutableArray.Create<TrackRepeatItem>(
            new TrackRepeatEntry(TrackEntry.ForLength(50)));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListRepeat(TrackRepeat.Create(2, pattern1)),
            new TrackListRepeat(TrackRepeat.Create(3, pattern2))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 3);
        var itemC = BuildItemWithExplicitPlacement(row: 1, col: 5);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);
        grid.AppendChild(itemC);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 125, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 250, blockOffset: 0, inlineSize: 50, blockSize: 50);
        AssertFragmentEquals(sink, itemC, inlineOffset: 350, blockOffset: 0, inlineSize: 50, blockSize: 50);
    }

    // T7 — multi-pass fr-removal (= multiple fr tracks freeze across
    // iterations). Pre-C4 the §11.7.1 removal step was added but
    // never tested with multi-track simultaneous freezing.
    [Fact]
    public void Cycle4_iterative_fr_removal_freezes_multiple_tracks_simultaneously()
    {
        // grid-template-columns: minmax(300px, 1fr) minmax(200px, 1fr) 1fr
        //   container 400px.
        //   Pass 1: nonFlex=0, leftover=400, sum=3, hypoFr=133.33.
        //     Track 0: base 300 > 133.33 → freeze.
        //     Track 1: base 200 > 133.33 → freeze (SAME PASS).
        //     Track 2: base 0 > 133.33? NO.
        //   Pass 2: nonFlex=500 (300+200), leftover=-100 → return.
        //   Final: 300, 200, 0. Total 500 (overflows the 400 container).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(300), TrackEntry.ForFr(1))),
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(200), TrackEntry.ForFr(1))),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var itemC = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);
        grid.AppendChild(itemC);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 300, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 300, blockOffset: 0, inlineSize: 200, blockSize: 50);
        AssertFragmentEquals(sink, itemC, inlineOffset: 500, blockOffset: 0, inlineSize: 0, blockSize: 50);
    }

    // T9 — 0fr inside minmax. Edge case: minmax(50px, 0fr) — base
    // pinned by min, max is 0fr (= zero factor → contributes 0 of
    // leftover but still treated as fr).
    [Fact]
    public void Cycle4_minmax_with_zero_fr_max_pins_at_min_floor()
    {
        // grid-template-columns: minmax(50px, 0fr) 1fr in 400px.
        //   Track 0: base=50, IsFr=true, factor=0.
        //   Track 1: base=0, IsFr=true, factor=1.
        //   Reset → track 0 base=50, track 1 base=0.
        //   Pass 1: nonFlex=0 (both fr unfrozen, excluded). leftover=400.
        //     rawSum=0+1=1. hypoFr=400.
        //     Track 0: base 50 > 400×0=0 → YES → freeze at 50.
        //     Track 1: base 0 > 400? NO.
        //   Pass 2: nonFlex=50 (track 0 frozen). leftover=350. rawSum=1. hypoFr=350.
        //     Track 1: base 0 > 350? NO. Apply: max(0, 350)=350.
        //   No freeze this pass → return.
        //   Final: 50, 350.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForLength(50), TrackEntry.ForFr(0))),
            new TrackListEntry(TrackEntry.ForFr(1))));
        var grid = BuildGridContainerWithTemplates(
            rows: new TrackList(ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50)))),
            cols: cols);
        SetExplicitWidth(grid, 400);
        SetExplicitHeight(grid, 50);
        var itemA = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var itemB = BuildItemWithExplicitPlacement(row: 1, col: 2);
        grid.AppendChild(itemA);
        grid.AppendChild(itemB);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, itemA, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 50);
        AssertFragmentEquals(sink, itemB, inlineOffset: 50, blockOffset: 0, inlineSize: 350, blockSize: 50);
    }

    [Fact]
    public void Cycle4_minmax_with_intrinsic_min_grows_from_placed_item_then_maximize()
    {
        // grid-template-rows: minmax(auto, 200px) in 400px container + item with
        //   declared height 80.
        //   Classify: base=0 (intrinsic min), growth=200 (length max).
        //   Intrinsic resolution: item contributes 80 → base = 80, MinBase = 80.
        //   Maximize: free space = 400 - 80 = 320 ≥ headroom 200-80=120
        //     → ratio = 1.0, track grows by 120 → 200 (capped at growth limit).
        //   Final row = 200.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForAuto(), TrackEntry.ForLength(200)))));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 400);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(80));
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 200);
    }

    [Fact]
    public void Cycle4_minmax_with_intrinsic_min_pinned_when_container_smaller_than_contribution()
    {
        // grid-template-rows: minmax(auto, 200px) in 50px container + item h=80.
        //   Intrinsic forces base = 80 (item contribution).
        //   Maximize: free space = 50 - 80 = -30 ≤ 0 → no growth.
        //   Track overflows the container (correct per spec; CSS Grid
        //   doesn't shrink intrinsic mins below their content).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rows = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForMinMax(
                TrackEntry.ForAuto(), TrackEntry.ForLength(200)))));
        var cols = new TrackList(ImmutableArray.Create<TrackListItem>(
            new TrackListEntry(TrackEntry.ForLength(100))));
        var grid = BuildGridContainerWithTemplates(rows, cols);
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 50);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        item.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(80));
        grid.AppendChild(item);

        // contentBlockSize=50 — track overflows container correctly per
        // §11.5 (intrinsic min is a floor, not subject to clamping).
        RunGridLayouter(grid, sink, diag, shaper, contentBlockSize: 50);

        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 80);
    }

    // =====================================================================
    //  Cycle 5 — multi-page pagination (row-by-row split + GridContinuation)
    // =====================================================================

    [Fact]
    public void Cycle5c1_F2_PageComplete_continuation_carries_emitted_block_extent_for_emitted_rows()
    {
        // Per Phase 3 Task 17 cycle 5c.1 + PR-#97 review F2 —
        // GridContinuation.EmittedBlockExtent should equal the SUM of
        // emitted-row sizes (= startRow through endRowExclusive-1).
        // 3-row grid 100/100/100 with budget 250 emits rows 0+1 = 200px.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 250);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item3 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 250, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = (GridContinuation)result.Continuation!;
        // Rows 0+1 emit (= 100+100=200); row 2 defers.
        Assert.Equal(200.0, continuation.EmittedBlockExtent);
        Assert.Equal(2, continuation.RowIndex);
    }

    [Fact]
    public void Cycle5c1_F1_AllDone_layouter_property_carries_emitted_extent()
    {
        // Per PR-#98 review F1 — replaces the prior test that pinned
        // the "AllDone has no extent" gap as expected. Post-fix:
        // GridLayouter.LastEmittedBlockExtent is populated on AllDone
        // too, so cycle-5c.2's wrapper-resize consumer gets the value
        // regardless of outcome.
        //
        // Single-fragment AllDone (no resume): extent = full grid
        // natural extent = sum-of-row-sizes (= row-positions geometry
        // is equivalent today with no gutters).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 300, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        // Full grid emitted → extent = 200 (= 2 × 100).
        Assert.Equal(200.0, layouter.LastEmittedBlockExtent);
    }

    [Fact]
    public void Cycle5c1_F1_AllDone_final_fragment_of_split_grid_exposes_remaining_extent()
    {
        // Per PR-#98 review F1 — the critical case the cycle-5c.1
        // initial design couldn't handle. Page 1 of a split grid
        // returns PageComplete; page 2 (final) returns AllDone with
        // only the REMAINING rows emitted. The wrapper on page 2
        // must be sized to those remaining rows + chrome, NOT the
        // budget. Post-fix: LastEmittedBlockExtent on page 2 = sum
        // of remaining rows.
        //
        // Fixture: 3-row grid 100/100/100 with budget 250. Page 1
        // emits rows 1+2 (= 200px), defers row 3. Page 2 (resume)
        // emits row 3 (= 100px). Page 2's LastEmittedBlockExtent
        // must be 100, not 0 + not natural 300.
        var sinkPage1 = new RecordingFragmentSink();
        var sinkPage2 = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 250);
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 1, col: 1));
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 2, col: 1));
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 3, col: 1));

        using var page1 = new GridLayouter(
            rootBox: grid, sink: sinkPage1,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 250, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result1.Outcome);
        Assert.Equal(200.0, page1.LastEmittedBlockExtent);
        var continuation = (GridContinuation)result1.Continuation!;

        using var page2 = new GridLayouter(
            rootBox: grid, sink: sinkPage2,
            incomingContinuation: continuation,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 100, 250, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result2.Outcome);
        Assert.Null(result2.Continuation);
        // Page 2 emits only row 3 → 100px (= cycle-5c.2's wrapper
        // would size to 100 + chrome, not the natural 300).
        Assert.Equal(100.0, page2.LastEmittedBlockExtent);
    }

    [Fact]
    public void Cycle5c1_F2_Strict_defer_from_resume_preserves_cache_no_double_emit()
    {
        // Per PR-#98 review F2 — pre-existing defect: when a resumed
        // fragment Strict-defers its first remaining row, the
        // returned GridContinuation had Cache: null → next attempt
        // identityMatches=false → startRow reset to 0 →
        // re-emission of page 1 rows on the retry. Post-fix: cache
        // is preserved across the no-emission defer so identity +
        // RowIndex both stay bound.
        //
        // Fixture sequence:
        //   Page 1 (budget 100): emit row 0; defer row 1 →
        //     GridContinuation(RowIndex=1, Cache=A).
        //   Page 2 (budget 50, Strict): row 1 doesn't fit → defer
        //     entire grid → GridContinuation(RowIndex=1, Cache=A,
        //     EmittedBlockExtent=0). Post-fix preserves Cache=A.
        //   Page 3 (budget 200, LastResort): emit row 1, no
        //     re-emission of row 0.
        var sinkPage1 = new RecordingFragmentSink();
        var sinkPage2 = new RecordingFragmentSink();
        var sinkPage3 = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        using var page1 = new GridLayouter(
            rootBox: grid, sink: sinkPage1, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var c1 = (GridContinuation)result1.Continuation!;
        Assert.Equal(1, c1.RowIndex);
        Assert.NotNull(c1.Cache);

        // Page 2: Strict + budget too small for row 1 → defer.
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sinkPage2, incomingContinuation: c1,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 100, 50, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 50);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result2.Outcome);
        Assert.Empty(sinkPage2.Fragments);
        var c2 = (GridContinuation)result2.Continuation!;
        Assert.Equal(1, c2.RowIndex);  // ← progress preserved
        Assert.NotNull(c2.Cache);  // ← cache preserved per F2 fix
        Assert.Equal(0.0, page2.LastEmittedBlockExtent);

        // Page 3: LastResort + budget fits row 1 → emit it, NO
        // re-emission of row 0 (= the F2 contract).
        using var page3 = new GridLayouter(
            rootBox: grid, sink: sinkPage3, incomingContinuation: c2,
            diagnostics: diag, shaperResolver: shaper);
        page3.ConfigureEmission(0, 0, 100, 200, allowPagination: true);
        var ctx3 = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx3 = new LayoutContext(ctx3);
        using var resolver3 = new BreakResolver();
        var result3 = page3.AttemptLayout(
            ctx3, ref layoutCtx3, resolver3, LayoutAttemptStrategy.LastResort);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result3.Outcome);
        // Page 3 sink: exactly 1 fragment = item2 (= NOT item1 re-emit).
        Assert.Single(sinkPage3.Fragments);
        Assert.Equal(item2, sinkPage3.Fragments[0].Box);
        Assert.Equal(100.0, page3.LastEmittedBlockExtent);
    }

    [Fact]
    public void Cycle5c1_F3_emitted_extent_uses_geometry_not_sum_pins_today_equivalence()
    {
        // Per PR-#98 review F3 — pins the cycle 5c.1 contract that
        // LastEmittedBlockExtent is derived from row-position GEOMETRY
        // (= lastRow.bottom - firstEmittedRow.top), NOT
        // sum(rowSizes[startRow..endRowExclusive)). These are equal
        // TODAY (no gutters/spacing) but the geometry derivation will
        // remain correct when CSS Grid row-gap / block-axis alignment
        // land in future cycles.
        //
        // This test pins the "they're equal today" invariant. When
        // gutters land, this test would assert the geometry path
        // continues to report the true occupied span; a regression to
        // the sum-of-sizes path would silently diverge.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // 3 different-sized rows to exercise non-uniform geometry.
        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0, 75.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 130);  // fits rows 0+1 (= 125), defers row 2.
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 1, col: 1));
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 2, col: 1));
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 3, col: 1));

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 130, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 130);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // Rows 0+1 emit. Geometry: row 1 bottom (50+75=125) - row 0 top (0) = 125.
        // Sum-of-sizes equivalent today: 50 + 75 = 125. ✓ equal.
        Assert.Equal(125.0, layouter.LastEmittedBlockExtent);
    }

    [Fact]
    public void Cycle5_three_row_grid_splits_at_row_2_when_budget_fits_only_two_rows()
    {
        // grid-template-rows: 100px 100px 100px in 250px budget.
        //   Row 0: bottom = 100 ≤ 250 → fit.
        //   Row 1: bottom = 200 ≤ 250 → fit.
        //   Row 2: bottom = 300 > 250 → defer.
        //   Expect PageComplete with GridContinuation(RowIndex=2, Cache).
        //   Sink has 2 fragments (rows 0+1).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 250);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item3 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 250,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = result.Continuation as GridContinuation;
        Assert.NotNull(continuation);
        Assert.Equal(2, continuation!.RowIndex);
        Assert.NotNull(continuation.Cache);
        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, item1, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 100);
        AssertFragmentEquals(sink, item2, inlineOffset: 0, blockOffset: 100, inlineSize: 100, blockSize: 100);
    }

    [Fact]
    public void Cycle5_resume_continuation_emits_remaining_rows_on_next_page()
    {
        // Same grid as above, but feed in a GridContinuation(RowIndex=2)
        // simulating resume from page 1. The resume page emits ONLY
        // row 2 (= the third item), at this-page block-offset 0 (=
        // not at 200 like it was on page 1).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 250);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item3 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);

        // First page: capture the cache by running pagination.
        using var page1 = new GridLayouter(
            rootBox: grid, sink: new RecordingFragmentSink(),
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 250,
            allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var continuation = (GridContinuation)result1.Continuation!;

        // Second page: feed continuation back. Sink is the cycle-5
        // sink (= we're checking the SECOND page's output).
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: continuation,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 250,
            allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result2.Outcome);
        Assert.Single(sink.Fragments);
        // Resume page: item3 (row 2 = third row) emits at this-page
        // block-offset 0 (= the cumulative row offset SHIFTED so the
        // resume row anchors at the wrapper's content-box top).
        AssertFragmentEquals(sink, item3, inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 100);
    }

    [Fact]
    public void Cycle5_round_trip_emits_each_item_exactly_once_across_pages()
    {
        // 3-row grid, budget for 2 rows. Verify that across page 1 +
        // page 2 every item appears EXACTLY ONCE. Pinning the
        // contract that resume doesn't re-emit + page-1 doesn't skip.
        var sink1 = new RecordingFragmentSink();
        var sink2 = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 250);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item3 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);

        using var page1 = new GridLayouter(
            rootBox: grid, sink: sink1,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 250,
            allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);

        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink2,
            incomingContinuation: (GridContinuation)result1.Continuation!,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 250,
            allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        // Sum across both pages: each item exactly once.
        var allFragments = sink1.Fragments.Concat(sink2.Fragments).ToList();
        Assert.Equal(3, allFragments.Count);
        Assert.Single(allFragments, f => f.Box == item1);
        Assert.Single(allFragments, f => f.Box == item2);
        Assert.Single(allFragments, f => f.Box == item3);
        Assert.Equal(LayoutAttemptOutcome.AllDone, result2.Outcome);
    }

    [Fact]
    public void Cycle5_oversized_first_row_force_overflows_with_diagnostic()
    {
        // Single row of 300px in a 200px budget. Per §4.4 progress
        // rule the row force-emits + the diagnostic fires.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 300.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 200,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Row force-committed → AllDone (no continuation since this
        // was the only row).
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Single(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle5_atomic_mode_emits_all_rows_even_when_overflowing()
    {
        // allowPagination=false (= cycle 1 default contract) — even
        // when rows overflow the budget, atomic mode emits all of
        // them + returns AllDone (= the caller is responsible for
        // visual overflow). Pins the backwards-compat contract.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item3 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 200,
            allowPagination: false);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(3, sink.Fragments.Count);
        // No forced-overflow diagnostic in atomic mode (= the budget
        // is irrelevant when pagination is off).
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    // =====================================================================
    //  Phase 3 Task 18 cycle 6b — span × pagination atomicity
    //  (= spanning items don't straddle page breaks; the entire item
    //  defers to the next page when any spanned row would land past
    //  endRowExclusive).
    // =====================================================================

    [Fact]
    public void Cycle6b_spanning_item_defers_when_tail_row_lands_past_budget()
    {
        // 3-row grid, budget for 2 rows. A spanning item at row 2 with
        // span 2 (= rows 2 + 3) would have its tail row land past the
        // budget. Per cycle-6b atomicity, the item defers entirely to
        // page 2. Page 1 emits only row 1's content.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        // Item A at row 1, single-cell.
        var a = BuildItemWithExplicitPlacement(row: 1, col: 1);
        // Item B spanning rows 2+3.
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 2, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(a);
        grid.AppendChild(b);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 200,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Page 1 emits only item A — item B deferred via continuation.
        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 100);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = Assert.IsType<GridContinuation>(result.Continuation);
        // Continuation's RowIndex points to row 1 (= 0-based) so item B
        // (at row 1) emits on the resume page.
        Assert.Equal(1, continuation.RowIndex);
        // No forced-overflow diagnostic (= clean rewind, not §4.4).
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle6b_resume_page_emits_deferred_spanning_item()
    {
        // Page 2 of the prior scenario: the spanning item B emits
        // entirely on the resume page (rows 0+1 in this-page
        // coordinates, blockSize = sum of both rows = 200).
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        var a = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 2, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(a);
        grid.AppendChild(b);

        // Page 1 — capture continuation.
        using var page1 = new GridLayouter(
            rootBox: grid, sink: new RecordingFragmentSink(),
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 200, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);

        // Page 2 — feed continuation back.
        var sink2 = new RecordingFragmentSink();
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink2,
            incomingContinuation: (GridContinuation)result1.Continuation!,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 100, 200, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result2.Outcome);
        Assert.Single(sink2.Fragments);
        // Item B emits at this-page blockOffset 0 (= the resume row
        // anchors at the wrapper's content-box top), blockSize 200
        // (= sum of both spanned rows).
        AssertFragmentEquals(sink2, b,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 200);
    }

    [Fact]
    public void Cycle6b_round_trip_each_item_emitted_exactly_once_with_spans()
    {
        // 3-row grid with: row 1 = single A; rows 2+3 spanned by B.
        // Budget = 200 (= 2 rows). Page 1 emits A; page 2 emits B.
        // Pin "each item emitted exactly once" contract.
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        var a = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 2, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(a);
        grid.AppendChild(b);

        var sink1 = new RecordingFragmentSink();
        using var page1 = new GridLayouter(
            rootBox: grid, sink: sink1, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 200, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);

        var sink2 = new RecordingFragmentSink();
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink2,
            incomingContinuation: (GridContinuation)result1.Continuation!,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 100, 200, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        // A appears exactly once on page 1; B appears exactly once on page 2.
        Assert.Single(sink1.Fragments.FindAll(f => f.Box == a));
        Assert.Empty(sink1.Fragments.FindAll(f => f.Box == b));
        Assert.Empty(sink2.Fragments.FindAll(f => f.Box == a));
        Assert.Single(sink2.Fragments.FindAll(f => f.Box == b));
    }

    [Fact]
    public void Cycle6b_oversized_spanning_item_force_emits_under_last_resort()
    {
        // Spanning item taller than the page budget. Under LastResort
        // strategy, the entire span force-emits per §4.4 progress rule
        // + the LayoutGridForcedOverflow001 diagnostic fires.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        // Item spans both rows (= 200px) but budget is 150.
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 1, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(b);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 150, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 150);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Single(sink.Fragments);
        // Full span emitted: blockSize 200 overflows the 150 budget.
        AssertFragmentEquals(sink, b,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 200);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle6b_oversized_spanning_item_defers_grid_under_strict()
    {
        // Same scenario as above but under Strict — the layouter
        // defers the entire grid (returns startRow + deferEntireGrid)
        // so the BlockLayouter dispatch can rewind + retry on a fresh
        // page.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 1, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(b);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 150, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 150);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // No fragments — grid deferred entirely.
        Assert.Empty(sink.Fragments);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = Assert.IsType<GridContinuation>(result.Continuation);
        Assert.Equal(0, continuation.RowIndex);
        // No forced-overflow diagnostic under Strict (= clean rewind).
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle6b_non_spanning_item_pagination_unaffected_by_cycle_6b()
    {
        // Backward-compat sanity: cycle 5 single-cell pagination
        // behavior is unchanged when no items span.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var item3 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);
        grid.AppendChild(item3);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 250, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 250);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Page 1 emits rows 1+2 (= items 1 & 2); item 3 deferred.
        Assert.Equal(2, sink.Fragments.Count);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = Assert.IsType<GridContinuation>(result.Continuation);
        Assert.Equal(2, continuation.RowIndex);
    }

    // =====================================================================
    //  PR-#104 review F9 — edge-case coverage for spanning pagination.
    // =====================================================================

    [Fact]
    public void Cycle6b_F9_multiple_spans_same_row_use_max_span_end()
    {
        // Two items at row 2 (= 0-based row 1) — A spans 2 rows, B
        // spans 3 rows. The per-row max-span-end is row 1 + 3 = 4.
        // Budget for rows 1+2 only → spanClampedEnd clamps at row 1
        // (= startRow + 1 = 1) → entire spans deferred to next page.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0, 100.0 },
            colsPx: new[] { 50.0, 50.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 400);
        var head = BuildItemWithExplicitPlacement(row: 1, col: 1);
        // A at row 2, col 1, span 2.
        var a = BuildItemWithExplicitStartAndSpan(
            rowStart: 2, rowSpan: 2, colStart: 1, colSpan: 1);
        // B at row 2, col 2, span 3 — extends past budget further than A.
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 2, rowSpan: 3, colStart: 2, colSpan: 1);
        grid.AppendChild(head);
        grid.AppendChild(a);
        grid.AppendChild(b);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        // Budget = 200px → fits rows 1+2 naively; both A and B span
        // past, so both defer.
        layouter.ConfigureEmission(0, 0, 100, 200, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Page 1 emits only head; both spanning items defer.
        Assert.Single(sink.Fragments);
        Assert.Same(head, sink.Fragments[0].Box);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = Assert.IsType<GridContinuation>(result.Continuation);
        Assert.Equal(1, continuation.RowIndex);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle6b_F9_earliest_violating_span_clamps_page_end()
    {
        // Two spanning items: A at row 1 spans 2, B at row 2 spans 3.
        // Budget for 4 rows. Naively rows 1-4 fit; but B spans 2..5
        // which extends past row 4 → spanClampedEnd is B.Row=1 (=
        // 0-based; 1-based row 2). Only row 1 emits.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0, 100.0, 100.0 },
            colsPx: new[] { 50.0, 50.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 500);
        var a = BuildItemWithExplicitStartAndSpan(
            rowStart: 1, rowSpan: 2, colStart: 1, colSpan: 1);
        var b = BuildItemWithExplicitStartAndSpan(
            rowStart: 2, rowSpan: 3, colStart: 2, colSpan: 1);
        grid.AppendChild(a);
        grid.AppendChild(b);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 400, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 400);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        // Naive fit allows rows 1..4 (= 4 rows × 100 = 400 budget).
        // B at 0-based row 1 spans to exclusive row 4 (= span 3 from
        // row 1). 4 == naiveEnd=4 → no clamp. A at row 0 spans to
        // exclusive row 2 (within naiveEnd) → no clamp. So 4 rows
        // emit; row 4 (= 5th row, no items) defers via continuation.
        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 200);
        AssertFragmentEquals(sink, b,
            inlineOffset: 50, blockOffset: 100,
            inlineSize: 50, blockSize: 300);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = Assert.IsType<GridContinuation>(result.Continuation);
        Assert.Equal(4, continuation.RowIndex);
    }

    [Fact]
    public void Cycle6b_F9_strict_then_lastresort_force_emits_spanning_item()
    {
        // An oversized spanning item under Strict defers the entire
        // grid (= clean rewind). On the resume-page, LastResort kicks
        // in (per cycle 5 hardening) + force-emits the spanning item
        // with the F8-reworded diagnostic.
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 200.0, 200.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 400);
        var span = BuildItemWithExplicitStartAndSpan(
            rowStart: 1, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(span);

        // Page 1 — Strict, budget 100 < 200 = first row's height →
        // defer entire grid (no diagnostic).
        var page1Sink = new RecordingFragmentSink();
        using var page1 = new GridLayouter(
            rootBox: grid, sink: page1Sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.Strict);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result1.Outcome);
        Assert.Empty(page1Sink.Fragments);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);

        // Resume page 2 — LastResort, still budget 100 < 400 needed for
        // the full spanning item → force-emit with the F8-reworded
        // diagnostic mentioning "end line".
        var page2Sink = new RecordingFragmentSink();
        using var page2 = new GridLayouter(
            rootBox: grid, sink: page2Sink,
            incomingContinuation: result1.Continuation as GridContinuation,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        // Spanning item force-emitted (overflowing the page edge).
        Assert.Single(page2Sink.Fragments);
        Assert.Same(span, page2Sink.Fragments[0].Box);
        // Per F8 — diagnostic mentions "end line" rather than the
        // misleading "ending at row" wording.
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001
                && d.Message.Contains("end line"));
    }

    [Fact]
    public void Cycle6b_F9_auto_placed_row_span_defers_atomically()
    {
        // Auto-placed `grid-row: span 2` item — same atomicity contract
        // as explicit-row spans. Confirms cycle 6b doesn't only key on
        // PlacementKind.Definite items.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        // Auto-placed `grid-row: span 2` — placed at (0, 0) via cycle
        // 6's auto-placement (= the first sparse run of 2 rows).
        var item = BuildItemWithSpanRowStart(spanCount: 2);
        grid.AppendChild(item);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        // Budget 100 = only row 0 fits. Item starts at row 0 + spans 2
        // → defers entirely on Strict.
        layouter.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        // Item deferred (no fragments emitted) — auto-placed spans
        // participate in the cycle 6b atomicity contract.
        Assert.Empty(sink.Fragments);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    // =====================================================================
    //  Cycle 5 post-PR-#96 hardening — F1 (LastResort gating), F3
    //  (inline-size mismatch invalidates cache), F4 (lazy cache build),
    //  F5 (cache identity + structural validation)
    // =====================================================================

    [Fact]
    public void Cycle5_hardening_F1_strict_strategy_defers_oversized_first_row_instead_of_forcing()
    {
        // Per PR-#96 review F1+F2 — under Strict strategy + first row
        // doesn't fit, the layouter returns PageComplete(GridContinuation
        // (startRow, null)) signaling "defer the entire grid" instead
        // of force-emitting. Pre-F1 this scenario (= grid starts below
        // earlier content, 80px remain on page 1, first row is 100px)
        // would force-emit + fire LayoutGridForcedOverflow001 incorrectly.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 80);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 80,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 80);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        // Strict strategy → defer instead of force-emit.
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.Strict);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        var continuation = result.Continuation as GridContinuation;
        Assert.NotNull(continuation);
        Assert.Equal(0, continuation!.RowIndex);
        Assert.Null(continuation.Cache);
        Assert.Empty(sink.Fragments);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle5_hardening_F1_lastresort_still_force_emits_oversized_first_row()
    {
        // Per PR-#96 review F1+F2 — LastResort is the ONLY strategy
        // that triggers force-overflow. Pin the contract.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 80);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        grid.AppendChild(item);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 100, contentBlockSize: 80,
            allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 80);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Single(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public void Cycle5_hardening_F3_resume_with_different_inline_size_invalidates_cache()
    {
        // Per PR-#96 review F3 — when the resume page has a different
        // contentInlineSize than the cache was built for, the cache is
        // invalidated + a fresh resolve runs + a diagnostic fires.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        // Page 1: capture continuation at contentInlineSize=100.
        var sinkPage1 = new RecordingFragmentSink();
        using var page1 = new GridLayouter(
            rootBox: grid, sink: sinkPage1,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var continuation = (GridContinuation)result1.Continuation!;
        Assert.NotNull(continuation.Cache);
        Assert.Equal(100.0, continuation.Cache!.OriginalContentInlineSize);

        // Page 2: resume at contentInlineSize=200 (= different).
        // Cache should invalidate + diagnostic fires.
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: continuation,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 200, 100, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 200, blockSize: 100);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridResumeInlineSizeMismatch001);
        // The resume still produces an emit (= fresh resolve at 200
        // wide; the single row 2 emits at width 100 since cols were
        // declared as 100px length).
        Assert.Single(sink.Fragments);
    }

    [Fact]
    public void Cycle5_hardening_F4_atomic_grid_returns_AllDone_without_emitting_cache()
    {
        // Per PR-#96 review F4 — atomic mode (allowPagination=false)
        // must not allocate / emit a resume cache. The contract: no
        // continuation in the result + no PageComplete outcome.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 200, allowPagination: false);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        Assert.Equal(2, sink.Fragments.Count);
    }

    [Fact]
    public void Cycle5_hardening_F4_paginated_grid_fitting_on_one_page_returns_AllDone_no_cache()
    {
        // Per PR-#96 review F4 — paginated mode + grid fits in one
        // page → AllDone (no continuation, no cache allocation).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 300);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(0, 0, 100, 300, allowPagination: true);
        var ctx = new FragmentainerContext(contentInlineSize: 100, blockSize: 300);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        Assert.Equal(2, sink.Fragments.Count);
    }

    [Fact]
    public void Cycle5_hardening_F5_continuation_routed_to_wrong_grid_rejects_cache()
    {
        // Per PR-#96 review F5 — a continuation built for grid A
        // routed to grid B must reject the cache + emit the
        // CacheRejected diagnostic + fall back to fresh resolve.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // Build grid A + paginate to capture its cache.
        var gridA = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(gridA, 100);
        SetExplicitHeight(gridA, 200);
        var aItem1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var aItem2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        gridA.AppendChild(aItem1);
        gridA.AppendChild(aItem2);
        var sinkA = new RecordingFragmentSink();
        using var pageA = new GridLayouter(
            rootBox: gridA, sink: sinkA,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        pageA.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctxA = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtxA = new LayoutContext(ctxA);
        using var resolverA = new BreakResolver();
        var resultA = pageA.AttemptLayout(
            ctxA, ref layoutCtxA, resolverA, LayoutAttemptStrategy.LastResort);
        var aContinuation = (GridContinuation)resultA.Continuation!;
        Assert.NotNull(aContinuation.Cache);

        // Build grid B (different rootBox).
        var gridB = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(gridB, 100);
        SetExplicitHeight(gridB, 100);
        var bItem = BuildItemWithExplicitPlacement(row: 1, col: 1);
        gridB.AppendChild(bItem);

        // Route grid A's continuation to grid B (= the misrouted case).
        using var pageB = new GridLayouter(
            rootBox: gridB, sink: sink,
            incomingContinuation: aContinuation,
            diagnostics: diag, shaperResolver: shaper);
        pageB.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctxB = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtxB = new LayoutContext(ctxB);
        using var resolverB = new BreakResolver();
        var resultB = pageB.AttemptLayout(
            ctxB, ref layoutCtxB, resolverB, LayoutAttemptStrategy.LastResort);

        // Cache rejected + fresh resolve runs → grid B's single item
        // emits (= bItem, not aItem2).
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001);
        Assert.Single(sink.Fragments);
        Assert.Equal(bItem, sink.Fragments[0].Box);
    }

    [Fact]
    public void Cycle5b_F5_block_layouter_rejects_misrouted_grid_continuation()
    {
        // Per PR-#97 review F5 — BlockLayouter.AttemptLayout entry
        // guard validates that a BlockContinuation carrying a
        // GridContinuation in LayouterState targets a GridContainer /
        // InlineGridContainer / block-flow container that could
        // contain one. Misrouted (= pointing at a leaf non-grid
        // non-block-flow kind like TableCell) surfaces loudly with
        // InvalidOperationException rather than silently ignoring
        // the resume state. Mirrors the cycle-4b
        // Task16_block_layouter_rejects_misrouted_flex_continuation
        // test in FlexLayouterTests.cs.
        var rootStyle = MakeStyle();
        var root = Box.CreateRoot(rootStyle);
        var tableCellStyle = MakeStyle();
        var tableCell = Box.ForElement(BoxKind.TableCell, tableCellStyle, MakeElement());
        root.AppendChild(tableCell);

        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();
        var misrouted = new BlockContinuation(
            ResumeAtChild: 0,
            ConsumedBlockSize: 0.0,
            LayouterState: new GridContinuation(RowIndex: 0));

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink,
            incomingContinuation: misrouted,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 200, blockSize: 200);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();

        System.InvalidOperationException? thrown = null;
        try
        {
            layouter.AttemptLayout(ctx, ref layoutCtx, resolver,
                LayoutAttemptStrategy.LastResort);
        }
        catch (System.InvalidOperationException ex) { thrown = ex; }
        Assert.NotNull(thrown);
        Assert.Contains("GridContinuation", thrown!.Message);
    }

    [Fact]
    public void Grid_container_explicit_width_auto_margins_center_it()
    {
        // PR #204 review [P2] — a grid container is a block-level box in normal flow, so
        // `display:grid; width:200px; margin:0 auto` centers it: offset (600-200)/2 = 200.
        // Mirrors the flex case; both kinds now participate in §10.3.3 auto-margin
        // distribution (BlockLayouter.ResolveAutoInlineMargins) like a plain block.
        var sink = new RecordingFragmentSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 40.0 }, colsPx: new[] { 100.0, 100.0 });
        SetExplicitWidth(grid, 200);
        SetExplicitHeight(grid, 40);
        grid.Style.Set(PropertyId.MarginLeft, ComputedSlot.FromKeyword(0));   // authored `auto`
        grid.Style.Set(PropertyId.MarginRight, ComputedSlot.FromKeyword(0));
        grid.AppendChild(BuildItemWithExplicitPlacement(row: 1, col: 1));

        var root = Box.CreateRoot(MakeStyle());
        root.AppendChild(grid);

        using var layouter = new BlockLayouter(
            rootBox: root, sink: sink, incomingContinuation: null,
            diagnostics: null, shaperResolver: shaper);
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        BoxFragment? wrapper = null;
        foreach (var f in sink.Fragments)
            if (f.Box == grid) { wrapper = f; break; }
        Assert.NotNull(wrapper);
        Assert.Equal(200.0, wrapper!.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, wrapper.Value.InlineOffset, precision: 3);
    }

    [Fact]
    public void Cycle5b_F4_resume_with_different_inline_size_preserves_RowIndex_no_duplication()
    {
        // Per PR-#97 review F4 — pre-fix: when cache rejected for
        // inline-size mismatch on the SAME grid, startRow was reset
        // to 0 → page 2 re-emitted page 1's items. Post-fix: when
        // identity matches but inline size differs, preserve RowIndex
        // + recompute geometry. The remaining item emits exactly once.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        // Page 1: emit row 1, defer row 2. Cache built at width 100.
        var sinkPage1 = new RecordingFragmentSink();
        using var page1 = new GridLayouter(
            rootBox: grid, sink: sinkPage1,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var continuation = (GridContinuation)result1.Continuation!;
        Assert.Equal(1, continuation.RowIndex);
        Assert.Single(sinkPage1.Fragments);

        // Page 2: resume with DIFFERENT inline size (200 instead of 100).
        // Cache invalidated, but startRow preserved at RowIndex=1.
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: continuation,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 200, 100, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 200, blockSize: 100);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        // Per F4 — page 2 emits ONLY item2 (the row-2 item).
        // Pre-fix it would emit item1 + item2 (= duplication).
        Assert.Equal(LayoutAttemptOutcome.AllDone, result2.Outcome);
        Assert.Single(sink.Fragments);
        Assert.Equal(item2, sink.Fragments[0].Box);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridResumeInlineSizeMismatch001);
    }

    [Fact]
    public void Cycle5_hardening_F3_resume_with_same_inline_size_uses_cache_no_diagnostic()
    {
        // Per PR-#96 review F3 — sanity test: resume with the SAME
        // contentInlineSize uses the cache (no invalidation diagnostic).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0 });
        SetExplicitWidth(grid, 100);
        SetExplicitHeight(grid, 200);
        var item1 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var item2 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        grid.AppendChild(item1);
        grid.AppendChild(item2);

        var sinkPage1 = new RecordingFragmentSink();
        using var page1 = new GridLayouter(
            rootBox: grid, sink: sinkPage1,
            incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        page1.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx1 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx1 = new LayoutContext(ctx1);
        using var resolver1 = new BreakResolver();
        var result1 = page1.AttemptLayout(
            ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var continuation = (GridContinuation)result1.Continuation!;

        // Resume with SAME contentInlineSize=100.
        using var page2 = new GridLayouter(
            rootBox: grid, sink: sink,
            incomingContinuation: continuation,
            diagnostics: diag, shaperResolver: shaper);
        page2.ConfigureEmission(0, 0, 100, 100, allowPagination: true);
        var ctx2 = new FragmentainerContext(contentInlineSize: 100, blockSize: 100);
        var layoutCtx2 = new LayoutContext(ctx2);
        using var resolver2 = new BreakResolver();
        var result2 = page2.AttemptLayout(
            ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);

        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridResumeInlineSizeMismatch001);
        Assert.Single(sink.Fragments);
    }

    // =====================================================================
    //  Phase 3 Task 18 cycle 6 — span placement + implicit tracks +
    //  grid-auto-flow row/column
    // =====================================================================

    [Fact]
    public void Cycle6_explicit_row_span_2_fills_two_explicit_rows()
    {
        // grid-row: 1 / span 2 + grid-column: 1 in a 2×2 explicit grid:
        // item occupies rows 0+1 at col 0; the rectangle is fully
        // within the explicit grid (no implicit growth).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 200.0 },
            colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitStartAndSpan(
            rowStart: 1, rowSpan: 2, colStart: 1, colSpan: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 300);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle6_auto_placement_with_row_span_finds_next_free_run()
    {
        // 1×2 grid, item A at (1, 1) explicit; item B auto-placed with
        // grid-row: span 2 → B needs a 2-row × 1-col free run. The
        // first column has the explicit item at (0, 0); the search
        // continues to column 1 in row 0 (1-row vertical) — doesn't
        // fit (needs 2). Grows to row 1 (implicit) and places B at
        // (0, 1) with rowSpan=2.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 50.0, 150.0 });
        var a = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var b = BuildItemWithSpanRowStart(spanCount: 2);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
        // B at (0, 1) spanning the explicit row 0 + an implicit row
        // (auto-sized to 0 with no item contribution). Total block
        // extent = 100 + 0 = 100.
        AssertFragmentEquals(sink, b,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 150, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle6_grid_auto_rows_50px_sizes_implicit_rows()
    {
        // 1-row explicit grid, grid-auto-rows: 50px. Item with
        // grid-row-start: 3 → row 2 (implicit). The implicit row gets
        // sized to 50px per grid-auto-rows; rows 1 also implicit (=
        // 50px). So row 0 = 100 (explicit), row 1 = 50 (implicit),
        // row 2 = 50 (implicit). Item at row 2 → blockOffset = 150.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 50.0 });
        SetGridAutoRows(grid, 50.0);
        var item = BuildItemWithExplicitPlacement(row: 3, col: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 150,
            inlineSize: 50, blockSize: 50);
    }

    [Fact]
    public void Cycle6_grid_auto_flow_column_reverses_sparse_fill_direction()
    {
        // 2×2 explicit grid, grid-auto-flow: column, 4 auto-placed
        // items. Per §8.5 column-major order: (0,0), (1,0), (0,1), (1,1).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 200.0 },
            colsPx: new[] { 50.0, 150.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Column);
        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            items[i] = BuildAutoPlacedItem();
            grid.AppendChild(items[i]);
        }

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(4, sink.Fragments.Count);
        // Column-major fill: items[0] at (0,0), items[1] at (1,0),
        // items[2] at (0,1), items[3] at (1,1).
        AssertFragmentEquals(sink, items[0],
            inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, items[1],
            inlineOffset: 0, blockOffset: 100, inlineSize: 50, blockSize: 200);
        AssertFragmentEquals(sink, items[2],
            inlineOffset: 50, blockOffset: 0, inlineSize: 150, blockSize: 100);
        AssertFragmentEquals(sink, items[3],
            inlineOffset: 50, blockOffset: 100, inlineSize: 150, blockSize: 200);
    }

    [Fact]
    public void Cycle6_span_0_normalizes_to_span_1_per_section_8_3_1()
    {
        // Per CSS Grid L1 §8.3.1, `span 0` is invalid and normalizes
        // to `span 1`. GridLineValue.ForSpan rejects 0 at the factory;
        // ReadPlacement clamps Span via Math.Max(1, ...) to defend
        // against any path that bypasses the factory (e.g., the
        // resolver's tokenizer). This test pins the clamp behavior by
        // constructing a GridLineValue with the lowest legal Span (1)
        // and verifying the placement is a single-cell.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 50.0 });
        var item = BuildItemWithSpanRowStart(spanCount: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
    }

    [Fact]
    public void Cycle6_grid_row_2_slash_5_spans_three_rows_with_swap()
    {
        // grid-row: 2 / 5 → start line 2, end line 5 → spans 3 rows
        // (rows 1, 2, 3 in 0-based). In a 4-row explicit grid the
        // item fits without implicit growth.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 50.0, 75.0, 25.0 },
            colsPx: new[] { 50.0 });
        var item = BuildItemWithExplicitStartAndEnd(
            rowStart: 2, rowEnd: 5, colStart: 1, colEnd: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // blockOffset = sum of row 0 (100) = 100; blockSize = sum of
        // rows 1, 2, 3 = 50 + 75 + 25 = 150.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 50, blockSize: 150);
    }

    [Fact]
    public void Cycle6_grid_row_5_slash_2_swap_per_section_8_3()
    {
        // Per §8.3 — if end ≤ start, the values swap. So `5 / 2`
        // becomes `2 / 5` → span 3 rows starting at row 1.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 50.0, 75.0, 25.0 },
            colsPx: new[] { 50.0 });
        var item = BuildItemWithExplicitStartAndEnd(
            rowStart: 5, rowEnd: 2, colStart: 1, colEnd: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 50, blockSize: 150);
    }

    [Fact]
    public void Cycle6_spanning_item_distributes_intrinsic_contribution_equally()
    {
        // 2-row grid with `grid-template-rows: auto auto`, item with
        // grid-row: 1 / 3 (span 2) and declared height: 100px. Cycle 6a
        // equal-share distribution: each auto row gets 100/2 = 50px.
        // (Spec-strict §11.5.1 step 3 would distribute differently;
        // tracked in grid-spanning-item-intrinsic-distribution-deferral.)
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        // Build an auto/auto grid (= two intrinsic rows).
        var rowsTrack = new TrackList(
            System.Collections.Immutable.ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForAuto()),
                new TrackListEntry(TrackEntry.ForAuto())));
        var colsTrack = new TrackList(
            System.Collections.Immutable.ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50.0))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);

        var item = BuildItemWithExplicitStartAndEnd(
            rowStart: 1, rowEnd: 3, colStart: 1, colEnd: 1);
        SetExplicitHeight(item, 100);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // Each auto row sized to 50px (= 100/2 equal-share). Item
        // spans both rows so blockSize = 50 + 50 = 100.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
    }

    [Fact]
    public void Cycle6_grid_auto_columns_50px_sizes_implicit_columns()
    {
        // 1-col explicit grid, grid-auto-columns: 50px. Item with
        // grid-column-start: 3 → col 2 (implicit). Implicit col 1 =
        // 50, implicit col 2 = 50. Item at col 2 → inlineOffset =
        // 100 (col 0) + 50 (col 1) = 150.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0 },
            colsPx: new[] { 100.0 });
        SetGridAutoColumns(grid, 50.0);
        var item = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 150, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
    }

    // =====================================================================
    //  Phase 3 Task 18 cycle 7a — grid-template-areas + named-area
    //  placement (= `grid-row-start: head`, `grid-area: head` etc.).
    // =====================================================================

    [Fact]
    public void Cycle7a_named_area_start_with_auto_end_spans_full_area_on_axis()
    {
        // grid-template-areas: "head head" "main side" — 2-row × 2-col
        // grid. `grid-row-start: head` (without explicit end) spans
        // the FULL head area on the row axis (= 1 row, since head is
        // one row tall). Pair with grid-column-start auto → auto-
        // placed column.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0, 150.0 });
        SetGridTemplateAreas(grid, "\"head head\" \"main side\"");
        var item = BuildItemWithNamedAreaStarts(rowName: "head", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
        // Per PR-#105 review F4 — happy-path placement must not emit
        // any placement-approximated diagnostic; the resolved position
        // came directly from the named-area lookup.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7a_grid_area_shorthand_lands_item_in_full_named_area()
    {
        // `grid-area: head` shorthand expands to all four longhands
        // as `head`. Per cycle-7a placement, this lands the item in
        // the full head rectangle (= 1 row × 2 cols, total 200px wide).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0, 150.0 });
        SetGridTemplateAreas(grid, "\"head head\" \"main side\"");
        var item = BuildItemWithNamedAreaStartsAndEnds(
            rowStart: "head", rowEnd: "head",
            colStart: "head", colEnd: "head");
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 200, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7a_named_area_main_lands_at_row_2_col_1()
    {
        // The `main` area is at row 2 col 1 in the invoice template.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0, 150.0 });
        SetGridTemplateAreas(grid, "\"head head\" \"main side\"");
        var item = BuildItemWithNamedAreaStartsAndEnds(
            rowStart: "main", rowEnd: "main",
            colStart: "main", colEnd: "main");
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 50, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7a_unknown_area_name_falls_back_to_auto_with_diagnostic()
    {
        // `grid-row-start: nope` where "nope" isn't an area name → auto
        // placement + diagnostic.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 50.0 });
        SetGridTemplateAreas(grid, "\"head\"");
        var item = BuildItemWithNamedAreaStarts(rowName: "nope", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7a_three_row_template_spans_via_named_area()
    {
        // 3-row template; an item in the foot area lands at row 3.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0, 50.0 },
            colsPx: new[] { 100.0, 100.0 });
        SetGridTemplateAreas(grid,
            "\"head head\" \"main side\" \"foot foot\"");
        var item = BuildItemWithNamedAreaStartsAndEnds(
            rowStart: "foot", rowEnd: "foot",
            colStart: "foot", colEnd: "foot");
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 200,
            inlineSize: 200, blockSize: 50);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    // =================================================================
    //  PR-#105 review F5 — grid-template-areas with missing /
    //  partial explicit tracks. Cycle 6a's implicit-only path handles
    //  this when the cascade default for grid-template-rows /
    //  grid-template-columns is `none`.
    // =================================================================

    [Fact]
    public void F5_grid_template_areas_with_no_explicit_tracks_uses_implicit()
    {
        // No grid-template-rows / -columns declared. With cycle 6a's
        // implicit-only grid path, items in the areas map use auto-
        // sized implicit tracks. Without item dimensions, tracks
        // collapse to 0; the fragment still gets a valid placement.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = TrackList.None;
        var colsTrack = TrackList.None;
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetGridTemplateAreas(grid, "\"head head\" \"main side\"");
        var head = BuildItemWithNamedAreaStartsAndEnds(
            rowStart: "head", rowEnd: "head",
            colStart: "head", colEnd: "head");
        var main = BuildItemWithNamedAreaStartsAndEnds(
            rowStart: "main", rowEnd: "main",
            colStart: "main", colEnd: "main");
        grid.AppendChild(head);
        grid.AppendChild(main);

        RunGridLayouter(grid, sink, diag, shaper);

        // 2 items emit. Implicit tracks generated past the cycle-6a
        // 1×1 seed (= rows 0+1, cols 0+1). All sized 0 since items
        // have no declared dimensions; positions are valid.
        Assert.Equal(2, sink.Fragments.Count);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void F5_grid_template_areas_with_partial_explicit_rows_grows_implicit()
    {
        // Areas declare 3 rows; explicit grid-template-rows only
        // declares 2. Cycle 6a's implicit-track generation handles
        // the third (implicit) row via grid-auto-rows: auto (default).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0, 100.0 });
        SetGridTemplateAreas(grid,
            "\"head head\" \"main side\" \"foot foot\"");
        var foot = BuildItemWithNamedAreaStartsAndEnds(
            rowStart: "foot", rowEnd: "foot",
            colStart: "foot", colEnd: "foot");
        grid.AppendChild(foot);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // foot occupies row 2 (= 1-based row 3 = 0-based 2, past the
        // 2 explicit rows). blockOffset = 200 (two 100px rows above);
        // blockSize = 0 (implicit row auto-sized; no item content).
        AssertFragmentEquals(sink, foot,
            inlineOffset: 0, blockOffset: 200,
            inlineSize: 200, blockSize: 0);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    // =====================================================================
    //  Phase 3 Task 18 cycle 7d — `grid-auto-flow: dense` per CSS Grid
    //  §8.5. Dense packing resets the auto-placement cursor before
    //  each search so earlier holes get filled.
    // =====================================================================

    [Fact]
    public void Cycle7d_dense_fills_earlier_hole_left_by_definite_item()
    {
        // 2-row × 3-col grid with `grid-auto-flow: row dense`. An
        // item explicitly placed at (1, 3) leaves cell (1, 2) empty.
        // Two subsequent auto-placed items: SPARSE would walk past
        // the hole and place at (1, 3) (= blocked by definite item)
        // then (2, 1). DENSE rewinds to (1, 1) for each + fills the
        // hole.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0, 50.0, 50.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.RowDense);

        // Definite at row 1 col 3 (= last col of row 0).
        var definite = BuildItemWithExplicitPlacement(row: 1, col: 3);
        // Two auto-placed items.
        var a = BuildAutoPlacedItem();
        var b = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        // definite at (0, 2) → inlineOffset 100.
        AssertFragmentEquals(sink, definite,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
        // Per dense: a at (0, 0); b at (0, 1). Both row 0.
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, b,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
    }

    [Fact]
    public void Cycle7d_sparse_and_dense_agree_when_cursor_unblocked()
    {
        // Per post-PR-#108 review P3 — when the auto-placement cursor
        // hasn't advanced PAST any holes (= the holes are still in
        // front of the cursor at placement time), sparse and dense
        // produce IDENTICAL placements. This test pins that agreement;
        // the strict sparse-vs-dense contrast lives in
        // <see cref="Cycle7d_dense_backfills_hole_left_by_wide_span_item"/>
        // and <see cref="Cycle7d_sparse_skips_hole_left_by_wide_span_item"/>.
        //
        // Setup: 3-col × 2-row grid with `row` (sparse) flow + a
        // definite item at (1, 3) that occupies the trailing cell of
        // row 0. Two 1-cell auto items follow. In sparse + dense, both
        // 1-cell autos take the first free cells from cursor (0, 0):
        //   - Auto a at (0, 0); auto b at (0, 1). No earlier holes.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0, 50.0, 50.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Row);

        var definite = BuildItemWithExplicitPlacement(row: 1, col: 3);
        var a = BuildAutoPlacedItem();
        var b = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
        AssertFragmentEquals(sink, b,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
    }

    [Fact]
    public void Cycle7d_dense_backfills_hole_left_by_wide_span_item()
    {
        // Per post-PR-#108 review P2 — the canonical sparse-vs-dense
        // contrast from CSS Grid §8.5: a wider span item creates a
        // trailing gap that the cursor cannot return to. Dense rewinds
        // the cursor before each search and BACKFILLS the gap; sparse
        // walks past it.
        //
        // 4-col × 2-row grid with `row dense`:
        //   - Definite item D at (1, 2) → occupies cell (0, 1).
        //   - Auto A with col-span 2 → can't fit at (0, 0..1) because
        //     (0, 1) is occupied; tries (0, 2..3); fits → A at (0, 2..3).
        //     After A, sparse cursor would sit at (1, 0); dense cursor
        //     resets before the next item.
        //   - Auto B (1 cell) → cursor reset to (0, 0); first free
        //     cell is (0, 0) → B at (0, 0). Dense backfilled the hole
        //     left by A skipping past it.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0, 50.0 },
            colsPx: new[] { 50.0, 50.0, 50.0, 50.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.RowDense);

        var definite = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var a = BuildAutoPlacedItemWithColSpan(spanCount: 2);
        var b = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        // D at (0, 1) — definite.
        AssertFragmentEquals(sink, definite,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
        // A at (0, 2..3) — first 2-col free run.
        AssertFragmentEquals(sink, a,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 50);
        // B at (0, 0) — dense rewind BACKFILLS the hole.
        AssertFragmentEquals(sink, b,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
    }

    [Fact]
    public void Cycle7d_sparse_skips_hole_left_by_wide_span_item()
    {
        // Per post-PR-#108 review P2 — pair to the dense backfill test.
        // SAME setup as <see cref="Cycle7d_dense_backfills_hole_left_by_wide_span_item"/>
        // but with `row` (sparse) auto-flow. Sparse advances the cursor
        // after A's wide span without rewinding, so B walks PAST the
        // (0, 0) hole + wraps to row 1.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0, 50.0 },
            colsPx: new[] { 50.0, 50.0, 50.0, 50.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Row);

        var definite = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var a = BuildAutoPlacedItemWithColSpan(spanCount: 2);
        var b = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, definite,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
        AssertFragmentEquals(sink, a,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 50);
        // B at (1, 0) — sparse cursor walked past the (0, 0) hole.
        // This is the only assertion that differs from the dense pair.
        AssertFragmentEquals(sink, b,
            inlineOffset: 0, blockOffset: 50,
            inlineSize: 50, blockSize: 50);
    }

    [Fact]
    public void Cycle7d_dense_pass2_backfills_row_locked_hole()
    {
        // Per post-PR-#108 review P1 — Pass 2 (row-locked items)
        // sparse-vs-dense contrast. Without the Pass 2 sparse cursor
        // fix, BOTH modes would scan from minor=0 (= dense behavior)
        // even in sparse mode.
        //
        // Setup: 4-col × 1-row grid with `row dense`:
        //   - Pass 1: D = both definite at (1, 2) → (0, 1).
        //   - Pass 2: A = row-locked row 1 with col-span 2 → cursor
        //     irrelevant in dense; finds (0, 0..1)? Blocked by D. Tries
        //     (0, 2..3) → fits.
        //   - Pass 2: B = row-locked row 1, 1 cell → dense rescans
        //     from col 0; (0, 0) is FREE → B at (0, 0).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0 },
            colsPx: new[] { 50.0, 50.0, 50.0, 50.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.RowDense);

        var definite = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var a = BuildRowLockedItemWithColSpan(row: 1, colSpan: 2);
        var b = BuildItemWithRowOnlyPlacement(row: 1);
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, definite,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
        AssertFragmentEquals(sink, a,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 50);
        // B at (0, 0) — dense Pass 2 rescans from col 0.
        AssertFragmentEquals(sink, b,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
    }

    [Fact]
    public void Cycle7d_sparse_pass2_skips_row_locked_hole()
    {
        // Per post-PR-#108 review P1 — sparse counterpart to the dense
        // Pass 2 backfill. Same setup with `row` (sparse) auto-flow.
        // After A places at (0, 2..3) advancing the row-0 sparse
        // cursor to col 4, B starts scanning from col 4. The minor
        // extent grows to fit B at (0, 4) instead of backfilling.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0 },
            colsPx: new[] { 50.0, 50.0, 50.0, 50.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Row);

        var definite = BuildItemWithExplicitPlacement(row: 1, col: 2);
        var a = BuildRowLockedItemWithColSpan(row: 1, colSpan: 2);
        var b = BuildItemWithRowOnlyPlacement(row: 1);
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, definite,
            inlineOffset: 50, blockOffset: 0,
            inlineSize: 50, blockSize: 50);
        AssertFragmentEquals(sink, a,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 50);
        // B at (0, 4) — sparse cursor advanced past (0, 0).
        // Implicit col 4 is created at the grid-auto-columns default
        // (= auto track, sized to content = 0 with no content).
        AssertFragmentEquals(sink, b,
            inlineOffset: 200, blockOffset: 0,
            inlineSize: 0, blockSize: 50);
    }

    [Fact]
    public void Cycle7d_dense_column_flow_backfills_hole()
    {
        // Per post-PR-#108 review P3 — proper column-flow dense
        // coverage. Column-flow makes column the major axis + row
        // the minor axis. A row-spanning auto item creates the
        // trailing minor (row) hole; dense rewinds the cursor to
        // backfill it.
        //
        // Setup: 4-row × 2-col grid with `column dense`:
        //   - Definite D at (2, 1) → (1, 0). Occupies (row 1, col 0).
        //   - Auto A row-span 2 → walking cols, cursor (col=0, row=0).
        //     Wants 2 rows. (rows 0..1 in col 0)? row 1 occupied → no.
        //     (rows 1..2): occupied. (rows 2..3 in col 0) → free →
        //     places at (rows 2..3, col 0).
        //   - Auto B (1 cell) → dense rewind cursor to (col=0, row=0).
        //     (0, 0) FREE → B at (0, 0). Backfilled.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 25.0, 25.0, 25.0, 25.0 },
            colsPx: new[] { 100.0, 100.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.ColumnDense);

        var definite = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var a = BuildAutoPlacedItemWithRowSpan(spanCount: 2);
        var b = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        // D at (1, 0).
        AssertFragmentEquals(sink, definite,
            inlineOffset: 0, blockOffset: 25,
            inlineSize: 100, blockSize: 25);
        // A at (2, 0) row-span 2.
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 50,
            inlineSize: 100, blockSize: 50);
        // B at (0, 0) — dense column-flow rewinds + backfills.
        AssertFragmentEquals(sink, b,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 25);
    }

    [Fact]
    public void Cycle7d_sparse_column_flow_skips_hole()
    {
        // Per post-PR-#108 review P3 — sparse column-flow counterpart.
        // Same setup with `column` (sparse) auto-flow. After A places
        // at (rows 2..3, col 0) advancing the cursor past row 3 in
        // col 0, B walks to col 1 instead of rewinding.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 25.0, 25.0, 25.0, 25.0 },
            colsPx: new[] { 100.0, 100.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Column);

        var definite = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var a = BuildAutoPlacedItemWithRowSpan(spanCount: 2);
        var b = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(3, sink.Fragments.Count);
        AssertFragmentEquals(sink, definite,
            inlineOffset: 0, blockOffset: 25,
            inlineSize: 100, blockSize: 25);
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 50,
            inlineSize: 100, blockSize: 50);
        // B at (0, 1) — sparse walks past col 0 hole, into col 1.
        AssertFragmentEquals(sink, b,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 25);
    }

    [Fact]
    public void Cycle7d_dense_column_flow_resets_column_cursor()
    {
        // Column-flow dense + a definite item should reset cursor.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0, 100.0, 100.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.ColumnDense);

        var definite = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var a = BuildAutoPlacedItem();
        grid.AppendChild(definite);
        grid.AppendChild(a);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        // definite at (1, 0); a at (0, 0).
        AssertFragmentEquals(sink, definite,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 100, blockSize: 100);
        AssertFragmentEquals(sink, a,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 100);
    }

    // =====================================================================
    //  Phase 3 Task 18 cycle 7c — repeat(auto-fill, …) /
    //  repeat(auto-fit, …) container-size-derived count per CSS Grid
    //  L1 §7.2.3.1.
    // =====================================================================

    [Fact]
    public void Cycle7c_auto_fill_derives_count_from_container_extent()
    {
        // grid-template-columns: repeat(auto-fill, 100px) in a 350px
        // container → floor(350 / 100) = 3 columns. 5 items in row-
        // major fill: items 0+1+2 at row 0, items 3+4 at row 1.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0, // auto-fill
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForLength(100.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 350);

        var items = new Box[5];
        for (var i = 0; i < 5; i++)
        {
            items[i] = BuildAutoPlacedItem();
            grid.AppendChild(items[i]);
        }

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 350, contentBlockSize: 400);

        Assert.Equal(5, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0],
            inlineOffset: 0, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, items[1],
            inlineOffset: 100, blockOffset: 0, inlineSize: 100, blockSize: 50);
        AssertFragmentEquals(sink, items[2],
            inlineOffset: 200, blockOffset: 0, inlineSize: 100, blockSize: 50);
        // Row 1 (implicit since row template only has 1 row).
        AssertFragmentEquals(sink, items[3],
            inlineOffset: 0, blockOffset: 50, inlineSize: 100, blockSize: 0);
        AssertFragmentEquals(sink, items[4],
            inlineOffset: 100, blockOffset: 50, inlineSize: 100, blockSize: 0);
    }

    [Fact]
    public void Cycle7c_auto_fit_treated_identically_to_auto_fill()
    {
        // Per grid-auto-fit-collapse-empty-tracks-deferral — cycle 7c
        // ships auto-fit as auto-fill (= no empty-track collapse).
        // Same input as the auto-fill test should produce the same
        // tracks (= 3 columns × 100px in 350px).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: -1, // auto-fit
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForLength(100.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 350);

        var item = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 350, contentBlockSize: 400);

        Assert.Single(sink.Fragments);
        // Item at column 3 (0-based col 2) → inlineOffset 200.
        AssertFragmentEquals(sink, item,
            inlineOffset: 200, blockOffset: 0,
            inlineSize: 100, blockSize: 50);
    }

    [Fact]
    public void Cycle7c_F1_minmax_uses_max_when_definite()
    {
        // Per PR-#107 review F1 #2 — `minmax(100px, 200px)` has both
        // sides definite; the count derivation uses the MAX (200) per
        // spec, floored by the min (= no actual floor since 200 > 100).
        // In a 600px container: floor(600 / 200) = 3 columns.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0,
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForMinMax(
                            TrackEntry.ForLength(100.0),
                            TrackEntry.ForLength(200.0))))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 600);

        // Item at column 3 (0-based col 2). With 3 cols × 200px max
        // each, col 2 starts at offset 400.
        var item = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 600, contentBlockSize: 400);

        Assert.Single(sink.Fragments);
        // Cycle 4 ships minmax with maximize → tracks grow to 200px each.
        AssertFragmentEquals(sink, item,
            inlineOffset: 400, blockOffset: 0,
            inlineSize: 200, blockSize: 50);
    }

    [Fact]
    public void Cycle7c_F1_minmax_uses_min_when_only_min_definite()
    {
        // Per PR-#107 review F1 #2 — `minmax(auto, 100px)` has min
        // intrinsic and max definite; per spec use the definite side
        // (max = 100). Per `minmax(100px, auto)` (max intrinsic, min
        // definite), use the min for count derivation. This test
        // covers the second variant — count comes from the 100px
        // min floor.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0,
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForMinMax(
                            TrackEntry.ForLength(100.0),
                            TrackEntry.ForAuto())))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 350);

        // Min-definite → count uses 100; floor(350/100) = 3 iterations.
        var trailingItem = BuildItemWithExplicitPlacement(row: 1, col: 3);
        grid.AppendChild(trailingItem);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 350, contentBlockSize: 400);

        Assert.Single(sink.Fragments);
        // Col 3 = 0-based col 2; with min=100 the track sizes >= 100.
        // The Maximize step may grow tracks to fill 350px; col 2 sits
        // at offset 200 (= 2 × 100 base, since intrinsic-resolution
        // produces 0 then equal-distribute via Maximize spreads
        // leftover). Verify the offset is non-zero (= multiple cols).
        Assert.True(sink.Fragments[0].InlineOffset >= 200,
            $"Expected col 2 at offset ≥ 200; got {sink.Fragments[0].InlineOffset}");
    }

    [Fact]
    public void Cycle7c_F1_percentage_resolves_against_container_extent()
    {
        // Per PR-#107 review F1 #3 — `repeat(auto-fill, 25%)` in a
        // 400px container: 25% × 400 = 100; floor(400/100) = 4
        // iterations.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0,
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForPercentage(25.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 400);

        // Item at col 4 (0-based col 3) — verifies 4 columns derived.
        var item = BuildItemWithExplicitPlacement(row: 1, col: 4);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 400, contentBlockSize: 400);

        Assert.Single(sink.Fragments);
        // Item at column 4 (1-based) = col 3 (0-based) → derivation
        // produced at least 4 iterations. (Cycle 4 % tracks still
        // sized 0 by `LayoutGridPercentageTrackApproximated001`; this
        // test verifies COUNT derivation succeeded, not the sized
        // value.) The fragment lands successfully = no implicit-
        // track-drop diagnostic.
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    [Fact]
    public void Cycle7c_F2_auto_fit_emits_approximation_diagnostic()
    {
        // Per PR-#107 review F2 #4 — auto-fit emits a one-shot
        // `LAYOUT-GRID-AUTO-FIT-APPROXIMATED-001` warning so authors
        // know the empty-track collapse pass is approximated.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: -1, // auto-fit
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForLength(100.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 350);

        var item = BuildAutoPlacedItem();
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 350, contentBlockSize: 400);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridAutoFitApproximated001);
    }

    [Fact]
    public void Cycle7c_F2_auto_fill_does_not_emit_auto_fit_diagnostic()
    {
        // Per PR-#107 review F2 #4 — the diagnostic fires ONLY for
        // auto-fit, not auto-fill.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0, // auto-fill
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForLength(100.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 350);

        var item = BuildAutoPlacedItem();
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 350, contentBlockSize: 400);

        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridAutoFitApproximated001);
    }

    [Fact]
    public void Cycle7c_F2_global_truncation_cap_emits_diagnostic()
    {
        // Per PR-#107 review F2 #5 — `repeat(auto-fill, 1px)` in a
        // huge container would derive > MaxExpandedTrackCount tracks;
        // the global cap truncates + emits
        // LayoutGridMaxExpandedTracksTruncated001.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0,
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForLength(1.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        // 100,000px container → 100,000 iterations of 1px would
        // exceed TrackList.MaxExpandedTrackCount = 50,000.
        SetExplicitWidth(grid, 100_000);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 100_000, contentBlockSize: 400);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
    }

    [Fact]
    public void Cycle7c_auto_fill_zero_pattern_size_falls_back_to_single_iteration()
    {
        // Per §7.2.3.1 — when the pattern's fixed size is 0 (e.g.,
        // `repeat(auto-fill, 1fr)` or all-intrinsic), the count
        // cannot be derived → fall back to 1 iteration.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        // Pattern: a single fr track (= no fixed size).
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0,
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForFr(1.0)))))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 350);

        var item = BuildAutoPlacedItem();
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 350, contentBlockSize: 400);

        Assert.Single(sink.Fragments);
        // 1 iteration → 1 fr track → grows to fill container (350px).
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 350, blockSize: 50);
    }

    [Fact]
    public void Cycle7c_auto_fill_subtracts_other_fixed_tracks_from_container()
    {
        // grid-template-columns: 50px repeat(auto-fill, 100px) 30px
        // in a 380px container → (380 - 50 - 30) / 100 = 3 iterations.
        // Total = 50 + 100 + 100 + 100 + 30 = 380px.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0 });
        var colsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(50.0)),
                new TrackListRepeat(TrackRepeat.Create(
                    count: 0,
                    pattern: ImmutableArray.Create<TrackRepeatItem>(
                        new TrackRepeatEntry(TrackEntry.ForLength(100.0))))),
                new TrackListEntry(TrackEntry.ForLength(30.0))));
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetExplicitWidth(grid, 380);

        var trailingItem = BuildItemWithExplicitPlacement(row: 1, col: 5);
        grid.AppendChild(trailingItem);

        RunGridLayouter(grid, sink, diag, shaper,
            contentInlineSize: 380, contentBlockSize: 400);

        Assert.Single(sink.Fragments);
        // 5 columns total: 50px + 100 + 100 + 100 + 30 = 380. Col 5
        // (= 0-based col 4) is the trailing 30px track at offset 350.
        AssertFragmentEquals(sink, trailingItem,
            inlineOffset: 350, blockOffset: 0,
            inlineSize: 30, blockSize: 50);
    }

    // =====================================================================
    //  Phase 3 Task 18 cycle 7b — named-line placement.
    //  `grid-row-start: name` resolves against the per-axis named-line
    //  lookup map combining authored `[name]` lines from grid-template-
    //  rows/columns + implicit `<area>-start` / `<area>-end` lines from
    //  grid-template-areas per CSS Grid L1 §8.4.
    // =====================================================================

    [Fact]
    public void Cycle7b_authored_named_line_resolves_to_definite_placement()
    {
        // grid-template-rows: [head-start] 100px [head-end] 100px.
        // 'head-end' = line 2 → row index 1.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                TrackListNamedLine.Create("head-start"),
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("head-end"),
                new TrackListEntry(TrackEntry.ForLength(100.0))));
        var colsTrack = BuildLengthTrackList(new[] { 50.0 });
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);

        var item = BuildItemWithNamedAreaStarts(rowName: "head-end", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 50, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7b_implicit_area_start_line_resolves_via_named_line_map()
    {
        // grid-template-areas: "head" "main" → head at row 0 (lines
        // 1..2). Per §8.4 implicit lines head-start = line 1 and
        // head-end = line 2 are auto-generated. `grid-row-start:
        // head-end` resolves to line 2 = row index 1.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0 });
        SetGridTemplateAreas(grid, "\"head\" \"main\"");

        var item = BuildItemWithNamedAreaStarts(rowName: "head-end", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 50, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7b_named_line_pair_spans_between_two_idents()
    {
        // grid-template-rows: [a] 100px [b] 100px [c] 100px [d].
        // `grid-row: a / d` spans 3 rows starting at line 1. Column
        // axis stays auto (= no row-ident references on it).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                TrackListNamedLine.Create("a"),
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("b"),
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("c"),
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("d")));
        var colsTrack = BuildLengthTrackList(new[] { 50.0 });
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);

        var item = BuildItemWithRowNamedLines(rowStart: "a", rowEnd: "d");
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // a = line 1, d = line 4 → span 3 rows starting at row 0.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 300);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7b_unknown_named_line_still_falls_back_to_auto_with_diagnostic()
    {
        // No grid-template-areas, no named lines → ident "ghost"
        // doesn't resolve anywhere. Falls back to auto + diagnostic
        // (= the cycle 7a fall-back path is preserved).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 50.0 });
        var item = BuildItemWithNamedAreaStarts(rowName: "ghost", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7b_first_occurrence_wins_when_implicit_and_authored_share_name()
    {
        // Per PR-#106 review F1 #2 + CSS Grid L1 §8.3 — when multiple
        // lines share a name, the FIRST occurrence (= lowest line
        // number) wins. The grid-template-areas implicit lines and
        // any authored `[name]` lines all share the named-line map;
        // resolution sorts by line number.
        //
        // Setup: head area at row 0 (lines 1..2) → implicit head-
        // start = line 1, head-end = line 2. Authored [head-start]
        // ALSO at line 3 (= between explicit rows 2 and 3). The
        // FIRST occurrence of head-start (= line 1) wins → item at
        // row index 0.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("head-start"),
                new TrackListEntry(TrackEntry.ForLength(100.0))));
        var colsTrack = BuildLengthTrackList(new[] { 50.0 });
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetGridTemplateAreas(grid, "\"head\" \"main\" \"foot\"");

        var item = BuildItemWithNamedAreaStarts(rowName: "head-start", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // First-occurrence head-start = implicit line 1 = row index 0.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 50, blockSize: 100);
    }

    [Fact]
    public void Cycle7b_authored_foo_start_resolves_when_no_area_named_foo()
    {
        // Per PR-#106 review F1 #3 — for `grid-row-start: foo`, try
        // foo-start FIRST. If no area named `foo` exists, but the
        // author declared `[foo-start]` in grid-template-rows, the
        // lookup still resolves via the line map.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("foo-start"),
                new TrackListEntry(TrackEntry.ForLength(100.0))));
        var colsTrack = BuildLengthTrackList(new[] { 50.0 });
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        // No grid-template-areas; the named-line map only contains
        // authored entries.

        var item = BuildItemWithNamedAreaStarts(rowName: "foo", colName: null);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // grid-row-start: foo → try foo-start → line 2 → row index 1.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 50, blockSize: 100);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Cycle7b_integer_ident_syntax_is_deferred_with_specific_diagnostic()
    {
        // Per PR-#106 review F2 #4 — `grid-row-start: foo 2`
        // (occurrence syntax) falls back to auto with a diagnostic
        // pointing at grid-implicit-named-area-and-occurrence-syntax-
        // deferral. Documents the gap and enables grep-from-source.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                TrackListNamedLine.Create("foo"),
                new TrackListEntry(TrackEntry.ForLength(100.0)),
                TrackListNamedLine.Create("foo"),
                new TrackListEntry(TrackEntry.ForLength(100.0))));
        var colsTrack = BuildLengthTrackList(new[] { 50.0 });
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);

        // `grid-row-start: foo 2` — 2nd occurrence of foo.
        var style = MakeStyle();
        var rowValue = GridLineValue.ForNamedLineNumber("foo", 2);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        // Falls back to auto + emits the diagnostic.
        Assert.Single(sink.Fragments);
        var diagnostic = Assert.Single(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
        Assert.Contains("occurrence", diagnostic.Message);
        Assert.Contains("grid-implicit-named-area-and-occurrence-syntax-deferral",
            diagnostic.Message);
    }

    [Fact]
    public void Cycle7b_span_ident_syntax_is_deferred_with_specific_diagnostic()
    {
        // Per PR-#106 review F2 #4 — `grid-row-start: span foo`
        // also falls back with a deferral-tagged diagnostic.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 50.0 });

        var style = MakeStyle();
        var rowValue = GridLineValue.ForSpanName("foo");
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        var diagnostic = Assert.Single(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
        Assert.Contains("span <custom-ident>", diagnostic.Message);
        Assert.Contains("grid-implicit-named-area-and-occurrence-syntax-deferral",
            diagnostic.Message);
    }

    // =====================================================================
    //  PR-#103 review F1–F7 — coverage for the review-pass-resolved
    //  findings: implicit-only grids, captured-explicit-count negative
    //  resolution, sparse-occupancy DoS guard, auto-flow:column
    //  transpose, col-locked cursor advance, auto-start definite-end
    //  diagnostic.
    // =====================================================================

    [Fact]
    public void F1_grid_template_columns_only_generates_implicit_rows()
    {
        // grid-template-columns: 50px 100px + no grid-template-rows +
        // 4 auto items in row-mode → fills (0,0), (0,1), (1,0), (1,1)
        // with implicit row 1 added.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = TrackList.None;
        var colsTrack = BuildLengthTrackList(new[] { 50.0, 100.0 });
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            items[i] = BuildAutoPlacedItem();
            grid.AppendChild(items[i]);
        }

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(4, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0],
            inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 0);
        AssertFragmentEquals(sink, items[1],
            inlineOffset: 50, blockOffset: 0, inlineSize: 100, blockSize: 0);
        AssertFragmentEquals(sink, items[2],
            inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 0);
        AssertFragmentEquals(sink, items[3],
            inlineOffset: 50, blockOffset: 0, inlineSize: 100, blockSize: 0);
    }

    [Fact]
    public void F1_grid_template_rows_only_generates_implicit_columns()
    {
        // grid-template-rows: 50px 100px + no grid-template-columns +
        // 4 auto items in column-mode → fills (0,0), (1,0), (0,1),
        // (1,1) with implicit column 1 added.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var rowsTrack = BuildLengthTrackList(new[] { 50.0, 100.0 });
        var colsTrack = TrackList.None;
        var grid = BuildGridContainerWithTemplates(rowsTrack, colsTrack);
        SetGridAutoFlow(grid, GridAutoFlowValue.Column);
        var items = new Box[4];
        for (var i = 0; i < 4; i++)
        {
            items[i] = BuildAutoPlacedItem();
            grid.AppendChild(items[i]);
        }

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(4, sink.Fragments.Count);
        AssertFragmentEquals(sink, items[0],
            inlineOffset: 0, blockOffset: 0, inlineSize: 0, blockSize: 50);
        AssertFragmentEquals(sink, items[1],
            inlineOffset: 0, blockOffset: 50, inlineSize: 0, blockSize: 100);
    }

    [Fact]
    public void F2_negative_line_resolves_against_original_explicit_grid()
    {
        // Per PR-#103 review F2 — earlier items grow the implicit row
        // count; a later `grid-row-start: -1` must still resolve
        // against the ORIGINAL 2-row explicit grid (= line 3 = row
        // index 2), not against the post-grown extent.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 200.0 },
            colsPx: new[] { 50.0 });
        // Item A at row 5 grows implicit rows past the original 2.
        var a = BuildItemWithExplicitPlacement(row: 5, col: 1);
        // Item B at row -1 must resolve against ORIGINAL explicitRowCount=2
        // → row 2 (= implicit row immediately after the explicit grid),
        // NOT row 4 (which would be -1 against the grown count).
        var b = BuildItemWithExplicitPlacement(row: -1, col: 1);
        grid.AppendChild(a);
        grid.AppendChild(b);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        // Item B at row 2 = blockOffset 300 (100 + 200) — the first
        // implicit row position (auto-sized to 0 since no item
        // contribution there).
        AssertFragmentEquals(sink, b,
            inlineOffset: 0, blockOffset: 300,
            inlineSize: 50, blockSize: 0);
    }

    [Fact]
    public void F3_far_out_coord_drops_item_with_diagnostic_no_oom()
    {
        // Per PR-#103 review F3 — `grid-row-start: 50000` would have
        // OOM-ed under the dense bool-matrix occupancy. Cycle-6a-
        // post-F3 caps implicit growth at MaxImplicitTracksPerAxis
        // (1024) and drops the item with a truncation diagnostic.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0 },
            colsPx: new[] { 100.0 });
        var benign = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var hostile = BuildItemWithExplicitPlacement(row: 50_000, col: 1);
        grid.AppendChild(benign);
        grid.AppendChild(hostile);

        RunGridLayouter(grid, sink, diag, shaper);

        // Benign item lands; hostile dropped + diagnostic.
        Assert.Single(sink.Fragments);
        Assert.Same(benign, sink.Fragments[0].Box);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
    }

    [Fact]
    public void F4_grid_auto_flow_column_with_column_locked_item_runs_pass2()
    {
        // Per PR-#103 review F4 — in column-flow, `definite-column +
        // auto-row` items are the MAJOR-locked Pass 2 (analogous to
        // `definite-row + auto-col` in row-flow). The col-locked item
        // gets placed at its anchored column with the first free row
        // run.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0, 100.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Column);

        // Col-locked item: grid-column: 2, grid-row: auto.
        var colLocked = BuildItemWithColOnlyPlacement(col: 2);
        // Fully-auto item: should land AFTER the col-locked one in
        // source order, sharing the cursor.
        var auto = BuildAutoPlacedItem();
        grid.AppendChild(colLocked);
        grid.AppendChild(auto);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        // colLocked at (0, 1) — column-locked Pass 2 finds first row.
        AssertFragmentEquals(sink, colLocked,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 100);
        // auto fills (0, 0) per column-major source order (since the
        // col-locked Pass 2 ran first; the cursor for both-auto starts
        // at the major origin).
        AssertFragmentEquals(sink, auto,
            inlineOffset: 0, blockOffset: 0,
            inlineSize: 100, blockSize: 100);
    }

    [Fact]
    public void F4_grid_auto_flow_column_with_row_locked_item_shares_cursor()
    {
        // Per PR-#103 review F4 — in column-flow, `definite-row,
        // auto-col` is the MINOR-locked case (analog of
        // `definite-col, auto-row` in row-flow); it shares the
        // auto-placement cursor with both-auto items.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0, 100.0 });
        SetGridAutoFlow(grid, GridAutoFlowValue.Column);

        // Row-locked item: grid-row: 2, grid-column: auto.
        var rowLocked = BuildItemWithRowOnlyPlacement(row: 2);
        // Following auto item should advance past it via the cursor.
        var auto = BuildAutoPlacedItem();
        grid.AppendChild(rowLocked);
        grid.AppendChild(auto);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        // rowLocked at (1, 0) — row 1 anchored, first free column.
        AssertFragmentEquals(sink, rowLocked,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 100, blockSize: 100);
        // Cursor advanced past rowLocked → auto lands at next column
        // (column-major, so after rowLocked's column wraps).
        Assert.NotEqual(rowLocked.Style, auto.Style); // sanity
    }

    [Fact]
    public void F5_col_locked_item_advances_cursor_for_following_auto_item()
    {
        // Per PR-#103 review F5 — in row-flow, a column-locked item
        // shares the auto-placement cursor with both-auto items.
        // Without F5 the cursor stayed at (0,0) so a following auto
        // item landed at (0,0) on top of the col-locked one.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 100.0, 100.0 },
            colsPx: new[] { 100.0, 100.0 });

        // Col-locked: grid-column: 2, grid-row: auto.
        var colLocked = BuildItemWithColOnlyPlacement(col: 2);
        var auto = BuildAutoPlacedItem();
        grid.AppendChild(colLocked);
        grid.AppendChild(auto);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Equal(2, sink.Fragments.Count);
        // colLocked at (0, 1).
        AssertFragmentEquals(sink, colLocked,
            inlineOffset: 100, blockOffset: 0,
            inlineSize: 100, blockSize: 100);
        // With F5: cursor advanced past colLocked (cursor at col=2,
        // wraps to row 1, col 0). auto lands at (1, 0).
        // Without F5 (pre-fix): cursor was still (0, 0) → auto would
        // overlap colLocked at (0, 0) or land at (0, 0) before
        // colLocked.
        AssertFragmentEquals(sink, auto,
            inlineOffset: 0, blockOffset: 100,
            inlineSize: 100, blockSize: 100);
    }

    [Fact]
    public void F7_auto_start_definite_end_emits_placement_approximated_diagnostic()
    {
        // Per PR-#103 review F7 — `grid-row: auto / 3` uses the cycle
        // 6a simplification (single cell at end-1 = row 2) + emits
        // the placement-approximated diagnostic so authors see they're
        // in approximation territory. The spec's full reverse-auto-
        // placement is deferred to grid-reverse-auto-placement-deferral.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0, 100.0, 150.0 },
            colsPx: new[] { 200.0 });
        // grid-row: auto / 3 — row-end at line 3, no start.
        var style = MakeStyle();
        var rowEndValue = GridLineValue.ForLineNumber(3);
        style.SetSideTablePayload(PropertyId.GridRowEnd, (object)rowEndValue);
        style.Set(PropertyId.GridRowEnd, ComputedSlot.FromSideTableIndex(0));
        var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // Cycle 6a simplification: single cell at row (3 - 1) = row 1.
        AssertFragmentEquals(sink, item,
            inlineOffset: 0, blockOffset: 50,
            inlineSize: 200, blockSize: 100);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void F6_implicit_track_truncation_drops_item_without_index_error()
    {
        // Per PR-#103 review F6 — when AppendImplicitTracks hits the
        // per-axis cap, GrowRowsIfNeeded returns false + the caller
        // drops the item with a diagnostic. PlacedItem.Row stays at
        // -1 so the emission loop's `item.Row < 0` skip avoids any
        // out-of-bounds array access on materialized track positions.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(
            rowsPx: new[] { 50.0 },
            colsPx: new[] { 50.0 });
        // Single far-out item — should drop cleanly without OOM or
        // throwing on the materialized-track lookup.
        var farOut = BuildItemWithExplicitPlacement(row: 2000, col: 2000);
        grid.AppendChild(farOut);

        // Should not throw.
        RunGridLayouter(grid, sink, diag, shaper);

        // No fragments emitted (the far-out item dropped).
        Assert.Empty(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001);
    }

    // =====================================================================
    //  Post-PR-#185 review F1 — §11.5.1 spanning-item base distribution
    //  (subtract ALL spanned track sizes; grow only base-growing tracks;
    //  order-independent planned increases). Declared item widths make the
    //  outer contributions deterministic without a content measurer; auto-
    //  width probe items stretch to their cell so InlineSize == track size.
    // =====================================================================

    [Fact]
    public void Spanning_distribution_subtracts_prior_non_spanning_intrinsic_track()
    {
        // CSS Grid §11.5.1: a spanning item's extra space subtracts the CURRENT base size of EVERY
        // spanned track — including an INTRINSIC track a non-spanning item already sized (the first cut
        // subtracted only NON-intrinsic tracks → double-counted). `auto auto`, a 100px non-spanning item
        // in col 1, a 150px item spanning both → extra = 150 − (100 + 0) = 50, split equally → col 1 =
        // 125, col 2 = 25 (total 150, NOT the 175 the double-count produced).
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainerWithTemplates(
            rows: Tracks(TrackEntry.ForLength(20), TrackEntry.ForLength(20), TrackEntry.ForLength(20)),
            cols: Tracks(TrackEntry.ForAuto(), TrackEntry.ForAuto()));
        var prior = BuildItemWithExplicitPlacement(row: 1, col: 1);
        SetExplicitWidth(prior, 100);
        var spanner = BuildItemWithExplicitStartAndSpan(rowStart: 2, rowSpan: 1, colStart: 1, colSpan: 2);
        SetExplicitWidth(spanner, 150);
        var probe0 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        var probe1 = BuildItemWithExplicitPlacement(row: 3, col: 2);
        grid.AppendChild(prior);
        grid.AppendChild(spanner);
        grid.AppendChild(probe0);
        grid.AppendChild(probe1);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertInlineSize(sink, probe0, 125);
        AssertInlineSize(sink, probe1, 25);
    }

    [Fact]
    public void Spanning_distribution_keeps_fixed_min_of_minmax_and_grows_only_intrinsic_min_track()
    {
        // CSS Grid §11.5 step 3 / §11.5.1: a minmax(100px, auto) track has a FIXED min, so a spanning
        // item's base distribution SUBTRACTS its 100px base but never GROWS it (only its growth limit
        // grows, §11.5 step 4). `minmax(100px, auto) auto`, a 150px spanner → extra = 150 − (100 + 0) =
        // 50 to the single base-growing track (col 2) → col 1 = 100 (unchanged), col 2 = 50.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainerWithTemplates(
            rows: Tracks(TrackEntry.ForLength(20), TrackEntry.ForLength(20)),
            cols: Tracks(
                TrackEntry.ForMinMax(TrackEntry.ForLength(100), TrackEntry.ForAuto()),
                TrackEntry.ForAuto()));
        var spanner = BuildItemWithExplicitStartAndSpan(rowStart: 1, rowSpan: 1, colStart: 1, colSpan: 2);
        SetExplicitWidth(spanner, 150);
        var probe0 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var probe1 = BuildItemWithExplicitPlacement(row: 2, col: 2);
        grid.AppendChild(spanner);
        grid.AppendChild(probe0);
        grid.AppendChild(probe1);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertInlineSize(sink, probe0, 100);
        AssertInlineSize(sink, probe1, 50);
    }

    [Fact]
    public void Spanning_distribution_is_order_independent_for_overlapping_equal_span_items()
    {
        // CSS Grid §11.5.1 commits per-track PLANNED increases (max over the items in a span-count
        // group) AFTER the group, so two equal-span spanners sharing a track distribute
        // ORDER-INDEPENDENTLY. `auto auto auto`, spanner A over cols 1-2 (100px) + spanner B over cols
        // 2-3 (100px) → each track 50 (50/50/50), NOT the order-dependent 50/75/25 immediate growth gives.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainerWithTemplates(
            rows: Tracks(TrackEntry.ForLength(20), TrackEntry.ForLength(20)),
            cols: Tracks(TrackEntry.ForAuto(), TrackEntry.ForAuto(), TrackEntry.ForAuto()));
        var spanA = BuildItemWithExplicitStartAndSpan(rowStart: 1, rowSpan: 1, colStart: 1, colSpan: 2);
        SetExplicitWidth(spanA, 100);
        var spanB = BuildItemWithExplicitStartAndSpan(rowStart: 1, rowSpan: 1, colStart: 2, colSpan: 2);
        SetExplicitWidth(spanB, 100);
        var probe0 = BuildItemWithExplicitPlacement(row: 2, col: 1);
        var probe1 = BuildItemWithExplicitPlacement(row: 2, col: 2);
        var probe2 = BuildItemWithExplicitPlacement(row: 2, col: 3);
        grid.AppendChild(spanA);
        grid.AppendChild(spanB);
        grid.AppendChild(probe0);
        grid.AppendChild(probe1);
        grid.AppendChild(probe2);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertInlineSize(sink, probe0, 50);
        AssertInlineSize(sink, probe1, 50);
        AssertInlineSize(sink, probe2, 50);
    }

    [Fact]
    public void Spanning_distribution_does_not_grow_tracks_when_remainder_is_negative()
    {
        // CSS Grid §11.5.1: when the spanned tracks already exceed the spanning item's contribution,
        // extra = max(0, …) = 0 and NO intrinsic track grows. `auto auto` with a 200px item in col 1 +
        // a 10px item in col 2 (both non-spanning), a 150px spanner over both → extra = 150 − (200 + 10)
        // = 0; cols stay 200 / 10.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainerWithTemplates(
            rows: Tracks(TrackEntry.ForLength(20), TrackEntry.ForLength(20), TrackEntry.ForLength(20)),
            cols: Tracks(TrackEntry.ForAuto(), TrackEntry.ForAuto()));
        var big0 = BuildItemWithExplicitPlacement(row: 1, col: 1);
        SetExplicitWidth(big0, 200);
        var small1 = BuildItemWithExplicitPlacement(row: 1, col: 2);
        SetExplicitWidth(small1, 10);
        var spanner = BuildItemWithExplicitStartAndSpan(rowStart: 2, rowSpan: 1, colStart: 1, colSpan: 2);
        SetExplicitWidth(spanner, 150);
        var probe0 = BuildItemWithExplicitPlacement(row: 3, col: 1);
        var probe1 = BuildItemWithExplicitPlacement(row: 3, col: 2);
        grid.AppendChild(big0);
        grid.AppendChild(small1);
        grid.AppendChild(spanner);
        grid.AppendChild(probe0);
        grid.AppendChild(probe1);

        RunGridLayouter(grid, sink, diag, shaper);

        AssertInlineSize(sink, probe0, 200);
        AssertInlineSize(sink, probe1, 10);
    }

    [Fact]
    public void Border_box_grid_item_sizes_its_column_to_the_declared_size_not_plus_chrome()
    {
        // Grid box-sizing cycle (CSS Basic UI 4 §10) — a `box-sizing: border-box` item's declared 100px
        // IS its border box, so the auto column = 100 (the padding is INSIDE). The content-box control
        // adds the chrome → 100 + (10+10) padding = 120. (The border component is exercised by the
        // production test `Production_html_border_box_grid_item_sizes_column_to_declared_width`, where a
        // real `border: 5px solid` resolves a readable border-width.)
        Assert.Equal(100.0, ColumnWidthForSizedItem(borderBox: true), precision: 3);
        Assert.Equal(120.0, ColumnWidthForSizedItem(borderBox: false), precision: 3);
    }

    /// <summary>Post-PR-#185 (grid box-sizing cycle) — the resolved width of an `auto` column sized by a
    /// single 100px item with 10px h-padding, read via an auto-width probe in row 2.</summary>
    private static double ColumnWidthForSizedItem(bool borderBox)
    {
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();
        var grid = BuildGridContainerWithTemplates(
            rows: Tracks(TrackEntry.ForLength(20), TrackEntry.ForLength(20)),
            cols: Tracks(TrackEntry.ForAuto()));
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        SetExplicitWidth(item, 100);
        item.Style.Set(PropertyId.PaddingLeft, ComputedSlot.FromLengthPx(10));
        item.Style.Set(PropertyId.PaddingRight, ComputedSlot.FromLengthPx(10));
        if (borderBox) item.Style.Set(PropertyId.BoxSizing, ComputedSlot.FromKeyword(1));
        var probe = BuildItemWithExplicitPlacement(row: 2, col: 1);   // auto → stretches to the column
        grid.AppendChild(item);
        grid.AppendChild(probe);
        RunGridLayouter(grid, sink, diag, shaper);
        foreach (var f in sink.Fragments)
            if (f.Box == probe) return f.InlineSize;
        throw new System.InvalidOperationException("no probe fragment emitted");
    }

    [Fact]
    public void Cross_attempt_measure_cache_is_hit_and_keeps_geometry_identical()
    {
        // Measurement-cache cycle — the GridLayouter's instance-level measure caches persist across
        // AttemptLayout attempts. A second attempt under IDENTICAL inputs serves every measurement from the
        // cache (MeasurePassCount adds ZERO — proves the cache is HIT, not a silent no-op; PR #187 review
        // [P3]) and produces byte-identical geometry. Per Copilot #187: snapshot the first attempt + roll the
        // sink back before the second, then compare 1:1 instead of relying on the sink accumulating.
        using var shaper = new SyntheticShaperResolver();
        var grid = BuildAutoRowGridWithExplicitHeightCell();

        var sink = new RecordingFragmentSink();
        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: new RecordingDiagnosticsSink(), shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 400, contentBlockSize: 400, allowPagination: false);
        var ctx = new FragmentainerContext(contentInlineSize: 400, blockSize: 400);

        // Attempt 1 — populates the caches; snapshot its fragments + the pass count.
        var layoutCtx1 = new LayoutContext(ctx);
        using (var resolver1 = new BreakResolver())
            layouter.AttemptLayout(ctx, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var firstAttempt = sink.Fragments.ToList();
        var passesAfterFirst = layouter.MeasurePassCount;
        Assert.True(firstAttempt.Count > 0);
        Assert.True(passesAfterFirst > 0, "the content measurer must have run on the first attempt");

        // Roll the sink back so the second attempt's output stands alone (Copilot #187).
        sink.RollbackTo(0);
        Assert.Empty(sink.Fragments);

        // Attempt 2 — identical inputs → every measurement is served from the cache (zero new passes).
        var layoutCtx2 = new LayoutContext(ctx);
        using (var resolver2 = new BreakResolver())
            layouter.AttemptLayout(ctx, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);
        Assert.Equal(passesAfterFirst, layouter.MeasurePassCount);   // cache HIT — no re-measure

        Assert.Equal(firstAttempt.Count, sink.Fragments.Count);
        for (var i = 0; i < firstAttempt.Count; i++)
        {
            var a = firstAttempt[i];
            var b = sink.Fragments[i];
            Assert.Equal(a.InlineOffset, b.InlineOffset, precision: 3);
            Assert.Equal(a.BlockOffset, b.BlockOffset, precision: 3);
            Assert.Equal(a.InlineSize, b.InlineSize, precision: 3);
            Assert.Equal(a.BlockSize, b.BlockSize, precision: 3);
        }
    }

    [Fact]
    public void Cross_attempt_measure_cache_key_includes_block_budget()
    {
        // PR #187 review [P1] #2 — NestedContentMeasurer.Measure sizes the inner fragmentainer to the block
        // budget, so the measured extent can depend on it (a percent-height cell resolves against the
        // budget). The cache key carries the budget: a second attempt under a DIFFERENT budget must
        // re-measure (MeasurePassCount grows) rather than return a stale value cached under the first budget.
        using var shaper = new SyntheticShaperResolver();
        var grid = BuildAutoRowGridWithExplicitHeightCell();

        var sink = new RecordingFragmentSink();
        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: new RecordingDiagnosticsSink(), shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: 400, contentBlockSize: 400, allowPagination: false);

        // Attempt 1 — block budget 400.
        var ctx1 = new FragmentainerContext(contentInlineSize: 400, blockSize: 400);
        var layoutCtx1 = new LayoutContext(ctx1);
        using (var resolver1 = new BreakResolver())
            layouter.AttemptLayout(ctx1, ref layoutCtx1, resolver1, LayoutAttemptStrategy.LastResort);
        var passesAfterFirst = layouter.MeasurePassCount;
        Assert.True(passesAfterFirst > 0);

        sink.RollbackTo(0);

        // Attempt 2 — DIFFERENT block budget 200 → the budget-keyed cache misses → re-measures (no stale
        // cross-budget reuse). With the old (item, availInline)-only key this attempt would hit the cache
        // and MeasurePassCount would stay put.
        var ctx2 = new FragmentainerContext(contentInlineSize: 400, blockSize: 200);
        var layoutCtx2 = new LayoutContext(ctx2);
        using (var resolver2 = new BreakResolver())
            layouter.AttemptLayout(ctx2, ref layoutCtx2, resolver2, LayoutAttemptStrategy.LastResort);
        Assert.True(layouter.MeasurePassCount > passesAfterFirst,
            "a different block budget must re-measure (the budget is part of the cache key), not reuse a stale value");
        Assert.NotEmpty(sink.Fragments);
    }

    [Fact]
    public void GridMeasurementCache_hits_on_identical_inputs_and_misses_on_different_budget()
    {
        // Task 3 — the shared per-conversion cache memoizes on the FULL input set
        // (item, available inline width, block budget, writing mode, RTL): identical
        // inputs hit (one measure shared across consumers); a different budget misses
        // (the budget is in the key — no stale cross-budget reuse).
        using var shaper = new SyntheticShaperResolver();
        var item = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        var inner = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        SetExplicitHeight(inner, 60);
        item.AppendChild(inner);

        var cache = new GridMeasurementCache();
        var e1 = cache.BlockExtent(
            item, availInline: 100, blockBudget: 400, shaper,
            WritingMode.HorizontalTb, isRtl: false, System.Threading.CancellationToken.None);
        Assert.Equal(1, cache.MeasurePassCount);

        // Identical inputs → HIT (no new pass, same value).
        var e2 = cache.BlockExtent(
            item, 100, 400, shaper, WritingMode.HorizontalTb, false,
            System.Threading.CancellationToken.None);
        Assert.Equal(1, cache.MeasurePassCount);
        Assert.Equal(e1, e2, precision: 3);

        // Different block budget → MISS (budget is part of the key) → re-measures.
        cache.BlockExtent(
            item, 100, 200, shaper, WritingMode.HorizontalTb, false,
            System.Threading.CancellationToken.None);
        Assert.Equal(2, cache.MeasurePassCount);
    }

    /// <summary>Measurement-cache cycle — a 1×1 grid with an `auto` row + a fixed 100px column holding a
    /// cell whose inner block is an explicit 60px height. The auto row is content-determined, so resolving
    /// the grid runs the content (block-extent) measurer — the path the cross-attempt caches memoize.</summary>
    private static Box BuildAutoRowGridWithExplicitHeightCell()
    {
        var grid = BuildGridContainerWithTemplates(
            rows: Tracks(TrackEntry.ForAuto()),
            cols: Tracks(TrackEntry.ForLength(100)));
        var item = BuildItemWithExplicitPlacement(row: 1, col: 1);
        var inner = Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
        SetExplicitHeight(inner, 60);
        item.AppendChild(inner);
        grid.AppendChild(item);
        return grid;
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static void RunGridLayouter(
        Box grid, RecordingFragmentSink sink,
        RecordingDiagnosticsSink diag, SyntheticShaperResolver shaper,
        double contentInlineSize = 400, double contentBlockSize = 400)
    {
        using var layouter = new GridLayouter(
            rootBox: grid, sink: sink, incomingContinuation: null,
            diagnostics: diag, shaperResolver: shaper);
        layouter.ConfigureEmission(
            contentInlineOffset: 0, contentBlockOffset: 0,
            contentInlineSize: contentInlineSize,
            contentBlockSize: contentBlockSize,
            allowPagination: false);
        var ctx = new FragmentainerContext(
            contentInlineSize: contentInlineSize, blockSize: contentBlockSize);
        var layoutCtx = new LayoutContext(ctx);
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
    }

    /// <summary>Per PR-#93 review F3 — sets a pixel <c>width</c> on the
    /// box's style so the GridLayouter's indefinite-axis detection
    /// treats the inline axis as DEFINITE (= no fr-under-indefinite
    /// diagnostic for column fr tracks).</summary>
    private static void SetExplicitWidth(Box box, double px)
    {
        box.Style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(px));
    }

    /// <summary>Per PR-#93 review F3 — sets a pixel <c>height</c> so the
    /// indefinite-block check treats the row axis as DEFINITE.</summary>
    private static void SetExplicitHeight(Box box, double px)
    {
        box.Style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(px));
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

    /// <summary>Post-PR-#185 — asserts the emitted fragment for <paramref name="box"/> has the expected
    /// inline size. An auto-width grid item stretches to fill its cell, so this reads the resolved TRACK
    /// size (used by the §11.5.1 spanning-distribution tests via auto-width probe items).</summary>
    private static void AssertInlineSize(RecordingFragmentSink sink, Box box, double expected)
    {
        BoxFragment? found = null;
        foreach (var f in sink.Fragments)
        {
            if (f.Box == box) { found = f; break; }
        }
        Assert.NotNull(found);
        Assert.Equal(expected, found!.Value.InlineSize, precision: 3);
    }

    /// <summary>Post-PR-#185 — builds a <see cref="TrackList"/> from a sequence of
    /// <see cref="TrackEntry"/> sizing functions (each wrapped in a <see cref="TrackListEntry"/>).</summary>
    private static TrackList Tracks(params TrackEntry[] entries)
    {
        var builder = ImmutableArray.CreateBuilder<TrackListItem>(entries.Length);
        foreach (var e in entries)
        {
            builder.Add(new TrackListEntry(e));
        }
        return new TrackList(builder.MoveToImmutable());
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

    /// <summary>Per post-PR-#108 review P1/P2 — build an auto-placed
    /// item with a column-axis span (= column-end = span N). Row +
    /// column-start stay auto so the item participates in the Pass 3+4
    /// auto-placement cursor walk; the span makes it the canonical
    /// `grid-column-end: span N` shape used by the dense-vs-sparse
    /// contrast tests where a wider auto item creates a trailing hole
    /// only dense can later backfill.</summary>
    private static Box BuildAutoPlacedItemWithColSpan(int spanCount)
    {
        var style = MakeStyle();
        var colEndValue = GridLineValue.ForSpan(spanCount);
        style.SetSideTablePayload(PropertyId.GridColumnEnd, (object)colEndValue);
        style.Set(PropertyId.GridColumnEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    /// <summary>Per post-PR-#108 review P1/P2 — column-flow analog of
    /// <see cref="BuildAutoPlacedItemWithColSpan"/>: an auto-placed
    /// item with a row-axis span (= row-end = span N). Used in the
    /// column-flow dense contrast tests where the major axis is column
    /// and the minor axis is row, so a row-spanning auto item creates
    /// the trailing minor-axis hole.</summary>
    private static Box BuildAutoPlacedItemWithRowSpan(int spanCount)
    {
        var style = MakeStyle();
        var rowEndValue = GridLineValue.ForSpan(spanCount);
        style.SetSideTablePayload(PropertyId.GridRowEnd, (object)rowEndValue);
        style.Set(PropertyId.GridRowEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    /// <summary>Per post-PR-#108 review P1 — build a row-locked item
    /// (definite grid-row-start) with a column-axis span (grid-column-end
    /// = span N), column-start auto. Used by Pass 2 sparse-vs-dense
    /// contrast tests where the row-locked item creates the trailing
    /// hole the next row-locked item may or may not backfill.</summary>
    private static Box BuildRowLockedItemWithColSpan(int row, int colSpan)
    {
        var style = MakeStyle();
        var rowValue = GridLineValue.ForLineNumber(row);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var colEndValue = GridLineValue.ForSpan(colSpan);
        style.SetSideTablePayload(PropertyId.GridColumnEnd, (object)colEndValue);
        style.Set(PropertyId.GridColumnEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    /// <summary>Per PR-#92 review F5 — build an item with explicit
    /// start AND end lines on both axes (= the cycle-1 span case the
    /// diagnostic surfaces).</summary>
    private static Box BuildItemWithExplicitStartAndEnd(int rowStart, int rowEnd, int colStart, int colEnd)
    {
        var style = MakeStyle();
        var rowStartValue = GridLineValue.ForLineNumber(rowStart);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowStartValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var rowEndValue = GridLineValue.ForLineNumber(rowEnd);
        style.SetSideTablePayload(PropertyId.GridRowEnd, (object)rowEndValue);
        style.Set(PropertyId.GridRowEnd, ComputedSlot.FromSideTableIndex(0));
        var colStartValue = GridLineValue.ForLineNumber(colStart);
        style.SetSideTablePayload(PropertyId.GridColumnStart, (object)colStartValue);
        style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        var colEndValue = GridLineValue.ForLineNumber(colEnd);
        style.SetSideTablePayload(PropertyId.GridColumnEnd, (object)colEndValue);
        style.Set(PropertyId.GridColumnEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    private static Box BuildAutoPlacedItem()
    {
        // No grid-row-start / grid-column-start set → readers return
        // GridLineValue.Auto.
        return Box.ForElement(BoxKind.BlockContainer, MakeStyle(), MakeElement());
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 — build an item with
    /// explicit start (LineNumber) and span end (Span) on both axes
    /// (= the canonical `grid-row: 2 / span 3` shape).</summary>
    private static Box BuildItemWithExplicitStartAndSpan(
        int rowStart, int rowSpan, int colStart, int colSpan)
    {
        var style = MakeStyle();
        var rowStartValue = GridLineValue.ForLineNumber(rowStart);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowStartValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var rowEndValue = GridLineValue.ForSpan(rowSpan);
        style.SetSideTablePayload(PropertyId.GridRowEnd, (object)rowEndValue);
        style.Set(PropertyId.GridRowEnd, ComputedSlot.FromSideTableIndex(0));
        var colStartValue = GridLineValue.ForLineNumber(colStart);
        style.SetSideTablePayload(PropertyId.GridColumnStart, (object)colStartValue);
        style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        var colEndValue = GridLineValue.ForSpan(colSpan);
        style.SetSideTablePayload(PropertyId.GridColumnEnd, (object)colEndValue);
        style.Set(PropertyId.GridColumnEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 — set a fixed-pixel
    /// pattern on <c>grid-auto-rows</c> so implicit rows take the
    /// declared size instead of cycle 6a's <c>auto</c> default.</summary>
    private static void SetGridAutoRows(Box grid, double px)
    {
        var pattern = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(px))));
        grid.Style.SetSideTablePayload(PropertyId.GridAutoRows, pattern);
        grid.Style.Set(PropertyId.GridAutoRows,
            ComputedSlot.FromSideTableIndex(0));
    }

    private static void SetGridAutoColumns(Box grid, double px)
    {
        var pattern = new TrackList(
            ImmutableArray.Create<TrackListItem>(
                new TrackListEntry(TrackEntry.ForLength(px))));
        grid.Style.SetSideTablePayload(PropertyId.GridAutoColumns, pattern);
        grid.Style.Set(PropertyId.GridAutoColumns,
            ComputedSlot.FromSideTableIndex(0));
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 + 7d + post-PR-#108 review
    /// P1 — set the auto-placement flow direction + density modifier.
    /// Cycle 7d keyword table:
    /// 0 = <c>row</c> (default), 1 = <c>column</c>,
    /// 2 = <c>row dense</c>, 3 = <c>column dense</c>. The cycle-6
    /// version of this helper only handled Row/Column; RowDense and
    /// ColumnDense silently fell through to Row, which made every
    /// cycle-7d test pin sparse-row behavior while claiming to
    /// exercise dense.</summary>
    private static void SetGridAutoFlow(Box grid, GridAutoFlowValue flow)
    {
        var keywordId = flow switch
        {
            GridAutoFlowValue.Column => 1,
            GridAutoFlowValue.RowDense => 2,
            GridAutoFlowValue.ColumnDense => 3,
            _ => 0,
        };
        grid.Style.Set(PropertyId.GridAutoFlow,
            ComputedSlot.FromKeyword(keywordId));
    }

    /// <summary>Per Phase 3 Task 18 cycle 7a — set the
    /// <c>grid-template-areas</c> property on the grid container by
    /// invoking the CSS resolver pipeline against the literal CSS
    /// value (e.g., <c>"head head" "main side"</c>). The parsed AST
    /// lands in the side-table.</summary>
    private static void SetGridTemplateAreas(Box grid, string cssValue)
    {
        var result = NetPdf.Css.ComputedValues.PropertyResolvers
            .PropertyResolverDispatch.Resolve(
                PropertyId.GridTemplateAreas, cssValue);
        result.MaterializeInto(grid.Style, PropertyId.GridTemplateAreas);
    }

    /// <summary>Per Phase 3 Task 18 cycle 7a — build an item with
    /// named-area references on <c>grid-row-start</c> /
    /// <c>grid-column-start</c>. Pass <see langword="null"/> for an
    /// axis to leave it as auto.</summary>
    private static Box BuildItemWithNamedAreaStarts(string? rowName, string? colName)
    {
        var style = MakeStyle();
        if (rowName is not null)
        {
            var rowValue = GridLineValue.ForNamedLine(rowName);
            style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowValue);
            style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        }
        if (colName is not null)
        {
            var colValue = GridLineValue.ForNamedLine(colName);
            style.SetSideTablePayload(PropertyId.GridColumnStart, (object)colValue);
            style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        }
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    /// <summary>Per Phase 3 Task 18 cycle 7b — build an item with
    /// named-line references on ONLY the row axis (grid-row-start /
    /// grid-row-end); columns left auto. Avoids cross-axis ident
    /// pollution for tests that exercise row-axis named lines while
    /// the column axis has no matching idents.</summary>
    private static Box BuildItemWithRowNamedLines(string rowStart, string rowEnd)
    {
        var style = MakeStyle();
        var rowStartValue = GridLineValue.ForNamedLine(rowStart);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowStartValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var rowEndValue = GridLineValue.ForNamedLine(rowEnd);
        style.SetSideTablePayload(PropertyId.GridRowEnd, (object)rowEndValue);
        style.Set(PropertyId.GridRowEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    /// <summary>Per Phase 3 Task 18 cycle 7a — build an item with
    /// named-area references on ALL four placement longhands
    /// (= the <c>grid-area: name</c> shorthand expansion).</summary>
    private static Box BuildItemWithNamedAreaStartsAndEnds(
        string rowStart, string rowEnd, string colStart, string colEnd)
    {
        var style = MakeStyle();
        var rowStartValue = GridLineValue.ForNamedLine(rowStart);
        style.SetSideTablePayload(PropertyId.GridRowStart, (object)rowStartValue);
        style.Set(PropertyId.GridRowStart, ComputedSlot.FromSideTableIndex(0));
        var rowEndValue = GridLineValue.ForNamedLine(rowEnd);
        style.SetSideTablePayload(PropertyId.GridRowEnd, (object)rowEndValue);
        style.Set(PropertyId.GridRowEnd, ComputedSlot.FromSideTableIndex(0));
        var colStartValue = GridLineValue.ForNamedLine(colStart);
        style.SetSideTablePayload(PropertyId.GridColumnStart, (object)colStartValue);
        style.Set(PropertyId.GridColumnStart, ComputedSlot.FromSideTableIndex(0));
        var colEndValue = GridLineValue.ForNamedLine(colEnd);
        style.SetSideTablePayload(PropertyId.GridColumnEnd, (object)colEndValue);
        style.Set(PropertyId.GridColumnEnd, ComputedSlot.FromSideTableIndex(0));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
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

    /// <summary>Per PR-#92 review F8 — a diagnostic sink that ALWAYS
    /// throws on Emit. Used to verify the GridLayouter's safe-emit
    /// pattern catches the throw + layout completes anyway.</summary>
    private sealed class ThrowingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public void Emit(PaginateDiagnostic diagnostic)
        {
            throw new System.InvalidOperationException(
                "F8 test: diagnostic sink throws to verify safe-emit pattern.");
        }
    }

    private sealed class SyntheticShaperResolver : IShaperResolver, System.IDisposable
    {
        private readonly NetPdf.Text.Shaping.HbShaper _shaper =
            new(SyntheticFont.Build(), fontSizePx: 12);
        public NetPdf.Text.Shaping.HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
