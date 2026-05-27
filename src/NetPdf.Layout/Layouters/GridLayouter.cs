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
    /// <see langword="true"/>, <see cref="AttemptLayout"/> would split
    /// rows across pages + emit a <see cref="GridContinuation"/>.
    /// Cycle 1 (Hello World) only ships atomic emission (false);
    /// multi-page pagination is cycle-5 scope. The parameter exists
    /// on <see cref="ConfigureEmission"/> for forward-compat with the
    /// FlexLayouter shape; a true value currently throws.</summary>
    private bool _allowPagination;

    // Per cycle 3 post-PR-#94 hardening — the
    // _emittedTrackKindDiagnostic + _emittedFrIndefiniteDiagnostic
    // one-shot-guard fields moved to GridSizing.SizingContext (= the
    // sizing service now owns per-resolve diagnostic-emission state).
    // Cycle 5 will add a _incomingGridContinuation field for the
    // multi-page resume contract; for now the constructor rejects any
    // non-null incoming continuation.

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

        // Per Phase 3 Task 17 cycle 3 post-PR-#94 review F1 + F6 — the
        // sizing + placement pipeline was extracted to GridSizing.Resolve.
        // BlockLayouter.PreMeasureGridRowExtent now calls the same
        // service so wrapper measurement + emission agree (= no more
        // auto-height-grid-overlaps-sibling defect from PR-#94 F1).
        var sizing = GridSizing.Resolve(
            gridBox: _rootBox,
            contentInlineOffset: _contentInlineOffset,
            contentBlockOffset: _contentBlockOffset,
            contentInlineSize: _contentInlineSize,
            contentBlockSize: _contentBlockSize,
            emit: SafeEmit,
            cancellationToken: cancellationToken);

        if (!sizing.HasExplicitTracks)
        {
            // Per PR-#92 review F6 — implicit-track diagnostics were
            // already emitted by GridSizing.Resolve.
            return LayoutAttemptResult.AllDone(cost: 0.0);
        }
        if (!sizing.IsGeometryFinite)
        {
            return LayoutAttemptResult.AllDone(cost: 0.0);
        }
        // Begin pre-#94-extraction emission section — orphan code below
        // is gated by an `if (false)` block I'm about to delete via a
        // separate edit. Keep this minimal.

        var rowSizes = sizing.RowSizes;
        var colSizes = sizing.ColSizes;
        var rowPositions = sizing.RowPositions;
        var colPositions = sizing.ColPositions;
        var placedItems = sizing.PlacedItems;


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
