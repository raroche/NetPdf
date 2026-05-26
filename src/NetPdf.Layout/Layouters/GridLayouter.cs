// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 17 cycle 1 (Hello World) — the grid layouter.
/// Lays out the direct children of a <see cref="BoxKind.GridContainer"/> /
/// <see cref="BoxKind.InlineGridContainer"/> as grid items per CSS
/// Grid Layout L1.
///
/// <para><b>Cycle 1 + 2 scope</b>:</para>
/// <list type="bullet">
///   <item><b>Track kinds supported</b>: <c>&lt;length&gt;</c> (cycle 1)
///   + <c>&lt;flex&gt;</c> (= fr; cycle 2 via CSS Grid §11.7
///   "Find the Size of an fr" algorithm with the spec-correct
///   <c>max(SUM(factors), 1.0)</c> divisor floor — NOT the naive
///   per-track floor or the proportional <c>flex / sumFlex</c>
///   split). Track entries with any other kind (auto, min-content,
///   max-content, minmax, fit-content, repeat) still emit
///   <see cref="PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001"/>
///   + contribute 0 px to the track sum — cycle 3-7 expand
///   coverage.</item>
///   <item><b>fr under indefinite axis</b> (= auto height for rows /
///   auto width for cols) emits
///   <see cref="PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001"/>
///   + collapses fr tracks to 0. Per §11.7 the spec resolves these
///   via intrinsic sizing, which cycle 2 doesn't have yet (cycle 3
///   scope).</item>
///   <item>Integer line-number placement only via
///   <c>grid-{row,column}-{start,end}</c>. Span / named-line forms
///   degrade to auto-placement + emit
///   <see cref="PaginateDiagnosticCodes.LayoutGridPlacementApproximated001"/>.</item>
///   <item>Single-cell items only (no spanning). Each item occupies
///   exactly one cell defined by its (row-start, column-start).</item>
///   <item>Source-order sparse auto-placement per CSS Grid §8.5
///   (= row-major scan with a single cursor). Items with definite
///   placement are placed first; auto-placed items fill remaining
///   cells.</item>
///   <item>No item-level alignment (<c>justify-self</c> /
///   <c>align-self</c>). Items fill their cell (= cell-aligned).</item>
/// </list>
///
/// <para><b>Out of cycle 1 scope</b>:</para>
/// <list type="bullet">
///   <item>fr / intrinsic / minmax / fit-content / repeat track types
///   — cycles 2-4 + 7 expand coverage.</item>
///   <item>Multi-page grid pagination — cycle 5 ships the
///   <c>GridContinuation</c> resume contract.</item>
///   <item><c>span N</c> + auto-flow + named areas — cycles 6-7.</item>
///   <item>Cross-cell alignment (justify-self / align-self) — separate
///   Task sharing the <c>&lt;self-position&gt;</c> decoder with
///   FlexLayouter.</item>
/// </list>
///
/// <para><b>Item placement contract per §8.5 (sparse mode):</b></para>
/// <list type="number">
///   <item>Initialize a 2-D occupancy grid sized to the explicit
///   track count (rows × columns). Per CSS Grid §7.5 the implicit
///   grid auto-generates additional tracks when items exceed the
///   explicit grid; cycle 1 doesn't yet support this — items placed
///   outside the explicit grid drop silently with
///   <see cref="PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001"/>.</item>
///   <item>First pass — place items with BOTH row-start AND
///   column-start as explicit integers. Mark their cells occupied
///   (cells may be claimed by multiple items per §8.5; the painter
///   renders each independently). Cursor stays at (1, 1).</item>
///   <item>Second pass — items locked to a definite ROW (definite
///   row-start, auto column-start). Walk the declared row from
///   column 1 to find the first free cell; place there. Mark cell
///   occupied. Cursor stays at (1, 1).</item>
///   <item>Third pass — items locked to a definite COLUMN (auto
///   row-start, definite column-start). Walk the declared column
///   from row 1 to find the first free cell; place there. Mark
///   occupied. Cursor stays.</item>
///   <item>Fourth pass — items with BOTH auto (= the sparse
///   auto-placement case). Walk from cursor in row-major order
///   (= columns within a row, then advance to next row); place at
///   first free cell. Mark occupied. Update cursor to AFTER this
///   cell (= (cursor.row, cursor.col + 1) wrapping to next row).</item>
/// </list>
///
/// <para>The dispatching <see cref="BlockLayouter"/> has already emitted
/// the grid container's BoxFragment (= the wrapper at its declared
/// border-box size); this layouter emits the per-item content fragments
/// INSIDE that wrapper's content area. The wrapper's content geometry
/// is passed via <see cref="ConfigureEmission"/>.</para>
/// </summary>
internal sealed class GridLayouter : ILayouter, IDisposable
{
    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    private readonly IPaginateDiagnosticsSink? _diagnostics;
    private readonly IShaperResolver? _shaperResolver;

    private double _contentInlineOffset;
    private double _contentBlockOffset;
    private double _contentInlineSize;
    private double _contentBlockSize;
    private bool _emissionConfigured;

    /// <summary>Per Phase 3 Task 17 cycle 1 — when
    /// <see langword="true"/>, <see cref="AttemptLayout"/> would split
    /// rows across pages + emit a <see cref="GridContinuation"/>.
    /// Cycle 1 (Hello World) only ships atomic emission (false);
    /// multi-page pagination is cycle-5 scope. The parameter exists
    /// on <see cref="ConfigureEmission"/> for forward-compat with the
    /// FlexLayouter shape; a true value currently throws.</summary>
    private bool _allowPagination;

    /// <summary>Per Phase 3 Task 17 cycle 1 — one-shot guard so the
    /// unsupported-track-kind diagnostic doesn't spam per-track. Cycle
    /// 5 will add a <c>_incomingGridContinuation</c> field for the
    /// resume contract; for now the constructor rejects any non-null
    /// incoming continuation so we don't carry useless state.</summary>
    private bool _emittedTrackKindDiagnostic;

    /// <summary>Construct a layouter for the grid container
    /// <paramref name="rootBox"/>. The box's <see cref="Box.Kind"/>
    /// MUST be <see cref="BoxKind.GridContainer"/> or
    /// <see cref="BoxKind.InlineGridContainer"/>; otherwise the
    /// constructor throws (= mirrors FlexLayouter's gate).</summary>
    /// <param name="rootBox">The grid container box.</param>
    /// <param name="sink">The same sink the caller uses; per-item
    /// fragments append after the grid wrapper fragment (which the
    /// caller has already emitted).</param>
    /// <param name="incomingContinuation">Per cycle 1 — MUST be
    /// <see langword="null"/>. Multi-page grid splitting via
    /// <see cref="GridContinuation"/> is cycle-5 scope. A non-null
    /// value throws so misrouted continuations surface loudly rather
    /// than silently restarting from row 0.</param>
    /// <param name="diagnostics">Optional diagnostic sink for the
    /// cycle-1 unsupported-feature diagnostics.</param>
    /// <param name="shaperResolver">Optional inline shaper for future
    /// cycles' inline content inside grid items; cycle 1 doesn't yet
    /// route through the inline pass.</param>
    public GridLayouter(
        Box rootBox,
        IBlockFragmentSink sink,
        LayoutContinuation? incomingContinuation = null,
        IPaginateDiagnosticsSink? diagnostics = null,
        IShaperResolver? shaperResolver = null)
    {
        ArgumentNullException.ThrowIfNull(rootBox);
        ArgumentNullException.ThrowIfNull(sink);

        if (rootBox.Kind is not (BoxKind.GridContainer or BoxKind.InlineGridContainer))
        {
            throw new ArgumentException(
                "GridLayouter expects a box with BoxKind.GridContainer or "
                + $"BoxKind.InlineGridContainer; got BoxKind.{rootBox.Kind}. "
                + "The dispatching BlockLayouter's IsGridContainer predicate "
                + "is the guard for this contract — the wrong kind would "
                + "silently emit no items + drop all content, hiding the "
                + "integration bug.",
                nameof(rootBox));
        }

        if (incomingContinuation is not null)
        {
            // Per cycle 1 — only GridContinuation is accepted in
            // principle; cycle 1 ships atomic emission, so even
            // GridContinuation is rejected with a clear error message
            // (cycle 5 ships the resume contract).
            if (incomingContinuation is not GridContinuation)
            {
                throw new ArgumentException(
                    "GridLayouter accepts only GridContinuation; "
                    + $"got {incomingContinuation.GetType().Name}. "
                    + "Misrouted continuation is a layouter-dispatch bug.",
                    nameof(incomingContinuation));
            }
            throw new ArgumentException(
                "GridLayouter cycle 1 ships atomic emission only; multi-page "
                + "grid pagination via GridContinuation is cycle-5 scope. "
                + "A non-null GridContinuation indicates the dispatching "
                + "BlockLayouter prematurely flipped on grid pagination.",
                nameof(incomingContinuation));
        }

        _rootBox = rootBox;
        _sink = sink;
        _diagnostics = diagnostics;
        _shaperResolver = shaperResolver;
    }

    /// <summary>Per cycle 1 — set the grid container's content-box
    /// geometry in the outer fragmentainer's coordinate space. The
    /// dispatching <see cref="BlockLayouter"/> has already emitted the
    /// wrapper fragment; this method tells the layouter WHERE to
    /// anchor the per-item content fragments inside that wrapper's
    /// content area. MUST be called before <see cref="AttemptLayout"/>.
    ///
    /// <para>Mirrors <see cref="FlexLayouter.ConfigureEmission"/>.</para>
    /// </summary>
    /// <param name="contentInlineOffset">Inline-axis offset (CSS px
    /// from the fragmentainer's content-area origin) of the container's
    /// content-box inline-start edge.</param>
    /// <param name="contentBlockOffset">Block-axis offset of the
    /// container's content-box block-start edge.</param>
    /// <param name="contentInlineSize">Container's content-box inline
    /// extent.</param>
    /// <param name="contentBlockSize">Container's content-box block
    /// extent.</param>
    /// <param name="allowPagination">Per Phase 3 Task 17 cycle 1 —
    /// cycle 1 ships atomic emission only; a true value here is
    /// reserved for cycle 5's multi-page grid pagination. Currently
    /// passing true throws at <see cref="AttemptLayout"/> time.</param>
    public void ConfigureEmission(
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize,
        bool allowPagination = false)
    {
        if (!double.IsFinite(contentInlineSize) || contentInlineSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentInlineSize),
                $"contentInlineSize must be finite + positive; got {contentInlineSize}. "
                + "A non-positive content-box inline extent yields degenerate per-track "
                + "sizing; the dispatching BlockLayouter's GridGeometryHelper floors "
                + "this at 1.0 — a value <= 0 means the helper wasn't called.");
        }
        if (!double.IsFinite(contentBlockSize) || contentBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentBlockSize),
                $"contentBlockSize must be finite + positive; got {contentBlockSize}.");
        }
        if (!double.IsFinite(contentInlineOffset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentInlineOffset),
                $"contentInlineOffset must be finite; got {contentInlineOffset}.");
        }
        if (!double.IsFinite(contentBlockOffset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentBlockOffset),
                $"contentBlockOffset must be finite; got {contentBlockOffset}.");
        }

        _contentInlineOffset = contentInlineOffset;
        _contentBlockOffset = contentBlockOffset;
        _contentInlineSize = contentInlineSize;
        _contentBlockSize = contentBlockSize;
        _allowPagination = allowPagination;
        _emissionConfigured = true;
    }

    public LayoutAttemptResult AttemptLayout(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        ArgumentNullException.ThrowIfNull(resolver);

        if (!_emissionConfigured)
        {
            throw new InvalidOperationException(
                "GridLayouter.AttemptLayout invoked without prior "
                + "ConfigureEmission. The dispatching BlockLayouter "
                + "must set the content-box geometry before invoking "
                + "the inner layouter.");
        }
        if (_allowPagination)
        {
            throw new InvalidOperationException(
                "GridLayouter cycle 1 ships atomic emission only; "
                + "allowPagination=true is cycle-5 scope. The dispatching "
                + "BlockLayouter must not flip the gate on yet.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 1 — resolve track sizes from grid-template-rows /
        // grid-template-columns. Cycle 1 supported only Length tracks;
        // cycle 2 adds fr distribution via CSS Grid §11.7 "Find the
        // Size of an fr" algorithm. Other non-Length non-fr kinds
        // (auto / intrinsic / minmax / fit-content / repeat) still
        // contribute 0 + emit a one-shot diagnostic — cycle 3+ widens
        // coverage.
        //
        // The container's content extent on each axis feeds the fr
        // distribution (= leftover = containerSize - sumOfNonFlexBases).
        //
        // Per PR-#93 review F3 — detect indefinite axes (= auto height
        // for rows / auto width for cols) so fr tracks on an
        // indefinite axis emit
        // <see cref="PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001"/>
        // rather than silently collapsing to 0. Per §11.7, fr under
        // indefinite space resolves via intrinsic (= max-content)
        // sizing; cycle 2 doesn't have intrinsic resolution yet
        // (cycle 3), so we surface the approximation. NB: column
        // axis is rarely indefinite in practice since the available
        // inline space is always definite under BlockLayouter dispatch;
        // the diagnostic is primarily a row-axis safety net.
        var heightSlot = _rootBox.Style.Get(PropertyId.Height);
        var widthSlot = _rootBox.Style.Get(PropertyId.Width);
        var isBlockIndefinite =
            heightSlot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        var isInlineIndefinite =
            widthSlot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        var rowSizes = ResolveTrackSizes(
            _rootBox.Style.ReadGridTemplateRows(),
            _contentBlockSize,
            isAxisIndefinite: isBlockIndefinite);
        var colSizes = ResolveTrackSizes(
            _rootBox.Style.ReadGridTemplateColumns(),
            _contentInlineSize,
            isAxisIndefinite: isInlineIndefinite);

        // No explicit tracks → no cells to place into. Cycle 1 doesn't
        // generate implicit tracks (§7.5). Per PR-#92 review F6 — emit
        // an implicit-track diagnostic for EACH grid-item child that
        // gets silently dropped (= matches the per-item diagnostic
        // contract documented in LayoutGridImplicitTrackUnsupported001).
        // Pre-F6 the early return silently swallowed all children.
        if (rowSizes.Count == 0 || colSizes.Count == 0)
        {
            foreach (var child in _rootBox.Children)
            {
                if (!IsGridItem(child)) continue;
                EmitImplicitTrackDiagnostic(child);
            }
            return LayoutAttemptResult.AllDone(cost: 0.0);
        }

        // Step 2 — compute track positions (cumulative sums starting
        // at the content-area origin). Per PR-#92 review F9 — validate
        // that cumulative sums stay finite. Hostile CSS like
        // `grid-template-rows: 1e300px 1e300px` can overflow during
        // accumulation even though each individual track passed the
        // AST-time finite-value check.
        var rowPositions = ComputeTrackPositions(rowSizes, _contentBlockOffset);
        var colPositions = ComputeTrackPositions(colSizes, _contentInlineOffset);
        if (!IsTrackGeometryFinite(rowPositions, rowSizes)
            || !IsTrackGeometryFinite(colPositions, colSizes))
        {
            SafeEmit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001,
                Message: "Grid container's resolved track positions or "
                    + "per-track (position + size) overflowed to a "
                    + "non-finite value (NaN / ±Infinity). Individual "
                    + "tracks were finite at AST-construction time but "
                    + "their cumulative sums overflowed (= probably hostile "
                    + "CSS with very large track lengths). Item emission "
                    + "is skipped to prevent corrupting downstream paint / "
                    + "PDF geometry.",
                Severity: PaginateDiagnosticSeverity.Warning));
            return LayoutAttemptResult.AllDone(cost: 0.0);
        }

        // Step 3 — initialize the 2-D occupancy grid (rowCount × colCount).
        var rowCount = rowSizes.Count;
        var colCount = colSizes.Count;
        var occupancy = new bool[rowCount, colCount];

        // Step 4 — gather + classify items into 4 placement passes.
        // Per CSS Grid §8.5 the algorithm is:
        //   pass 1: both explicit  (placed at declared cell)
        //   pass 2: row-locked     (explicit row, auto column)
        //   pass 3: column-locked  (auto row, explicit column)
        //   pass 4: both auto      (sparse cursor scan)
        // Within each pass items walk in DOM (source) order.
        var placedItems = new List<PlacedItem>(_rootBox.Children.Count);
        foreach (var child in _rootBox.Children)
        {
            if (!IsGridItem(child)) continue;
            // Per PR-#92 review F5 — read both start AND end line
            // values so a non-default end (= author requested a span)
            // can emit LayoutGridPlacementApproximated001. Cycle 1
            // doesn't support span; the item still falls back to
            // single-cell placement at the start line, but the
            // diagnostic surfaces the silent area shrink.
            CheckEndLineForSpanDiagnostic(child.Style, isRow: true);
            CheckEndLineForSpanDiagnostic(child.Style, isRow: false);
            placedItems.Add(new PlacedItem
            {
                Box = child,
                RowSpec = ReadPlacement(child.Style, isRow: true),
                ColSpec = ReadPlacement(child.Style, isRow: false),
                // Per cycle 1 — every item occupies a single cell (no
                // span). Row/Col are 0-based final positions; assigned
                // by the placement loops below.
                Row = -1,
                Col = -1,
            });
        }

        // Pass 1 — both explicit.
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.RowSpec.Kind != PlacementKind.Definite
                || item.ColSpec.Kind != PlacementKind.Definite)
            {
                continue;
            }
            var rowZero = ResolveDefiniteToZeroBased(item.RowSpec.Line, rowCount);
            var colZero = ResolveDefiniteToZeroBased(item.ColSpec.Line, colCount);
            if (rowZero < 0 || rowZero >= rowCount
                || colZero < 0 || colZero >= colCount)
            {
                EmitImplicitTrackDiagnostic(item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            occupancy[rowZero, colZero] = true;
            placedItems[i] = item;
        }

        // Pass 2 — row-locked (definite row, auto column).
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.RowSpec.Kind != PlacementKind.Definite
                || item.ColSpec.Kind != PlacementKind.Auto)
            {
                continue;
            }
            if (item.Row >= 0) continue; // already placed by pass 1
            var rowZero = ResolveDefiniteToZeroBased(item.RowSpec.Line, rowCount);
            if (rowZero < 0 || rowZero >= rowCount)
            {
                EmitImplicitTrackDiagnostic(item.Box);
                continue;
            }
            var colZero = FindFirstFreeColumnInRow(occupancy, rowZero, colCount);
            if (colZero < 0)
            {
                EmitImplicitTrackDiagnostic(item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            occupancy[rowZero, colZero] = true;
            placedItems[i] = item;
        }

        // Pass 3 — Per CSS Grid §8.5 + PR-#92 review F4 — column-locked
        // and both-auto items are processed TOGETHER in source order
        // with a SHARED auto-placement cursor. Pre-F4 these were split
        // into two passes (col-locked first, both-auto second), which
        // reordered items contrary to spec: e.g., children
        // [auto, col-locked-to-col-1] in a 2-col grid would place the
        // col-locked first (at (1,1)) then the auto at (1,2), instead
        // of the spec-correct auto at (1,1) then col-locked at (2,1).
        //
        // Algorithm (sparse mode — the cycle-1 default; dense is cycle 7+):
        //   cursor = (0, 0)
        //   for each remaining unplaced item in DOM/source order:
        //     if column-locked (auto row, definite col):
        //       walk DOWN the declared column starting at cursor.row
        //       until finding a free cell; place there.
        //       (Per §8.5 the cursor doesn't advance for column-locked
        //        items — the cursor's purpose is sparse auto-placement
        //        order, which col-locked items don't participate in.)
        //     if both-auto:
        //       walk row-major from cursor until finding a free cell;
        //       place there. Advance cursor past the placement.
        var cursorRow = 0;
        var cursorCol = 0;
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.Row >= 0) continue; // already placed by pass 1 or 2

            // Items with definite row + auto col are pass-2 work; if we
            // see one here it means pass 2 dropped it (= out-of-range
            // declared row). Don't fall through to auto-placement;
            // those drops were already diagnosed.
            if (item.RowSpec.Kind == PlacementKind.Definite
                && item.ColSpec.Kind == PlacementKind.Auto)
            {
                continue;
            }

            // Column-locked: definite column, auto row.
            if (item.RowSpec.Kind == PlacementKind.Auto
                && item.ColSpec.Kind == PlacementKind.Definite)
            {
                var colZero = ResolveDefiniteToZeroBased(item.ColSpec.Line, colCount);
                if (colZero < 0 || colZero >= colCount)
                {
                    EmitImplicitTrackDiagnostic(item.Box);
                    continue;
                }
                // Walk down the column from cursor.row to find a free cell.
                // Per §8.5 the column-locked item searches from the
                // cursor's CURRENT row (not necessarily row 0).
                var rowZero = FindFirstFreeRowInColumnFrom(
                    occupancy, colZero, rowCount, startRow: cursorRow);
                if (rowZero < 0)
                {
                    EmitImplicitTrackDiagnostic(item.Box);
                    continue;
                }
                item.Row = rowZero;
                item.Col = colZero;
                occupancy[rowZero, colZero] = true;
                placedItems[i] = item;
                // Per §8.5 the cursor doesn't advance for column-locked
                // items.
                continue;
            }

            // Both auto: sparse cursor walk.
            if (item.RowSpec.Kind == PlacementKind.Auto
                && item.ColSpec.Kind == PlacementKind.Auto)
            {
                if (!TryFindNextSparseCell(
                        occupancy, cursorRow, cursorCol, rowCount, colCount,
                        out var foundRow, out var foundCol))
                {
                    EmitImplicitTrackDiagnostic(item.Box);
                    continue;
                }
                item.Row = foundRow;
                item.Col = foundCol;
                occupancy[foundRow, foundCol] = true;
                placedItems[i] = item;
                // Advance cursor past the placed cell.
                cursorRow = foundRow;
                cursorCol = foundCol + 1;
                if (cursorCol >= colCount)
                {
                    cursorRow++;
                    cursorCol = 0;
                }
                continue;
            }

            // Both definite: handled by pass 1. If we reach here, pass 1
            // dropped the item (out-of-range placement). Already diagnosed.
        }

        // Step 5 — emit each placed item as a BoxFragment at its cell's
        // top-left + sized to the cell's extents, THEN per PR-#92
        // review F1 dispatch the item's INNER content via a sub-
        // BlockLayouter so text + nested blocks + replaced content
        // actually render. Pre-F1 only the empty item rectangle
        // survived; real markup like
        //   <div style="display:grid">Invoice total</div>
        // emitted a cell but lost the text. Mirrors the TableLayouter
        // cell-content dispatch pattern (= MeasuringFragmentSink with
        // coordinate translation from inner-cell origin to outer
        // fragmentainer coordinates; sub-BlockLayouter with the item
        // as rootBox).
        foreach (var item in placedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Row < 0 || item.Col < 0) continue;
            var inlineOffset = colPositions[item.Col];
            var blockOffset = rowPositions[item.Row];
            var inlineSize = colSizes[item.Col];
            var blockSize = rowSizes[item.Row];
            _sink.Emit(new BoxFragment(
                Box: item.Box,
                InlineOffset: inlineOffset,
                BlockOffset: blockOffset,
                InlineSize: inlineSize,
                BlockSize: blockSize));

            // F1 — only dispatch inner content when the item has
            // children (text-runs / nested boxes).
            if (item.Box.Children.Count > 0)
            {
                if (inlineSize > 0 && blockSize > 0)
                {
                    DispatchGridItemContents(
                        itemBox: item.Box,
                        cellInlineOffset: inlineOffset,
                        cellBlockOffset: blockOffset,
                        cellInlineSize: inlineSize,
                        cellBlockSize: blockSize,
                        outerFragmentainer: fragmentainer,
                        outerLayout: ref layout,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    // Per PR-#93 review F2 — zero-sized cell.
                    // Sub-BlockLayouter dispatch requires a positive
                    // content extent (= ConfigureEmission validation);
                    // a 0fr column / 0px row would otherwise throw.
                    // Cycle 2 emits the outer item fragment at the
                    // zero-sized geometry + surfaces the inner
                    // content drop via this diagnostic. Cycle 3 will
                    // ship the zero-area inner-layout strategy.
                    SafeEmit(new PaginateDiagnostic(
                        Code: PaginateDiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001,
                        Message: $"Grid item (kind={item.Box.Kind}) at cell "
                            + $"({item.Row + 1}, {item.Col + 1}) resolved to "
                            + $"a zero-sized area ({inlineSize}px × "
                            + $"{blockSize}px). Inner content (= "
                            + $"{item.Box.Children.Count} child boxes) is "
                            + "skipped. A zero-area grid cell is not "
                            + "equivalent to `display: none` per CSS; "
                            + "cycle 3 ships the zero-area inner-layout "
                            + "strategy (= content emits at natural extent "
                            + "+ visually overflows the cell).",
                        Severity: PaginateDiagnosticSeverity.Warning));
                }
            }
        }

        return LayoutAttemptResult.AllDone(cost: 0.0);
    }

    /// <summary>Per PR-#92 review F1 — lay out one grid item's INNER
    /// content (text-runs, nested blocks, etc.) via a sub-BlockLayouter
    /// rooted at <paramref name="itemBox"/>. The inner layouter emits
    /// fragments in cell-relative coordinates; the
    /// <see cref="TranslatingFragmentSink"/> translates them into outer
    /// fragmentainer coordinates so the painter sees absolute positions.
    /// Mirrors the TableLayouter cell-content dispatch pattern
    /// (= MeasuringFragmentSink + sub-BlockLayouter with the cell as
    /// rootBox).
    ///
    /// <para>Cycle 1 contract: items emit at the cell's geometry
    /// (= cellInlineSize × cellBlockSize). Item content that overflows
    /// the cell is emitted at its natural extent (= visual overflow);
    /// cell-internal alignment (justify-self / align-self) is a
    /// separate Task. Inner pagination uses LastResort strategy so the
    /// item content commits in one pass (= cycle 5+ adds proper
    /// multi-page grid pagination).</para></summary>
    private void DispatchGridItemContents(
        Box itemBox,
        double cellInlineOffset,
        double cellBlockOffset,
        double cellInlineSize,
        double cellBlockSize,
        FragmentainerContext outerFragmentainer,
        ref LayoutContext outerLayout,
        CancellationToken cancellationToken)
    {
        // Translating sink: converts inner cell-relative offsets into
        // outer fragmentainer-coordinate fragments before forwarding to
        // the outer sink.
        var translatingSink = new TranslatingFragmentSink(
            outerSink: _sink,
            inlineTranslation: cellInlineOffset,
            blockTranslation: cellBlockOffset);

        // Inner fragmentainer sized to the cell. Per cycle 1 this is
        // atomic — the sub-BlockLayouter commits the item's content in
        // one pass via LastResort; overflow is the spec'd cycle-1
        // behavior (item content exceeding the cell renders at natural
        // extent + visually overflows).
        var innerFragmentainer = new FragmentainerContext(
            contentInlineSize: cellInlineSize,
            blockSize: cellBlockSize);
        var innerLayout = new LayoutContext(innerFragmentainer)
        {
            Diagnostics = outerLayout.Diagnostics,
            WritingMode = outerLayout.WritingMode,
            IsRtl = outerLayout.IsRtl,
        };

        using var itemLayouter = new BlockLayouter(
            rootBox: itemBox,
            sink: translatingSink,
            incomingContinuation: null,
            diagnostics: _diagnostics,
            shaperResolver: _shaperResolver);
        using var itemResolver = new BreakResolver();
        _ = itemLayouter.AttemptLayout(
            innerFragmentainer,
            ref innerLayout,
            itemResolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);
    }

    /// <summary>Per PR-#92 review F1 — a fragment-sink wrapper that
    /// translates inner (cell-relative) offsets to outer fragmentainer
    /// coordinates before forwarding to the real sink. Mirrors
    /// <c>TableLayouter.MeasuringFragmentSink</c>'s translation but
    /// without the buffering/measurement layer (= cycle-1 grid doesn't
    /// pre-measure rows yet; cycle 5+ adds that).</summary>
    private sealed class TranslatingFragmentSink : IBlockFragmentSink
    {
        private readonly IBlockFragmentSink _outer;
        private readonly double _inlineTranslation;
        private readonly double _blockTranslation;
        private readonly int _baseline;

        public TranslatingFragmentSink(
            IBlockFragmentSink outerSink,
            double inlineTranslation,
            double blockTranslation)
        {
            _outer = outerSink;
            _inlineTranslation = inlineTranslation;
            _blockTranslation = blockTranslation;
            _baseline = outerSink.Cursor;
        }

        public int Cursor => _outer.Cursor - _baseline;

        public void Emit(BoxFragment fragment)
        {
            _outer.Emit(fragment with
            {
                InlineOffset = fragment.InlineOffset + _inlineTranslation,
                BlockOffset = fragment.BlockOffset + _blockTranslation,
            });
        }

        public void RollbackTo(int cursor)
        {
            // Map inner cursor to outer cursor + delegate.
            _outer.RollbackTo(_baseline + cursor);
        }
    }

    public void Dispose()
    {
        // No unmanaged resources; matches FlexLayouter's pattern of a
        // trivial Dispose for symmetry with the ILayouter consumers'
        // `using` declarations.
    }

    // =====================================================================
    //  Track sizing (cycle 1: Length; cycle 2: + Fr via §11.7)
    // =====================================================================

    /// <summary>Flatten the parsed <see cref="TrackList"/> AST into a
    /// list of px track sizes per CSS Grid §11.
    ///
    /// <para><b>Cycle 1 (Hello World)</b>: only Length tracks sized;
    /// non-Length contributed 0 + emitted a diagnostic.</para>
    ///
    /// <para><b>Cycle 2 (post-PR-#92 review hardening continuation)</b>:
    /// adds <c>&lt;flex&gt;</c> (fr) tracks via CSS Grid §11.7 "Find
    /// the Size of an fr" algorithm. The remaining non-Length non-fr
    /// kinds (auto / min-content / max-content / minmax / fit-content
    /// / repeat) still contribute 0 + emit
    /// <see cref="PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001"/>.
    /// Cycle 3 ships intrinsic; cycle 4 ships minmax / fit-content /
    /// repeat(int); cycle 7 ships auto-fill / auto-fit.</para>
    ///
    /// <para><b>§11.7 fr distribution</b> (= the spec-correct
    /// algorithm — NOT the naive <c>flex / sumFlex</c> proportional
    /// split which is wrong for sub-1 factors + negative leftover):
    /// <list type="number">
    ///   <item><c>leftover = containerExtent - sumOfNonFlexBases</c>;
    ///   if <c>leftover &lt;= 0</c>, every fr track gets 0.</item>
    ///   <item><c>flexFactorSum = Σ max(flex, 1)</c> over fr tracks.
    ///   Per §11.7.1 the sum is floored so fractional factors below 1
    ///   don't blow up the divisor.</item>
    ///   <item><c>hypoFr = leftover / flexFactorSum</c>.</item>
    ///   <item>Each fr track's size = <c>hypoFr * flex</c> (RAW flex,
    ///   not floored — only the divisor floors). Cycle 2 has no
    ///   content-derived base sizes (= L19 deferral; cycle 3 ships
    ///   intrinsic), so the iterative "remove fr track whose base &gt;
    ///   hypoFr * flex" step from the spec is a no-op + skipped.
    ///   Future cycle 3 will add the iteration.</item>
    /// </list></para></summary>
    private List<double> ResolveTrackSizes(
        TrackList trackList, double containerExtent, bool isAxisIndefinite)
    {
        var sizes = new List<double>(trackList.Items.Length);
        // Per cycle 2 — gather (kind, value) per track in a single pass.
        // Length tracks get their px value immediately; fr tracks get
        // 0 placeholder + are resolved in the §11.7 pass below; other
        // kinds get 0 + a diagnostic.
        // Parallel arrays: <c>isFr</c> distinguishes fr tracks
        // (including 0fr, which is a valid fr factor per §7.2.3) from
        // non-fr tracks (= relying on frFactor != 0 was the cycle-2
        // initial-draft bug since 0fr is ALSO 0). <c>frFactors</c>
        // holds the raw fr factor for fr tracks; the value is unused
        // for non-fr.
        var isFr = new List<bool>(trackList.Items.Length);
        var frFactors = new List<double>(trackList.Items.Length);
        foreach (var item in trackList.Items)
        {
            if (item is TrackListEntry entry)
            {
                switch (entry.Entry.Kind)
                {
                    case GridTrackKind.Length when !entry.Entry.IsPercentage:
                        sizes.Add(entry.Entry.LengthPx);
                        isFr.Add(false);
                        frFactors.Add(0);
                        break;
                    case GridTrackKind.Fr:
                        // Placeholder; resolved in the §11.7 pass below.
                        sizes.Add(0);
                        isFr.Add(true);
                        frFactors.Add(entry.Entry.FrValue);
                        break;
                    default:
                        // Length-with-percentage / Auto / MinContent /
                        // MaxContent / MinMax / FitContent — cycle 3+
                        // scope. Contribute 0.
                        EmitTrackKindDiagnostic(entry.Entry.Kind);
                        sizes.Add(0);
                        isFr.Add(false);
                        frFactors.Add(0);
                        break;
                }
            }
            else if (item is TrackListRepeat)
            {
                EmitTrackKindDiagnostic(GridTrackKind.Length);
                // Skip the repeat entirely — cycle 4 lands integer-
                // count expansion + cycle 7 lands auto-fill/auto-fit.
            }
            // TrackListNamedLine: no track contribution; named-line
            // position resolution is cycle 7's scope.
        }

        // Per PR-#93 review F3 — when the axis is indefinite, fr
        // tracks should resolve via intrinsic / max-content sizing per
        // §11.7. Cycle 2 doesn't have intrinsic resolution yet
        // (= cycle 3 scope), so we emit a diagnostic + leave fr tracks
        // at 0 (= the same outcome as the §11.7 "no leftover" branch
        // produces for an auto-height grid pre-grown to chrome+length-
        // only extent). The diagnostic is the loud signal that the
        // author's intent is being approximated.
        if (isAxisIndefinite)
        {
            for (var i = 0; i < isFr.Count; i++)
            {
                if (isFr[i])
                {
                    EmitFrUnderIndefiniteDiagnostic();
                    break;
                }
            }
        }

        // §11.7 fr distribution pass. For indefinite axes the pass
        // still runs but `leftover` is typically 0 (= the wrapper was
        // pre-grown to chrome + non-fr extent), so fr tracks naturally
        // resolve to 0. The diagnostic above is the loud-signal layer;
        // the distribution math is the silent-fallback layer.
        ResolveFrTracks(sizes, isFr, frFactors, containerExtent);
        return sizes;
    }

    /// <summary>Per PR-#93 review F3 — one-shot diagnostic for fr
    /// tracks on an indefinite axis. Cycle 2 doesn't have intrinsic
    /// sizing yet (= cycle 3 scope); the diagnostic surfaces the
    /// approximation so authors aren't silently misled.</summary>
    private bool _emittedFrIndefiniteDiagnostic;
    private void EmitFrUnderIndefiniteDiagnostic()
    {
        if (_emittedFrIndefiniteDiagnostic) return;
        _emittedFrIndefiniteDiagnostic = true;
        SafeEmit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001,
            Message: "Grid container has fr tracks on an axis with "
                + "indefinite extent (= auto height for rows / auto "
                + "width for columns). Per CSS Grid §11.7 fr under "
                + "indefinite space resolves via intrinsic / max-content "
                + "sizing, which cycle 2 doesn't yet support (= cycle 3 "
                + "scope). fr tracks collapse to 0 in cycle 2. Author "
                + "intent: declare an explicit height/width on the grid "
                + "container, OR wait for cycle 3.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    /// <summary>Per CSS Grid §11.7 "Find the Size of an fr" — distribute
    /// <paramref name="containerExtent"/> minus the non-fr base extent
    /// among the fr tracks in <paramref name="sizes"/>. Cycle 2 scope:
    /// pure fr distribution with no content-derived base sizes (= L19
    /// deferral); the spec's iterative "remove fr track whose base &gt;
    /// hypoFr * flex" step is a no-op for cycle 2 since every fr track
    /// has base 0.
    ///
    /// <para><b>Pre-conditions</b>: <paramref name="sizes"/> and
    /// <paramref name="frFactors"/> are parallel arrays of the same
    /// length; the fr-track slots in <paramref name="sizes"/> are 0
    /// placeholders (= the value this method fills in); the non-fr
    /// slots already hold their final px sizes; <paramref name="frFactors"/>
    /// holds the raw fr factor for each fr track + 0 for non-fr.</para>
    ///
    /// <para><b>Negative-leftover case</b>: when non-fr tracks already
    /// fill or exceed the container, leftover &lt;= 0 → fr tracks all
    /// get 0 (= container visually overflows; matches the spec's
    /// behavior since fr distributes ONLY the positive leftover).</para></summary>
    private void ResolveFrTracks(
        List<double> sizes, List<bool> isFr, List<double> frFactors,
        double containerExtent)
    {
        // Sum non-fr base extents.
        double nonFlexBase = 0;
        var hasFr = false;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (isFr[i])
            {
                hasFr = true;
            }
            else
            {
                nonFlexBase += sizes[i];
            }
        }
        if (!hasFr) return;

        var leftover = containerExtent - nonFlexBase;
        if (leftover <= 0 || !double.IsFinite(leftover))
        {
            // No positive leftover — fr tracks pin at 0 (already
            // initialized). Spec: container may visually overflow.
            return;
        }

        // §11.7.1 step 3 per PR-#93 review F1 — the flex-factor floor
        // applies to the TOTAL sum ONCE, NOT per-track. Spec text:
        // "Let flex factor sum be the sum of the flex factors of all
        // the flexible tracks. If flex factor sum is less than 1, set
        // it to 1." So flexFactorSum = max(Σ factors, 1.0). Pre-F1
        // we floored per-track (Σ max(factor, 1)) which is materially
        // different + made fractional factors deliver less than the
        // container's leftover (e.g., `0.25fr 0.25fr` in 400px yielded
        // 50/50 instead of the spec's 100/100; `0fr 1fr` yielded
        // 0/200 instead of 0/400; `0.5fr 1fr` yielded 100/200 instead
        // of 133.33/266.67).
        //
        // 0fr CONTRIBUTES 0 to the raw sum (= it's a valid track with
        // a zero multiplier; the post-floor sum still includes the
        // other tracks' factors).
        double rawFlexFactorSum = 0;
        for (var i = 0; i < frFactors.Count; i++)
        {
            if (isFr[i])
            {
                rawFlexFactorSum += frFactors[i];
            }
        }
        // Per PR-#93 review F4 — flexFactorSum can overflow to
        // ±Infinity even though each individual factor is finite
        // (e.g., `1e308fr 1e308fr` → 2e308 overflows). Without this
        // guard `hypoFr = leftover / ∞ = 0` and tracks silently
        // resolve to 0. Surface as a non-finite geometry diagnostic
        // + skip distribution (= same outcome as
        // <see cref="IsTrackGeometryFinite"/>'s pre-existing path for
        // cumulative position overflow).
        if (!double.IsFinite(rawFlexFactorSum))
        {
            SafeEmit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001,
                Message: "Grid container's flex-factor sum overflowed to "
                    + "a non-finite value (= individual fr factors finite, "
                    + "but their sum exceeded double.MaxValue). fr "
                    + "distribution is skipped to prevent silent collapse "
                    + "to 0-sized tracks. Hostile CSS like "
                    + "`grid-template-columns: 1e308fr 1e308fr` triggers "
                    + "this guard.",
                Severity: PaginateDiagnosticSeverity.Warning));
            return;
        }
        var flexFactorSum = System.Math.Max(rawFlexFactorSum, 1.0);
        if (flexFactorSum <= 0) return; // defensive (unreachable since max(.,1) >= 1)

        // hypoFr = leftover / flexFactorSum.
        var hypoFr = leftover / flexFactorSum;
        if (!double.IsFinite(hypoFr)) return; // defensive

        // Cycle 2: no content-derived base sizes → no iterative
        // removal step needed. Each fr track's size = hypoFr * flex
        // (= 0 * hypoFr = 0 for 0fr tracks — they contribute 0 to
        // the sum AND receive 0 leftover, naturally collapsing).
        // Cycle 3 ships intrinsic sizing → the iteration step will
        // land then to handle "base > hypoFr * flex" force-fixed cases.
        for (var i = 0; i < sizes.Count; i++)
        {
            if (isFr[i])
            {
                sizes[i] = hypoFr * frFactors[i];
            }
        }
    }

    /// <summary>Compute cumulative track positions from track sizes.
    /// Returns the START position of each track (= 0-indexed). Track 0
    /// starts at <paramref name="originOffset"/>; each subsequent track
    /// starts at the prior track's start + its size.</summary>
    private static List<double> ComputeTrackPositions(
        List<double> sizes, double originOffset)
    {
        var positions = new List<double>(sizes.Count);
        var cursor = originOffset;
        foreach (var size in sizes)
        {
            positions.Add(cursor);
            cursor += size;
        }
        return positions;
    }

    /// <summary>Per PR-#92 review F9 — true when every position in
    /// <paramref name="positions"/> AND every (position + size) per
    /// track is finite (= no NaN, no ±Infinity). The position list
    /// alone doesn't catch the overflow at the LAST track
    /// (= position[N-1] is finite but position[N-1] + size[N-1] would
    /// be the position of the next-track-that-doesn't-exist; this
    /// overflowing right-edge IS used as the item's
    /// <c>InlineOffset + InlineSize</c> in the BoxFragment, so we must
    /// validate it). Caller emits a diagnostic + skips item emission
    /// when this returns false.</summary>
    private static bool IsTrackGeometryFinite(
        List<double> positions, List<double> sizes)
    {
        for (var i = 0; i < positions.Count; i++)
        {
            if (!double.IsFinite(positions[i])) return false;
            // (position + size) must also be finite — that's the
            // track's right/bottom edge as emitted in a BoxFragment.
            if (!double.IsFinite(positions[i] + sizes[i])) return false;
        }
        return true;
    }

    private void EmitTrackKindDiagnostic(GridTrackKind kind)
    {
        if (_emittedTrackKindDiagnostic) return;
        _emittedTrackKindDiagnostic = true;
        SafeEmit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001,
            Message: $"Grid track kind {kind} is not yet supported "
                + "(cycle 2 ships <length> + <flex>/fr via §11.7; "
                + "intrinsic / minmax / fit-content / repeat are cycle "
                + "3-7 scope). The track contributes 0 px to the track "
                + "sum.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    private void EmitImplicitTrackDiagnostic(Box itemBox)
    {
        SafeEmit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001,
            Message: $"Grid item (kind={itemBox.Kind}) was placed outside "
                + "the explicit grid bounds; cycle 1 doesn't yet generate "
                + "implicit tracks per §7.5. Item dropped (no fragment "
                + "emitted). Cycle 6 will ship implicit-track generation.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    /// <summary>Per PR-#92 review F8 — defensive diagnostic emission.
    /// The pagination contract treats diagnostic emission as nonfatal
    /// (= a malformed or throwing client sink must not abort layout).
    /// Catching <see cref="Exception"/> at this layer matches the
    /// safe-emit pattern used elsewhere in the layouter family +
    /// satisfies the F8 throwing-sink regression test. The caught
    /// exception is silently swallowed (no inner sink to forward to);
    /// if/when the project gains a fallback diagnostic channel for
    /// throwing sinks, this is the single hook to wire it through.</summary>
    private void SafeEmit(PaginateDiagnostic diagnostic)
    {
        if (_diagnostics is null) return;
        try
        {
            _diagnostics.Emit(diagnostic);
        }
        catch
        {
            // Diagnostic emission must not abort layout. Swallow the
            // exception per the F8 nonfatal-contract; client sinks are
            // responsible for not throwing in production but we defend
            // here so a malformed sink can't take the whole document
            // down.
        }
    }

    // =====================================================================
    //  Item placement
    // =====================================================================

    private enum PlacementKind : byte
    {
        Auto = 0,
        Definite = 1,
    }

    /// <summary>Per cycle 1 — read a grid-line placement spec and
    /// classify it. Only <see cref="GridLineKind.Auto"/> and
    /// <see cref="GridLineKind.LineNumber"/> (= integer, no named-line
    /// qualifier) are supported; <see cref="GridLineKind.Span"/> /
    /// <see cref="GridLineKind.NamedLine"/> / LineNumber-with-name fall
    /// back to Auto + emit
    /// <see cref="PaginateDiagnosticCodes.LayoutGridPlacementApproximated001"/>.</summary>
    private PlacementSpec ReadPlacement(ComputedStyle style, bool isRow)
    {
        var value = isRow
            ? style.ReadGridRowStart()
            : style.ReadGridColumnStart();
        return value.Kind switch
        {
            GridLineKind.Auto => new PlacementSpec(PlacementKind.Auto, 0),
            GridLineKind.LineNumber when value.NamedLine is null
                => new PlacementSpec(PlacementKind.Definite, value.LineNumber),
            // Named-line / span / line-with-name — cycle 6/7 scope.
            _ => EmitPlacementApproximatedAndFallToAuto(value.Kind),
        };
    }

    private PlacementSpec EmitPlacementApproximatedAndFallToAuto(GridLineKind kind)
    {
        SafeEmit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
            Message: $"Grid item placement kind {kind} is not supported in "
                + "cycle 1; falling back to auto-placement. Cycle 6 will "
                + "ship span; cycle 7 will ship named lines + areas.",
            Severity: PaginateDiagnosticSeverity.Warning));
        return new PlacementSpec(PlacementKind.Auto, 0);
    }

    /// <summary>Per PR-#92 review F5 — diagnose non-default
    /// <c>grid-{row,column}-end</c> values (= author requested a span)
    /// so the cycle-1 single-cell approximation is visible. The end-line
    /// value isn't consumed for placement (cycle 6 will), but its
    /// presence indicates the author intended a multi-cell area; the
    /// diagnostic warns that the area is being shrunk.
    ///
    /// <para><b>What counts as "non-default":</b> any value other than
    /// <see cref="GridLineKind.Auto"/>. CSS Grid's default end is
    /// <c>auto</c> = span 1 = single cell. The literal
    /// <c>grid-row-end: 2</c> when start is 1 is technically equivalent
    /// to single-cell, but distinguishing that case requires resolving
    /// both endpoints to compare; cycle 1 takes the conservative path
    /// + emits the diagnostic for any non-auto end so the author sees
    /// the cycle-1 limitation surfaced.</para></summary>
    private void CheckEndLineForSpanDiagnostic(ComputedStyle style, bool isRow)
    {
        var endValue = isRow
            ? style.ReadGridRowEnd()
            : style.ReadGridColumnEnd();
        if (endValue.Kind != GridLineKind.Auto)
        {
            SafeEmit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
                Message: (isRow ? "grid-row-end" : "grid-column-end")
                    + $" = {endValue.Kind} (non-default) — cycle 1 doesn't"
                    + " support multi-cell spans, so the item is placed at"
                    + " its start line as a single cell (= the authored"
                    + " area is shrunk). Cycle 6 ships span semantics.",
                Severity: PaginateDiagnosticSeverity.Warning));
        }
    }

    /// <summary>Resolve a CSS Grid line number (1-based, with negatives
    /// counting from the end per §8.3) to a 0-based TRACK index for
    /// single-cell placement (= the item occupies the track AFTER its
    /// start line). Returns a value outside <c>[0, trackCount)</c> to
    /// signal "implicit-track required" (= cycle 1 drops the item).
    ///
    /// <para><b>Line vs track per CSS Grid §8.</b> For N explicit tracks
    /// there are N+1 explicit lines (= line K is the boundary between
    /// track K-1 and track K, 1-based). Single-cell placement at
    /// <c>grid-row-start: K</c> occupies the track AFTER line K (= track
    /// index K-1, 0-based). When K = N+1 (= the last line), there's no
    /// track after it; the implicit grid would auto-generate a new
    /// track per §7.5. Cycle 1 doesn't support implicit tracks, so
    /// out-of-range returns trigger the implicit-track diagnostic + the
    /// item drops.</para>
    ///
    /// <para><b>Negative line numbers per §8.3 (post-PR-#92 review F3 fix).</b>
    /// "Negative integers count back from the end of the explicit grid"
    /// — so <c>-1</c> = the LAST explicit line = line N+1 (1-based) =
    /// the line AFTER the last explicit track. Single-cell placement at
    /// <c>grid-row-start: -1</c> in a 2-track grid means start at line 3
    /// (= the line after the last track) → requires implicit row →
    /// drops in cycle 1. <c>-2</c> = line N (1-based) = start of the
    /// last explicit track → track index N-1. Pre-F3 the formula was
    /// off by one (treated <c>-1</c> as the last explicit track instead
    /// of the line after it).</para></summary>
    private static int ResolveDefiniteToZeroBased(int lineNumber, int trackCount)
    {
        if (lineNumber == 0)
        {
            // Per §8.3 line number 0 is invalid; cycle-0b parser rejects
            // it but defense in depth.
            return -1;
        }
        if (lineNumber > 0)
        {
            // Positive: 1-based line K → 0-based track index K-1.
            return lineNumber - 1;
        }
        // Negative per §8.3 — count from the end of the explicit grid.
        // Lines are 1-indexed from the explicit grid's start (1..N+1);
        // -1 = the LAST line = line N+1; -K = line N+2-K.
        // Single-cell placement at start line L (1-based) occupies track
        // index L-1, so track = (N+2-K) - 1 = N+1-K = trackCount + 1 + lineNumber
        // (since lineNumber is -K).
        return trackCount + 1 + lineNumber;
    }

    /// <summary>Pass 2 helper — find the first column in <paramref name="row"/>
    /// that's not occupied. Returns -1 when the row is full.</summary>
    private static int FindFirstFreeColumnInRow(bool[,] occupancy, int row, int colCount)
    {
        for (var c = 0; c < colCount; c++)
        {
            if (!occupancy[row, c]) return c;
        }
        return -1;
    }

    /// <summary>Pass 3 helper — find the first row in <paramref name="col"/>
    /// that's not occupied. Returns -1 when the column is full.</summary>
    private static int FindFirstFreeRowInColumn(bool[,] occupancy, int col, int rowCount)
    {
        for (var r = 0; r < rowCount; r++)
        {
            if (!occupancy[r, col]) return r;
        }
        return -1;
    }

    /// <summary>Per PR-#92 review F4 — column-locked variant that searches
    /// from <paramref name="startRow"/> rather than from row 0. Per CSS
    /// Grid §8.5 the merged-pass column-locked item searches from the
    /// shared auto-placement cursor's current row (not necessarily 0).
    /// Returns -1 when the column has no free row from
    /// <paramref name="startRow"/> onwards (= would need an implicit row).</summary>
    private static int FindFirstFreeRowInColumnFrom(
        bool[,] occupancy, int col, int rowCount, int startRow)
    {
        for (var r = startRow; r < rowCount; r++)
        {
            if (!occupancy[r, col]) return r;
        }
        return -1;
    }

    /// <summary>Pass 4 helper — find the next free cell in row-major
    /// order starting from <paramref name="startRow"/> /
    /// <paramref name="startCol"/>. Returns true when a cell is found.
    /// Sparse mode per §8.5 — the cursor never rewinds, so once the
    /// scan exhausts (= startRow >= rowCount), we report failure.</summary>
    private static bool TryFindNextSparseCell(
        bool[,] occupancy, int startRow, int startCol,
        int rowCount, int colCount,
        out int row, out int col)
    {
        if (startRow >= rowCount)
        {
            row = -1;
            col = -1;
            return false;
        }
        var r = startRow;
        var c = startCol;
        while (r < rowCount)
        {
            while (c < colCount)
            {
                if (!occupancy[r, c])
                {
                    row = r;
                    col = c;
                    return true;
                }
                c++;
            }
            c = 0;
            r++;
        }
        row = -1;
        col = -1;
        return false;
    }

    /// <summary>Per Phase 3 Task 17 cycle 1 — grid item predicate.
    /// Mirrors the CSS Grid §6.4 contract: any in-flow child of a
    /// grid container is a grid item. Cycle 1 only emits block-level
    /// + atomic-inline items (= matches the FlexLayouter cycle-1
    /// scope); text runs + inline-level children at the top level
    /// would need anonymous-grid-item wrapping per §6.4 (= cycle 4+
    /// scope, same precedent as FlexLayouter L15's anonymous wrapping).</summary>
    private static bool IsGridItem(Box box)
    {
        // For cycle 1, accept only block-outer-display children that
        // can be cleanly positioned in a cell. Text runs + inline
        // elements would require anonymous-grid-item wrapping; punt to
        // a later cycle.
        return box.Kind is BoxKind.BlockContainer
            or BoxKind.GridContainer or BoxKind.InlineGridContainer
            or BoxKind.FlexContainer or BoxKind.InlineFlexContainer
            or BoxKind.Table or BoxKind.InlineTable
            or BoxKind.BlockReplacedElement
            or BoxKind.ListItem;
    }

    // =====================================================================
    //  Helper types
    // =====================================================================

    /// <summary>Per cycle 1 — a single grid item's placement state as
    /// it moves through the §8.5 placement passes.</summary>
    private struct PlacedItem
    {
        public Box Box;
        public PlacementSpec RowSpec;
        public PlacementSpec ColSpec;
        /// <summary>0-based final row index after placement. -1 when
        /// unplaced (= dropped via implicit-track diagnostic).</summary>
        public int Row;
        /// <summary>0-based final column index after placement.</summary>
        public int Col;
    }

    /// <summary>Per cycle 1 — a classified placement spec for one axis.
    /// Either <see cref="PlacementKind.Auto"/> (= no explicit position;
    /// <see cref="Line"/> ignored) or
    /// <see cref="PlacementKind.Definite"/> with the 1-based authored
    /// line number in <see cref="Line"/>.</summary>
    private readonly record struct PlacementSpec(PlacementKind Kind, int Line);
}
