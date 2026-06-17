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

    // Per-GridLayouter (cross-AttemptLayout) measurement caches (measurement-cache cycle). Repeated
    // AttemptLayout calls on the SAME layouter instance re-resolve the tracks + re-measure every
    // content-determined item; persisting the measurements on the instance lets a later attempt reuse an
    // earlier one's NestedContentMeasurer passes. The key is the FULL set of inputs NestedContentMeasurer
    // consumes — (item, available inline width, block budget, writing mode, RTL) — so a cached value is
    // only ever reused when it was measured under IDENTICAL inputs. (A percent-height cell, e.g., measures
    // a different content block extent under a different block budget because the atomic pass sizes its
    // inner fragmentainer to that budget; keying on the budget keeps the reuse correct rather than stale.
    // Writing mode + RTL are stable per grid instance, but are in the key for correctness-by-construction.)
    // The max-content inline extent is width-independent, so the inline cache omits AvailInline.
    // NOTE (scope): the production grid dispatch (BlockLayouter.DispatchGridInner) builds a FRESH
    // GridLayouter per page and calls AttemptLayout ONCE, so these instance caches benefit same-instance
    // retries only. The cross-page / cross-COMPONENT win — sharing with BlockLayouter.PreMeasureGridRowExtent
    // via a per-conversion cache threaded through the layout context — is the immediate follow-up; see
    // docs/deferrals.md.
    private readonly System.Collections.Generic.Dictionary<
        (Box Item, double AvailInline, double BlockBudget, WritingMode Wm, bool Rtl), double> _blockExtentCache = new();
    private readonly System.Collections.Generic.Dictionary<
        (Box Item, double BlockBudget, WritingMode Wm, bool Rtl), double> _inlineExtentCache = new();

    // Instrumentation (measurement-cache cycle) — counts the NestedContentMeasurer.Measure passes that
    // actually RAN (cache misses). A second AttemptLayout under identical inputs must add ZERO passes; a
    // regression test asserts this (proving the cache is HIT, not a silent no-op) and asserts a DIFFERENT
    // block budget DOES add passes (proving the budget is in the key — no stale cross-budget reuse).
    internal int MeasurePassCount { get; private set; }

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

    /// <summary>Per Phase 3 Task 17 cycle 5c.3 + post-PR-#110
    /// review P3#2 — the per-page block-axis budget for emission.
    /// Separates the "geometry input" (= <see cref="_contentBlockSize"/>,
    /// used by <see cref="GridSizing.Resolve"/> to compute row sizes
    /// against the authored container extent) from the "page budget"
    /// (= page-remaining capacity that drives the
    /// <see cref="ComputePaginatedRowRange"/> row-fit cut-off). When
    /// <see langword="null"/>, the budget falls back to
    /// <see cref="_contentBlockSize"/> (= the pre-cycle-5c.3 behavior
    /// where geometry + budget were the same value); when non-null,
    /// pagination uses the explicit budget so explicit-height grids
    /// resolve rows against authored height while still paginating
    /// per page-remaining capacity. Pre-PR-#110 review the field
    /// used a <c>-1.0</c> sentinel; the nullable shape makes the
    /// "either-or" contract self-documenting + matches
    /// <see cref="ConfigureEmission"/>'s parameter shape.
    ///
    /// <para><b>Why this separation</b>: pre-5c.3 a 400px-tall grid
    /// with <c>grid-template-rows: 100px 1fr</c> on a 250px page got
    /// its <c>contentBlockSize</c> clamped to 250px BEFORE
    /// <see cref="GridSizing.Resolve"/> ran — the 1fr row redistributed
    /// against the smaller budget (= 100 + 150 instead of authored
    /// 100 + 300), silently losing 150px of grid geometry per
    /// <c>grid-explicit-height-paginate-deferral</c>. By passing the
    /// authored geometry to <c>Resolve</c> + the page budget
    /// separately to row-fit selection, explicit-height grids
    /// paginate correctly while authored row geometry stays
    /// authoritative.</para></summary>
    private double? _pageBlockBudget;

    /// <summary>Per Phase 3 Task 17 cycle 5 — the resume-from-prior-
    /// page state, captured by the constructor. When non-null the
    /// layouter skips the resolution + placement passes (using the
    /// cached snapshot in <see cref="GridContinuation.Cache"/>) and
    /// emits only items at rows ≥ <see cref="GridContinuation.RowIndex"/>.
    /// </summary>

    /// <summary>Per Phase 3 Task 17 cycle 5c.1 + post-PR-#98 review
    /// F1 — the TRUE occupied block-axis span of the rows emitted on
    /// the most recent <see cref="AttemptLayout"/> call. Populated on
    /// EVERY outcome (PageComplete + AllDone + Strict-defer), so
    /// consumers (= cycle 5c.2's wrapper-resize) get the value
    /// regardless of whether a continuation was produced.
    ///
    /// <para><b>Why a layouter property + not just on the
    /// continuation</b>: cycle-5c.1's initial design carried this
    /// solely on <see cref="GridContinuation.EmittedBlockExtent"/>,
    /// which only exists when <c>PageComplete</c> fires. The final
    /// fragment of a split grid returns <c>AllDone</c> with no
    /// continuation but still needs its wrapper resized to only the
    /// rows emitted on that final page. Per the PR-#98 review F1
    /// recommendation, the field moved to a result-level channel
    /// (= this property) that's available for all outcomes.</para>
    ///
    /// <para><b>Why <see cref="GridContinuation.EmittedBlockExtent"/>
    /// still exists</b>: kept as a redundant convenience for cycle
    /// 5c.2 consumers that already capture the continuation; both
    /// channels carry the same value when a continuation is
    /// produced. Cycle 5c.2 reads this property as the primary
    /// source.</para>
    ///
    /// <para><b>Per-outcome semantics</b>:</para>
    /// <list type="bullet">
    ///   <item><b>PageComplete (split)</b>: span of rows
    ///   <c>[startRow, endRowExclusive)</c> committed on this
    ///   fragment.</item>
    ///   <item><b>AllDone with incoming continuation</b>: span of
    ///   the REMAINING rows emitted on this (final) page.</item>
    ///   <item><b>AllDone without incoming continuation</b>: span
    ///   of the full grid (= natural extent). Cycle 5c.2 doesn't
    ///   need to resize this case but the value is accurate.</item>
    ///   <item><b>Strict-defer (PageComplete, RowIndex==startRow,
    ///   no rows emitted)</b>: 0 (= nothing committed on this
    ///   fragment).</item>
    ///   <item><b>Early-return (no explicit tracks / non-finite
    ///   geometry / startRow ≥ rowCount)</b>: 0.</item>
    /// </list></summary>
    public double LastEmittedBlockExtent { get; private set; }
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
    /// <param name="pageBlockBudget">Per Phase 3 Task 17 cycle 5c.3 —
    /// the per-page block-axis budget for emission, separating
    /// "geometry input" (= <paramref name="contentBlockSize"/>, used
    /// by <see cref="GridSizing.Resolve"/> to compute row sizes
    /// against the authored container extent) from "page budget" (=
    /// page-remaining capacity that drives row-fit pagination). Pass
    /// <see langword="null"/> (default) to use
    /// <paramref name="contentBlockSize"/> as both inputs (= pre-5c.3
    /// behavior — required for auto-height grids where the values
    /// are the same anyway, and for any caller that hasn't been
    /// updated yet). Pass a non-null value when an explicit-height
    /// grid clamps for break-check fit but resolves row geometry
    /// against the larger authored extent; the budget controls
    /// pagination cut-off without corrupting row sizing.</param>
    public void ConfigureEmission(
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize,
        bool allowPagination = false,
        double? pageBlockBudget = null)
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

        // Per Phase 3 Task 17 cycle 5c.3 — validate the optional page
        // budget. Non-null values must be finite + positive (otherwise
        // they'd produce a zero / negative row-fit budget downstream,
        // and the cycle-1 ComputePaginatedRowRange contract requires
        // a positive budget to make progress). Null falls through to
        // contentBlockSize (= legacy single-input behavior).
        if (pageBlockBudget is { } budget
            && (!double.IsFinite(budget) || budget <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageBlockBudget),
                $"pageBlockBudget must be finite + positive when non-null; "
                + $"got {budget}. Pass null to use contentBlockSize as the "
                + "budget (= pre-cycle-5c.3 single-input behavior).");
        }

        _contentInlineOffset = contentInlineOffset;
        _contentBlockOffset = contentBlockOffset;
        _contentInlineSize = contentInlineSize;
        _contentBlockSize = contentBlockSize;
        _allowPagination = allowPagination;
        _pageBlockBudget = pageBlockBudget;
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
            //
            // Non-block-pagination arc (grid CONTENT-sized rows) — wire a
            // content measurer so `grid-auto-rows: auto` (and min/max-content)
            // rows size to their cells' content block extent instead of
            // collapsing to 0. The measurer lays each item out at its column
            // width via the shared NestedContentMeasurer. Reading the ref-struct
            // layout context's fields into locals first (a lambda can't capture
            // a ref struct).
            var measureWritingMode = layout.WritingMode;
            var measureIsRtl = layout.IsRtl;
            var measureBlockBudget = fragmentainer.BlockSize;
            // Cross-COMPONENT per-conversion cache (measurement-cache cycle) — when the
            // root pipeline wired a shared GridMeasurementCache through the layout
            // context, PREFER it so this grid's pre-measure (BlockLayouter.
            // PreMeasureGridRowExtent), emission Resolve, and successive page dispatches
            // all reuse one another's measurements. Null (e.g. a direct-layouter test) →
            // the per-instance caches below (#187), so correctness holds either way.
            var sharedMeasureCache = layout.GridMeasureCache as GridMeasurementCache;
            // Memoize per item box for this Resolve — a row-SPANNING intrinsic
            // item is otherwise re-measured once per intersected row track
            // (ResolveIntrinsicTracks's per-track loop). An item's available
            // inline (column) width is deterministic within a Resolve, so the
            // box reference is a sufficient key.
            // Keyed by (item, availInline) — post-PR-#184 review F1: the block-extent measurement depends
            // on the AVAILABLE (column) width, so a cache keyed by box alone could return a height measured
            // at a stale width if the same item were ever measured at two widths. (With the columns-first
            // ordering in GridSizing.Resolve the row pass uses the FINAL column width, so this is also
            // defensive.)
            // Measurement-cache cycle — the instance-level caches persist across this grid's AttemptLayout
            // attempts. Keyed by the FULL measurement input set (item, available inline width, block budget,
            // writing mode, RTL) so a hit only reuses a value measured under IDENTICAL inputs (a percent-
            // height cell measures a different extent under a different budget — the key keeps that correct).
            var measureCache = _blockExtentCache;
            GridSizing.GridContentMeasurer contentMeasurer = (item, availInline) =>
            {
                if (sharedMeasureCache is not null)
                {
                    return sharedMeasureCache.BlockExtent(
                        item, availInline, measureBlockBudget, _shaperResolver,
                        measureWritingMode, measureIsRtl, cancellationToken);
                }
                var key = (item, availInline, measureBlockBudget, measureWritingMode, measureIsRtl);
                if (measureCache.TryGetValue(key, out var cached)) return cached;
                MeasurePassCount++;
                var extent = NestedContentMeasurer.Measure(
                    item, availInline, measureBlockBudget, _shaperResolver,
                    measureWritingMode, measureIsRtl, cancellationToken)
                    .ContentBlockExtent;
                measureCache[key] = extent;
                return extent;
            };
            // Grid content-width cycle — a SECOND measurer reporting the cell's MAX-CONTENT inline extent
            // (ContentInlineExtent) at the caller's unconstrained probe width, so auto / min-content /
            // max-content COLUMNS size to their content width. Separate cache (different measured value —
            // width, not height). Max-content is width-INDEPENDENT, so the key omits AvailInline but carries
            // (block budget, writing mode, RTL) for the same cross-attempt correctness as the block cache.
            var widthMeasureCache = _inlineExtentCache;
            GridSizing.GridContentMeasurer widthMeasurer = (item, availInline) =>
            {
                if (sharedMeasureCache is not null)
                {
                    return sharedMeasureCache.InlineExtent(
                        item, availInline, measureBlockBudget, _shaperResolver,
                        measureWritingMode, measureIsRtl, cancellationToken);
                }
                var key = (item, measureBlockBudget, measureWritingMode, measureIsRtl);
                if (widthMeasureCache.TryGetValue(key, out var cached)) return cached;
                MeasurePassCount++;
                var extent = NestedContentMeasurer.Measure(
                    item, availInline, measureBlockBudget, _shaperResolver,
                    measureWritingMode, measureIsRtl, cancellationToken)
                    .ContentInlineExtent;
                widthMeasureCache[key] = extent;
                return extent;
            };
            var sizing = GridSizing.Resolve(
                gridBox: _rootBox,
                contentInlineOffset: 0,
                contentBlockOffset: 0,
                contentInlineSize: _contentInlineSize,
                contentBlockSize: _contentBlockSize,
                emit: SafeEmit,
                cancellationToken: cancellationToken,
                contentMeasurer: contentMeasurer,
                widthMeasurer: widthMeasurer);

            if (!sizing.HasExplicitTracks)
            {
                LastEmittedBlockExtent = 0;
                return LayoutAttemptResult.AllDone(cost: 0.0);
            }
            if (!sizing.IsGeometryFinite)
            {
                LastEmittedBlockExtent = 0;
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
            LastEmittedBlockExtent = 0;
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
            // Per Phase 3 Task 17 cycle 5c.3 — pagination budget is the
            // explicit page-budget (when ConfigureEmission supplied
            // one) OR the content-block-size (= legacy single-input
            // behavior). For auto-height grids the two are the same;
            // for explicit-height grids the page-budget is the
            // page-remaining capacity while the content-block-size
            // stays at the authored height (preserving row geometry).
            var paginationBudget = _pageBlockBudget ?? _contentBlockSize;
            (endRowExclusive, needsContinuation, var deferEntireGrid) =
                ComputePaginatedRowRange(
                    startRow: startRow,
                    rowSizesView: rowSizesRelative,
                    rowPositionsView: rowPositionsRelative,
                    placedItems: placedItems,
                    budget: paginationBudget,
                    strategy: strategy);

            if (deferEntireGrid)
            {
                // Strict + first remaining row doesn't fit. No rows
                // committed on this fragment → emitted-extent = 0.
                LastEmittedBlockExtent = 0;

                // Per PR-#98 review F2 — PRESERVE the incoming cache
                // (when present) so the next attempt's identity check
                // still matches. Pre-F2 we passed Cache: null which
                // made identityMatches=false → startRow reset to 0 →
                // re-emission of prior-page rows. Now: identity stays
                // bound to this rootBox + RowIndex preserves progress.
                return LayoutAttemptResult.PageComplete(
                    cost: 0.0,
                    continuation: new GridContinuation(
                        RowIndex: startRow,
                        Cache: _incomingContinuation?.Cache,
                        EmittedBlockExtent: 0));
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
            //
            // Per Phase 3 Task 18 cycle 6 — the gate now keys on the
            // item's STARTING row (= item.Row). Spanning items whose
            // tail rows fall past endRowExclusive currently render
            // with the rectangle overflowing the page edge; the
            // atomic-to-row-span rewind contract is cycle 6b scope.
            // See `docs/deferrals.md#grid-row-span-atomic-pagination-deferral`.
            if (item.Row < startRow || item.Row >= endRowExclusive) continue;
            var rowSpan = Math.Max(1, item.RowSpan);
            var colSpan = Math.Max(1, item.ColSpan);
            var inlineOffset = _contentInlineOffset + colPositionsRelative[item.Col];
            var blockOffset = _contentBlockOffset
                + (rowPositionsRelative[item.Row] - rowOffsetShift);
            // Per Phase 3 Task 18 cycle 6 — size is the sum of the
            // item's spanned tracks (= the rectangle's extent). For
            // span=1 items this collapses to the single-track size.
            var inlineSize = SumTrackSizes(colSizesRelative, item.Col, colSpan);
            var blockSize = SumTrackSizes(rowSizesRelative, item.Row, rowSpan);
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

        // Per Phase 3 Task 17 cycle 5c.1 + post-PR-#98 review F3 —
        // compute the TRUE occupied content-box span via GEOMETRY,
        // not sum-of-row-sizes. Pre-F3 we summed rowSizesRelative
        // which is equal today only because gutters/alignment
        // spacing aren't implemented. Once CSS Grid row-gap /
        // block-axis alignment lands, sum-of-sizes will under-report
        // the fragment's occupied span. Geometry derivation =
        // (lastEmittedRow.bottom - firstEmittedRow.top), correct
        // regardless of gutters/spacing.
        double emittedExtent = 0;
        if (endRowExclusive > startRow)
        {
            var lastRowIdx = endRowExclusive - 1;
            var startTop = startRow > 0 ? rowPositionsRelative[startRow] : 0.0;
            var lastRowBottom =
                rowPositionsRelative[lastRowIdx] + rowSizesRelative[lastRowIdx];
            emittedExtent = lastRowBottom - startTop;
        }
        LastEmittedBlockExtent = emittedExtent;

        // Per Phase 3 Task 17 cycle 5 + post-PR-#96 review F4 — build
        // the resume cache LAZILY: only when a split actually produces
        // PageComplete. Atomic mode + paginated-but-fits-on-one-page
        // skip the allocation entirely.
        if (needsContinuation)
        {
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
        // AllDone — emittedExtent already populated on the layouter
        // property above so cycle 5c.2's wrapper-resize consumer
        // gets the value regardless of whether the final fragment
        // was a resume (= emits remaining rows + needs resize) or
        // a single-page no-resume (= natural extent).
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
            // Per Phase 3 Task 18 cycle 6 — validate the FAR corner of
            // the item's rectangle, not just its origin. Spans default
            // to 1 for legacy caches per GridItemPlacement's record
            // defaults.
            var rowSpan = Math.Max(1, p.RowSpan);
            var colSpan = Math.Max(1, p.ColSpan);
            if (p.Row < 0 || p.Col < 0
                || p.Row + rowSpan > rowCount
                || p.Col + colSpan > colCount)
            {
                SafeEmit(new PaginateDiagnostic(
                    Code: PaginateDiagnosticCodes.LayoutGridResumeCacheRejected001,
                    Message: $"Grid resume cache placement rectangle "
                        + $"[{p.Row}, {p.Row + rowSpan}) × [{p.Col}, {p.Col + colSpan}) "
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
    /// <para><b>Span-aware computation (cycle 6b scope)</b>: per the
    /// cycle-5 design contract, spanning items are ATOMIC TO THEIR
    /// ROW SPAN. If an item starts on this page (item.Row in
    /// [startRow, naiveEnd)) but extends past (item.Row + item.RowSpan
    /// &gt; naiveEnd), the entire item defers — we rewind endRowExclusive
    /// to item.Row so the item moves to the next page. If the spanning
    /// item starts at startRow and can't fit in any single page (=
    /// taller than the fragmentainer), the §4.4 progress rule kicks
    /// in: LastResort force-emits the full item (= rectangle overflows
    /// the page edge); other strategies defer the entire grid + the
    /// resume page eventually reaches LastResort.</para>
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
            IReadOnlyList<PlacedItem> placedItems,
            double budget,
            LayoutAttemptStrategy strategy)
    {
        var rowCount = rowSizesView.Count;
        var baselineRelative = startRow > 0 ? rowPositionsView[startRow] : 0.0;

        // Step 1 — naive row-by-row fit. naiveEnd is the largest K
        // such that sum_of_row_sizes[startRow..K) fits the budget.
        var naiveEnd = startRow;
        for (var r = startRow; r < rowCount; r++)
        {
            var rowBottomThisPage =
                (rowPositionsView[r] + rowSizesView[r]) - baselineRelative;
            if (rowBottomThisPage <= budget + GridSizing.SizeEpsilonPublic)
            {
                naiveEnd = r + 1;
                continue;
            }
            break;
        }

        // Per PR-#104 review F7 — precompute per-row max-span-end
        // (= the maximum `item.Row + item.RowSpan` across all items
        // anchored at that row). Single O(items + rowCount) pass; the
        // span-atomicity check is then O(rowsOnThisPage) instead of
        // O(items × pages). The full cache-plan version that amortizes
        // across pages is tracked under `grid-fragment-plan-shared-
        // sizing-deferral`.
        var maxSpanEndPerRow = new int[rowCount];
        for (var r = 0; r < rowCount; r++) maxSpanEndPerRow[r] = r + 1;
        foreach (var item in placedItems)
        {
            if (item.Row < 0 || item.Row >= rowCount) continue;
            var itemRowSpan = Math.Max(1, item.RowSpan);
            var itemEnd = item.Row + itemRowSpan;
            if (itemEnd > maxSpanEndPerRow[item.Row])
            {
                maxSpanEndPerRow[item.Row] = itemEnd;
            }
        }

        // Step 2 — span atomicity (cycle 6b). For every spanning item
        // that starts on this page (item.Row in [startRow, naiveEnd))
        // but extends past (item.Row + item.RowSpan > naiveEnd), clamp
        // endRowExclusive to item.Row so the item defers to next page.
        // Uses the per-row precompute so the cost is O(rowsOnThisPage).
        var spanClampedEnd = naiveEnd;
        for (var r = startRow; r < spanClampedEnd; r++)
        {
            if (maxSpanEndPerRow[r] > naiveEnd)
            {
                spanClampedEnd = r;
                break;
            }
        }

        // Step 3 — handle the "nothing committed on this page" case.
        if (spanClampedEnd <= startRow)
        {
            // Either the budget-based naive computation dropped the
            // first row (naiveEnd == startRow) OR a spanning item
            // starting at startRow extends past it.
            if (strategy == LayoutAttemptStrategy.LastResort)
            {
                // Force-emit something per §4.4 progress rule. Two
                // sub-cases:
                //  (a) A spanning item starts at startRow + extends
                //      past — emit the full item span (= rectangle
                //      overflows the page edge).
                //  (b) The single row at startRow is taller than
                //      the budget (cycle-5 contract) — emit it.
                var forceEnd = startRow + 1;
                if (startRow < rowCount && maxSpanEndPerRow[startRow] > forceEnd)
                {
                    forceEnd = maxSpanEndPerRow[startRow];
                }
                if (forceEnd > rowCount) forceEnd = rowCount;

                if (forceEnd > startRow + 1)
                {
                    // Per PR-#104 review F8 — forceEnd is the EXCLUSIVE
                    // 0-based row index after the last occupied row;
                    // its 1-based equivalent is the END LINE of the
                    // spanning item (= the line AFTER the last cell).
                    SafeEmit(new PaginateDiagnostic(
                        Code: PaginateDiagnosticCodes.LayoutGridForcedOverflow001,
                        Message: $"Grid spanning item at row "
                            + $"{startRow + 1} (occupying rows "
                            + $"{startRow + 1}..{forceEnd} inclusive, "
                            + $"end line {forceEnd + 1}) does not fit "
                            + $"the fragmentainer block budget "
                            + $"({budget:F1}px) on its first attempt "
                            + "under LastResort strategy. Per CSS "
                            + "Fragmentation L3 §4.4 progress rule the "
                            + "item is force-emitted in full to prevent "
                            + "pagination deadlock; content overflows "
                            + "the fragmentainer-block-end region.",
                        Severity: PaginateDiagnosticSeverity.Warning));
                }
                else
                {
                    SafeEmit(new PaginateDiagnostic(
                        Code: PaginateDiagnosticCodes.LayoutGridForcedOverflow001,
                        Message: $"Grid row {startRow + 1} (height "
                            + $"{rowSizesView[startRow]:F1}px) exceeds the "
                            + $"fragmentainer block budget "
                            + $"({budget:F1}px) on its first attempt "
                            + "under LastResort strategy. Per CSS "
                            + "Fragmentation L3 §4.4 progress rule the "
                            + "row is force-emitted to prevent pagination "
                            + "deadlock; content overflows the "
                            + "fragmentainer-block-end region.",
                        Severity: PaginateDiagnosticSeverity.Warning));
                }
                return (forceEnd, NeedsContinuation: forceEnd < rowCount, DeferEntireGrid: false);
            }
            else
            {
                // Strict / default — defer the whole grid.
                return (startRow, NeedsContinuation: false, DeferEntireGrid: true);
            }
        }

        // Step 4 — some rows fit. Continuation needed if more rows
        // remain past spanClampedEnd.
        var needsContinuation = spanClampedEnd < rowCount;
        return (spanClampedEnd, NeedsContinuation: needsContinuation, DeferEntireGrid: false);
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 — sum the contiguous-track
    /// sizes <c>sizes[start..start+span]</c>. Defends against
    /// boundary-overrun (= a malformed cache where item.Row + RowSpan
    /// exceeds rowCount silently clamps to the available extent
    /// instead of throwing). For span=1 the result is just
    /// <c>sizes[start]</c>.</summary>
    private static double SumTrackSizes(
        IReadOnlyList<double> sizes, int start, int span)
    {
        if (start < 0 || start >= sizes.Count) return 0;
        var endExclusive = Math.Min(start + Math.Max(1, span), sizes.Count);
        double sum = 0;
        for (var i = start; i < endExclusive; i++) sum += sizes[i];
        return sum;
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
            // Per Phase 3 Task 18 cycle 6 — persist the item's span
            // extents so the resume layouter re-emits the rectangle.
            placementsBuilder.Add(new GridItemPlacement(
                p.Box, p.Row, p.Col,
                Math.Max(1, p.RowSpan),
                Math.Max(1, p.ColSpan)));
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
                // Per Phase 3 Task 18 cycle 6 — restore span extents
                // so the resume emission loop sizes the rectangle
                // correctly. Legacy caches built before cycle 6 default
                // RowSpan / ColSpan to 1 per the GridItemPlacement
                // record's parameter defaults.
                RowSpan = Math.Max(1, p.RowSpan),
                ColSpan = Math.Max(1, p.ColSpan),
                // RowSpec/ColSpec aren't used post-placement so default
                // (Auto, Line=0, Span=1) is fine.
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
            blockTranslation: cellBlockOffset,
            // The grid item owns its own content's decoration: an inline-only-root
            // content fragment (box == itemBox) paints text only (the item geometry
            // fragment already painted its background / border).
            decorationOwner: itemBox);

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
            shaperResolver: _shaperResolver,
            // Per Phase 3 Task 17 cycle 5c.2b post-PR-#100 review
            // P1#1 — grid items are a CSS Fragmentation L3
            // "parallel flow" + GridLayouter discards the inner
            // BlockLayouter's result (= no continuation propagation
            // to the outer grid). A paginatable direct-child grid
            // inside a grid item would return
            // PageComplete(GridContinuation) → silent row loss on
            // the discarded continuation. Suppress grid pagination
            // here until nested-continuation propagation is wired
            // (= 5c.2d scope).
            disableGridPagination: true,
            // PR-#182 review P1 — a grid cell with a tall nested column-flex
            // would otherwise PageComplete(FlexContinuation) into this discarded
            // result + drop the deferred items. Suppress flex pagination too so
            // the cell content is atomic.
            disableFlexPagination: true,
            // Non-block-pagination arc (grid content-sized rows) — the common
            // grid item (`<div>text</div>`) has DIRECT inline children; opt the
            // nested layouter into emitting the inline-only ROOT's own content
            // (else the block-only child loop skips the cell's text — the same
            // gap flex items had). See BlockLayouter's _layoutRootInlineContent.
            layoutRootInlineContent: true);
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
        private readonly Box? _decorationOwner;

        public TranslatingFragmentSink(
            IBlockFragmentSink outerSink,
            double inlineTranslation,
            double blockTranslation,
            Box? decorationOwner = null)
        {
            _outer = outerSink;
            _inlineTranslation = inlineTranslation;
            _blockTranslation = blockTranslation;
            _baseline = outerSink.Cursor;
            _decorationOwner = decorationOwner;
        }

        public int Cursor => _outer.Cursor - _baseline;

        public void Emit(BoxFragment fragment)
        {
            // The inline-only-root content fragment (box == the grid item) paints
            // text only — the item's grid GEOMETRY fragment already paints its
            // decoration, so suppress it here to avoid a double paint. Block-CHILD
            // fragments (box != the item) keep their own decoration.
            var suppressDecoration = _decorationOwner is not null
                && ReferenceEquals(fragment.Box, _decorationOwner);
            _outer.Emit(fragment with
            {
                InlineOffset = fragment.InlineOffset + _inlineTranslation,
                BlockOffset = fragment.BlockOffset + _blockTranslation,
                SuppressBoxDecoration = fragment.SuppressBoxDecoration || suppressDecoration,
            });
        }

        public void RollbackTo(int cursor)
        {
            // Map inner cursor to outer cursor + delegate.
            _outer.RollbackTo(_baseline + cursor);
        }

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            // Per Phase 3 Task 17 cycle 5c.2b — translate inner
            // cursor index to the outer sink's absolute index then
            // delegate. Mirrors RollbackTo's translation pattern.
            _outer.UpdateFragmentBlockSize(_baseline + cursor, newBlockSize);
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
