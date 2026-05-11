// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 12 sub-cycle 1 + 2 + plan §"TableLayouter" —
/// Hello-World table layouter. Walks the inner content of a
/// <see cref="BoxKind.Table"/> (or <see cref="BoxKind.InlineTable"/>)
/// wrapper: finds the <see cref="BoxKind.TableGrid"/>, collects table
/// rows (recursing into row groups), splits the content-inline-size
/// equally across the columns implied by the placed cell grid, stacks
/// rows vertically, dispatches each cell's content through a nested
/// <see cref="BlockLayouter"/> for recursive layout, and emits one
/// <see cref="BoxFragment"/> per row + per cell into the same
/// <see cref="IBlockFragmentSink"/> the outer block layouter uses.
///
/// <para><b>Sub-cycle 2 — <c>colspan</c> / <c>rowspan</c> cell
/// merging.</b> Cell placement uses the CSS Tables L3 §3 + HTML5
/// "Forming a table" 2D occupancy-grid algorithm: each row walks
/// left-to-right with a column cursor; for each cell, the cursor
/// advances past any slots already occupied by rowspan cells from
/// previous rows, then the cell anchors at the cursor and marks its
/// <c>rowspan × colspan</c> slot rectangle as occupied. Column count =
/// max(occupiedColIndex + 1) across all rows. Spanning cells receive
/// <c>colspan × columnWidth</c> of inline space + their block size
/// sums all rowHeights they cover. Row heights start as
/// <c>max(content extent)</c> over <c>rowspan=1</c> cells; for
/// <c>rowspan&gt;1</c> cells (processed in pass 2, sorted by ascending
/// rowspan), any excess content above the natural row-height sum lands
/// on the LAST row of the span — a deterministic naive distribution.
/// The CSS Tables L3 spec-strict distribution-proportional algorithm
/// is sub-cycle 3 work.</para>
///
/// <para><b>Two-phase protocol (post-Finding-1 hardening).</b> The
/// layouter exposes a public <see cref="MeasureContentHeight"/> method
/// that pre-computes the row stack height WITHOUT forwarding fragments
/// to the outer sink. The dispatching <see cref="BlockLayouter"/> calls
/// <see cref="MeasureContentHeight"/> BEFORE emitting the wrapper
/// fragment so it can size the wrapper's border-box block extent to
/// <c>max(cssHeight, tableContentHeight)</c>; the wrapper's emitted
/// extent then drives <see cref="FragmentainerContext.UsedBlockSize"/>
/// (siblings of the table no longer overlap its content). After the
/// wrapper fragment lands, <see cref="AttemptLayout"/> consumes the
/// cached per-row measurements + emits row/cell fragments + flushes
/// the buffered cell content in paint-safe order.</para>
///
/// <para><b>Paint-safe emit order (post-Finding-2 + Finding-3
/// hardening).</b> Sub-cycle 1 buffered cell content in
/// <see cref="MeasuringFragmentSink"/> during the measure phase.
/// Sub-cycle 2 + Finding 3 split the emit into THREE table-wide
/// phases — all row fragments first, then all cell fragments
/// (anchored at their origin), then all cell content via
/// <see cref="MeasuringFragmentSink.FlushTo"/>. The whole-table
/// ordering guarantees a row r+1 background can never paint over
/// a rowspan cell's content anchored at row r (Finding 3 pre-fix:
/// the previous row-major loop flushed row r's rowspan-cell content
/// before emitting row r+1's background).</para>
///
/// <para><b>Algorithm (post-sub-cycle 2):</b></para>
/// <list type="number">
///   <item>Locate the wrapper's <see cref="BoxKind.TableGrid"/> child.
///   If missing (defensive — <c>BoxBuilder</c>'s table fixup is supposed
///   to insert it), emit a diagnostic + return AllDone.</item>
///   <item>Walk the TableGrid's children in document order, recursing
///   into <see cref="BoxKind.TableRowGroup"/> /
///   <see cref="BoxKind.TableHeaderGroup"/> /
///   <see cref="BoxKind.TableFooterGroup"/> to collect their
///   <see cref="BoxKind.TableRow"/> children. Captions ARE detected
///   under the wrapper (per BoxBuilder Rec 5) but their content is
///   skipped + a <c>LAYOUT-TABLE-FEATURE-UNSUPPORTED-001</c>
///   diagnostic fires with the caption text snippet so authors see
///   what's being dropped (deferred behavior — see
///   <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>).
///   Column groups and columns are skipped silently.</item>
///   <item><b>Sub-cycle 2:</b> place cells onto a 2D occupancy grid
///   (<see cref="CellPlacement"/>) using the HTML5 forming-a-table
///   algorithm — for each row, advance a column cursor past slots
///   already occupied by rowspan continuations from earlier rows, then
///   anchor the cell at the cursor with its <c>colspan × rowspan</c>
///   slot rectangle marked occupied. The column count = max occupied
///   column index + 1 across all rows.</item>
///   <item>Split the available inline-size equally:
///   <c>columnWidth = contentInlineSize / columnCount</c>. No
///   author column widths, no shrink-to-fit, no min/max content
///   sizing (auto + fixed layout algorithms still deferred).</item>
///   <item>Measure pass: for each cell at its origin, lay out its
///   content into a <see cref="MeasuringFragmentSink"/> that BUFFERS
///   fragments with an inline translation baked in but a deferred
///   block translation. Track the per-cell maximum block extent.</item>
///   <item>Row height pass: <c>rowHeight[r] = max(content extent)</c>
///   over cells with <c>rowspan=1</c> anchored at row <c>r</c>. Then
///   a second pass for cells with <c>rowspan&gt;1</c> (processed
///   ascending rowspan): if
///   <c>sum(rowHeight[originRow..originRow+rowspan-1]) &lt; cellContent</c>,
///   add the excess to <c>rowHeight[originRow+rowspan-1]</c> (the last
///   row of the span). Deterministic + simple; spec-strict
///   distribution-proportional algorithm is sub-cycle 3.</item>
///   <item>Emit pass (when <see cref="AttemptLayout"/> runs): for each
///   row, emit the row fragment + the cell fragments anchored at that
///   row (skipping continuation slots from previous rows' rowspans),
///   then flush each cell's buffered content with the finalized block
///   translation applied. Advance the row cursor.</item>
/// </list>
///
/// <para><b>Remaining deferrals</b> (see
/// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>):</para>
/// <list type="bullet">
///   <item>CSS Tables L3 §3 auto-table-layout column-width algorithm
///   (shrink-to-fit / intrinsic min/max content).</item>
///   <item>CSS Tables L3 §3.5 fixed-table-layout algorithm (column
///   widths from <c>&lt;col&gt;</c> + first-row cell widths).</item>
///   <item>Border-collapse model + <c>border-spacing</c> per
///   §6.3.</item>
///   <item><c>&lt;thead&gt;</c> / <c>&lt;tfoot&gt;</c> repetition
///   across pages.</item>
///   <item>Captions (<see cref="BoxKind.TableCaption"/>) — content is
///   skipped + a diagnostic emits with a snippet of the caption text
///   so authors see what is being dropped.</item>
///   <item><c>&lt;col&gt;</c> / <c>&lt;colgroup&gt;</c> column-specific
///   widths.</item>
///   <item>Multi-fragmentainer table splitting (rows that cross
///   pages). If the table doesn't fit on the current fragmentainer,
///   the layouter emits all rows anyway + a forced-overflow
///   diagnostic.</item>
///   <item>Right-to-left tables / writing-mode flips.</item>
///   <item>Spec-strict CSS Tables L3 rowspan distribution-proportional
///   algorithm. Sub-cycle 2 uses a naive "extra height to the last
///   row of the span" approach.</item>
/// </list>
///
/// <para><b>Pagination scope.</b> Like
/// <see cref="BlockLayouter.EmitBlockSubtreeRecursive"/>, the
/// TableLayouter is invoked AFTER the outer-loop's resolver
/// consultation + checkpoint capture for the table wrapper. The
/// outer loop has already decided to commit the table on this
/// fragmentainer. The current implementation does NOT consult the
/// resolver inside the table; rows that overflow the page emit a
/// single <c>PAGINATION-FORCED-OVERFLOW-001</c> diagnostic + commit
/// anyway. Multi-page table splitting (rows that defer to the next
/// page) is deferred — see
/// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.</para>
///
/// <para><b>Per-cell break-resolver isolation (post-Finding-3 hardening).</b>
/// Each cell-content layout dispatches a <see cref="BlockLayouter"/>
/// with a FRESH <see cref="BreakResolver"/> instance scoped to that
/// cell. The outer resolver's checkpoint state is never touched by
/// cell-internal pagination — preserving the outer table's rewind /
/// resume contract.</para>
///
/// <para><b>Cancellation.</b> The token is propagated to the nested
/// <see cref="BlockLayouter"/> for cell content + checked between
/// rows + cells. A long-running deeply-nested cell layout responds
/// to cancellation through the inner BlockLayouter's own checks.</para>
/// </summary>
internal sealed class TableLayouter : ILayouter, IDisposable
{
    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    // _incomingContinuation field removed per PR #49 Copilot review +
    // PR #49 hardening — the constructor validates the parameter is
    // null (sub-cycle 1 rejects mid-table resume) + then has no
    // further use for it. Sub-cycle 2 will re-introduce as a real
    // TableContinuation when multi-page row splitting lands.
    private readonly IPaginateDiagnosticsSink? _diagnostics;
    private readonly IShaperResolver? _shaperResolver;

    /// <summary>Construct a layouter for <paramref name="rootBox"/>'s
    /// inner table content. <paramref name="rootBox"/> MUST be a
    /// <see cref="BoxKind.Table"/> or <see cref="BoxKind.InlineTable"/>
    /// wrapper.</summary>
    /// <param name="rootBox">The <see cref="BoxKind.Table"/> or
    /// <see cref="BoxKind.InlineTable"/> wrapper. Its outer fragment
    /// is emitted by the caller; this layouter emits the
    /// row + cell fragments inside.</param>
    /// <param name="sink">The same sink the caller uses — row + cell
    /// fragments append after the wrapper fragment.</param>
    /// <param name="incomingContinuation">Reserved for future multi-page
    /// row resume (deferred — see
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>). The
    /// constructor accepts <see langword="null"/> only; any non-null
    /// value throws.</param>
    /// <param name="diagnostics">Diagnostic sink for the
    /// <c>PAGINATION-FORCED-OVERFLOW-001</c> + structural-anomaly
    /// codes.</param>
    /// <param name="shaperResolver">Optional inline shaper resolver
    /// threaded into the nested <see cref="BlockLayouter"/> for cell
    /// content layout.</param>
    /// <exception cref="ArgumentException">When
    /// <paramref name="rootBox"/> is not a Table or InlineTable wrapper,
    /// or when <paramref name="incomingContinuation"/> is non-null
    /// (multi-page resume not yet supported — see
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>).</exception>
    public TableLayouter(
        Box rootBox,
        IBlockFragmentSink sink,
        LayoutContinuation? incomingContinuation = null,
        IPaginateDiagnosticsSink? diagnostics = null,
        IShaperResolver? shaperResolver = null)
    {
        ArgumentNullException.ThrowIfNull(rootBox);
        ArgumentNullException.ThrowIfNull(sink);

        if (rootBox.Kind is not (BoxKind.Table or BoxKind.InlineTable))
        {
            throw new ArgumentException(
                $"TableLayouter expects a Table or InlineTable wrapper; got "
                + $"BoxKind.{rootBox.Kind}. The wrong kind would silently emit no "
                + "table content, hiding the integration bug.",
                nameof(rootBox));
        }

        if (incomingContinuation is not null)
        {
            // A future TableContinuation will resume at a given row
            // index. The current implementation doesn't yet support
            // mid-table page resume — fail loud so the caller surfaces
            // the missing capability instead of silently restarting.
            throw new ArgumentException(
                "TableLayouter does not yet support resume-after-"
                + "PageComplete (multi-fragmentainer table splitting is "
                + "deferred — see docs/deferrals.md#table-auto-fixed-spans-borders). "
                + "Pass null incomingContinuation.",
                nameof(incomingContinuation));
        }

        _rootBox = rootBox;
        _sink = sink;
        // incomingContinuation discarded after the null-validation
        // above. See field-removal comment + Copilot review on PR #49.
        _diagnostics = diagnostics;
        _shaperResolver = shaperResolver;
    }

    /// <summary>Pre-emit context describing where in the fragmentainer
    /// the rows should land. Set by the caller via
    /// <see cref="ConfigureEmission"/> BEFORE
    /// <see cref="AttemptLayout"/> runs.
    ///
    /// <para>The dispatch site (<see cref="BlockLayouter"/>) computes
    /// the wrapper's content-area top-left + content-inline-size from
    /// the wrapper's box-model reads. The TableLayouter consumes those
    /// values rather than re-deriving them — the outer block path
    /// already paid for the reads + we want one source of truth for
    /// the wrapper's geometry.</para></summary>
    /// <param name="contentInlineOffset">Inline-axis position (CSS px
    /// from the fragmentainer's content-area origin) of the wrapper's
    /// content-box inline-start edge.</param>
    /// <param name="contentBlockOffset">Block-axis position (CSS px
    /// from the fragmentainer's content-area origin) of the wrapper's
    /// content-box block-start edge (= wrapper's border-box top +
    /// border-block-start + padding-block-start).</param>
    /// <param name="contentInlineSize">Wrapper's content-box inline
    /// extent (CSS px) = available width to split across columns.</param>
    public void ConfigureEmission(
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize)
    {
        _contentInlineOffset = contentInlineOffset;
        _contentBlockOffset = contentBlockOffset;
        _contentInlineSize = contentInlineSize;
        _emissionConfigured = true;
    }

    private double _contentInlineOffset;
    private double _contentBlockOffset;
    private double _contentInlineSize;
    private bool _emissionConfigured;

    // ====================================================================
    //  Cached measure-phase state (Finding 1 + 2 hardening).
    //  MeasureContentHeight populates these; AttemptLayout consumes them.
    //  Two-phase protocol: dispatcher calls MeasureContentHeight first to
    //  size the wrapper border-box, then AttemptLayout to commit emits.
    // ====================================================================

    /// <summary>Sub-cycle 2 — placement record produced by the 2D
    /// occupancy-grid algorithm. Each <see cref="BoxKind.TableCell"/>
    /// in the table corresponds to exactly one
    /// <see cref="CellPlacement"/> anchored at
    /// (<see cref="OriginRow"/>, <see cref="OriginCol"/>); the cell
    /// also occupies the slot rectangle
    /// <c>[OriginRow..OriginRow+RowSpan-1] × [OriginCol..OriginCol+ColSpan-1]</c>.
    /// Continuation slots (non-origin slots inside the rectangle) are
    /// marked occupied in the local occupancy grid built by
    /// <see cref="PlaceCellsOntoGrid"/> but have no
    /// <see cref="CellPlacement"/> record of their own — the emit
    /// pass skips them.
    ///
    /// <para>Per Finding 5 — <see cref="DiagnosticsBuffer"/> captures
    /// the cell's per-content-layout diagnostics; flushed to the
    /// outer sink in <see cref="AttemptLayout"/>'s emit pass so
    /// uncommitted measure-pass diagnostics don't leak when the
    /// outer resolver discards the table.</para></summary>
    private readonly struct CellPlacement
    {
        public CellPlacement(
            Box cell,
            int originRow,
            int originCol,
            int rowSpan,
            int colSpan,
            MeasuringFragmentSink contentBuffer,
            BufferingDiagnosticsSink? diagnosticsBuffer,
            double contentBlockExtent)
        {
            Cell = cell;
            OriginRow = originRow;
            OriginCol = originCol;
            RowSpan = rowSpan;
            ColSpan = colSpan;
            ContentBuffer = contentBuffer;
            DiagnosticsBuffer = diagnosticsBuffer;
            ContentBlockExtent = contentBlockExtent;
        }
        public Box Cell { get; }
        public int OriginRow { get; }
        public int OriginCol { get; }
        public int RowSpan { get; }
        public int ColSpan { get; }
        public MeasuringFragmentSink ContentBuffer { get; }
        public BufferingDiagnosticsSink? DiagnosticsBuffer { get; }
        public double ContentBlockExtent { get; }
    }

    /// <summary>Per-row record (post-sub-cycle 2) — stores the row's
    /// finalized height. Per-cell placements are stored in
    /// <see cref="_measuredPlacements"/> rather than per-row because a
    /// rowspan cell logically belongs to multiple rows.</summary>
    private readonly struct RowMeasurement
    {
        public RowMeasurement(Box row, double rowHeight)
        {
            Row = row;
            RowHeight = rowHeight;
        }
        public Box Row { get; }
        public double RowHeight { get; }
    }

    private bool _measureDone;
    private double _measuredContentHeight;
    private int _measuredColumnCount;
    private double _measuredColumnWidth;
    private List<RowMeasurement>? _measuredRows;
    private List<CellPlacement>? _measuredPlacements;
    private List<Box>? _measuredCaptions; // wrapper-direct caption children
    private bool _measuredMissingGrid;
    // Per Finding 6 — cells whose rowspan / colspan attribute parsed as
    // exactly 0 (HTML5 §4.9.11 "remainder" semantic), captured during
    // PlaceCellsOntoGrid + drained as deferral diagnostics from
    // EmitDeferralDiagnostics. Each entry records the cell + which
    // axis (`true` = rowspan, `false` = colspan).
    private List<(Box Cell, bool IsRowspan)>? _measuredSpanZeroNotes;
    // Per Finding 4 — true when PlaceCellsOntoGrid exceeded the
    // MaxOccupiedSlots budget + capped subsequent cells at
    // colspan = rowspan = 1. Drained as a LAYOUT-TABLE-SLOT-BUDGET-
    // EXCEEDED-001 diagnostic from EmitDeferralDiagnostics.
    private bool _measuredSlotBudgetExceeded;
    private long _measuredSlotsUsed;

    /// <inheritdoc />
    public LayoutAttemptResult AttemptLayout(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        ArgumentNullException.ThrowIfNull(resolver);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_emissionConfigured)
        {
            throw new InvalidOperationException(
                "TableLayouter.AttemptLayout was called before "
                + "ConfigureEmission. The dispatching BlockLayouter must "
                + "supply the wrapper's content-area geometry so the "
                + "layouter knows where to anchor the rows.");
        }

        // Two-phase protocol: dispatcher should have called
        // MeasureContentHeight first. If it didn't, run the measure
        // pass lazily now — keeps direct-construction call sites (older
        // tests) working without the explicit pre-measure step.
        if (!_measureDone)
        {
            _ = MeasureContentHeight(fragmentainer, ref layout, cancellationToken);
        }

        // Emit caption diagnostics + missing-grid diagnostic first.
        EmitDeferralDiagnostics(ref layout);

        if (_measuredMissingGrid)
        {
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        var rows = _measuredRows;
        if (rows is null || rows.Count == 0)
        {
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        if (_measuredColumnCount == 0 || _measuredColumnWidth <= 0)
        {
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        var columnWidth = _measuredColumnWidth;
        var placements = _measuredPlacements ?? new List<CellPlacement>(0);

        // Per Finding 5 — flush each cell's BufferingDiagnosticsSink to
        // the outer diagnostic sink now that AttemptLayout has committed
        // to running the emit pass. Before this commit, cell-internal
        // diagnostics from the measure pass are buffered — if the outer
        // resolver had rewound + discarded this layouter's work the
        // buffer would have been dropped + the user would not see
        // diagnostics for never-emitted fragments.
        var commitDiagSink = layout.Diagnostics ?? _diagnostics;
        if (commitDiagSink is not null)
        {
            for (var pIdx = 0; pIdx < placements.Count; pIdx++)
            {
                placements[pIdx].DiagnosticsBuffer?.FlushTo(commitDiagSink);
            }
        }

        // Per Finding 3 — emit in three phases for the WHOLE table so
        // the painter sees backgrounds before borders before content
        // across all rows AND across all cells regardless of rowspan.
        //
        //   Phase A: row fragments for ALL rows.
        //   Phase B: cell fragments for ALL cells (only at origin).
        //   Phase C: cell-content fragments via FlushTo.
        //
        // Pre-Finding-3 the loop emitted row → cells-at-this-row →
        // content for each row in turn; a rowspan cell anchored at
        // row r flushed its content immediately, then row r+1's
        // background painted over it.
        //
        // Per Copilot perf #1 — group placements by origin row so the
        // Phase B + Phase C loops don't iterate the full placements
        // list per row (O(R × P) → O(R + P)). The document-order
        // sequence inside each group is preserved by walking
        // placements once + appending to its origin-row bucket.
        //
        // Per Copilot perf #2 — pre-compute a prefix-sum row-end array
        // so a rowspan cell's block extent is a single subtraction
        // instead of a `for k in 0..RowSpan` loop.
        var rowBlockOffsets = new double[rows.Count];
        var rowEndBlockOffset = new double[rows.Count + 1];
        var rowCursorBlock = _contentBlockOffset;
        rowEndBlockOffset[0] = rowCursorBlock;
        for (var r = 0; r < rows.Count; r++)
        {
            rowBlockOffsets[r] = rowCursorBlock;
            rowCursorBlock += rows[r].RowHeight;
            rowEndBlockOffset[r + 1] = rowCursorBlock;
        }

        // Group placements by origin row. Each row's bucket preserves
        // document order because we walk placements in document order.
        // Empty rows get null buckets (no allocation cost).
        var placementsByRow = new List<CellPlacement>?[rows.Count];
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            var p = placements[pIdx];
            var bucket = placementsByRow[p.OriginRow];
            if (bucket is null)
            {
                bucket = new List<CellPlacement>(capacity: 2);
                placementsByRow[p.OriginRow] = bucket;
            }
            bucket.Add(p);
        }

        // Phase A — emit row fragments for every row. Even an "empty"
        // row gets a (degenerate) fragment because its rowHeight may
        // be 0 (rowspan continuation slots from earlier rows kept the
        // row in the row list).
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowMeasure = rows[r];
            _sink.Emit(new BoxFragment(
                Box: rowMeasure.Row,
                InlineOffset: _contentInlineOffset,
                BlockOffset: rowBlockOffsets[r],
                InlineSize: _contentInlineSize,
                BlockSize: rowMeasure.RowHeight));
        }

        // Phase B — emit cell fragments for every placement anchored
        // in each row, in document order across rows. Sizing uses the
        // prefix-sum to compute block extent in O(1).
        for (var r = 0; r < rows.Count; r++)
        {
            var bucket = placementsByRow[r];
            if (bucket is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < bucket.Count; i++)
            {
                var placement = bucket[i];
                var cellInlineOffset = _contentInlineOffset
                    + (placement.OriginCol * columnWidth);
                var cellInlineSize = placement.ColSpan * columnWidth;
                var cellBlockOffset = rowBlockOffsets[r];
                var cellBlockSize =
                    rowEndBlockOffset[placement.OriginRow + placement.RowSpan]
                    - rowEndBlockOffset[placement.OriginRow];
                _sink.Emit(new BoxFragment(
                    Box: placement.Cell,
                    InlineOffset: cellInlineOffset,
                    BlockOffset: cellBlockOffset,
                    InlineSize: cellInlineSize,
                    BlockSize: cellBlockSize));
            }
        }

        // Phase C — drain each cell's buffered content fragments via
        // FlushTo with the cell's finalized block origin. The
        // resulting outer-sink order (across all rows, all cells, all
        // content) is paint-safe: rows → cells → cell content.
        for (var r = 0; r < rows.Count; r++)
        {
            var bucket = placementsByRow[r];
            if (bucket is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < bucket.Count; i++)
            {
                var placement = bucket[i];
                placement.ContentBuffer.FlushTo(_sink, rowBlockOffsets[r]);
            }
        }

        // Forced-overflow detection — emit once if the row stack
        // extends past the fragmentainer block-size. Multi-page table
        // splitting is deferred — see
        // docs/deferrals.md#table-auto-fixed-spans-borders.
        var totalRowsBottom = rowEndBlockOffset[rows.Count];
        if (totalRowsBottom > fragmentainer.BlockSize)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    $"TableLayouter: table on fragmentainer page index "
                    + $"{fragmentainer.PageIndex} overflows page block-"
                    + $"size {fragmentainer.BlockSize:0.##} (rows extend to "
                    + $"{totalRowsBottom:0.##}). The layouter commits all "
                    + "rows anyway; multi-fragmentainer splitting is "
                    + "deferred — see "
                    + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }

        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 1) —
    /// pre-compute the total row-stack block extent for the table's
    /// content area, WITHOUT forwarding fragments to the outer sink.
    /// The dispatching <see cref="BlockLayouter"/> calls this BEFORE
    /// emitting the wrapper fragment so it can size the wrapper's
    /// border-box block extent to <c>max(cssHeight, tableContentHeight)</c>;
    /// the wrapper's emitted extent then drives
    /// <see cref="FragmentainerContext.UsedBlockSize"/>, preventing
    /// siblings from overlapping the table's rows.
    ///
    /// <para>Per Finding 2 — cell-content layout uses a buffering
    /// <see cref="MeasuringFragmentSink"/>; the buffers are retained
    /// on this layouter for the subsequent emit pass to drain via
    /// <see cref="MeasuringFragmentSink.FlushTo"/>.</para>
    ///
    /// <para>Per Finding 3 — each cell-content layout dispatches
    /// against a FRESH <see cref="BreakResolver"/> scoped to that cell
    /// so the outer resolver's checkpoint state is preserved
    /// untouched.</para>
    ///
    /// <para>Idempotent — calling twice returns the cached value
    /// without re-running the cell layouts.</para>
    /// </summary>
    /// <returns>Total content block-axis height (sum of row heights;
    /// 0 when no rows are present or the wrapper has no
    /// <see cref="BoxKind.TableGrid"/> child).</returns>
    public double MeasureContentHeight(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        cancellationToken.ThrowIfCancellationRequested();

        if (_measureDone)
        {
            return _measuredContentHeight;
        }

        if (!_emissionConfigured)
        {
            throw new InvalidOperationException(
                "TableLayouter.MeasureContentHeight was called before "
                + "ConfigureEmission. The dispatching BlockLayouter must "
                + "supply the wrapper's content-area geometry (specifically "
                + "the content inline-size) so the layouter can split "
                + "columns + measure cell content.");
        }

        // Collect captions (direct wrapper children) for the deferral
        // diagnostic emitted from AttemptLayout. Per BoxBuilder Rec 5
        // captions stay at the wrapper level (NOT inside the grid).
        _measuredCaptions = CollectCaptions(_rootBox);

        // Locate the TableGrid child.
        var grid = FindTableGrid(_rootBox);
        if (grid is null)
        {
            _measuredMissingGrid = true;
            _measureDone = true;
            return 0;
        }

        // Collect rows in document order (recursing into row groups).
        var rows = new List<Box>();
        CollectRows(grid, rows, cancellationToken);

        if (rows.Count == 0)
        {
            _measureDone = true;
            return 0;
        }

        // Sub-cycle 2 — place cells onto a 2D occupancy grid using the
        // HTML5 forming-a-table algorithm. PlaceCells produces the
        // CellPlacement list + the column count = max occupied
        // column + 1 across rows.
        var placements = new List<CellPlacement>();
        var spanZeroNotes = new List<(Box Cell, bool IsRowspan)>();
        var (columnCount, slotBudgetExceeded, slotsUsed) =
            PlaceCellsOntoGrid(rows, placements, spanZeroNotes, cancellationToken);

        _measuredColumnCount = columnCount;
        _measuredPlacements = placements;
        _measuredSpanZeroNotes = spanZeroNotes.Count > 0 ? spanZeroNotes : null;
        _measuredSlotBudgetExceeded = slotBudgetExceeded;
        _measuredSlotsUsed = slotsUsed;

        if (columnCount == 0)
        {
            _measureDone = true;
            return 0;
        }

        var columnWidth = _contentInlineSize / columnCount;
        _measuredColumnWidth = columnWidth;
        if (columnWidth <= 0)
        {
            _measureDone = true;
            return 0;
        }

        // Sub-cycle 2 — measure each cell's content into its buffer.
        // The buffer captures the inline translation (the cell's column
        // origin) at Emit time but defers the block translation until
        // FlushTo — because the block origin depends on the row's
        // height which can't be finalized until all cells are measured
        // (rowspan cells in particular force a second-pass distribution
        // that may extend row heights).
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placement = placements[pIdx];
            var cellInlineOffset = _contentInlineOffset
                + (placement.OriginCol * columnWidth);
            var cellInlineSize = placement.ColSpan * columnWidth;
            var (buffer, diagBuffer) = MeasureCellContent(
                cellBox: placement.Cell,
                cellInlineOffset: cellInlineOffset,
                cellInlineSize: cellInlineSize,
                fragmentainer: fragmentainer,
                layout: ref layout,
                cancellationToken: cancellationToken);
            placements[pIdx] = new CellPlacement(
                cell: placement.Cell,
                originRow: placement.OriginRow,
                originCol: placement.OriginCol,
                rowSpan: placement.RowSpan,
                colSpan: placement.ColSpan,
                contentBuffer: buffer,
                diagnosticsBuffer: diagBuffer,
                contentBlockExtent: buffer.MaxBlockExtentFromCellOrigin);
        }

        // Sub-cycle 2 — row-height pass.
        //   Pass A: rowHeight[r] = max content extent over cells with
        //           rowspan=1 anchored at row r.
        //   Pass B: for each cell with rowspan>1 (ascending rowspan),
        //           extend rowHeight[originRow+rowspan-1] with any
        //           excess content height not covered by the natural
        //           row-height sum across the cell's span.
        //
        // Why ascending rowspan in pass B? So shorter-span cells
        // settle the heights of the rows they cover before longer-
        // span cells consult those heights. This is NOT the CSS
        // Tables L3 §11 distribution-proportional algorithm — that's
        // sub-cycle 3 work — but it's deterministic + simple.
        var rowHeights = new double[rows.Count];
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            var placement = placements[pIdx];
            if (placement.RowSpan != 1) continue;
            if (placement.ContentBlockExtent > rowHeights[placement.OriginRow])
            {
                rowHeights[placement.OriginRow] = placement.ContentBlockExtent;
            }
        }

        // Collect rowspan>1 placement indices + sort by ascending
        // RowSpan. Manual selection-style sort to avoid LINQ
        // (hot-path discipline — CLAUDE.md cross-cutting rule 5).
        var spanIndices = new List<int>();
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            if (placements[pIdx].RowSpan > 1)
            {
                spanIndices.Add(pIdx);
            }
        }
        // Simple insertion sort by RowSpan (typically very few span
        // cells per table).
        for (var i = 1; i < spanIndices.Count; i++)
        {
            var k = spanIndices[i];
            var kSpan = placements[k].RowSpan;
            var j = i - 1;
            while (j >= 0 && placements[spanIndices[j]].RowSpan > kSpan)
            {
                spanIndices[j + 1] = spanIndices[j];
                j--;
            }
            spanIndices[j + 1] = k;
        }

        for (var s = 0; s < spanIndices.Count; s++)
        {
            var placement = placements[spanIndices[s]];
            var spanned = 0.0;
            for (var k = 0; k < placement.RowSpan; k++)
            {
                spanned += rowHeights[placement.OriginRow + k];
            }
            if (placement.ContentBlockExtent > spanned)
            {
                rowHeights[placement.OriginRow + placement.RowSpan - 1]
                    += (placement.ContentBlockExtent - spanned);
            }
        }

        // Materialize the per-row RowMeasurement list + total height.
        var measured = new List<RowMeasurement>(capacity: rows.Count);
        var totalContentHeight = 0.0;
        for (var r = 0; r < rows.Count; r++)
        {
            measured.Add(new RowMeasurement(rows[r], rowHeights[r]));
            totalContentHeight += rowHeights[r];
        }

        _measuredRows = measured;
        _measuredContentHeight = totalContentHeight;
        _measureDone = true;
        return totalContentHeight;
    }

    /// <summary>Per Finding 4 — DoS-resistant cap on the cumulative
    /// <c>rowspan × colspan</c> slot count for one table. Legal HTML
    /// attribute values give a single cell up to
    /// <c>65534 × 1000 = ~65M</c> slots; without a cap a hostile
    /// author can force unbounded CPU + memory work in the placement
    /// pass. 1M is generous for legitimate documents (a 1000-row × 1000-
    /// column matrix) yet keeps placement bounded.</summary>
    private const long MaxOccupiedSlots = 1_000_000;

    /// <summary>Sub-cycle 2 + Finding 4 + 6 — place each cell onto the
    /// 2D occupancy grid via the HTML5 "Forming a table" algorithm
    /// (CSS Tables L3 §3). For each row in document order: reset a
    /// column cursor to 0; for each <see cref="BoxKind.TableCell"/>
    /// child of the row, advance the cursor past any slots occupied by
    /// rowspan continuations from earlier rows, then anchor the cell at
    /// the cursor + mark its <c>rowspan × colspan</c> slot rectangle as
    /// occupied.
    ///
    /// <para><b>Per Finding 4 hardening:</b></para>
    /// <list type="bullet">
    ///   <item><b>Interval-list occupancy</b> — per-row occupied
    ///     ranges stored as a sorted <c>List&lt;(startCol, endCol)&gt;</c>.
    ///     Slot-query = binary search; range insertion = merge with
    ///     adjacent intervals. Drops the per-cell complexity from
    ///     O(rowspan × colspan) hash insertions to O(rowspan × log).</item>
    ///   <item><b>Slot budget</b> — cumulative <c>rowspan × colspan</c>
    ///     across cells is bounded at <see cref="MaxOccupiedSlots"/>
    ///     (1M). Cells crossing the budget are capped at
    ///     <c>rowspan = colspan = 1</c>; the caller emits
    ///     <c>LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001</c>.</item>
    ///   <item><b>Current-row optimization</b> — rowspan continuations
    ///     only mark slots in the ROWS BELOW the origin. The current
    ///     row's own colspan slots are skipped naturally by the column
    ///     cursor advancing past <c>colSpan</c>; marking them in the
    ///     occupancy list would be redundant work.</item>
    ///   <item><b>Cancellation</b> — checked per row-iteration so a
    ///     budget-respecting but slow placement still responds to
    ///     cancellation.</item>
    /// </list>
    /// <para>Per Finding 6 — cells whose rowspan / colspan attribute
    /// parsed as exactly <c>0</c> (HTML5 §4.9.11 "remainder" semantic
    /// — currently deferred) are appended to <paramref name="spanZeroNotes"/>
    /// for the caller to surface as
    /// <c>LAYOUT-TABLE-FEATURE-UNSUPPORTED-001</c> diagnostics.</para>
    /// </summary>
    /// <returns>Tuple <c>(columnCount, slotBudgetExceeded, slotsUsed)</c>.
    /// <c>columnCount</c> = max(occupied column index) + 1 across all
    /// rows; 0 when no cells were placed.</returns>
    private static (int columnCount, bool slotBudgetExceeded, long slotsUsed) PlaceCellsOntoGrid(
        List<Box> rows,
        List<CellPlacement> placements,
        List<(Box Cell, bool IsRowspan)> spanZeroNotes,
        CancellationToken cancellationToken)
    {
        // occupancy[r] = sorted, non-overlapping interval list of
        // occupied column ranges in row r. Each interval is
        // [startCol, endColExclusive). Lazily allocated so empty rows
        // stay cheap.
        var occupancy = new List<(int StartCol, int EndColExclusive)>?[rows.Count];

        var columnCount = 0;
        long slotsUsed = 0;
        var slotBudgetExceeded = false;
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[r];
            var colCursor = 0;
            for (var i = 0; i < row.Children.Count; i++)
            {
                var ch = row.Children[i];
                if (ch.Kind != BoxKind.TableCell)
                {
                    continue;
                }
                var (rowSpan, colSpan, rowSpanWasZero, colSpanWasZero) = ReadSpans(ch);
                if (rowSpanWasZero) spanZeroNotes.Add((ch, IsRowspan: true));
                if (colSpanWasZero) spanZeroNotes.Add((ch, IsRowspan: false));

                // Per Finding 4 — slot budget check. Compute the
                // cell's contribution PRE-clamping (since the clamp
                // below changes the placement geometry). Past the cap,
                // cap the cell to 1×1 + flag the budget-exceeded
                // condition for the deferral diagnostic emit. Don't
                // crash — truncate.
                var rowSpanForBudget = rowSpan;
                var colSpanForBudget = colSpan;
                long cellSlots;
                if (slotBudgetExceeded)
                {
                    rowSpanForBudget = 1;
                    colSpanForBudget = 1;
                    cellSlots = 1;
                }
                else
                {
                    cellSlots = (long)rowSpanForBudget * colSpanForBudget;
                    if (slotsUsed + cellSlots > MaxOccupiedSlots)
                    {
                        slotBudgetExceeded = true;
                        rowSpanForBudget = 1;
                        colSpanForBudget = 1;
                        cellSlots = 1;
                    }
                }
                slotsUsed += cellSlots;

                // Advance past slots already occupied by earlier rows'
                // rowspan continuations.
                var rowOccupancy = occupancy[r];
                if (rowOccupancy is not null)
                {
                    while (IntervalListContains(rowOccupancy, colCursor))
                    {
                        colCursor++;
                    }
                }

                // Anchor at (r, colCursor) + mark the slot rectangle.
                placements.Add(new CellPlacement(
                    cell: ch,
                    originRow: r,
                    originCol: colCursor,
                    rowSpan: rowSpanForBudget,
                    colSpan: colSpanForBudget,
                    contentBuffer: null!, // assigned during measure pass
                    diagnosticsBuffer: null,
                    contentBlockExtent: 0));

                // Per Finding 4 #3 — only mark ROWS BELOW the origin
                // for rowspan>1 (the current row's own colspan slots
                // are skipped naturally by the column cursor advancing
                // past colSpan). For rowspan=1 cells nothing needs
                // marking in the occupancy list — the cursor advance
                // covers the current row.
                if (rowSpanForBudget > 1)
                {
                    for (var rr = 1; rr < rowSpanForBudget; rr++)
                    {
                        var targetRow = r + rr;
                        if (targetRow >= rows.Count)
                        {
                            // rowSpan extends past the table — HTML5
                            // §4.9.11 step 14 clamps to the available
                            // rows. We bail out of the marking loop;
                            // the placement's recorded rowSpan is
                            // clamped below.
                            break;
                        }
                        var slotList = occupancy[targetRow];
                        if (slotList is null)
                        {
                            slotList = new List<(int, int)>(capacity: 4);
                            occupancy[targetRow] = slotList;
                        }
                        IntervalListInsert(slotList, colCursor, colCursor + colSpanForBudget);
                    }
                }

                // Track the max column index observed (= colCursor +
                // colSpanForBudget - 1; columnCount = max + 1).
                var maxColInThisCell = colCursor + colSpanForBudget;
                if (maxColInThisCell > columnCount)
                {
                    columnCount = maxColInThisCell;
                }

                colCursor += colSpanForBudget;
            }
        }

        // If a cell's recorded rowSpan exceeded the table's row count,
        // clamp it now so the emit pass's
        // rowHeights[originRow+rowSpan-1] access stays in-bounds.
        for (var i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            if (p.OriginRow + p.RowSpan > rows.Count)
            {
                var clampedSpan = rows.Count - p.OriginRow;
                if (clampedSpan < 1) clampedSpan = 1;
                placements[i] = new CellPlacement(
                    cell: p.Cell,
                    originRow: p.OriginRow,
                    originCol: p.OriginCol,
                    rowSpan: clampedSpan,
                    colSpan: p.ColSpan,
                    contentBuffer: p.ContentBuffer,
                    diagnosticsBuffer: p.DiagnosticsBuffer,
                    contentBlockExtent: p.ContentBlockExtent);
            }
        }

        return (columnCount, slotBudgetExceeded, slotsUsed);
    }

    /// <summary>Per Finding 4 — binary-search the per-row sorted
    /// interval list for the rightmost interval whose
    /// <c>StartCol &lt;= col</c>; if found, the slot at <paramref name="col"/>
    /// is occupied iff <c>col &lt; EndColExclusive</c>. Intervals are
    /// kept sorted + non-overlapping by
    /// <see cref="IntervalListInsert"/>.</summary>
    private static bool IntervalListContains(
        List<(int StartCol, int EndColExclusive)> list, int col)
    {
        // Standard binary search for the rightmost StartCol <= col.
        var lo = 0;
        var hi = list.Count - 1;
        var candidate = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (list[mid].StartCol <= col)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (candidate < 0) return false;
        return col < list[candidate].EndColExclusive;
    }

    /// <summary>Per Finding 4 — insert <c>[startCol, endColExclusive)</c>
    /// into the sorted, non-overlapping interval list, merging with
    /// any adjacent / overlapping intervals so the list invariant
    /// (sorted by <c>StartCol</c>, no two intervals touch or overlap)
    /// is preserved. The list is mutated in place. Empty input
    /// (<c>startCol &gt;= endColExclusive</c>) is a no-op.</summary>
    private static void IntervalListInsert(
        List<(int StartCol, int EndColExclusive)> list,
        int startCol, int endColExclusive)
    {
        if (startCol >= endColExclusive) return;
        // Find the first interval whose EndColExclusive >= startCol
        // (anything earlier is strictly to the left + doesn't touch
        // the new range; anything later potentially merges).
        var insertAt = 0;
        while (insertAt < list.Count && list[insertAt].EndColExclusive < startCol)
        {
            insertAt++;
        }
        // From insertAt forward, merge every interval whose StartCol
        // <= endColExclusive (touching = merge to coalesce the list).
        var mergedStart = startCol;
        var mergedEnd = endColExclusive;
        var removeUpTo = insertAt; // exclusive
        while (removeUpTo < list.Count && list[removeUpTo].StartCol <= mergedEnd)
        {
            if (list[removeUpTo].StartCol < mergedStart) mergedStart = list[removeUpTo].StartCol;
            if (list[removeUpTo].EndColExclusive > mergedEnd) mergedEnd = list[removeUpTo].EndColExclusive;
            removeUpTo++;
        }
        if (removeUpTo > insertAt)
        {
            list.RemoveRange(insertAt, removeUpTo - insertAt);
        }
        list.Insert(insertAt, (mergedStart, mergedEnd));
    }

    /// <summary>Emit the deferral / structural-anomaly diagnostics
    /// recorded during measure. Called from <see cref="AttemptLayout"/>
    /// once the dispatcher has committed to running the emit pass; we
    /// don't emit during measure because the measure pass may be
    /// called speculatively (e.g., from a wrapper-sizing pre-pass).
    /// </summary>
    private void EmitDeferralDiagnostics(ref LayoutContext layout)
    {
        var sink = layout.Diagnostics ?? _diagnostics;
        if (sink is null)
        {
            return;
        }

        if (_measuredMissingGrid)
        {
            // Per Finding 5 — this is a malformed-box-tree anomaly, NOT
            // a pagination overflow. Use the table-specific feature-
            // unsupported code so consumers don't see a misleading
            // overflow signal.
            OptimizingBreakResolver.SafeEmit(
                sink,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                    "TableLayouter: Table wrapper has no TableGrid child — "
                    + "malformed box tree from BoxBuilder. Table content "
                    + "silently dropped. This is a box-generation invariant "
                    + "violation, not a pagination overflow.",
                    PaginateDiagnosticSeverity.Warning));
        }

        // Sub-cycle 2 — the colspan / rowspan deferral diagnostic is
        // gone; the 2D occupancy-grid algorithm now correctly merges
        // cells. The LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 code stays
        // in the diagnostic catalog because captions + the missing-
        // TableGrid anomaly still emit it.

        // Per Finding 6 — rowspan="0" / colspan="0" HTML5 §4.9.11
        // "remainder of row-group / column-group" semantics aren't
        // implemented; PlaceCellsOntoGrid clamped them to 1 + recorded
        // each occurrence here for surfacing as a deferral diagnostic.
        if (_measuredSpanZeroNotes is { Count: > 0 } spanZeroNotes)
        {
            for (var i = 0; i < spanZeroNotes.Count; i++)
            {
                var (cell, isRowspan) = spanZeroNotes[i];
                var axis = isRowspan ? "rowspan" : "colspan";
                var cellSnippet = NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(
                    cell.SourceElement?.TagName ?? "<anonymous>",
                    maxLength: 40);
                OptimizingBreakResolver.SafeEmit(
                    sink,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                        $"TableLayouter: {axis}=\"0\" on <{cellSnippet}> cell "
                        + "clamped to 1 — HTML5 §4.9.11 \"spans remainder of "
                        + "row-group / column-group\" semantics are deferred. "
                        + "See docs/deferrals.md#table-auto-fixed-spans-borders.",
                        PaginateDiagnosticSeverity.Warning));
            }
        }

        // Per Finding 4 — slot-budget exceeded; PlaceCellsOntoGrid capped
        // subsequent cells at rowspan = colspan = 1 once the cumulative
        // span product crossed MaxOccupiedSlots. The table still renders
        // but spans past the cap were truncated.
        if (_measuredSlotBudgetExceeded)
        {
            OptimizingBreakResolver.SafeEmit(
                sink,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutTableSlotBudgetExceeded001,
                    $"TableLayouter: cumulative rowspan x colspan slot count "
                    + $"exceeded the {MaxOccupiedSlots} DoS budget "
                    + $"(reached {_measuredSlotsUsed}). Subsequent cells were "
                    + "capped at rowspan = colspan = 1 — the table still "
                    + "renders but the author's recorded spans past the cap "
                    + "were truncated. This guard defends against hostile "
                    + "HTML with attribute values like "
                    + "rowspan=\"65534\" colspan=\"1000\".",
                    PaginateDiagnosticSeverity.Warning));
        }

        if (_measuredCaptions is { Count: > 0 } captions)
        {
            for (var i = 0; i < captions.Count; i++)
            {
                // Per Finding 7 — sanitize the caption text snippet
                // before it flows into the diagnostic message. Author-
                // controlled caption content can carry C0/C1 control
                // chars (ANSI escape injection / log-parser confusion)
                // that ExtractCaptionTextSnippet (line-break-only
                // collapse) wouldn't catch.
                var captionText = NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(
                    ExtractCaptionTextSnippet(captions[i]),
                    maxLength: 80);
                OptimizingBreakResolver.SafeEmit(
                    sink,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                        $"TableLayouter: table caption skipped — captions are "
                        + $"not yet laid out per CSS Tables L3 §11; sub-cycle "
                        + $"3 work — see "
                        + $"docs/deferrals.md#table-auto-fixed-spans-borders. "
                        + $"Caption text: \"{captionText}\".",
                        PaginateDiagnosticSeverity.Warning));
            }
        }
    }

    /// <summary>Sub-cycle 1 — find the wrapper's
    /// <see cref="BoxKind.TableGrid"/> child. Returns
    /// <see langword="null"/> when no grid is present (defensive —
    /// box generation should always insert one).</summary>
    private static Box? FindTableGrid(Box wrapper)
    {
        for (var i = 0; i < wrapper.Children.Count; i++)
        {
            if (wrapper.Children[i].Kind == BoxKind.TableGrid)
            {
                return wrapper.Children[i];
            }
        }
        return null;
    }

    /// <summary>Per Finding 4 — collect the wrapper's direct
    /// <see cref="BoxKind.TableCaption"/> children. Per BoxBuilder
    /// Rec 5 captions are kept under the wrapper (NOT inside the
    /// grid) so this walks the wrapper, not the grid.</summary>
    private static List<Box> CollectCaptions(Box wrapper)
    {
        var captions = new List<Box>();
        for (var i = 0; i < wrapper.Children.Count; i++)
        {
            if (wrapper.Children[i].Kind == BoxKind.TableCaption)
            {
                captions.Add(wrapper.Children[i]);
            }
        }
        return captions;
    }

    /// <summary>Per Finding 4 — extract a short text snippet from a
    /// caption box for inclusion in the deferral diagnostic. Walks the
    /// caption's descendants collecting TextRun text content, capped
    /// at 80 characters. The snippet lets authors identify WHICH
    /// caption is being dropped when a document has multiple
    /// tables.</summary>
    private static string ExtractCaptionTextSnippet(Box caption)
    {
        const int MaxLength = 80;
        var sb = new StringBuilder(capacity: MaxLength + 8);
        WalkForText(caption, sb, MaxLength);
        var raw = sb.ToString();
        // Collapse any embedded line breaks so the diagnostic stays
        // on one line.
        return raw.Replace('\n', ' ').Replace('\r', ' ').Trim();

        static void WalkForText(Box box, StringBuilder sb, int cap)
        {
            if (sb.Length >= cap) return;
            if (box.Kind == BoxKind.TextRun)
            {
                var text = box.Text;
                if (text.Length == 0) return;
                var remaining = cap - sb.Length;
                if (text.Length <= remaining)
                {
                    sb.Append(text);
                }
                else
                {
                    sb.Append(text, 0, remaining);
                    sb.Append('…');
                }
                return;
            }
            for (var i = 0; i < box.Children.Count; i++)
            {
                if (sb.Length >= cap) return;
                WalkForText(box.Children[i], sb, cap);
            }
        }
    }

    /// <summary>Sub-cycle 1 — walk <paramref name="grid"/>'s children
    /// in document order, appending every <see cref="BoxKind.TableRow"/>
    /// (including those nested inside <see cref="BoxKind.TableRowGroup"/>
    /// / <see cref="BoxKind.TableHeaderGroup"/> /
    /// <see cref="BoxKind.TableFooterGroup"/>) to
    /// <paramref name="rows"/>. Captions, column groups, columns are
    /// skipped — captions live at the wrapper level (Finding 4
    /// handles them); column groups + columns are sub-cycle 2+
    /// work.</summary>
    private static void CollectRows(Box grid, List<Box> rows, CancellationToken cancellationToken)
    {
        for (var i = 0; i < grid.Children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = grid.Children[i];
            switch (child.Kind)
            {
                case BoxKind.TableRow:
                    rows.Add(child);
                    break;
                case BoxKind.TableRowGroup:
                case BoxKind.TableHeaderGroup:
                case BoxKind.TableFooterGroup:
                    // Recurse one level — row groups can only contain
                    // rows per Tables L3 §10 anonymous-table-object rules.
                    for (var j = 0; j < child.Children.Count; j++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var inner = child.Children[j];
                        if (inner.Kind == BoxKind.TableRow)
                        {
                            rows.Add(inner);
                        }
                    }
                    break;
                // TableColumnGroup, TableColumn, TableCaption — skipped
                // per sub-cycle 1 deferrals.
                default:
                    break;
            }
        }
    }

    /// <summary>Sub-cycle 2 + Finding 6 — read the <c>rowspan</c> +
    /// <c>colspan</c> values off <paramref name="cell"/>'s source HTML
    /// element. The attribute is read directly from the DOM (the CSS
    /// counterparts <c>table-column-span</c> / <c>table-row-span</c>
    /// aren't part of the cascade yet — they're HTML attribute mapped
    /// per HTML5 §4.9.11).
    ///
    /// <para>Per HTML5 spec ranges:</para>
    /// <list type="bullet">
    ///   <item><c>colspan</c> defaults to 1; valid range
    ///     <c>[1, 1000]</c>. The special value <c>0</c> per HTML5
    ///     §4.9.11 means "spans all remaining columns in the column
    ///     group" — currently deferred (see
    ///     <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>);
    ///     the caller emits
    ///     <c>LAYOUT-TABLE-FEATURE-UNSUPPORTED-001</c> + clamps to 1.
    ///     Other out-of-range / non-numeric values fall back to 1.</item>
    ///   <item><c>rowspan</c> defaults to 1; valid range
    ///     <c>[1, 65534]</c>. The special value <c>0</c> per HTML5
    ///     §4.9.11 means "spans all remaining rows in the row group
    ///     / table section" — currently deferred; the caller emits
    ///     <c>LAYOUT-TABLE-FEATURE-UNSUPPORTED-001</c> + clamps to 1.
    ///     Other out-of-range / non-numeric values fall back to 1.</item>
    /// </list>
    /// <para>Anonymous cells (no <see cref="Box.SourceElement"/>) get
    /// the default <c>(1, 1)</c> spans with both <c>zero</c> flags
    /// false.</para></summary>
    /// <returns>Tuple <c>(rowSpan, colSpan, rowSpanWasZero,
    /// colSpanWasZero)</c>. The two boolean flags signal that the
    /// underlying attribute parsed as <c>0</c> + was clamped to 1
    /// (Finding 6 deferral diagnostic); other clamps don't surface a
    /// diagnostic.</returns>
    private static (int rowSpan, int colSpan, bool rowSpanWasZero, bool colSpanWasZero) ReadSpans(Box cell)
    {
        var el = cell.SourceElement;
        if (el is null)
        {
            return (1, 1, false, false);
        }
        var (colSpan, colSpanWasZero) = ParseSpanAttribute(
            el.GetAttribute("colspan"),
            maxValue: 1000);
        var (rowSpan, rowSpanWasZero) = ParseSpanAttribute(
            el.GetAttribute("rowspan"),
            maxValue: 65534);
        return (rowSpan, colSpan, rowSpanWasZero, colSpanWasZero);
    }

    /// <summary>Sub-cycle 2 + Finding 6 — parse a colspan / rowspan
    /// attribute value into an integer span, defaulting to 1 on
    /// null/empty, non-numeric, or out-of-range input. The
    /// <c>wasZero</c> flag is true when the attribute parsed to
    /// exactly <c>0</c> + was clamped to 1 (HTML5 §4.9.11 "remainder"
    /// semantics — currently deferred; the caller surfaces a
    /// <c>LAYOUT-TABLE-FEATURE-UNSUPPORTED-001</c> diagnostic so
    /// authors see the unimplemented semantic).</summary>
    private static (int value, bool wasZero) ParseSpanAttribute(string? raw, int maxValue)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (1, false);
        }
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return (1, false);
        }
        if (n == 0)
        {
            // HTML5 §4.9.11 — span="0" means "spans the remainder of
            // the row group / column group". Currently deferred (see
            // docs/deferrals.md#table-auto-fixed-spans-borders); the
            // caller clamps to 1 + emits the deferral diagnostic. Other
            // less-than-1 values (negative numbers) silently fall back
            // to 1 because HTML5 treats them as malformed.
            return (1, true);
        }
        if (n < 1) return (1, false);
        if (n > maxValue) return (maxValue, false);
        return (n, false);
    }

    /// <summary>Sub-cycle 1 + 2 — lay out <paramref name="cellBox"/>'s
    /// inner content via a nested <see cref="BlockLayouter"/>,
    /// BUFFERING the translated fragments in a
    /// <see cref="MeasuringFragmentSink"/> for later flush. Returns
    /// the buffer (carrying both the measured extent + the buffered
    /// fragments).
    ///
    /// <para>Per Finding 3 — the nested <see cref="BlockLayouter"/>
    /// runs against a FRESH <see cref="BreakResolver"/> scoped to the
    /// cell so the outer resolver's checkpoint state is preserved.
    /// </para>
    ///
    /// <para><b>Sub-cycle 2 — deferred block translation.</b> Pre-
    /// sub-cycle-2 the cell's block-axis origin was baked into the
    /// buffered fragments at Emit time; sub-cycle 2 defers it because
    /// rowspan distribution can extend row heights AFTER the measure
    /// pass observes content extents. The buffer applies inline
    /// translation eagerly at Emit, but block translation is added at
    /// <see cref="MeasuringFragmentSink.FlushTo"/> time once row
    /// heights have been finalized.</para></summary>
    /// <param name="cellBox">The <see cref="BoxKind.TableCell"/>
    /// box. The nested BlockLayouter treats this as a fresh root —
    /// its children lay out within the cell's allocated column.</param>
    /// <param name="cellInlineOffset">Inline-axis position of the
    /// cell's column-start edge in fragmentainer coordinates. Baked
    /// into the buffer at Emit time.</param>
    /// <param name="cellInlineSize">Inline extent of the cell's
    /// column (colspan-aware — colspan=N gives N × columnWidth).</param>
    /// <param name="fragmentainer">The outer fragmentainer; the nested
    /// layouter uses a scoped temporary to keep its own pagination
    /// accounting separate from the outer.</param>
    /// <param name="layout">The outer layout context (carries the
    /// diagnostic sink + counter state).</param>
    /// <param name="cancellationToken">Propagated to the inner
    /// layouter.</param>
    private (MeasuringFragmentSink Buffer, BufferingDiagnosticsSink? DiagnosticsBuffer) MeasureCellContent(
        Box cellBox,
        double cellInlineOffset,
        double cellInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        // The cell is a block-flow container. Wrap it in a Root box
        // would require allocating a parent — the simpler approach is
        // to give BlockLayouter the cell DIRECTLY as its root. The
        // BlockLayouter's child loop iterates `_rootBox.Children`, so
        // passing the cell makes it walk the cell's children +
        // produce fragments for them. The fragments arrive at the
        // measuring sink with offsets relative to the cell's
        // fragmentainer (which we synthesize below).
        //
        // Per sub-cycle 1 the cell content is treated as a single
        // "best effort" pass — no pagination splitting within a cell.

        // Sub-cycle 2 — pass 0 for the block translation. The block
        // origin can't be finalized until row-height distribution
        // completes (because rowspan cells may extend row heights
        // after the measure pass observes all content extents). The
        // emit pass passes the final cellBlockOffset to FlushTo,
        // which adds it to each buffered fragment's BlockOffset.
        var measuringSink = new MeasuringFragmentSink(
            outerSinkBaselineCursor: _sink.Cursor,
            inlineOffsetTranslation: cellInlineOffset,
            blockOffsetTranslation: 0);

        // Per CSS Tables L3 §11.5.3 — cell content lays out within the
        // cell's content area. Sub-cycle 1 doesn't yet read the cell's
        // padding/border (would require ComputedStyleLayoutExtensions
        // reads here); the cell's inline extent passed as the
        // fragmentainer's content-inline-size is the full column
        // width. Sub-cycle 2 will subtract padding + border to give
        // the cell-content area.
        var cellFragmentainer = new FragmentainerContext(
            contentInlineSize: cellInlineSize,
            blockSize: Math.Max(fragmentainer.BlockSize, 1));

        // Per Finding 5 — wrap the ambient diagnostic sink in a
        // BufferingDiagnosticsSink. Cell-internal diagnostics (e.g.,
        // a LAYOUT-INLINE-UNSUPPORTED-001 from a deeply-nested cell
        // content TextRun) are captured here + flushed only when the
        // outer AttemptLayout commits the table. If the outer resolver
        // discards the table (rewind / retry), the buffer is dropped
        // without leaking the cell-level emissions to the user. The
        // wrap is conditional on having a real ambient sink — anonymous
        // tables driven from tests that omit the diagnostic sink land
        // here with a null target + nothing to wrap.
        BufferingDiagnosticsSink? diagBuffer = null;
        IPaginateDiagnosticsSink? cellDiagnosticSink = layout.Diagnostics ?? _diagnostics;
        if (cellDiagnosticSink is not null)
        {
            diagBuffer = new BufferingDiagnosticsSink();
            cellDiagnosticSink = diagBuffer;
        }

        // The inner layout context carries forward the BUFFERED
        // diagnostic sink + writing mode but starts with the cell's
        // own available extents.
        var innerLayout = new LayoutContext(cellFragmentainer)
        {
            Diagnostics = cellDiagnosticSink,
            WritingMode = layout.WritingMode,
            IsRtl = layout.IsRtl,
        };

        using var cellLayouter = new BlockLayouter(
            rootBox: cellBox,
            sink: measuringSink,
            incomingContinuation: null,
            diagnostics: cellDiagnosticSink,
            shaperResolver: _shaperResolver);

        // Per Finding 3 — fresh BreakResolver scoped to the cell. The
        // outer resolver's checkpoint state is preserved untouched —
        // cell-internal pagination never modifies the outer table's
        // rewind / resume contract. The cell resolver's Dispose
        // releases any last-held checkpoint lease back to the pool.
        using var cellResolver = new BreakResolver();

        // Sub-cycle 1 — best-effort layout into the cell's column.
        // The inner LastResort strategy keeps the inner layouter
        // from returning NeedsRewind (which sub-cycle 1 has no path
        // to handle inside a cell).
        _ = cellLayouter.AttemptLayout(
            cellFragmentainer, ref innerLayout, cellResolver,
            LayoutAttemptStrategy.LastResort, cancellationToken);

        return (measuringSink, diagBuffer);
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 hardening (Finding 2) —
    /// fragment sink that BUFFERS emitted fragments (translating
    /// offsets from the cell's origin into fragmentainer coordinates)
    /// for later flush via <see cref="FlushTo"/>.
    ///
    /// <para>Pre-Finding-2 the sink forwarded translated fragments to
    /// the outer sink during measure, which produced a paint order
    /// where cell content fragments preceded the row + cell border-box
    /// fragments — so cell backgrounds / borders would paint OVER
    /// the text. Post-Finding-2 the sink retains the translated
    /// fragments in an internal list; the emit phase emits the row
    /// fragment, then the cell fragment, then calls
    /// <see cref="FlushTo"/> to drain the buffered content. The
    /// resulting outer-sink order — row → cell → cell content — is
    /// paint-safe.</para>
    ///
    /// <para><b>Rollback contract.</b> The inner
    /// <see cref="BlockLayouter"/> may call
    /// <see cref="IFragmentSink.RollbackTo"/> if it rewinds. Sub-
    /// cycle 1 uses the inner LastResort strategy which suppresses
    /// rewinds, so the rollback path isn't reached in practice. The
    /// sink still honors the contract — truncating the buffer to the
    /// requested cursor; the MaxBlockExtentFromCellOrigin field is
    /// left stale (over-estimated) per the documented sub-cycle 1
    /// approximation.</para></summary>
    internal sealed class MeasuringFragmentSink : IBlockFragmentSink
    {
        private readonly double _inlineTranslation;
        private readonly double _blockTranslation;
        private readonly int _outerCursorBaseline;
        private readonly List<BoxFragment> _buffered = new();

        public MeasuringFragmentSink(
            int outerSinkBaselineCursor,
            double inlineOffsetTranslation,
            double blockOffsetTranslation)
        {
            _inlineTranslation = inlineOffsetTranslation;
            _blockTranslation = blockOffsetTranslation;
            _outerCursorBaseline = outerSinkBaselineCursor;
        }

        /// <summary>Maximum block extent (BlockOffset + BlockSize)
        /// observed across forwarded fragments, expressed relative to
        /// the CELL'S origin (not the fragmentainer origin). Drives
        /// the row-height computation.</summary>
        public double MaxBlockExtentFromCellOrigin { get; private set; }

        /// <summary>Per Finding 2 — the cursor is the count of
        /// BUFFERED fragments, not a baselined outer cursor. The
        /// inner BlockLayouter's checkpoint capture uses this to
        /// record its emit point; rollback truncates the buffer to
        /// that point.</summary>
        public int Cursor => _buffered.Count;

        public void Emit(BoxFragment fragment)
        {
            // Translate inner-cell-relative offsets into fragmentainer
            // coordinates. The inner BlockLayouter writes BlockOffset
            // in its OWN fragmentainer coordinate space (which starts
            // at 0 = top of the inner page). We anchor that at the
            // cell's block-offset in the OUTER fragmentainer.
            var innerBlockOffset = fragment.BlockOffset;
            var innerInlineOffset = fragment.InlineOffset;
            var translated = fragment with
            {
                InlineOffset = innerInlineOffset + _inlineTranslation,
                BlockOffset = innerBlockOffset + _blockTranslation,
            };
            // Track the inner bottom edge (cell-relative) so the row
            // height resolves to max(inner-fragment-bottom).
            var innerBottom = innerBlockOffset + fragment.BlockSize;
            if (innerBottom > MaxBlockExtentFromCellOrigin)
            {
                MaxBlockExtentFromCellOrigin = innerBottom;
            }
            _buffered.Add(translated);
        }

        public void RollbackTo(int cursor)
        {
            // Sub-cycle 1 — the inner LastResort strategy suppresses
            // NeedsRewind, so this path isn't reached in practice.
            // Per Finding 2 we now truncate the buffer rather than
            // calling back to an outer sink (the outer sink doesn't
            // see anything from this measure pass until FlushTo).
            if (cursor < 0 || cursor > _buffered.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor),
                    $"MeasuringFragmentSink.RollbackTo: inner cursor "
                    + $"{cursor} out of range [0, {_buffered.Count}].");
            }
            if (cursor < _buffered.Count)
            {
                _buffered.RemoveRange(cursor, _buffered.Count - cursor);
            }
            // Recomputing MaxBlockExtentFromCellOrigin after a partial
            // rollback would require re-scanning forwarded fragments —
            // not worth it for the sub-cycle-1 unreachable path; we
            // leave the value stale, accepting that a rolled-back cell
            // measure is over-estimated. Documented for sub-cycle 2.
        }

        /// <summary>Per Finding 2 — drain the buffered fragments to
        /// <paramref name="target"/> in document order, then clear the
        /// buffer. Called by <see cref="AttemptLayout"/> after the
        /// row + cell fragments have been emitted so the outer sink
        /// receives them in paint-safe order (row → cell → cell
        /// content).
        ///
        /// <para><b>Sub-cycle 2 — deferred block translation.</b>
        /// <paramref name="additionalBlockOffset"/> is added to each
        /// buffered fragment's <c>BlockOffset</c> at flush time. This
        /// supports the rowspan distribution algorithm in
        /// <see cref="MeasureContentHeight"/>: cells are measured with
        /// a placeholder block translation (=0) baked into their
        /// buffer; the row-height pass then computes the final cell
        /// block origins; the emit pass calls
        /// <c>FlushTo(target, finalCellBlockOffset)</c> to apply the
        /// translation. Callers that already baked the block
        /// translation into the buffer at Emit time can pass 0 (the
        /// default).</para></summary>
        public void FlushTo(IBlockFragmentSink target, double additionalBlockOffset = 0)
        {
            ArgumentNullException.ThrowIfNull(target);
            for (var i = 0; i < _buffered.Count; i++)
            {
                var f = _buffered[i];
                if (additionalBlockOffset != 0)
                {
                    f = f with { BlockOffset = f.BlockOffset + additionalBlockOffset };
                }
                target.Emit(f);
            }
            _buffered.Clear();
        }

        /// <summary>Per Finding 2 — exposed for diagnostics / tests
        /// that need to inspect the buffered fragments without
        /// flushing. Returns a snapshot count + the buffer reference
        /// for read-only walks. Production callers should use
        /// <see cref="FlushTo"/>.</summary>
        internal IReadOnlyList<BoxFragment> Buffered => _buffered;

        /// <summary>The outer-sink cursor at the moment this measure
        /// sink was created. Currently unused (sub-cycle 1 doesn't
        /// drive rewinds inside cells), but retained for sub-cycle 2
        /// resume semantics where a partial cell flush would need to
        /// translate buffer cursors into outer cursors.</summary>
        internal int OuterCursorBaseline => _outerCursorBaseline;
    }

    /// <summary>No-op disposer for symmetry with
    /// <see cref="BlockLayouter"/>. The TableLayouter doesn't hold any
    /// rentable resources (no checkpoint leases, no float manager —
    /// those belong to the outer BlockLayouter). Provided so
    /// <c>using var layouter = new TableLayouter(...)</c> reads the
    /// same as BlockLayouter at the call site.</summary>
    public void Dispose()
    {
        // Sub-cycle 2 may add a TableContinuation lease + row-level
        // checkpoint pool — wire the dispose here when it does.
    }
}
