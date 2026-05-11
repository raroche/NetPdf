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
/// <para><b>Paint-safe emit order (post-Finding-2 hardening).</b>
/// Sub-cycle 1 buffered cell content in
/// <see cref="MeasuringFragmentSink"/> during the measure phase. The
/// emit phase: emit the row fragment, then for each cell emit the
/// cell fragment, then drain its buffered content fragments via
/// <see cref="MeasuringFragmentSink.FlushTo"/>. This produces the
/// painter-friendly order row → cell → cell-content so backgrounds /
/// borders paint UNDER the text glyphs.</para>
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
/// fragmentainer. Sub-cycle 1 does NOT consult the resolver inside
/// the table; rows that overflow the page emit a single
/// <c>PAGINATION-FORCED-OVERFLOW-001</c> diagnostic + commit
/// anyway. Multi-page table splitting (rows that defer to the
/// next page) is sub-cycle 2 work.</para>
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
    /// <param name="incomingContinuation">Reserved for sub-cycle 2 multi-
    /// page resume. Sub-cycle 1 accepts <see langword="null"/> only
    /// (anything else throws).</param>
    /// <param name="diagnostics">Diagnostic sink for the
    /// <c>PAGINATION-FORCED-OVERFLOW-001</c> + structural-anomaly
    /// codes.</param>
    /// <param name="shaperResolver">Optional inline shaper resolver
    /// threaded into the nested <see cref="BlockLayouter"/> for cell
    /// content layout.</param>
    /// <exception cref="ArgumentException">When
    /// <paramref name="rootBox"/> is not a Table or InlineTable wrapper,
    /// or when <paramref name="incomingContinuation"/> is non-null
    /// (sub-cycle 1 resume not yet supported).</exception>
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
            // Sub-cycle 2 will define a TableContinuation that resumes
            // at a given row index. Sub-cycle 1 doesn't yet support
            // mid-table page resume — fail loud so the caller surfaces
            // the missing capability instead of silently restarting.
            throw new ArgumentException(
                "TableLayouter sub-cycle 1 does not yet support resume-after-"
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
    /// pass skips them.</summary>
    private readonly struct CellPlacement
    {
        public CellPlacement(
            Box cell,
            int originRow,
            int originCol,
            int rowSpan,
            int colSpan,
            MeasuringFragmentSink contentBuffer,
            double contentBlockExtent)
        {
            Cell = cell;
            OriginRow = originRow;
            OriginCol = originCol;
            RowSpan = rowSpan;
            ColSpan = colSpan;
            ContentBuffer = contentBuffer;
            ContentBlockExtent = contentBlockExtent;
        }
        public Box Cell { get; }
        public int OriginRow { get; }
        public int OriginCol { get; }
        public int RowSpan { get; }
        public int ColSpan { get; }
        public MeasuringFragmentSink ContentBuffer { get; }
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

        // Pre-compute the row block-offsets (cumulative sum of
        // rowHeights). A rowspan cell at originRow R uses
        // _rowBlockOffsets[R] as its block origin + sums rowHeights
        // [R..R+RowSpan-1] for its block extent.
        var rowBlockOffsets = new double[rows.Count];
        var rowCursorBlock = _contentBlockOffset;
        for (var r = 0; r < rows.Count; r++)
        {
            rowBlockOffsets[r] = rowCursorBlock;
            rowCursorBlock += rows[r].RowHeight;
        }

        var overflowDiagnosed = false;

        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowMeasure = rows[r];
            var row = rowMeasure.Row;
            var rowHeight = rowMeasure.RowHeight;
            var rowOriginBlock = rowBlockOffsets[r];

            // Emit row fragment FIRST (paint-safe order: backgrounds
            // before content). A zero-cell row still produces a row
            // measurement entry post-sub-cycle 2 (rowspan continuation
            // slots from earlier rows keep the row "occupied") — but if
            // its rowHeight is 0 the emit is a degenerate 0-height
            // fragment, which is harmless.
            _sink.Emit(new BoxFragment(
                Box: row,
                InlineOffset: _contentInlineOffset,
                BlockOffset: rowOriginBlock,
                InlineSize: _contentInlineSize,
                BlockSize: rowHeight));

            // Sub-cycle 2 — emit each cell anchored at THIS row (skip
            // cells whose OriginRow != r — those are continuation slots
            // or cells anchored at a different row). Walking placements
            // in document order keeps the cell-fragment sequence
            // deterministic.
            for (var pIdx = 0; pIdx < placements.Count; pIdx++)
            {
                var placement = placements[pIdx];
                if (placement.OriginRow != r)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var cellInlineOffset = _contentInlineOffset
                    + (placement.OriginCol * columnWidth);
                var cellInlineSize = placement.ColSpan * columnWidth;
                var cellBlockOffset = rowOriginBlock;
                var cellBlockSize = 0.0;
                for (var k = 0; k < placement.RowSpan; k++)
                {
                    cellBlockSize += rows[placement.OriginRow + k].RowHeight;
                }

                _sink.Emit(new BoxFragment(
                    Box: placement.Cell,
                    InlineOffset: cellInlineOffset,
                    BlockOffset: cellBlockOffset,
                    InlineSize: cellInlineSize,
                    BlockSize: cellBlockSize));

                // Drain the cell's buffered content fragments. The
                // measure pass baked the cell's inline-axis translation
                // into the buffer at Emit time but DEFERRED the block-
                // axis translation (because row heights — and therefore
                // cell block origins — couldn't be finalized until the
                // measure pass completed). FlushTo applies the
                // deferred block translation now that we know the
                // cell's block origin.
                placement.ContentBuffer.FlushTo(_sink, cellBlockOffset);
            }

            // Forced-overflow detection (sub-cycle 2 commits anyway —
            // multi-fragmentainer table splitting is sub-cycle 3+).
            var rowBottom = rowOriginBlock + rowHeight;
            if (!overflowDiagnosed && rowBottom > fragmentainer.BlockSize)
            {
                overflowDiagnosed = true;
                OptimizingBreakResolver.SafeEmit(
                    layout.Diagnostics ?? _diagnostics,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.PaginationForcedOverflow001,
                        $"TableLayouter: table on fragmentainer page index "
                        + $"{fragmentainer.PageIndex} overflows page block-"
                        + $"size {fragmentainer.BlockSize:0.##} (rows extend to "
                        + $"{rowBottom:0.##}). The layouter commits all "
                        + "rows anyway; multi-fragmentainer splitting is "
                        + "deferred — see "
                        + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                        PaginateDiagnosticSeverity.Warning));
            }
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
        var columnCount = PlaceCellsOntoGrid(rows, placements, cancellationToken);

        _measuredColumnCount = columnCount;
        _measuredPlacements = placements;

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
            var buffer = MeasureCellContent(
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

    /// <summary>Sub-cycle 2 — place each cell onto the 2D occupancy
    /// grid via the HTML5 "Forming a table" algorithm (CSS Tables L3
    /// §3). For each row in document order: reset a column cursor to
    /// 0; for each <see cref="BoxKind.TableCell"/> child of the row,
    /// advance the cursor past any slots occupied by rowspan
    /// continuations from earlier rows, then anchor the cell at the
    /// cursor + mark its <c>rowspan × colspan</c> slot rectangle as
    /// occupied.
    ///
    /// <para>Sparse occupancy storage —
    /// <c>HashSet&lt;(row,col)&gt;</c> would allocate a struct + hash
    /// per slot which dominates for dense tables; we use a
    /// <c>List&lt;HashSet&lt;int&gt;&gt;</c> keyed by row, holding the
    /// occupied column indices for that row. Each row's hashset is
    /// lazily allocated so empty rows stay cheap.</para>
    /// </summary>
    /// <returns>Column count = max(occupied column index) + 1 across
    /// all rows. Returns 0 when no cells were placed.</returns>
    private static int PlaceCellsOntoGrid(
        List<Box> rows,
        List<CellPlacement> placements,
        CancellationToken cancellationToken)
    {
        // occupancy[r] = set of occupied column indices in row r.
        // Lazily allocated to keep empty rows cheap.
        var occupancy = new HashSet<int>?[rows.Count];

        var columnCount = 0;
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
                var (rowSpan, colSpan) = ReadSpans(ch);

                // Advance past slots already occupied by earlier rows'
                // rowspan continuations.
                var rowOccupancy = occupancy[r];
                while (rowOccupancy is not null && rowOccupancy.Contains(colCursor))
                {
                    colCursor++;
                }

                // Anchor at (r, colCursor) + mark the slot rectangle.
                placements.Add(new CellPlacement(
                    cell: ch,
                    originRow: r,
                    originCol: colCursor,
                    rowSpan: rowSpan,
                    colSpan: colSpan,
                    contentBuffer: null!, // assigned during measure pass
                    contentBlockExtent: 0));

                for (var rr = 0; rr < rowSpan; rr++)
                {
                    var targetRow = r + rr;
                    if (targetRow >= rows.Count)
                    {
                        // rowSpan extends past the table — HTML5 says
                        // clamp the span to the available rows
                        // (Tables L3 §3 "End of table" + HTML5
                        // forming-table step 14). We let the recorded
                        // rowSpan stay so the emit pass can still index
                        // rowHeights correctly — but we don't allocate
                        // occupancy slots that don't exist. The emit
                        // pass clamps via the rowHeights array length.
                        // For sub-cycle 2 we do clamp the placement
                        // rowSpan to the available rows so geometry
                        // matches what the emit pass can render.
                        break;
                    }
                    var slotSet = occupancy[targetRow];
                    if (slotSet is null)
                    {
                        slotSet = new HashSet<int>();
                        occupancy[targetRow] = slotSet;
                    }
                    for (var cc = 0; cc < colSpan; cc++)
                    {
                        slotSet.Add(colCursor + cc);
                    }
                }

                // Track the max column index observed (= colCursor +
                // colSpan - 1; columnCount = max + 1).
                var maxColInThisCell = colCursor + colSpan;
                if (maxColInThisCell > columnCount)
                {
                    columnCount = maxColInThisCell;
                }

                colCursor += colSpan;
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
                    contentBlockExtent: p.ContentBlockExtent);
            }
        }

        return columnCount;
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

        if (_measuredCaptions is { Count: > 0 } captions)
        {
            for (var i = 0; i < captions.Count; i++)
            {
                var captionText = ExtractCaptionTextSnippet(captions[i]);
                OptimizingBreakResolver.SafeEmit(
                    sink,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                        $"TableLayouter: table caption skipped — sub-cycle 1 "
                        + $"does not yet lay out caption content. Caption "
                        + $"text: \"{captionText}\". See "
                        + "docs/deferrals.md#table-auto-fixed-spans-borders.",
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

    /// <summary>Sub-cycle 1 — count the <see cref="BoxKind.TableCell"/>
    /// children of <paramref name="row"/>. Used to pre-allocate the
    /// per-row measurement buffer.</summary>
    private static int CountTableCells(Box row)
    {
        var count = 0;
        for (var i = 0; i < row.Children.Count; i++)
        {
            if (row.Children[i].Kind == BoxKind.TableCell)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Sub-cycle 2 — read the <c>rowspan</c> + <c>colspan</c>
    /// values off <paramref name="cell"/>'s source HTML element. The
    /// attribute is read directly from the DOM (the CSS counterparts
    /// <c>table-column-span</c> / <c>table-row-span</c> aren't part of
    /// the cascade yet — they're HTML attribute mapped per
    /// HTML5 §4.9.11).
    ///
    /// <para>Per HTML5 spec ranges:</para>
    /// <list type="bullet">
    ///   <item><c>colspan</c> defaults to 1; valid range
    ///     <c>[1, 1000]</c>. Out-of-range / non-numeric values
    ///     (including <c>colspan="0"</c> which HTML5 treats as
    ///     "spans all remaining columns" — sub-cycle 2 simplifies
    ///     this to 1 per the locked design) fall back to 1.</item>
    ///   <item><c>rowspan</c> defaults to 1; valid range
    ///     <c>[1, 65534]</c>. Out-of-range / non-numeric values fall
    ///     back to 1.</item>
    /// </list>
    /// <para>Anonymous cells (no <see cref="Box.SourceElement"/>) get
    /// the default <c>(1, 1)</c> spans.</para></summary>
    /// <returns>Tuple <c>(rowSpan, colSpan)</c> with both values
    /// clamped to <c>[1, max]</c>.</returns>
    private static (int rowSpan, int colSpan) ReadSpans(Box cell)
    {
        var el = cell.SourceElement;
        if (el is null)
        {
            return (1, 1);
        }
        var colSpan = ParseSpanAttribute(
            el.GetAttribute("colspan"),
            maxValue: 1000);
        var rowSpan = ParseSpanAttribute(
            el.GetAttribute("rowspan"),
            maxValue: 65534);
        return (rowSpan, colSpan);
    }

    /// <summary>Sub-cycle 2 — parse a colspan / rowspan attribute
    /// value into an integer span, defaulting to 1 on null/empty,
    /// non-numeric, or out-of-range input.</summary>
    private static int ParseSpanAttribute(string? raw, int maxValue)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 1;
        }
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return 1;
        }
        // HTML5 colspan="0" means "spans all remaining columns" — sub-
        // cycle 2 doesn't implement that semantic + falls back to 1
        // (the safer of "ignore the attribute" + "treat as 1" is the
        // latter, which the existing HTML5 fallback for any out-of-
        // range value also produces). Sub-cycle 3 may revisit if a
        // corpus sample needs the "0 = remainder" behavior.
        if (n < 1) return 1;
        if (n > maxValue) return maxValue;
        return n;
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
    private MeasuringFragmentSink MeasureCellContent(
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

        // The inner layout context carries forward the ambient
        // diagnostic sink + writing mode but starts with the cell's
        // own available extents.
        var innerLayout = new LayoutContext(cellFragmentainer)
        {
            Diagnostics = layout.Diagnostics ?? _diagnostics,
            WritingMode = layout.WritingMode,
            IsRtl = layout.IsRtl,
        };

        using var cellLayouter = new BlockLayouter(
            rootBox: cellBox,
            sink: measuringSink,
            incomingContinuation: null,
            diagnostics: _diagnostics,
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

        return measuringSink;
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
