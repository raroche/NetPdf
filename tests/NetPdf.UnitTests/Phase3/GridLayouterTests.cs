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
    public void Negative_minus_one_is_implicit_track_drops_with_diagnostic()
    {
        // Per PR-#92 review F3 — `-1` = the line AFTER the last track
        // (= line N+1 in a grid with N tracks). Single-cell placement
        // at line N+1 requires an implicit row per §7.5 + cycle 1
        // doesn't support implicit tracks → item drops.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitPlacement(row: -1, col: -1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Empty(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
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
    public void Non_auto_grid_row_end_emits_placement_approximated_diagnostic()
    {
        // Per F5 — `grid-row: 1 / 3` (= span 2 rows) gets shrunk to a
        // single cell at the start line in cycle 1. The diagnostic
        // surfaces the silent area shrink.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0, 200.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitStartAndEnd(rowStart: 1, rowEnd: 3, colStart: 1, colEnd: 1);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        // Item lands at single cell (1, 1) per start lines only.
        AssertFragmentEquals(sink, item, inlineOffset: 0, blockOffset: 0, inlineSize: 50, blockSize: 100);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public void Non_auto_grid_column_end_emits_placement_approximated_diagnostic()
    {
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildGridContainer(rowsPx: new[] { 100.0 }, colsPx: new[] { 50.0, 150.0 });
        var item = BuildItemWithExplicitStartAndEnd(rowStart: 1, rowEnd: 1, colStart: 1, colEnd: 3);
        grid.AppendChild(item);

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Single(sink.Fragments);
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    // =====================================================================
    //  PR-#92 review F6 — empty grid emits per-item drop diagnostic
    // =====================================================================

    [Fact]
    public void Empty_grid_template_with_children_emits_implicit_track_diagnostic_per_item()
    {
        // Per F6 — pre-fix the 0-track early-return silently dropped
        // children; post-fix each grid-item child gets a diagnostic.
        var sink = new RecordingFragmentSink();
        var diag = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        var grid = BuildEmptyGridContainer();
        grid.AppendChild(BuildAutoPlacedItem());
        grid.AppendChild(BuildAutoPlacedItem());
        grid.AppendChild(BuildAutoPlacedItem());

        RunGridLayouter(grid, sink, diag, shaper);

        Assert.Empty(sink.Fragments);
        var implicitDiagnostics = diag.Diagnostics.FindAll(d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001);
        Assert.Equal(3, implicitDiagnostics.Count);
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
