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
/// <para><b>Cycle 1 + 2 + 3 scope</b> (= what this code actually
/// ships; the design doc's broader claims for cycle 3 are narrowed
/// here per PR-#94 review F4 + tracked in <c>docs/deferrals.md</c>):</para>
/// <list type="bullet">
///   <item><b>Track kinds supported</b>:
///   <list type="bullet">
///     <item><c>&lt;length&gt;</c> (cycle 1).</item>
///     <item><c>&lt;flex&gt;</c> / fr (cycle 2 via §11.7 with the
///     spec-correct <c>max(SUM(factors), 1.0)</c> divisor floor).</item>
///     <item><c>auto</c> / <c>min-content</c> / <c>max-content</c>
///     intrinsic kinds (cycle 3 via the L19 approximation — all
///     three resolve identically: max declared outer dimension
///     across items placed at the track, where outer dimension =
///     declared width/height + chrome (border + padding + margin)
///     on the matching axis).</item>
///   </list>
///   Track entries with any other kind (<c>minmax</c>,
///   <c>fit-content</c>, <c>repeat</c>) still emit
///   <see cref="PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001"/>
///   + contribute 0 px — cycle 4-7 expand coverage.</item>
///   <item><b>NOT in cycle 3</b>:
///   <list type="bullet">
///     <item>True intrinsic content measurement (= the L19 work) —
///     items with no explicit dimension contribute 0; cycle ??
///     L19 ships proper text + nested-content measurement.</item>
///     <item>The §11.6 Maximize step (= growing tracks up to their
///     growth limit; meaningful primarily with <c>minmax(min, max)</c>
///     where min &lt; max). Cycle 4 ships <c>minmax</c> + activates
///     the iterative §11.7 fr-removal step at the same time.</item>
///     <item>Auto-track stretch when <c>align-content: stretch</c> /
///     <c>justify-content: stretch</c> distributes remaining container
///     space across auto tracks. Separate task; tracked as a
///     follow-on cycle.</item>
///     <item><c>box-sizing: border-box</c> handling for intrinsic
///     contribution (= cycle 3 assumes content-box; border-box items
///     over-count chrome).</item>
///     <item>Percentage dimensions (<c>width: 50%</c>) as intrinsic
///     contribution (= percentages resolve against the cell, which is
///     what we're sizing — chicken-and-egg).</item>
///   </list></item>
///   <item><b>fr under indefinite axis</b> emits
///   <see cref="PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001"/>
///   + collapses fr tracks to 0. The proper fix is wrapper-measurement-
///   aware sizing (= future cycle) plus true intrinsic content
///   measurement for items without explicit dimensions (= L19).</item>
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
    /// <see langword="true"/>, <see cref="AttemptLayout"/> splits
    /// rows across pages + emits a <see cref="GridContinuation"/>.
    /// Per cycle 5 the gate is active: a true value triggers row-by-
    /// row pagination + continuation emission; a false value forces
    /// atomic emission (= the cycle-1 contract — all rows fit or the
    /// container overflows).</summary>
    private bool _allowPagination;

    /// <summary>Per Phase 3 Task 17 cycle 5 — the resume-from-prior-
    /// page state, captured by the constructor. When non-null the
    /// layouter skips the resolution + placement passes (using the
    /// cached snapshot in <see cref="GridContinuation.Cache"/>) and
    /// emits only items at rows ≥ <see cref="GridContinuation.RowIndex"/>.
    /// </summary>
    private readonly GridContinuation? _incomingContinuation;

    /// <summary>Construct a layouter for the grid container
    /// <paramref name="rootBox"/>. The box's <see cref="Box.Kind"/>
    /// MUST be <see cref="BoxKind.GridContainer"/> or
    /// <see cref="BoxKind.InlineGridContainer"/>; otherwise the
    /// constructor throws (= mirrors FlexLayouter's gate).</summary>
    /// <param name="rootBox">The grid container box.</param>
    /// <param name="sink">The same sink the caller uses; per-item
    /// fragments append after the grid wrapper fragment (which the
    /// caller has already emitted).</param>
    /// <param name="incomingContinuation">Per Phase 3 Task 17 cycle 5
    /// — accepts <see langword="null"/> (= fresh layout) OR a
    /// <see cref="GridContinuation"/> (= resume from a prior page).
    /// Any other continuation type throws as a misrouted-dispatch
    /// guard. The cycle-5 contract activates via the BlockLayouter
    /// dispatch wiring in cycle 5b; cycle 5 ships the layouter-side
    /// contract dormant at the dispatch sites.</param>
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

        // Per Phase 3 Task 17 cycle 5 — non-null continuation now
        // ACTIVATED. Only GridContinuation is accepted; any other
        // type signals a layouter-dispatch bug.
        if (incomingContinuation is not null
            && incomingContinuation is not GridContinuation)
        {
            throw new ArgumentException(
                "GridLayouter accepts only GridContinuation; "
                + $"got {incomingContinuation.GetType().Name}. "
                + "Misrouted continuation is a layouter-dispatch bug.",
                nameof(incomingContinuation));
        }

        _rootBox = rootBox;
        _sink = sink;
        _diagnostics = diagnostics;
        _shaperResolver = shaperResolver;
        _incomingContinuation = incomingContinuation as GridContinuation;

        // Per cycle 5 — validate the incoming continuation. Negative
        // RowIndex indicates a misformed continuation (should be ≥ 0;
        // value == sizing.RowSizes.Count means "all rows emitted"
        // which is a degenerate AllDone case the resume page handles
        // by returning early).
        if (_incomingContinuation is { RowIndex: < 0 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(incomingContinuation),
                $"GridContinuation.RowIndex must be ≥ 0; got "
                + $"{_incomingContinuation.RowIndex}. Negative values "
                + "indicate a continuation-construction bug.");
        }
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
    /// <param name="allowPagination">Per Phase 3 Task 17 cycle 5 —
    /// when <see langword="true"/>, <see cref="AttemptLayout"/>
    /// computes which rows fit the page budget + may return
    /// <c>PageComplete(GridContinuation)</c>. When <see langword="false"/>
    /// (cycle 1 default contract), all rows emit atomically regardless
    /// of fit (overflow is the caller's problem). Per PR-#96 F1+F2
    /// the force-overflow behavior is gated on
    /// <see cref="LayoutAttemptStrategy.LastResort"/>: a Strict
    /// strategy + first row doesn't fit defers the entire grid via
    /// <c>PageComplete(GridContinuation(startRow, null))</c>.</param>
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

        cancellationToken.ThrowIfCancellationRequested();

        // Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F3 + F5 —
        // resolve OR reuse the cached snapshot from the prior page.
        // The cache path skips the entire §11 sizing + §8.5 placement
        // algorithm (perf win on resume) AND is required for
        // correctness (sparse auto-placement is order-sensitive +
        // would yield a different placement if items were partially
        // emitted on the prior page).
        //
        // Per F3 — cache contains RELATIVE-FROM-CONTENT-ORIGIN
        // positions; emit adds the current page's contentInlineOffset/
        // contentBlockOffset back in. Per F5 — cache identity validated
        // against the rootBox to reject routed-to-wrong-grid bugs.
        // Per F3 — inline-size mismatch (different page widths) forces
        // a fresh sizing pass so fr / Maximize'd column widths recompute.
        IReadOnlyList<double> rowSizesRelative;     // 0-based; row sizes
        IReadOnlyList<double> colSizesRelative;     // 0-based; col sizes
        IReadOnlyList<double> rowPositionsRelative; // 0-based positions
        IReadOnlyList<double> colPositionsRelative; // 0-based positions
        IReadOnlyList<PlacedItem> placedItems;
        // Per PR-#97 review F4 — distinguish three cases when an
        // incoming continuation is present:
        //  (a) Identity mismatch (= cache routed to wrong grid) →
        //      reject cache + reset startRow to 0 (= fresh layout on
        //      this grid; the rejected RowIndex was for the wrong
        //      grid + carrying it forward would silently skip rows).
        //  (b) Same grid, inline-size matches → reuse cache verbatim.
        //  (c) Same grid, inline-size DIFFERS → reject cache for
        //      geometry recompute, BUT preserve RowIndex from the
        //      incoming continuation (= same grid, partial emission
        //      progress is real progress; resetting to 0 duplicates
        //      page-1 items).
        var identityMatches =
            _incomingContinuation?.Cache is { } incomingCacheForIdentity
            && ValidateCachePerF5(incomingCacheForIdentity, _rootBox);
        var inlineSizeMatches =
            _incomingContinuation?.Cache is { } incomingCacheForSize
            && Math.Abs(incomingCacheForSize.OriginalContentInlineSize
                - _contentInlineSize) <= GridSizing.SizeEpsilonPublic;
        var useExistingCache = identityMatches && inlineSizeMatches;

        if (useExistingCache)
        {
            var cache = _incomingContinuation!.Cache!;
            rowSizesRelative = cache.RowBaseSizes;
            colSizesRelative = cache.ColumnBaseSizes;
            rowPositionsRelative = cache.RowPositions;
            colPositionsRelative = cache.ColumnPositions;
            placedItems = ProjectCachedPlacements(cache.ItemPlacements);
        }
        else
        {
            // Per F3 — when the incoming inline-size differs from
            // cache's, emit a debug-level diagnostic so the cycle-5b
            // dispatch + future contributors see the invalidation.
            if (_incomingContinuation?.Cache is { } staleCache
                && Math.Abs(staleCache.OriginalContentInlineSize - _contentInlineSize)
                    > GridSizing.SizeEpsilonPublic)
            {
                SafeEmit(new PaginateDiagnostic(
                    Code: PaginateDiagnosticCodes.LayoutGridResumeInlineSizeMismatch001,
                    Message: $"Grid resume cache was built at "
                        + $"contentInlineSize={staleCache.OriginalContentInlineSize:F1}; "
                        + $"resume page presents {_contentInlineSize:F1}. Cache is "
                        + "invalidated + a fresh §11 sizing + §8.5 placement pass "
                        + "runs. Note: sparse auto-placement is order-sensitive — "
                        + "a different placement may emerge from the fresh resolve "
                        + "if items were partially emitted on the prior page. "
                        + "Caller should avoid resuming a grid into a page with a "
                        + "different inline content size.",
                    Severity: PaginateDiagnosticSeverity.Warning));
            }

            // Per Phase 3 Task 17 cycle 3 post-PR-#94 review F1 + F6 —
            // the sizing + placement pipeline was extracted to
            // GridSizing.Resolve. Per F3 — call with content offsets
            // set to 0 so the returned positions are relative-from-
            // content-origin (= cache shape).
            var sizing = GridSizing.Resolve(
                gridBox: _rootBox,
                contentInlineOffset: 0,
                contentBlockOffset: 0,
                contentInlineSize: _contentInlineSize,
                contentBlockSize: _contentBlockSize,
                emit: SafeEmit,
                cancellationToken: cancellationToken);

            if (!sizing.HasExplicitTracks)
            {
                return LayoutAttemptResult.AllDone(cost: 0.0);
            }
            if (!sizing.IsGeometryFinite)
            {
                return LayoutAttemptResult.AllDone(cost: 0.0);
            }

            rowSizesRelative = sizing.RowSizes;
            colSizesRelative = sizing.ColSizes;
            rowPositionsRelative = sizing.RowPositions;
            colPositionsRelative = sizing.ColPositions;
            placedItems = sizing.PlacedItems;
        }

        // Per Phase 3 Task 17 cycle 5 — determine which rows fit on
        // this page when pagination is active. Atomic mode (= cycle 1
        // contract, allowPagination=false) emits all rows from
        // startRow → end regardless of fit; pagination mode (= cycle 5)
        // computes the maximum row K that fits the contentBlockSize
        // budget + emits [startRow, K] inclusive.
        //
        // Per PR-#96 review F5 + PR-#97 review F4 — three cases:
        //  (a) Cache reused (= identity + inline-size both match) →
        //      use the RowIndex from the continuation.
        //  (b) Cache rejected for IDENTITY mismatch (= wrong grid) →
        //      reset startRow to 0. The rejected continuation's
        //      RowIndex was for the wrong grid; carrying it forward
        //      would silently skip rows of the receiving grid.
        //  (c) Cache rejected for inline-size mismatch on the SAME
        //      grid → preserve RowIndex (= partial-emission progress
        //      is real progress + same grid; resetting to 0 would
        //      duplicate page-1 items on page 2). Geometry recomputes
        //      from scratch via the fresh sizing pass above.
        int startRow;
        if (useExistingCache)
        {
            startRow = _incomingContinuation!.RowIndex;
        }
        else if (identityMatches)
        {
            // Same grid, different page geometry → preserve RowIndex
            // so partial-emission progress isn't lost.
            startRow = _incomingContinuation!.RowIndex;
        }
        else
        {
            // Different grid OR no continuation → fresh start.
            startRow = 0;
        }
        var rowCount = rowSizesRelative.Count;
        if (startRow >= rowCount)
        {
            // Degenerate "all rows already emitted" — happens when a
            // prior page emitted the last row + the resume page got
            // dispatched anyway (= benign; nothing to emit).
            return LayoutAttemptResult.AllDone(cost: 0.0);
        }

        int endRowExclusive;  // emit rows [startRow, endRowExclusive)
        bool needsContinuation;  // true → wrap rows [endRowExclusive, rowCount) in outgoing continuation
        if (!_allowPagination)
        {
            // Atomic emission (cycle 1 / cycle 5 atomic-path): emit
            // every remaining row. Overflow is the caller's problem.
            endRowExclusive = rowCount;
            needsContinuation = false;
        }
        else
        {
            // Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F1+F2:
            // strategy controls force-overflow behavior.
            // - Strict: defer the whole grid if first row doesn't fit
            //   (= return PageComplete(GridContinuation(startRow, null))
            //   signaling the BlockLayouter to rewind + retry the entire
            //   grid on a fresh page).
            // - LastResort: force-emit the oversized first row per
            //   §4.4 progress rule + emit diagnostic.
            (endRowExclusive, needsContinuation, var deferEntireGrid) =
                ComputePaginatedRowRange(
                    startRow: startRow,
                    rowSizesView: rowSizesRelative,
                    rowPositionsView: rowPositionsRelative,
                    budget: _contentBlockSize,
                    strategy: strategy);

            if (deferEntireGrid)
            {
                // Strict + first row doesn't fit + nothing committed.
                // Return PageComplete(GridContinuation(startRow, null))
                // so the dispatching BlockLayouter can rewind the grid
                // dispatch + retry on a fresh page (where LastResort
                // will eventually force-emit if needed).
                //
                // No cache emitted — the resume page must re-resolve
                // sizing (= a fresh page likely has a different
                // contentBlockSize budget anyway).
                return LayoutAttemptResult.PageComplete(
                    cost: 0.0,
                    continuation: new GridContinuation(
                        RowIndex: startRow,
                        Cache: null));
            }
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
        // Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F3 —
        // positions in the cache + the fresh resolve are RELATIVE
        // (= 0-based from content origin). Add the current page's
        // contentInlineOffset / contentBlockOffset at emit time.
        // On resume, the row at `startRow` anchors at this page's
        // contentBlockOffset (= shift by -rowPositionsRelative[startRow]).
        var rowOffsetShift = startRow > 0 ? rowPositionsRelative[startRow] : 0.0;
        foreach (var item in placedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Row < 0 || item.Col < 0) continue;
            // Per Phase 3 Task 17 cycle 5 — only emit items in the
            // [startRow, endRowExclusive) range. Items in rows before
            // startRow were emitted on a prior page (continuation
            // resume); items at or after endRowExclusive will be
            // emitted on the next page (outgoing continuation).
            if (item.Row < startRow || item.Row >= endRowExclusive) continue;
            var inlineOffset = _contentInlineOffset + colPositionsRelative[item.Col];
            var blockOffset = _contentBlockOffset
                + (rowPositionsRelative[item.Row] - rowOffsetShift);
            var inlineSize = colSizesRelative[item.Col];
            var blockSize = rowSizesRelative[item.Row];
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
                    // Per PR-#94 review F2 — zero-sized cell with
                    // children. Pre-F2 we skipped inner content + emitted
                    // a diagnostic; common markup like
                    //   <div style="display:grid; grid-template-rows:auto">
                    //     <div>text</div>
                    //   </div>
                    // would lose the text because the auto-row sized to
                    // the item's 0 explicit height (= L19 gap).
                    //
                    // Post-F2 cycle-3 ships the zero-area inner-layout
                    // strategy: fall back to the GRID's content extent
                    // for the inner FragmentainerContext so content
                    // renders at natural size + visually overflows the
                    // cell. Per CSS Grid §11.1 + §6.4 a zero-area cell
                    // is NOT equivalent to display:none; content should
                    // overflow per the painter's overflow rules.
                    //
                    // Diagnostic still fires so authors see that they're
                    // in approximation territory (= visual overflow may
                    // not match author intent).
                    SafeEmit(new PaginateDiagnostic(
                        Code: PaginateDiagnosticCodes.LayoutGridZeroSizedCellContentSkipped001,
                        Message: $"Grid item (kind={item.Box.Kind}) at cell "
                            + $"({item.Row + 1}, {item.Col + 1}) resolved to "
                            + $"a zero-sized area ({inlineSize}px × "
                            + $"{blockSize}px). Inner content (= "
                            + $"{item.Box.Children.Count} child boxes) "
                            + "emits at the grid container's content "
                            + "extent + visually overflows the cell "
                            + "(= per PR-#94 review F2 — content is no "
                            + "longer dropped, but it doesn't honor the "
                            + "cell's nominal zero geometry).",
                        Severity: PaginateDiagnosticSeverity.Warning));
                    // Fall back to grid's content extent so sub-layouter
                    // gets a positive-size FragmentainerContext + can
                    // emit content. The translating sink still anchors
                    // at the cell's (potentially zero-sized) origin.
                    DispatchGridItemContents(
                        itemBox: item.Box,
                        cellInlineOffset: inlineOffset,
                        cellBlockOffset: blockOffset,
                        cellInlineSize: inlineSize > 0 ? inlineSize : _contentInlineSize,
                        cellBlockSize: blockSize > 0 ? blockSize : _contentBlockSize,
                        outerFragmentainer: fragmentainer,
                        outerLayout: ref layout,
                        cancellationToken: cancellationToken);
                }
            }
        }

        // Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F4 — build
        // the resume cache LAZILY: only when a split actually produces
        // PageComplete. Atomic mode + paginated-but-fits-on-one-page
        // skip the allocation entirely.
        if (needsContinuation)
        {
            // Per Phase 3 Task 17 cycle 5c.1 + PR-#97 review F2 —
            // compute the TRUE emitted-rows extent so cycle-5c.2's
            // wrapper-resize path can size the wrapper to the
            // emitted-rows + chrome (= avoid the empty-space +
            // ConsumedBlockSize-inflation issues that blocked
            // cycle-5b activation). Sum the row sizes for the rows
            // ACTUALLY emitted on this fragment: [startRow,
            // endRowExclusive).
            double emittedExtent = 0;
            for (var r = startRow; r < endRowExclusive; r++)
            {
                emittedExtent += rowSizesRelative[r];
            }

            var outgoing = new GridContinuation(
                RowIndex: endRowExclusive,
                Cache: BuildResumeCache(
                    gridIdentity: _rootBox,
                    originalContentInlineSize: _contentInlineSize,
                    rowSizesRelative, colSizesRelative,
                    rowPositionsRelative, colPositionsRelative,
                    placedItems),
                EmittedBlockExtent: emittedExtent);
            return LayoutAttemptResult.PageComplete(
                cost: 0.0,
                continuation: outgoing);
        }
        return LayoutAttemptResult.AllDone(cost: 0.0);
    }

    /// <summary>Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F5 —
    /// structural validation of an incoming resume cache. Returns
    /// <see langword="true"/> only when the cache is bound to
    /// <paramref name="rootBox"/> + array lengths are consistent +
    /// no item placement is out-of-bounds + all geometry is finite.
    /// Returns <see langword="false"/> (= forces a fresh resolve) for
    /// any structural anomaly so a misrouted / malformed cache can't
    /// crash the emit loop or emit boxes from the wrong grid.</summary>
    private bool ValidateCachePerF5(GridResumeCache cache, Box rootBox)
    {
        if (!ReferenceEquals(cache.GridIdentity, rootBox))
        {
            SafeEmit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                Message: "Grid resume cache GridIdentity does not match "
                    + "the receiving GridLayouter's rootBox (= cache was "
                    + "built for a different grid container). The cache "
                    + "is rejected + a fresh §11 sizing + §8.5 placement "
                    + "pass runs against the current grid. This is a "
                    + "BlockLayouter-dispatch bug — continuations should "
                    + "round-trip back to the originating grid.",
                Severity: PaginateDiagnosticSeverity.Warning));
            return false;
        }
        var rowCount = cache.RowBaseSizes.Length;
        var colCount = cache.ColumnBaseSizes.Length;
        if (cache.RowPositions.Length != rowCount
            || cache.ColumnPositions.Length != colCount)
        {
            SafeEmit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                Message: "Grid resume cache has inconsistent array lengths "
                    + $"(RowBaseSizes={rowCount}, RowPositions={cache.RowPositions.Length}, "
                    + $"ColumnBaseSizes={colCount}, ColumnPositions={cache.ColumnPositions.Length}). "
                    + "Cache is rejected + a fresh resolve runs.",
                Severity: PaginateDiagnosticSeverity.Warning));
            return false;
        }
        for (var i = 0; i < rowCount; i++)
        {
            if (!double.IsFinite(cache.RowBaseSizes[i])
                || !double.IsFinite(cache.RowPositions[i]))
            {
                SafeEmit(new PaginateDiagnostic(
                    Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                    Message: $"Grid resume cache row {i} has non-finite "
                        + "size or position. Cache is rejected.",
                    Severity: PaginateDiagnosticSeverity.Warning));
                return false;
            }
        }
        for (var i = 0; i < colCount; i++)
        {
            if (!double.IsFinite(cache.ColumnBaseSizes[i])
                || !double.IsFinite(cache.ColumnPositions[i]))
            {
                SafeEmit(new PaginateDiagnostic(
                    Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                    Message: $"Grid resume cache column {i} has non-finite "
                        + "size or position. Cache is rejected.",
                    Severity: PaginateDiagnosticSeverity.Warning));
                return false;
            }
        }
        foreach (var p in cache.ItemPlacements)
        {
            if (p.Box is not Box)
            {
                SafeEmit(new PaginateDiagnostic(
                    Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                    Message: "Grid resume cache contains a non-Box "
                        + $"item payload (type={p.Box?.GetType().Name ?? "null"}). "
                        + "Cache is rejected.",
                    Severity: PaginateDiagnosticSeverity.Warning));
                return false;
            }
            if (p.Row >= rowCount || p.Col >= colCount)
            {
                SafeEmit(new PaginateDiagnostic(
                    Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                    Message: $"Grid resume cache placement ({p.Row}, {p.Col}) "
                        + $"is out of bounds for {rowCount}×{colCount} grid. "
                        + "Cache is rejected.",
                    Severity: PaginateDiagnosticSeverity.Warning));
                return false;
            }
        }
        return true;
    }

    /// <summary>Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F1
    /// + F2 partial — compute which rows fit the page budget.
    ///
    /// <para><b>Algorithm</b>: iterate rows from <paramref name="startRow"/>;
    /// each row's bottom edge (in this-page coordinates) is
    /// <c>rowPositionsRelative[r] + rowSizesRelative[r] -
    /// rowPositionsRelative[startRow]</c>. The last row that fits
    /// the budget sets <c>endRowExclusive</c>.</para>
    ///
    /// <para><b>Per PR-#96 review F1+F2 — strategy gates force-
    /// overflow</b>: when the FIRST row attempted doesn't fit:
    /// <list type="bullet">
    ///   <item><b>LastResort</b> — force-emit the row + emit
    ///   <c>LayoutGridForcedOverflow001</c> diagnostic. This is the
    ///   §4.4 progress rule: "you must commit at least one element
    ///   per page" or pagination deadlocks. Only the dispatching
    ///   layouter (= a fresh-page retry chain) should reach this.</item>
    ///   <item><b>any other strategy (Strict / default)</b> — set
    ///   <c>deferEntireGrid = true</c> so the caller returns
    ///   <c>PageComplete(GridContinuation(startRow, null))</c>. The
    ///   BlockLayouter dispatch then rewinds the grid + retries it on
    ///   a fresh page. Pre-F1 the layouter ALWAYS force-emitted,
    ///   which mispaginated common "grid starts below earlier
    ///   content" layouts (= 80px remaining on page 1, first row is
    ///   100px → row should defer to page 2, not force-emit).</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Span-aware computation (cycle 6 scope)</b>: cycle 5
    /// assumes each item occupies exactly one row (= cycle 1
    /// contract). Cycle 6's <c>span N</c> placement extends this so
    /// the break point K must satisfy "no item placed at rows ≤ K
    /// has any span past K". The current implementation is forward-
    /// compatible: cycle 6 will add a per-row "max span end" lookup
    /// + extend the loop condition.</para>
    ///
    /// <para><b>F2 deferred (full IBreakResolver wiring)</b>: cycle 5
    /// hardening only respects strategy. Full integration (model rows
    /// as BreakOpportunity values, route through resolver, define
    /// rewind behavior) is tracked in
    /// <c>docs/deferrals.md#grid-break-resolver-integration-deferred</c>
    /// — picks up alongside CSS <c>break-before</c> /
    /// <c>break-after</c> / <c>break-inside</c> support for grid
    /// rows.</para></summary>
    private (int EndRowExclusive, bool NeedsContinuation, bool DeferEntireGrid)
        ComputePaginatedRowRange(
            int startRow,
            IReadOnlyList<double> rowSizesView,
            IReadOnlyList<double> rowPositionsView,
            double budget,
            LayoutAttemptStrategy strategy)
    {
        var rowCount = rowSizesView.Count;
        var baselineRelative = startRow > 0 ? rowPositionsView[startRow] : 0.0;
        var endRowExclusive = startRow;
        for (var r = startRow; r < rowCount; r++)
        {
            // Row r's bottom edge in this-page coordinates (= 0-based
            // from the start of this page's emitted region).
            var rowBottomThisPage =
                (rowPositionsView[r] + rowSizesView[r]) - baselineRelative;

            if (rowBottomThisPage <= budget + GridSizing.SizeEpsilonPublic)
            {
                // Row r fits — commit it.
                endRowExclusive = r + 1;
                continue;
            }

            // Row r doesn't fit.
            if (r == startRow)
            {
                // First row on this page can't fit. Per F1+F2: only
                // force-emit under LastResort; otherwise defer the
                // whole grid to the next page (= rewind signal).
                if (strategy == LayoutAttemptStrategy.LastResort)
                {
                    SafeEmit(new PaginateDiagnostic(
                        Code: PaginateDiagnosticCodes.LayoutGridForcedOverflow001,
                        Message: $"Grid row {r + 1} (height "
                            + $"{rowSizesView[r]:F1}px) exceeds the "
                            + $"fragmentainer block budget "
                            + $"({budget:F1}px) on its first attempt "
                            + "under LastResort strategy. Per CSS "
                            + "Fragmentation L3 §4.4 progress rule the "
                            + "row is force-emitted to prevent pagination "
                            + "deadlock; content overflows the "
                            + "fragmentainer-block-end region.",
                        Severity: PaginateDiagnosticSeverity.Warning));
                    endRowExclusive = r + 1;
                    continue;
                }
                else
                {
                    // Strict / default — defer the whole grid. Caller
                    // returns PageComplete(GridContinuation(startRow,
                    // null)); the BlockLayouter dispatch rewinds + the
                    // resume page eventually reaches LastResort.
                    return (startRow, NeedsContinuation: false, DeferEntireGrid: true);
                }
            }

            // Row r doesn't fit + we already committed at least one
            // row → break + caller emits continuation for rows
            // [endRowExclusive, rowCount).
            return (endRowExclusive, NeedsContinuation: true, DeferEntireGrid: false);
        }

        // All remaining rows fit on this page → no continuation.
        return (endRowExclusive, NeedsContinuation: false, DeferEntireGrid: false);
    }

    /// <summary>Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F3 +
    /// F4 + F5 — build the resume cache from the first-page Resolve
    /// result. Called LAZILY (only when a split actually produces
    /// PageComplete; atomic mode + paginated-but-fits-on-one-page
    /// skip the allocation).
    ///
    /// <para>Positions are RELATIVE to the grid's content origin
    /// (per F3) so resume on a page with different
    /// <c>contentInlineOffset</c> / <c>contentBlockOffset</c>
    /// correctly anchors per current page. Cache identity is bound
    /// to <paramref name="gridIdentity"/> (per F5) so a misrouted
    /// cache rejects loudly.</para></summary>
    private static GridResumeCache BuildResumeCache(
        Box gridIdentity,
        double originalContentInlineSize,
        IReadOnlyList<double> rowSizes,
        IReadOnlyList<double> colSizes,
        IReadOnlyList<double> rowPositions,
        IReadOnlyList<double> colPositions,
        IReadOnlyList<PlacedItem> placedItems)
    {
        var rowSizesArr = System.Collections.Immutable.ImmutableArray.CreateRange(rowSizes);
        var colSizesArr = System.Collections.Immutable.ImmutableArray.CreateRange(colSizes);
        var rowPosArr = System.Collections.Immutable.ImmutableArray.CreateRange(rowPositions);
        var colPosArr = System.Collections.Immutable.ImmutableArray.CreateRange(colPositions);
        var placementsBuilder = System.Collections.Immutable.ImmutableArray
            .CreateBuilder<GridItemPlacement>(placedItems.Count);
        foreach (var p in placedItems)
        {
            placementsBuilder.Add(new GridItemPlacement(p.Box, p.Row, p.Col));
        }
        return new GridResumeCache(
            GridIdentity: gridIdentity,
            OriginalContentInlineSize: originalContentInlineSize,
            RowBaseSizes: rowSizesArr,
            ColumnBaseSizes: colSizesArr,
            RowPositions: rowPosArr,
            ColumnPositions: colPosArr,
            ItemPlacements: placementsBuilder.ToImmutable());
    }

    /// <summary>Per Phase 3 Task 17 cycle 5 — project the cache's
    /// <see cref="GridItemPlacement"/> records back to the layouter-
    /// internal <see cref="PlacedItem"/> shape (= unwraps the Box-
    /// typed-as-object payload).</summary>
    private static List<PlacedItem> ProjectCachedPlacements(
        System.Collections.Immutable.ImmutableArray<GridItemPlacement> cached)
    {
        var list = new List<PlacedItem>(cached.Length);
        foreach (var p in cached)
        {
            list.Add(new PlacedItem
            {
                // Cache stores Box as object (= Paginate package can't
                // reference Layout's Box type without circular dep).
                // The cast is layouter-internal + always succeeds for
                // caches we built ourselves.
                Box = (Box)p.Box,
                Row = p.Row,
                Col = p.Col,
                // RowSpec/ColSpec aren't used post-placement so default
                // (Auto) is fine.
                RowSpec = default,
                ColSpec = default,
            });
        }
        return list;
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

    /// <summary>Per PR-#92 review F8 — defensive diagnostic emission.
    /// The pagination contract treats diagnostic emission as nonfatal
    /// (= a malformed or throwing client sink must not abort layout).
    /// Catching <see cref="Exception"/> here matches the safe-emit
    /// pattern used elsewhere in the layouter family. Also serves as
    /// the <see cref="GridSizing.EmitDiagnostic"/> callback the
    /// extracted sizing service uses.</summary>
    private void SafeEmit(PaginateDiagnostic diagnostic)
    {
        if (_diagnostics is null) return;
        try { _diagnostics.Emit(diagnostic); }
        catch
        {
            // Diagnostic emission must not abort layout. Swallow the
            // exception per the F8 nonfatal-contract.
        }
    }
}
