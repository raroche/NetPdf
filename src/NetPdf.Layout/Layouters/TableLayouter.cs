// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 12 sub-cycle 1 + 2 + 3 + 4 + 5 + plan §"TableLayouter" —
/// CSS Tables L3 table layouter. Walks the inner content of a
/// <see cref="BoxKind.Table"/> (or <see cref="BoxKind.InlineTable"/>)
/// wrapper: finds the <see cref="BoxKind.TableGrid"/>, collects table
/// rows (recursing into row groups), runs the column-width algorithm
/// per <c>table-layout: fixed</c> (CSS Tables L3 §3.5, sub-cycle 4 —
/// declared <c>&lt;col&gt;</c> / <c>&lt;colgroup&gt;</c> / first-row
/// cell widths) OR <c>table-layout: auto</c> (CSS Tables L3 §3,
/// sub-cycle 5 — shrink-to-fit via per-cell min/max-content
/// intrinsic widths), stacks rows vertically with rowspan / colspan
/// merging, dispatches each cell's content through a nested
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
/// is sub-cycle 4+ work — see
/// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.</para>
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
///   <see cref="BoxKind.TableRow"/> children. <b>Sub-cycle 3:</b>
///   captions (<see cref="BoxKind.TableCaption"/>) detected under the
///   wrapper (per BoxBuilder Rec 5) are now LAID OUT as block-level
///   boxes above (<c>caption-side: top</c>, default) or below
///   (<c>caption-side: bottom</c>) the row stack — see
///   <see cref="MeasureCaptions"/> + Phase 0 / Phase D emits in
///   <see cref="AttemptLayout"/>. The sub-cycle 1 + 2 deferral
///   diagnostic for captions is gone. Column groups and columns are
///   still skipped silently.</item>
///   <item><b>Sub-cycle 2:</b> place cells onto a 2D occupancy grid
///   (<see cref="CellPlacement"/>) using the HTML5 forming-a-table
///   algorithm — for each row, advance a column cursor past slots
///   already occupied by rowspan continuations from earlier rows, then
///   anchor the cell at the cursor with its <c>colspan × rowspan</c>
///   slot rectangle marked occupied. The column count = max occupied
///   column index + 1 across all rows.</item>
///   <item><b>Sub-cycle 4 + 5:</b> compute per-column widths via
///   <see cref="ComputeColumnWidths"/>. For <c>table-layout: fixed</c>
///   (CSS Tables L3 §3.5) a 4-pass algorithm reads <c>&lt;col&gt;</c>
///   / <c>&lt;colgroup&gt;</c> declarations (Pass A), then first-row
///   cell widths (Pass B), then equal-distributes the remainder to
///   undeclared columns (Pass C), then reconciles against the
///   wrapper's content-inline-size (Pass D — distributes leftover
///   or records inline-overflow). For <c>table-layout: auto</c>
///   (default — sub-cycle 5) the CSS Tables L3 §3 shrink-to-fit
///   algorithm runs via <see cref="ComputeColumnWidthsAuto"/>: per-
///   cell min/max-content widths are measured by speculative cell-
///   content layouts at extreme inline sizes
///   (<see cref="MeasureCellIntrinsicWidths"/>); per-column min/max
///   are aggregated (colspan distribution mirrors Pass B); the table
///   width is clamped to <c>[sumMin, sumMax]</c>; widths are
///   distributed via the overflow / saturated / interpolation
///   branches. The §3 spec-strict proportional-weight distribution
///   is approximated by linear interpolation — see
///   <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.</item>
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
///   distribution-proportional algorithm is sub-cycle 4+ work —
///   see <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.</item>
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
///   <item>CSS Tables L3 §3 spec-strict proportional-weight column-
///   width distribution — sub-cycle 5 ships a deterministic linear-
///   interpolation approximation between min and max plus a
///   deterministic equal-split colspan distribution. Sub-cycle 6+
///   may revisit.</item>
///   <item>Block-level fixed-width content + replaced elements in
///   cells don't differentiate min vs max (sub-cycle 5's measurement
///   reads inline-only-block line widths; block-level cell content
///   falls back to the border-box = available width).</item>
///   <item>Percentage column widths (e.g.
///   <c>&lt;col width="20%"&gt;</c>) — sub-cycle 4 treats them as 0,
///   falling back to Pass B / Pass C.</item>
///   <item>Border-collapse model + <c>border-spacing</c> per
///   §6.3.</item>
///   <item><c>&lt;thead&gt;</c> / <c>&lt;tfoot&gt;</c> repetition
///   across pages.</item>
///   <item>Multi-fragmentainer ROW-INTERNAL splitting (one row whose
///   content is taller than the remaining page is committed in full
///   + the existing forced-overflow diagnostic fires; cycle 2+ may
///   split row content across pages).</item>
///   <item>Right-to-left tables / writing-mode flips.</item>
///   <item>Spec-strict CSS Tables L3 rowspan distribution-proportional
///   algorithm. Sub-cycle 2 uses a naive "extra height to the last
///   row of the span" approach.</item>
///   <item>Nested-recursion continuation propagation past a single
///   depth — Task 13 cycle 1 hardening (Finding 1) carries
///   <see cref="TableContinuation"/> through
///   <see cref="BlockLayouter.EmitBlockSubtreeRecursive"/> when the
///   table is a DIRECT descendant of the just-emitted top-level
///   block (recursion depth = 1); deeper nesting falls back to the
///   atomic <see cref="NoBreakBreakResolver"/>. Sub-cycle 6+ may
///   generalize.</item>
/// </list>
///
/// <para><b>Pagination scope.</b> Per Phase 3 Task 13 cycle 1, the
/// layouter NOW consults the break resolver between rows: rows that
/// don't fit on the current fragmentainer are deferred to a
/// <see cref="TableContinuation"/> via
/// <see cref="LayoutAttemptResult.PageComplete"/>. The outer
/// <see cref="BlockLayouter"/> propagates the continuation through
/// its <see cref="BlockContinuation.LayouterState"/> slot.
/// <c>PAGINATION-FORCED-OVERFLOW-001</c> still fires for the
/// degenerate single-oversized-row case (a row taller than the
/// fragmentainer is unsplittable in cycle 1 — committed in full +
/// the existing forced-overflow diagnostic surfaces it). Row-INTERNAL
/// splitting + per-row <c>break-inside: avoid</c> support are cycle
/// 2+ scope — see
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
    // Per Phase 3 Task 13 cycle 1 — re-introduced for multi-page row
    // splitting. The constructor accepts a TableContinuation (only) +
    // stores it here; AttemptLayout resumes at NextRowIndex on
    // entry. Sub-cycle 2 (Task 13 cycle 2) will consume the RepeatHead /
    // RepeatFoot flags for <thead> / <tfoot> repetition; cycle 1 leaves
    // them at false. Pre-Task-13, the field was eliminated + the
    // constructor parameter was null-validated.
    private readonly TableContinuation? _incomingTableContinuation;
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
    /// <param name="incomingContinuation">Per Phase 3 Task 13 cycle 1 —
    /// when non-null MUST be a <see cref="TableContinuation"/>; the
    /// layouter resumes at
    /// <see cref="TableContinuation.NextRowIndex"/>. Any other
    /// non-null continuation type throws. <see langword="null"/> means
    /// "first-page emit / no resume".</param>
    /// <param name="diagnostics">Diagnostic sink for the
    /// <c>PAGINATION-FORCED-OVERFLOW-001</c> + structural-anomaly
    /// codes.</param>
    /// <param name="shaperResolver">Optional inline shaper resolver
    /// threaded into the nested <see cref="BlockLayouter"/> for cell
    /// content layout.</param>
    /// <exception cref="ArgumentException">When
    /// <paramref name="rootBox"/> is not a Table or InlineTable wrapper,
    /// or when <paramref name="incomingContinuation"/> is non-null and
    /// not a <see cref="TableContinuation"/> (cycle 1 accepts only
    /// table-shaped continuations).</exception>
    /// <exception cref="ArgumentOutOfRangeException">Per Phase 3 Task 13
    /// cycle 1 — when <paramref name="incomingContinuation"/>'s
    /// <see cref="TableContinuation.NextRowIndex"/> is negative
    /// (defensive validation; the actual upper bound depends on the
    /// row count which isn't known until the measure pass — the
    /// out-of-range upper bound is validated in
    /// <see cref="AttemptLayout"/>). The valid range on entry is
    /// <c>[0, rows.Count]</c> where <c>NextRowIndex == rows.Count</c>
    /// is the "all rows committed; emit bottom captions only" case
    /// (Finding 7 hardening — explicit early-return path).</exception>
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

        // Per Phase 3 Task 13 cycle 1 — accept TableContinuation (only).
        // Pre-cycle-1 the constructor rejected any non-null continuation.
        if (incomingContinuation is not null and not TableContinuation)
        {
            throw new ArgumentException(
                $"TableLayouter expects a TableContinuation; got "
                + $"{incomingContinuation.GetType().Name}. The wrong "
                + "continuation type would silently restart from the first "
                + "row + likely duplicate / drop content. Pass either null "
                + "(first-page emit) or a TableContinuation produced by a "
                + "prior AttemptLayout call.",
                nameof(incomingContinuation));
        }
        if (incomingContinuation is TableContinuation tc)
        {
            if (tc.NextRowIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(incomingContinuation),
                    $"TableContinuation.NextRowIndex={tc.NextRowIndex} "
                    + "is negative. Resume indices must be 0-based "
                    + "non-negative integers (the upper-bound row-count "
                    + "check runs in AttemptLayout once the measure pass "
                    + "has determined the row list).");
            }
            if (!double.IsFinite(tc.ConsumedBlockSize) || tc.ConsumedBlockSize < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(incomingContinuation),
                    $"TableContinuation.ConsumedBlockSize={tc.ConsumedBlockSize} "
                    + "must be finite + non-negative.");
            }
        }

        _rootBox = rootBox;
        _sink = sink;
        _incomingTableContinuation = incomingContinuation as TableContinuation;
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
    internal readonly struct CellPlacement
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
    internal readonly struct RowMeasurement
    {
        public RowMeasurement(Box row, double rowHeight)
        {
            Row = row;
            RowHeight = rowHeight;
        }
        public Box Row { get; }
        public double RowHeight { get; }
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 3 — measurement record
    /// for one <see cref="BoxKind.TableCaption"/> attached as a direct
    /// child of the wrapper. CSS Tables L3 §11.5.1–§11.5.2: a caption
    /// is laid out as a block-level box above (<c>caption-side: top</c>,
    /// default) or below (<c>caption-side: bottom</c>) the table grid.
    /// Inline-size matches the wrapper's content-inline-size (sub-cycle
    /// 3 doesn't yet ship auto-table-layout, so wrapper content-inline-
    /// size = grid's outer inline-size); block-size is the caption's
    /// content extent measured via a nested <see cref="BlockLayouter"/>
    /// into a buffered <see cref="MeasuringFragmentSink"/>.
    ///
    /// <para><b>Post sub-cycle 3 hardening (Finding 1):</b> the
    /// caption's CSS box model is now honored — margin / border /
    /// padding (all four block + inline edges) and an explicit
    /// <c>height</c> all contribute to the emitted fragment's
    /// border-box size + the surrounding stacking math. The
    /// <see cref="BorderBoxBlockSize"/> field is what the emit pass
    /// stamps on the caption's <c>BoxFragment.BlockSize</c>; the
    /// <see cref="MarginBoxBlockSize"/> field is what the table's
    /// content-extent totals + the row-stack-origin shift consume.
    /// <see cref="MarginBlockStart"/> is broken out separately so the
    /// emit pass can add it to the cumulative cursor BEFORE stamping
    /// the fragment's <c>BlockOffset</c>.</para>
    ///
    /// <para><b>Adjacent caption margin-collapse</b> (CSS 2.1 §8.3.1)
    /// is NOT implemented in sub-cycle 3 hardening — captions stack by
    /// margin SUM, not by collapse. Sub-cycle 4+ may revisit when the
    /// general BlockLayouter margin-collapse infrastructure becomes
    /// reusable here.</para></summary>
    internal readonly struct CaptionMeasurement
    {
        public CaptionMeasurement(
            Box caption,
            CaptionSide side,
            MeasuringFragmentSink contentBuffer,
            BufferingDiagnosticsSink? diagnosticsBuffer,
            double contentBlockExtent,
            // Finding 1 box-model fields:
            double marginBlockStart,
            double marginBlockEnd,
            double borderBlockStart,
            double borderBlockEnd,
            double paddingBlockStart,
            double paddingBlockEnd,
            double borderInlineStart,
            double borderInlineEnd,
            double paddingInlineStart,
            double paddingInlineEnd,
            double declaredBlockSize)
        {
            Caption = caption;
            Side = side;
            ContentBuffer = contentBuffer;
            DiagnosticsBuffer = diagnosticsBuffer;
            ContentBlockExtent = contentBlockExtent;
            MarginBlockStart = marginBlockStart;
            MarginBlockEnd = marginBlockEnd;
            BorderBlockStart = borderBlockStart;
            BorderBlockEnd = borderBlockEnd;
            PaddingBlockStart = paddingBlockStart;
            PaddingBlockEnd = paddingBlockEnd;
            BorderInlineStart = borderInlineStart;
            BorderInlineEnd = borderInlineEnd;
            PaddingInlineStart = paddingInlineStart;
            PaddingInlineEnd = paddingInlineEnd;
            DeclaredBlockSize = declaredBlockSize;
        }
        public Box Caption { get; }
        public CaptionSide Side { get; }
        public MeasuringFragmentSink ContentBuffer { get; }
        public BufferingDiagnosticsSink? DiagnosticsBuffer { get; }
        /// <summary>The caption's MEASURED inner content extent — the
        /// max BlockOffset+BlockSize observed across the nested
        /// BlockLayouter's buffered fragments. CSS calls this the
        /// content-box block-size before height clamping. Sub-cycle 3
        /// hardening uses this as the FLOOR for the resolved block-size
        /// per CSS Tables L3 §11.5 + CSS 2.1 §10.6 (an explicit
        /// <c>height</c> below the content extent does NOT clip;
        /// content is what wins). Documented as a TODO inline if
        /// sub-cycle 4 needs to introduce <c>overflow</c>-aware
        /// clipping.</summary>
        public double ContentBlockExtent { get; }
        public double MarginBlockStart { get; }
        public double MarginBlockEnd { get; }
        public double BorderBlockStart { get; }
        public double BorderBlockEnd { get; }
        public double PaddingBlockStart { get; }
        public double PaddingBlockEnd { get; }
        public double BorderInlineStart { get; }
        public double BorderInlineEnd { get; }
        public double PaddingInlineStart { get; }
        public double PaddingInlineEnd { get; }
        /// <summary>The explicit <c>height</c> from the caption's
        /// computed style, or 0 when unset / <c>auto</c>. Sub-cycle 3
        /// hardening: this acts as a FLOOR for the resolved block-size;
        /// the actual border-box block-size is
        /// <c>BorderBlockStart + PaddingBlockStart + max(DeclaredBlockSize,
        /// ContentBlockExtent) + PaddingBlockEnd + BorderBlockEnd</c>.</summary>
        public double DeclaredBlockSize { get; }

        /// <summary>The caption's border-box block-size — what the
        /// emit pass stamps on the <c>BoxFragment.BlockSize</c> field.
        /// Equals
        /// <c>BorderBlockStart + PaddingBlockStart + max(DeclaredBlockSize,
        /// ContentBlockExtent) + PaddingBlockEnd + BorderBlockEnd</c>.
        /// </summary>
        public double BorderBoxBlockSize =>
            BorderBlockStart + PaddingBlockStart
            + (DeclaredBlockSize > ContentBlockExtent
                ? DeclaredBlockSize
                : ContentBlockExtent)
            + PaddingBlockEnd + BorderBlockEnd;

        /// <summary>The caption's margin-box block-size — what the
        /// table's overall content extent + the row-stack origin shift
        /// accumulate.</summary>
        public double MarginBoxBlockSize =>
            MarginBlockStart + BorderBoxBlockSize + MarginBlockEnd;
    }

    private bool _measureDone;
    private double _measuredContentHeight;
    private int _measuredColumnCount;
    // Per Phase 3 Task 12 sub-cycle 4 — per-column-width array.
    // `table-layout: fixed` derives per-column widths from <col> /
    // <colgroup> + first-row cell widths (CSS Tables L3 §3.5);
    // sub-cycle 5 added `table-layout: auto` shrink-to-fit via per-
    // cell min/max-content intrinsic widths (CSS Tables L3 §3) with
    // sub-cycle 5 hardening Finding 2 also honoring declared widths
    // as min/max-content floors. Cell inline-size for a placement is
    // sum(columnWidths[col..col+colspan]). Per-column inline OFFSET
    // is the prefix-sum stored separately so the emit pass avoids
    // re-summing.
    private double[]? _measuredColumnWidths;
    private double[]? _measuredColumnOffsets;
    // Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 — table grid's
    // used inline-size after Pass D reconciliation. Equals
    // max(sum(_measuredColumnWidths), _contentInlineSize). When the
    // column sum was below contentInlineSize, Pass D distributed the
    // leftover equally across columns + this value == _contentInlineSize.
    // When the column sum exceeded contentInlineSize, declared widths
    // are preserved + this value == columnSum (the table overflows the
    // wrapper inline-axis; row + caption fragments grow to match;
    // LAYOUT-TABLE-INLINE-OVERFLOW-001 is recorded for emit).
    private double _measuredUsedInlineSize;
    // Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 — when Pass D
    // observed columnSum > contentInlineSize, record the values so the
    // AttemptLayout emit pass can surface
    // LAYOUT-TABLE-INLINE-OVERFLOW-001 with the actual numbers in the
    // message. Both are zero in the non-overflow case + the diagnostic
    // is not emitted.
    private bool _measuredInlineOverflowed;
    private double _measuredInlineOverflowColumnSum;
    private double _measuredInlineOverflowContentSize;
    private List<RowMeasurement>? _measuredRows;
    private List<CellPlacement>? _measuredPlacements;
    // Per Phase 3 Task 12 sub-cycle 3 — caption measurements live
    // alongside the row + cell measurements; sub-cycle 1 + 2 captured
    // only the raw caption Box for the deferral-diagnostic snippet,
    // sub-cycle 3 captures the buffered content + block extent + side
    // for the Phase 0 / Phase D emit passes.
    private List<CaptionMeasurement>? _measuredCaptionList;
    // Per Phase 3 Task 12 sub-cycle 3 — pre-summed top + bottom caption
    // block-extents so the AttemptLayout emit pass doesn't re-scan the
    // caption list. The row stack's block origin in the wrapper's
    // content area is contentBlockOffset + _measuredTopCaptionsTotal.
    private double _measuredTopCaptionsTotal;
    private double _measuredBottomCaptionsTotal;
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

    // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 —
    // intrinsic-measurement budget accounting. _intrinsicMeasurementOps
    // counts cumulative 2-ops-per-cell consumption during
    // ComputeColumnWidthsAuto's Step 1. When it crosses
    // MaxIntrinsicMeasurementOps, _intrinsicMeasurementBudgetExceeded
    // turns true + remaining cells fall back to (0, contentInlineSize).
    // _intrinsicMeasurementCellsMeasured / _intrinsicMeasurementCellsFellBack
    // are counters that drive the diagnostic message at emit time.
    private long _intrinsicMeasurementOps;
    private bool _intrinsicMeasurementBudgetExceeded;
    private int _intrinsicMeasurementCellsMeasured;
    private int _intrinsicMeasurementCellsFellBack;

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 —
    /// the table grid's used inline-size after Pass D reconciliation.
    /// Equals <c>max(sum(columnWidths), contentInlineSize)</c>. Only
    /// meaningful after <see cref="MeasureContentHeight"/> has run.
    ///
    /// <para>Sub-cycle 5+ — BlockLayouter's
    /// <c>PreMeasureTableIfNeeded</c> can consume this to size the
    /// table wrapper's auto-width to the actual grid extent. Sub-cycle
    /// 4 keeps the wrapper at <c>contentInlineSize</c>; the table
    /// grid + row + caption fragments grow to match this used inline-
    /// size when it exceeds the wrapper.</para></summary>
    internal double MeasuredUsedInlineSize => _measuredUsedInlineSize;

    /// <summary>Per Phase 3 Task 13 cycle 1 hardening (Finding 2) —
    /// dry-run pagination simulation to estimate the block-axis extent
    /// the table will commit on the current page WITHOUT actually
    /// running the emit pass. The dispatching BlockLayouter uses this
    /// to size the wrapper's border-box block extent to the
    /// per-page-committed size rather than the natural total — when
    /// the table will split across pages, the wrapper on page 1 should
    /// only reserve space for the rows that commit on page 1, NOT the
    /// full natural extent. This suppresses the outer block-flow's
    /// false PAGINATION-FORCED-OVERFLOW-001 diagnostic when the table
    /// itself splits cleanly.
    ///
    /// <para>Algorithm: walk the measured rows from
    /// <paramref name="resumeAtRow"/>; for each row compute the
    /// page-relative top + chunk; stop when the row would overflow the
    /// fragmentainer block-size (= "this row's bottom would exceed
    /// page bottom"). Return the cumulative block extent of the
    /// committed-rows-on-this-page window + top captions on the first
    /// page + bottom captions on the last page.</para>
    ///
    /// <para><b>needsSplitting</b> out param tells the caller whether
    /// the table will return PageComplete on this attempt — useful for
    /// suppressing the outer forced-overflow diagnostic.</para>
    /// </summary>
    internal double DryRunCommittedBlockSize(
        FragmentainerContext fragmentainer,
        int resumeAtRow,
        out bool needsSplitting,
        out bool willForceOverflowOnSingleRow)
    {
        needsSplitting = false;
        willForceOverflowOnSingleRow = false;
        if (!_measureDone || _measuredRows is null || _measuredRows.Count == 0)
        {
            // Degenerate paths (missing grid / zero rows / zero
            // columns) — fall back to the measured content height.
            return _measuredContentHeight;
        }
        var rows = _measuredRows;
        if (resumeAtRow > rows.Count)
        {
            // Caller bug — let AttemptLayout throw with the proper
            // ArgumentOutOfRangeException; here just return the
            // total.
            return _measuredContentHeight;
        }
        if (resumeAtRow == rows.Count)
        {
            // All rows already emitted; only bottom captions on this
            // page (Finding 7's NextRowIndex == rows.Count case).
            return _measuredBottomCaptionsTotal;
        }
        var isFirstPage = resumeAtRow == 0;
        // page-relative origin of row resumeAtRow: 0 + top captions on
        // first page; just 0 on resume pages (top captions skipped).
        // Note: "page-relative" here is RELATIVE TO THE WRAPPER'S
        // CONTENT-BOX ORIGIN (= the table's local frame), not the
        // fragmentainer's content origin. The remaining block size
        // available for rows is the page's BlockSize minus the
        // fragmentainer's UsedBlockSize at AttemptLayout entry minus
        // the wrapper's own border + padding block-start.
        var topCaptionsBlock = isFirstPage ? _measuredTopCaptionsTotal : 0.0;
        var rowStackOriginInFragmentainer =
            fragmentainer.UsedBlockSize + (_contentBlockOffset - _contentBlockOffset) // = UsedBlockSize
            + topCaptionsBlock;
        // The actual logic the row-pagination loop uses:
        //   fragmentainer.UsedBlockSize = rowBlockOffsets[r]
        //   chunk = rows[r].RowHeight
        //   if rowBlockOffsets[r] + chunk > fragmentainer.BlockSize
        //     defer this row.
        // We replicate that here without mutating fragmentainer state.
        var pageBlockSize = fragmentainer.BlockSize;
        var cursorInFragmentainer = rowStackOriginInFragmentainer;
        var committedRowsBlock = 0.0;
        var lastCommittedRowExclusive = resumeAtRow;
        for (var r = resumeAtRow; r < rows.Count; r++)
        {
            var chunk = rows[r].RowHeight;
            // Match BreakResolver: "if I add chunk to used, does it
            // exceed BlockSize?" — return BreakHere when:
            // cursor + chunk > pageBlockSize.
            if (cursorInFragmentainer + chunk > pageBlockSize)
            {
                // If THIS is the first row AND nothing's committed,
                // the row force-emits (single-oversized-row case).
                if (r == resumeAtRow
                    && !(isFirstPage && _measuredTopCaptionsTotal > 0))
                {
                    willForceOverflowOnSingleRow = true;
                    committedRowsBlock += chunk;
                    lastCommittedRowExclusive = r + 1;
                }
                else
                {
                    needsSplitting = true;
                }
                break;
            }
            committedRowsBlock += chunk;
            cursorInFragmentainer += chunk;
            lastCommittedRowExclusive = r + 1;
        }
        // Bottom captions only on the last page (= committed all rows).
        var committedBottomCaptions = lastCommittedRowExclusive == rows.Count
            ? _measuredBottomCaptionsTotal
            : 0.0;
        return topCaptionsBlock + committedRowsBlock + committedBottomCaptions;
    }

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

        // Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 — surface
        // the LAYOUT-TABLE-INLINE-OVERFLOW-001 diagnostic when Pass D
        // observed columnSum > contentInlineSize. The pre-Finding-1
        // path silently let the table grid overflow the wrapper; the
        // diagnostic gives authors a hook to tune their declarations.
        //
        // Sub-cycle 5 — the same diagnostic now also fires under
        // table-layout: auto when sum(min-content) > contentInlineSize
        // (the shrink-to-fit algorithm CAN'T narrow the columns below
        // their min-content widths without splitting words). The
        // message uses the generic phrasing "column widths" rather
        // than "declared column widths" so it serves both paths.
        if (_measuredInlineOverflowed)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutTableInlineOverflow001,
                    $"TableLayouter: sum of column widths "
                    + $"({_measuredInlineOverflowColumnSum:0.##}) exceeds "
                    + $"the wrapper's content-inline-size "
                    + $"({_measuredInlineOverflowContentSize:0.##}). The "
                    + "table grid overflows the wrapper in the inline "
                    + "axis; column widths are preserved + row + "
                    + "caption fragments grow to the column sum. Under "
                    + "table-layout: fixed this means declared widths "
                    + "summed past the wrapper; under table-layout: auto "
                    + "it means the min-content sum exceeds the wrapper. "
                    + "See docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }

        var rows = _measuredRows;
        var placements = _measuredPlacements ?? new List<CellPlacement>(0);
        var captions = _measuredCaptionList;

        // Per Phase 3 Task 13 cycle 1 — resume-state derivation. The
        // incoming TableContinuation names the row to RESUME AT;
        // priorConsumedBlock tracks cumulative across-page block-size
        // (mirrors BlockContinuation.ConsumedBlockSize). Top captions
        // emit only on the first page (resumeAtRow == 0); bottom
        // captions only on the last page (the loop completes all rows
        // without a BreakHere return).
        //
        // Per Phase 3 Task 13 cycle 1 hardening (Finding 7) — the
        // upper bound is INCLUSIVE: NextRowIndex == rows.Count means
        // "all rows already committed on prior pages; this resume
        // page emits bottom captions only". Strict > rows.Count is
        // the caller-bug case (the row list shrunk between pages, or
        // the dispatching layouter produced a malformed continuation).
        var resumeAtRow = _incomingTableContinuation?.NextRowIndex ?? 0;
        var priorConsumedBlock = _incomingTableContinuation?.ConsumedBlockSize ?? 0.0;
        if (rows is not null && resumeAtRow > rows.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_incomingTableContinuation),
                $"TableContinuation.NextRowIndex={resumeAtRow} is past the "
                + $"row count ({rows.Count}). The dispatching layouter must "
                + "produce continuations with NextRowIndex in [0, rows.Count].");
        }
        var isFirstPage = resumeAtRow == 0;
        // Finding 7 — NextRowIndex == rows.Count: all rows already
        // committed on prior pages. Emit the bottom captions (if any)
        // at the wrapper's content-block-offset (no row stack on this
        // page) + return AllDone. Indexing `rowBlockOffsets[resumeAtRow]`
        // below would be OUT-OF-RANGE — the rowBlockOffsets array has
        // rows.Count entries, indexed 0..rows.Count-1.
        if (rows is not null && resumeAtRow == rows.Count && rows.Count > 0)
        {
            // Flush any per-cell + per-caption diagnostic buffers
            // (mirrors the main commit-diagnostics block below — the
            // resume page still wants to surface measure-pass diags
            // for content that committed on prior pages but produced
            // diagnostics the outer sink hasn't seen yet).
            var allDoneCommitDiagSink = layout.Diagnostics ?? _diagnostics;
            if (allDoneCommitDiagSink is not null)
            {
                for (var pIdx = 0; pIdx < placements.Count; pIdx++)
                {
                    placements[pIdx].DiagnosticsBuffer?.FlushTo(allDoneCommitDiagSink);
                }
                if (captions is { Count: > 0 })
                {
                    for (var i = 0; i < captions.Count; i++)
                    {
                        captions[i].DiagnosticsBuffer?.FlushTo(allDoneCommitDiagSink);
                    }
                }
            }
            var allDoneUsedInlineSize = _measuredUsedInlineSize > 0
                ? _measuredUsedInlineSize
                : _contentInlineSize;
            var allDoneBottomCursor = _contentBlockOffset;
            if (captions is { Count: > 0 })
            {
                for (var i = 0; i < captions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var c = captions[i];
                    if (c.Side != CaptionSide.Bottom) continue;
                    allDoneBottomCursor += c.MarginBlockStart;
                    var borderBoxBlockOffset = allDoneBottomCursor;
                    var borderBoxBlockSize = c.BorderBoxBlockSize;
                    _sink.Emit(new BoxFragment(
                        Box: c.Caption,
                        InlineOffset: _contentInlineOffset,
                        BlockOffset: borderBoxBlockOffset,
                        InlineSize: allDoneUsedInlineSize,
                        BlockSize: borderBoxBlockSize));
                    var contentBlockOriginInFragmentainer =
                        borderBoxBlockOffset + c.BorderBlockStart + c.PaddingBlockStart;
                    c.ContentBuffer.FlushTo(_sink, contentBlockOriginInFragmentainer);
                    allDoneBottomCursor += borderBoxBlockSize + c.MarginBlockEnd;
                }
            }
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Sub-cycle 4 hardening (Finding 1) — used inline-size is the
        // post-Pass-D column sum (= max(columnSum, contentInlineSize)).
        // Row + caption fragments use THIS value for their InlineSize
        // so the rendered geometry matches the actual column extent.
        // Pre-fix the four emit sites used `_contentInlineSize` which
        // could leave visual gaps (columnSum < contentInlineSize) or
        // clip rendered backgrounds (columnSum > contentInlineSize).
        // Falls back to `_contentInlineSize` when the measure pass
        // didn't compute a used-inline-size (no-grid / captions-only
        // path).
        var usedInlineSize = _measuredUsedInlineSize > 0
            ? _measuredUsedInlineSize
            : _contentInlineSize;

        // Per Finding 5 + Task 13 cycle 1 hardening (Finding 4) —
        // flush each cell's + caption's BufferingDiagnosticsSink to
        // the outer diagnostic sink at COMMIT time. Pre-finding-4,
        // ALL cell + caption diagnostics flushed eagerly here before
        // the row-pagination loop. That leaked diagnostics for cells
        // in deferred rows onto the current page AND duplicated them
        // on the resume page. Post-fix: top-caption diagnostics flush
        // here (Phase 0 only on isFirstPage); per-row cell diagnostics
        // flush inside EmitRowWindow AFTER each row commits; bottom-
        // caption diagnostics flush at Phase D below.
        var commitDiagSink = layout.Diagnostics ?? _diagnostics;
        if (commitDiagSink is not null && isFirstPage && captions is { Count: > 0 })
        {
            for (var i = 0; i < captions.Count; i++)
            {
                var c = captions[i];
                if (c.Side != CaptionSide.Top) continue;
                c.DiagnosticsBuffer?.FlushTo(commitDiagSink);
            }
        }

        // Per Phase 3 Task 12 sub-cycle 3 — Phase 0: emit top-side
        // captions BEFORE the row stack. Stacks vertically in
        // document order at the wrapper's content-box origin. The
        // captions span the wrapper's content-inline-size (CSS Tables
        // L3 §11.5.1).
        //
        // Why emit captions even when the grid is missing? Authors
        // who hand-craft a malformed table tree still see their
        // caption text. The missing-grid diagnostic above flags the
        // structural anomaly so they can fix it.
        //
        // Sub-cycle 3 hardening (Finding 1) — the cursor walks
        // CAPTION MARGIN-BOXES, not content extents. For each
        // caption: shift by `MarginBlockStart`, stamp the BORDER-BOX
        // size as the fragment's BlockSize, flush content into the
        // caption's CONTENT BOX (= fragment top + border-block-start
        // + padding-block-start), advance by border-box + margin-
        // block-end.
        //
        // Per Phase 3 Task 13 cycle 1 — top captions emit ONLY on
        // the first page (when resumeAtRow == 0 / isFirstPage). On
        // resume pages the captions were already committed; the row
        // cursor starts at the wrapper's content-block-offset.
        //
        // Per Phase 3 Task 13 cycle 1 hardening (Finding 3 + Copilot
        // #1) — track whether non-row content (top captions) has
        // committed on the current page so the zero-progress detector
        // in the row-pagination loop knows a single oversized row
        // following a top caption is NOT zero-progress.
        var topCaptionCursor = _contentBlockOffset;
        var committedNonRowContent = false;
        if (isFirstPage && captions is { Count: > 0 })
        {
            for (var i = 0; i < captions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = captions[i];
                if (c.Side != CaptionSide.Top) continue;
                topCaptionCursor += c.MarginBlockStart;
                var borderBoxBlockOffset = topCaptionCursor;
                var borderBoxBlockSize = c.BorderBoxBlockSize;
                _sink.Emit(new BoxFragment(
                    Box: c.Caption,
                    InlineOffset: _contentInlineOffset,
                    BlockOffset: borderBoxBlockOffset,
                    InlineSize: usedInlineSize,
                    BlockSize: borderBoxBlockSize));
                // Caption inner content lands INSIDE the caption's
                // padding — content-box-top = fragment-top + border-
                // block-start + padding-block-start.
                var contentBlockOriginInFragmentainer =
                    borderBoxBlockOffset + c.BorderBlockStart + c.PaddingBlockStart;
                c.ContentBuffer.FlushTo(_sink, contentBlockOriginInFragmentainer);
                topCaptionCursor += borderBoxBlockSize + c.MarginBlockEnd;
                committedNonRowContent = true;
            }
        }

        // If the grid is missing or empty there are no rows / cells /
        // bottom captions to emit. The wrapper still gets its visual
        // height from MeasureContentHeight (which folded in the caption
        // totals); the early-return here just skips the row/cell + Phase
        // D emits. Bottom captions ARE emitted below for the no-grid
        // path so authors who attach <caption> + nothing else still
        // see their caption text.
        // Per Phase 3 Task 12 sub-cycle 4 — the scalar `_measuredColumnWidth`
        // was replaced by `_measuredColumnWidths` (per-column array). The
        // early-return guard now bails when EVERY column resolved to ≤0,
        // not just when a scalar mean ≤0. MeasureContentHeight's
        // `anyPositiveWidth` pre-check returns early (leaving
        // `_measuredRows` null) so the `rows is null` branch below covers
        // the captions-only and zero-positive-width paths uniformly.
        // `_measuredColumnWidths` may have been assigned (with all zeros)
        // before that early return — the guard treats the null `rows`
        // pointer as the authoritative "no row stack" signal.
        if (_measuredMissingGrid || rows is null || rows.Count == 0
            || _measuredColumnCount == 0 || _measuredColumnWidths is null
            || _measuredColumnOffsets is null)
        {
            // Phase D — even with no rows, render bottom captions
            // immediately after the top-caption stack so they don't
            // get visually orphaned.
            //
            // Sub-cycle 3 hardening (Finding 1) — same margin-box
            // cursor algorithm as the top-caption pass above. Each
            // caption contributes MarginBlockStart + BorderBoxBlockSize
            // + MarginBlockEnd to the cursor; the fragment's
            // BlockSize = BorderBoxBlockSize; content flushes into
            // the caption's content box.
            //
            // Per Phase 3 Task 13 cycle 1 — for a captions-only /
            // empty-row-stack table the whole emit lands on a single
            // page; resumeAtRow > 0 is rejected above so this branch
            // is the first + last page simultaneously. Emit bottom
            // captions only when isFirstPage (defensive; resumeAtRow >
            // 0 is impossible here).
            var bottomCursor = topCaptionCursor;
            if (isFirstPage && captions is { Count: > 0 })
            {
                for (var i = 0; i < captions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var c = captions[i];
                    if (c.Side != CaptionSide.Bottom) continue;
                    bottomCursor += c.MarginBlockStart;
                    var borderBoxBlockOffset = bottomCursor;
                    var borderBoxBlockSize = c.BorderBoxBlockSize;
                    _sink.Emit(new BoxFragment(
                        Box: c.Caption,
                        InlineOffset: _contentInlineOffset,
                        BlockOffset: borderBoxBlockOffset,
                        InlineSize: usedInlineSize,
                        BlockSize: borderBoxBlockSize));
                    var contentBlockOriginInFragmentainer =
                        borderBoxBlockOffset + c.BorderBlockStart + c.PaddingBlockStart;
                    c.ContentBuffer.FlushTo(_sink, contentBlockOriginInFragmentainer);
                    bottomCursor += borderBoxBlockSize + c.MarginBlockEnd;
                    // Per Phase 3 Task 13 cycle 1 hardening (Finding
                    // 4) — flush bottom caption diagnostics here in
                    // the no-grid / empty-grid path (top caption
                    // diagnostics already flushed at AttemptLayout
                    // entry).
                    if (commitDiagSink is not null)
                    {
                        c.DiagnosticsBuffer?.FlushTo(commitDiagSink);
                    }
                }
            }
            // Sub-cycle 3 hardening (Finding 4) — the no-grid /
            // empty-grid path now ALSO runs the forced-overflow check
            // so a caption-only table whose caption exceeds the
            // fragmentainer block-size emits PAGINATION-FORCED-
            // OVERFLOW-001 (pre-fix the early-return skipped the
            // check + the diagnostic was silently dropped).
            EmitOverflowDiagnosticIfNeeded(
                fragmentainer, bottomCursor, ref layout);
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Per Phase 3 Task 12 sub-cycle 4 — pull the per-column-offsets
        // prefix-sum cached by MeasureContentHeight (built from the
        // per-column-widths array). Cell inline-offset = column-offset
        // at OriginCol; cell inline-size = columnOffsets[OriginCol +
        // ColSpan] − columnOffsets[OriginCol]. This replaces the prior
        // scalar `placement.OriginCol * columnWidth` arithmetic.
        var columnOffsetsLocal = _measuredColumnOffsets;

        // Per Finding 3 — emit in three phases for the WHOLE table so
        // the painter sees backgrounds before borders before content
        // across all rows AND across all cells regardless of rowspan.
        //
        //   Phase 0 (sub-cycle 3): top-caption fragments — see above.
        //   Phase A: row fragments for the COMMITTED row window.
        //   Phase B: cell fragments for the same window (only at origin).
        //   Phase C: cell-content fragments via FlushTo.
        //   Phase D (sub-cycle 3): bottom-caption fragments — see below.
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
        //
        // Per sub-cycle 3 — the row cursor starts AFTER the top
        // captions, not at the wrapper's content origin. The top-
        // caption emit pass updated `topCaptionCursor` to track the
        // running offset; we anchor the row stack there.
        //
        // Per Phase 3 Task 13 cycle 1 — row-stack offsets are computed
        // RELATIVE TO the natural row-stack origin (the top-caption
        // cursor on page 1, the wrapper content-block-offset on
        // resume pages). Cycle 1 keeps rows ATOMIC (no row-internal
        // split); the row's content always emits at the same
        // page-relative anchor we measured during the measure pass.
        var rowStackOrigin = topCaptionCursor;
        var rowBlockOffsets = new double[rows.Count];
        var rowEndBlockOffset = new double[rows.Count + 1];
        var rowCursorBlock = rowStackOrigin;
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

        // Per Phase 3 Task 13 cycle 1 — row-level pagination loop.
        // Walk rows from `resumeAtRow`, consulting the break resolver
        // BEFORE committing each row. The resolver returns BreakHere
        // when adding the next row's chunk would overflow the
        // fragmentainer; on BreakHere we defer the row + subsequent
        // rows to a continuation. The exception is when the row is
        // the FIRST row to commit on this page (rowsEmittedOnPage ==
        // 0 + UsedBlockSize at top-of-page): a single oversized row
        // can't fit anywhere, so we emit it anyway + let the existing
        // PAGINATION-FORCED-OVERFLOW-001 diagnostic surface the issue
        // (matches BlockLayouter's forward-progress pattern).
        //
        // The resolver also returns BreakHere on author-forced breaks
        // (CSS Fragmentation L3 §3.1's `break-before: page` etc.).
        // Cycle 1 doesn't yet propagate forced-break metadata from
        // the row Box; cycle 2+ may consult ComputedStyle.BreakBefore
        // here.
        //
        // The break-resolver `UsedBlockSize` is per-fragmentainer for
        // the streaming path (per IBreakResolver.ConsiderBreakAt's
        // contract). The chunk size is the next row's row-height —
        // table rows are atomic in cycle 1, so the chunk equals the
        // row's full measured height.
        //
        // NOTE: A row whose rowspan cell extends past the row's own
        // height is sized via rowEndBlockOffset[r+1] - rowBlockOffsets[r]
        // = rows[r].RowHeight (the second pass already redistributed
        // the spanned-cell overflow to the LAST row of the span). So
        // breaking at row r doesn't truncate a rowspan-cell mid-render
        // as long as the entire span has been kept atomic — which it
        // is in cycle 1 (rowspan distribution happens during measure;
        // we don't re-split here).
        //
        // The break-resolver's `UsedBlockSize` for the candidate-r
        // opportunity is the page-relative offset where row r would
        // be PLACED. Because we want consistent break decisions
        // regardless of whether the table is the only content on the
        // page, we pass the actual `fragmentainer.UsedBlockSize` plus
        // the page-relative offset from the wrapper's content origin
        // to row r's top. The `chunkBlockSize` is row r's measured
        // height. (The resolver then sees: "if I add chunk to used,
        // does it exceed BlockSize?" — the correct overflow check.)
        var commitFromRow = resumeAtRow;
        var lastCommittedRowExclusive = resumeAtRow;
        var rowsEmittedOnPage = 0;
        // Snapshot UsedBlockSize at entry so we can:
        //  (a) detect "top of page" for the forced-overflow forward-
        //      progress fallback (matches BlockLayouter's
        //      `initialUsed` + `atTopOfPage` logic), and
        //  (b) restore fragmentainer.UsedBlockSize after the
        //      pagination loop. The loop advances UsedBlockSize as
        //      each row commits so the resolver's RemainingBlockSize
        //      check decreases correctly; the outer BlockLayouter
        //      expects UsedBlockSize at AT-TABLE-ENTRY-state for its
        //      own marginBoxBlockSizeForCursor advance to be
        //      authoritative (= it bumps UsedBlockSize by the
        //      wrapper's full margin-box-block-size after the table
        //      emit). Pre-restore the wrapper's advance would
        //      double-count the rows.
        var initialUsedBlockSize = fragmentainer.UsedBlockSize;

        // Per Phase 3 Task 13 cycle 1 — the resume page reinterprets
        // the measured `rowBlockOffsets` as page-relative WITH A
        // DELTA. The measure pass anchored row 0 at `_contentBlockOffset`
        // (page 1 origin). On resume, the resumed row should
        // effectively start at `rowStackOrigin` (= `_contentBlockOffset`
        // since top captions are skipped). The cell + row fragments
        // already in `rowBlockOffsets[]` are page-1-coordinates; on
        // resume we shift them by `-resumeRowOffsetShift` so the
        // emitted fragments land at the correct page-relative
        // position. Conceptually `rowBlockOffsets'[r] = rowBlockOffsets[r] -
        // resumeRowOffsetShift` for the resume page.
        //
        // resumeRowOffsetShift = rowBlockOffsets[resumeAtRow] -
        // rowStackOrigin. On page 1 (resumeAtRow=0) this is 0 (no
        // shift). On a resume page with rowStackOrigin matching the
        // wrapper's content-block-offset for THAT page, the shift
        // re-anchors row resumeAtRow at the new origin.
        var resumeRowOffsetShift = resumeAtRow == 0
            ? 0.0
            : rowBlockOffsets[resumeAtRow] - rowStackOrigin;

        // Shift rowBlockOffsets + rowEndBlockOffset by
        // -resumeRowOffsetShift so the rest of the emit math is
        // identical to page 1. Only the indices >= resumeAtRow are
        // emitted; the [0, resumeAtRow) entries are unused this page
        // (we shift the whole array anyway for simplicity).
        if (resumeRowOffsetShift != 0)
        {
            for (var r = 0; r < rowBlockOffsets.Length; r++)
            {
                rowBlockOffsets[r] -= resumeRowOffsetShift;
            }
            for (var r = 0; r < rowEndBlockOffset.Length; r++)
            {
                rowEndBlockOffset[r] -= resumeRowOffsetShift;
            }
        }

        // Bring UsedBlockSize up to the row-stack origin so the
        // resolver's RemainingBlockSize on the first opportunity
        // reflects the actual space available below the captions +
        // wrapper edges. Restored to initialUsedBlockSize before
        // return.
        fragmentainer.UsedBlockSize = rowStackOrigin;

        for (var r = resumeAtRow; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Compute the page-relative offset for THIS row's top.
            // After the resumeRowOffsetShift normalization above,
            // `rowBlockOffsets[r]` is fragmentainer-absolute (= the
            // same coordinate space as `fragmentainer.UsedBlockSize`).
            //
            // Update fragmentainer.UsedBlockSize to the candidate
            // row's top so the resolver's RemainingBlockSize check
            // reflects "remaining space below this row's top". The
            // resolver then sees chunkBlockSize ≤ RemainingBlockSize
            // ⟺ rowBottom ≤ BlockSize, the correct fit-check.
            fragmentainer.UsedBlockSize = rowBlockOffsets[r];
            var pageRelativeRowTop = rowBlockOffsets[r];
            var chunkBlockSize = rows[r].RowHeight;

            // Per Phase 3 review fix #2 — block-axis naming. Use the
            // TableRowBoundary class so the cost model's penalty
            // matrix keys correctly (currently identical to
            // BlockBoundary, but distinct for future cost tuning).
            var opportunity = new BreakOpportunity(
                UsedBlockSize: pageRelativeRowTop,
                ChunkBlockSize: chunkBlockSize,
                Class: BreakOpportunityClass.TableRowBoundary,
                ForceBreak: false,
                AvoidBreak: false,
                ForceParity: PageParity.Any,
                LinesBeforeBreak: 0,
                StrandsHeading: false,
                SplitsFlexOrGridLine: false,
                ParagraphId: 0);
            var decision = resolver.ConsiderBreakAt(opportunity, fragmentainer);

            // Per Phase 3 Task 13 cycle 1 hardening (Finding 5) —
            // surface Rewind as a diagnostic instead of silently
            // dropping it. TableLayouter doesn't register per-row
            // checkpoints (the outer BlockLayouter owns the table's
            // rewind frontier through the pre-table-emit checkpoint),
            // so a resolver returning Rewind names a checkpoint the
            // table never registered — that's a contract violation
            // worth surfacing. Fallback behavior is unchanged: we
            // treat it as Continue (fail open). Per-row checkpoint
            // capture for break-inside: avoid is cycle 2+ scope.
            if (decision.Action == BreakAction.Rewind)
            {
                OptimizingBreakResolver.SafeEmit(
                    layout.Diagnostics ?? _diagnostics,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.LayoutTableRewindNotSupported001,
                        $"TableLayouter: break resolver returned Rewind at row "
                        + $"boundary {r} (rowspan-aware row count = {rows.Count}). "
                        + "Cycle 1 doesn't register per-row checkpoints — the "
                        + "rewind target would name a checkpoint the table never "
                        + "captured. Falling back to Continue (the row commits "
                        + "in place). Per-row checkpoint capture for "
                        + "break-inside: avoid is cycle 2+ scope. See "
                        + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                        PaginateDiagnosticSeverity.Warning));
                // Synthesize a Continue so the rest of the loop sees
                // the fall-back path (commit the row).
                decision = new BreakDecision(BreakAction.Continue, 0, RewindTo: null);
            }

            if (decision.Action == BreakAction.BreakHere)
            {
                // The current row doesn't fit. Two subcases:
                //   (a) This is the FIRST row on the page AND we're
                //       at the top of the fragmentainer (nothing
                //       emitted yet by THIS table OR by upstream
                //       siblings). The row is unsplittable + larger
                //       than the page; emit anyway (forced-overflow
                //       forward progress). The PAGINATION-FORCED-
                //       OVERFLOW-001 diagnostic fires via
                //       EmitOverflowDiagnosticIfNeeded below.
                //   (b) Otherwise: defer this row + the rest to a
                //       continuation. Bottom captions only emit on
                //       the LAST page (when the loop completes
                //       naturally); we skip them here.
                var nothingEmittedThisPage = rowsEmittedOnPage == 0
                    && !committedNonRowContent;
                if (!nothingEmittedThisPage)
                {
                    // Per Phase 3 Task 13 cycle 1 hardening (Finding 6) —
                    // detect rowspan cells whose origin row commits on
                    // this page but whose span extends past the break
                    // (i.e., OriginRow < r AND OriginRow + RowSpan > r).
                    // Such a cell would be emitted at its FULL natural
                    // extent on the origin page + the continuation row
                    // would re-emit with the spanning cell missing —
                    // visual overflow + missing geometry. Locked design:
                    // force the break BEFORE the rowspan's origin row
                    // (cell stays atomic on the next page) + emit
                    // LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001. If forcing
                    // before the origin row would leave nothing
                    // committed on the current page, fall through to
                    // the forced-overflow path on the original row
                    // (single-oversized-row semantics).
                    var minOriginRow = int.MaxValue;
                    var minOriginRowSpan = 0;
                    for (var pIdx = 0; pIdx < placements.Count; pIdx++)
                    {
                        var p = placements[pIdx];
                        if (p.RowSpan > 1
                            && p.OriginRow >= commitFromRow
                            && p.OriginRow < r
                            && p.OriginRow + p.RowSpan > r)
                        {
                            if (p.OriginRow < minOriginRow)
                            {
                                minOriginRow = p.OriginRow;
                                minOriginRowSpan = p.RowSpan;
                            }
                        }
                    }
                    var rowspanCrossing = minOriginRow != int.MaxValue;
                    var rolledBackForRowspan = false;
                    if (rowspanCrossing)
                    {
                        // Force the break before the rowspan's origin
                        // row, IF doing so still leaves at least one
                        // committed row OR caption content on this
                        // page (= forward progress). Otherwise fall
                        // through to the forced-overflow path.
                        var rolledBackEmittedRows = minOriginRow - commitFromRow;
                        if (rolledBackEmittedRows > 0 || committedNonRowContent)
                        {
                            lastCommittedRowExclusive = minOriginRow;
                            rolledBackForRowspan = true;
                            OptimizingBreakResolver.SafeEmit(
                                layout.Diagnostics ?? _diagnostics,
                                new PaginateDiagnostic(
                                    PaginateDiagnosticCodes.LayoutTableRowspanCrossesPage001,
                                    $"TableLayouter: rowspan cell at origin row "
                                    + $"{minOriginRow} (rowspan={minOriginRowSpan}) "
                                    + $"would cross the page boundary at row {r}. "
                                    + "Cycle 1 keeps rowspan cells atomic across "
                                    + "pages — the break is forced BEFORE row "
                                    + $"{minOriginRow} so the spanning cell stays "
                                    + "together on the next page. CSS Tables L3 "
                                    + "§11 spec-strict rowspan distribution across "
                                    + "pages is sub-cycle 6+ scope. See "
                                    + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                                    PaginateDiagnosticSeverity.Warning));
                        }
                    }

                    // Subcase (b) — return PageComplete with a
                    // TableContinuation pointing at the unfit row.
                    // The committed window is [resumeAtRow,
                    // lastCommittedRowExclusive); the resume window
                    // is [r, rows.Count) — OR, after Finding 6's
                    // rowspan rollback, [minOriginRow, rows.Count).
                    var resumeRowForContinuation =
                        rolledBackForRowspan ? minOriginRow : r;
                    EmitRowWindow(
                        rows!, placements, placementsByRow, columnOffsetsLocal!,
                        rowBlockOffsets, rowEndBlockOffset, usedInlineSize,
                        windowStart: commitFromRow,
                        windowEndExclusive: lastCommittedRowExclusive,
                        cancellationToken,
                        diagSink: commitDiagSink);
                    var consumedThisAttempt =
                        (lastCommittedRowExclusive > commitFromRow
                            ? rowEndBlockOffset[lastCommittedRowExclusive] - rowBlockOffsets[commitFromRow]
                            : 0.0)
                        + (rowStackOrigin - _contentBlockOffset); // include top captions on first page
                    var nextContinuation = new TableContinuation(
                        RepeatHead: false,
                        RepeatFoot: false,
                        NextRowIndex: resumeRowForContinuation,
                        ConsumedBlockSize: priorConsumedBlock + consumedThisAttempt,
                        ColumnLayoutCache: BuildColumnLayoutCache());
                    // Restore fragmentainer.UsedBlockSize so the outer
                    // BlockLayouter's wrapper-advance arithmetic
                    // (marginBoxBlockSizeForCursor) doesn't double-
                    // count the rows we already advanced through. See
                    // the "snapshot UsedBlockSize at entry" comment
                    // above for the contract.
                    fragmentainer.UsedBlockSize = initialUsedBlockSize;
                    return LayoutAttemptResult.PageComplete(
                        nextContinuation, cost: decision.Cost);
                }
                // Subcase (a): fall through to emit r anyway.
            }

            // Continue / forced-overflow forward progress — commit
            // this row. The actual emission happens in a single batch
            // after the loop (EmitRowWindow) so the paint-safe order
            // (rows → cells → content) holds across the entire
            // committed window. We just advance the cursor here.
            lastCommittedRowExclusive = r + 1;
            rowsEmittedOnPage++;
        }

        // Loop completed without BreakHere — all remaining rows
        // commit on this page. Emit the committed window. Per Finding
        // 4, pass the diagnostic sink so per-row cell diagnostics flush
        // alongside their committed content.
        EmitRowWindow(
            rows!, placements, placementsByRow, columnOffsetsLocal!,
            rowBlockOffsets, rowEndBlockOffset, usedInlineSize,
            windowStart: commitFromRow,
            windowEndExclusive: lastCommittedRowExclusive,
            cancellationToken,
            diagSink: commitDiagSink);

        // Per Phase 3 Task 12 sub-cycle 3 — Phase D: emit bottom-side
        // captions AFTER the row stack. Stacks vertically in document
        // order. Starts at the row-stack bottom (= rowEndBlockOffset
        // at index lastCommittedRowExclusive).
        //
        // Sub-cycle 3 hardening (Finding 1) — same margin-box cursor
        // algorithm as the top-caption pass; each caption's
        // MarginBlockStart shifts the cursor, the fragment carries
        // BorderBoxBlockSize, content flushes into the caption's
        // content-box (= fragment top + border-block-start + padding-
        // block-start).
        //
        // Per Phase 3 Task 13 cycle 1 — bottom captions ONLY emit on
        // the LAST page, defined as "all rows committed without a
        // BreakHere return". The PageComplete branch above returns
        // before reaching this code so the captions stay buffered
        // for the resume page.
        //
        // Per Phase 3 Task 13 cycle 1 hardening (Finding 4) — also
        // flush each bottom caption's buffered diagnostics on the
        // LAST page (where they commit). Pre-finding-4 these
        // diagnostics flushed eagerly at AttemptLayout entry.
        var bottomCaptionCursor = rowEndBlockOffset[lastCommittedRowExclusive];
        if (captions is { Count: > 0 })
        {
            for (var i = 0; i < captions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = captions[i];
                if (c.Side != CaptionSide.Bottom) continue;
                bottomCaptionCursor += c.MarginBlockStart;
                var borderBoxBlockOffset = bottomCaptionCursor;
                var borderBoxBlockSize = c.BorderBoxBlockSize;
                _sink.Emit(new BoxFragment(
                    Box: c.Caption,
                    InlineOffset: _contentInlineOffset,
                    BlockOffset: borderBoxBlockOffset,
                    InlineSize: usedInlineSize,
                    BlockSize: borderBoxBlockSize));
                var contentBlockOriginInFragmentainer =
                    borderBoxBlockOffset + c.BorderBlockStart + c.PaddingBlockStart;
                c.ContentBuffer.FlushTo(_sink, contentBlockOriginInFragmentainer);
                bottomCaptionCursor += borderBoxBlockSize + c.MarginBlockEnd;
                if (commitDiagSink is not null)
                {
                    c.DiagnosticsBuffer?.FlushTo(commitDiagSink);
                }
            }
        }

        // Forced-overflow detection — emit once if the total table
        // content (top captions + row stack + bottom captions)
        // extends past the fragmentainer block-size. Multi-page table
        // splitting is HANDLED above for the typical case; this
        // diagnostic now fires only on the LAST page when:
        //   (a) a single row was taller than the fragmentainer +
        //       fell into the forced-overflow forward-progress branch
        //       (subcase (a) of BreakHere handling), or
        //   (b) the bottom-caption block + row stack overflowed past
        //       the fragmentainer.
        //
        // Sub-cycle 3 hardening (Finding 4) — extracted into
        // EmitOverflowDiagnosticIfNeeded so the early-return paths
        // (missing-grid / zero-row / zero-column) can reuse the same
        // logic without duplicating the message.
        EmitOverflowDiagnosticIfNeeded(
            fragmentainer, bottomCaptionCursor, ref layout);

        // Restore fragmentainer.UsedBlockSize so the outer
        // BlockLayouter's wrapper-advance arithmetic doesn't double-
        // count the rows. See the "snapshot UsedBlockSize at entry"
        // comment in the pagination loop for the contract.
        fragmentainer.UsedBlockSize = initialUsedBlockSize;
        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per Phase 3 Task 13 cycle 1 — emit the row + cell +
    /// content fragments for the row window
    /// <c>[windowStart, windowEndExclusive)</c>. Walks the three
    /// paint-safe phases (Phase A row backgrounds → Phase B cell
    /// backgrounds → Phase C cell content) over the committed window
    /// only. Pre-cycle-1 the loops walked the FULL rows list; the
    /// cycle 1 split lets the row-pagination loop commit a partial
    /// window when the resolver returns BreakHere for the unfit row.
    ///
    /// <para>A rowspan cell anchored inside the window but spanning
    /// PAST <paramref name="windowEndExclusive"/> is currently
    /// emitted at its FULL natural extent (the same extent the
    /// measure pass computed). Cycle 1 keeps cells atomic — a
    /// rowspan cell that would overflow the page bottom is committed
    /// in full + the existing forced-overflow diagnostic fires.
    /// Cycle 2+ may split rowspan cells across pages by computing a
    /// truncated block-size for cells whose
    /// <c>OriginRow + RowSpan &gt; windowEndExclusive</c>.</para>
    /// </summary>
    private void EmitRowWindow(
        List<RowMeasurement> rows,
        List<CellPlacement> placements,
        List<CellPlacement>?[] placementsByRow,
        double[] columnOffsets,
        double[] rowBlockOffsets,
        double[] rowEndBlockOffset,
        double usedInlineSize,
        int windowStart,
        int windowEndExclusive,
        CancellationToken cancellationToken,
        IPaginateDiagnosticsSink? diagSink = null)
    {
        if (windowEndExclusive <= windowStart) return;

        // Phase A — row fragments for the committed window. Even an
        // "empty" row gets a (degenerate) fragment because its
        // rowHeight may be 0 (rowspan continuation slots from
        // earlier rows kept the row in the row list).
        for (var r = windowStart; r < windowEndExclusive; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowMeasure = rows[r];
            _sink.Emit(new BoxFragment(
                Box: rowMeasure.Row,
                InlineOffset: _contentInlineOffset,
                BlockOffset: rowBlockOffsets[r],
                InlineSize: usedInlineSize,
                BlockSize: rowMeasure.RowHeight));
        }

        // Phase B — cell fragments for placements ANCHORED in the
        // window. A placement whose OriginRow falls in [windowStart,
        // windowEndExclusive) emits its cell fragment at its full
        // natural extent (rowspan cells preserved unsplit per cycle
        // 1's atomic-cell semantics; see method XML doc).
        for (var r = windowStart; r < windowEndExclusive; r++)
        {
            var bucket = placementsByRow[r];
            if (bucket is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < bucket.Count; i++)
            {
                var placement = bucket[i];
                var cellInlineOffset = _contentInlineOffset
                    + columnOffsets[placement.OriginCol];
                var cellInlineSize =
                    columnOffsets[placement.OriginCol + placement.ColSpan]
                    - columnOffsets[placement.OriginCol];
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
        // FlushTo. Paint-safe order across the window: rows → cells
        // → cell content.
        //
        // Per Phase 3 Task 13 cycle 1 hardening (Finding 4) — also
        // flush each committed cell's buffered diagnostics here so
        // diagnostics for cells in deferred rows stay buffered for
        // the resume page rather than leaking onto the current page.
        for (var r = windowStart; r < windowEndExclusive; r++)
        {
            var bucket = placementsByRow[r];
            if (bucket is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < bucket.Count; i++)
            {
                var placement = bucket[i];
                placement.ContentBuffer.FlushTo(_sink, rowBlockOffsets[r]);
                if (diagSink is not null)
                {
                    placement.DiagnosticsBuffer?.FlushTo(diagSink);
                }
            }
        }
        // Suppress unused-parameter warning when placements is reserved
        // for future content-side row-window-specific logic (e.g.,
        // cycle 2's rowspan truncation).
        _ = placements;
    }

    /// <summary>Per Phase 3 Task 13 cycle 1 hardening (Finding 8) —
    /// snapshot of the table's measure-pass state attached to a
    /// <see cref="TableContinuation"/> on <see cref="LayoutAttemptOutcome.PageComplete"/>.
    /// The resume-page <see cref="TableLayouter"/> loads the cache + skips
    /// the (expensive) re-measurement pass — column widths, cell
    /// placements, per-cell content buffers, captions, and rowspan-aware
    /// row heights are all reusable across pages because they don't
    /// depend on the fragmentainer's page-relative origin (which is the
    /// only thing that changes between pages).
    ///
    /// <para><b>Mutability contract.</b> The cache is meant to be a
    /// snapshot captured at <c>PageComplete</c>-emission time. The
    /// resume-page layouter reads but does NOT mutate these collections
    /// — they may be shared with the source layouter (which is
    /// disposed after the current page's emit, so no aliasing hazard
    /// in practice). The <see cref="CellPlacement.ContentBuffer"/> and
    /// <see cref="CaptionMeasurement.ContentBuffer"/> buffers ARE
    /// mutated by <see cref="MeasuringFragmentSink.FlushTo"/> on each
    /// flush; each cell + caption flushes AT MOST ONCE across all pages
    /// (committed once and only once), so the buffer lifecycle is
    /// well-defined: deferred cells / captions on page N still have
    /// their content buffered for page N+1.</para></summary>
    internal sealed class ColumnLayoutCache
    {
        public ColumnLayoutCache(
            int columnCount,
            double[] columnWidths,
            double[] columnOffsets,
            List<RowMeasurement> measuredRows,
            List<CellPlacement> measuredPlacements,
            List<CaptionMeasurement>? measuredCaptions,
            double measuredTopCaptionsTotal,
            double measuredBottomCaptionsTotal,
            double measuredUsedInlineSize,
            double measuredContentHeight)
        {
            ColumnCount = columnCount;
            ColumnWidths = columnWidths;
            ColumnOffsets = columnOffsets;
            MeasuredRows = measuredRows;
            MeasuredPlacements = measuredPlacements;
            MeasuredCaptions = measuredCaptions;
            MeasuredTopCaptionsTotal = measuredTopCaptionsTotal;
            MeasuredBottomCaptionsTotal = measuredBottomCaptionsTotal;
            MeasuredUsedInlineSize = measuredUsedInlineSize;
            MeasuredContentHeight = measuredContentHeight;
        }
        public int ColumnCount { get; }
        public double[] ColumnWidths { get; }
        public double[] ColumnOffsets { get; }
        public List<RowMeasurement> MeasuredRows { get; }
        public List<CellPlacement> MeasuredPlacements { get; }
        public List<CaptionMeasurement>? MeasuredCaptions { get; }
        public double MeasuredTopCaptionsTotal { get; }
        public double MeasuredBottomCaptionsTotal { get; }
        public double MeasuredUsedInlineSize { get; }
        public double MeasuredContentHeight { get; }
    }

    /// <summary>Per Phase 3 Task 13 cycle 1 hardening (Finding 8) —
    /// build a <see cref="ColumnLayoutCache"/> from the current
    /// layouter's measure-phase state. Called by <see cref="AttemptLayout"/>
    /// when returning <see cref="LayoutAttemptOutcome.PageComplete"/>
    /// + attached to the new <see cref="TableContinuation"/>. Returns
    /// <see langword="null"/> when the measure pass didn't populate
    /// the cache-relevant fields (degenerate paths — missing-grid,
    /// zero-row, etc.).</summary>
    private ColumnLayoutCache? BuildColumnLayoutCache()
    {
        if (_measuredRows is null
            || _measuredPlacements is null
            || _measuredColumnWidths is null
            || _measuredColumnOffsets is null)
        {
            return null;
        }
        return new ColumnLayoutCache(
            columnCount: _measuredColumnCount,
            columnWidths: _measuredColumnWidths,
            columnOffsets: _measuredColumnOffsets,
            measuredRows: _measuredRows,
            measuredPlacements: _measuredPlacements,
            measuredCaptions: _measuredCaptionList,
            measuredTopCaptionsTotal: _measuredTopCaptionsTotal,
            measuredBottomCaptionsTotal: _measuredBottomCaptionsTotal,
            measuredUsedInlineSize: _measuredUsedInlineSize,
            measuredContentHeight: _measuredContentHeight);
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 3 hardening (Finding 4) —
    /// shared overflow-detection helper: emits exactly one
    /// <see cref="PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
    /// Warning when the table's total content extent
    /// (<paramref name="tableBottomBlockOffset"/>) exceeds the
    /// fragmentainer's block-size. Multi-fragmentainer table splitting
    /// is deferred — see
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.
    ///
    /// <para>Pre-Finding-4 the overflow check ran only in the row-
    /// stack emit path; the missing-grid / zero-row / zero-column
    /// early-return paths skipped it. A caption-only table whose
    /// caption was taller than the fragmentainer would commit all
    /// caption content without emitting the diagnostic. Post-fix the
    /// helper runs in BOTH paths.</para></summary>
    private void EmitOverflowDiagnosticIfNeeded(
        FragmentainerContext fragmentainer,
        double tableBottomBlockOffset,
        ref LayoutContext layout)
    {
        if (tableBottomBlockOffset > fragmentainer.BlockSize)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    $"TableLayouter: table on fragmentainer page index "
                    + $"{fragmentainer.PageIndex} overflows page block-"
                    + $"size {fragmentainer.BlockSize:0.##} (table content "
                    + $"extends to {tableBottomBlockOffset:0.##}). The "
                    + "layouter commits all rows + captions anyway; "
                    + "multi-fragmentainer splitting is deferred — see "
                    + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }
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

        // Per Phase 3 Task 13 cycle 1 hardening (Finding 8) — load
        // the cached measure-phase state from the incoming
        // TableContinuation's ColumnLayoutCache, if any. Skips the
        // measure pass entirely on resume pages — column widths +
        // cell placements + per-cell buffered content + row heights
        // are all reusable across pages (they don't depend on the
        // page-relative origin which is the only thing that changes
        // between pages).
        if (_incomingTableContinuation?.ColumnLayoutCache
            is ColumnLayoutCache cachedColumnLayout)
        {
            _measuredColumnCount = cachedColumnLayout.ColumnCount;
            _measuredColumnWidths = cachedColumnLayout.ColumnWidths;
            _measuredColumnOffsets = cachedColumnLayout.ColumnOffsets;
            _measuredRows = cachedColumnLayout.MeasuredRows;
            _measuredPlacements = cachedColumnLayout.MeasuredPlacements;
            _measuredCaptionList = cachedColumnLayout.MeasuredCaptions;
            _measuredTopCaptionsTotal = cachedColumnLayout.MeasuredTopCaptionsTotal;
            _measuredBottomCaptionsTotal = cachedColumnLayout.MeasuredBottomCaptionsTotal;
            _measuredUsedInlineSize = cachedColumnLayout.MeasuredUsedInlineSize;
            _measuredContentHeight = cachedColumnLayout.MeasuredContentHeight;
            _measureDone = true;
            return _measuredContentHeight;
        }

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 6 —
        // caption measurement is DEFERRED until after the column-
        // widths pass has produced _measuredUsedInlineSize. CSS
        // Tables L3 §11.5.1 specifies the caption's used inline-size
        // equals the outer inline-size of the table grid; under
        // auto-table-layout that can DIFFER from the wrapper's
        // content-inline-size (when min-content overflow fires,
        // the grid's inline extent exceeds the wrapper, and the
        // captions should match the grid). Pre-fix captions were
        // always measured at _contentInlineSize, producing visual
        // narrow-captions over a wide grid.
        //
        // For the missing-grid + zero-row + zero-column early-return
        // paths the caption measurement still falls back to
        // _contentInlineSize (no grid → grid-outer-inline-size is
        // undefined; fall back to the wrapper).

        // Locate the TableGrid child.
        var grid = FindTableGrid(_rootBox);
        if (grid is null)
        {
            _measuredMissingGrid = true;
            // Captions still contribute to the total even if the grid
            // is missing — they were laid out as standalone block
            // boxes. The fallback inline-size = _contentInlineSize.
            _measuredCaptionList = MeasureCaptions(
                _rootBox, _contentInlineSize,
                fragmentainer, ref layout, cancellationToken,
                out _measuredTopCaptionsTotal, out _measuredBottomCaptionsTotal);
            _measureDone = true;
            _measuredContentHeight =
                _measuredTopCaptionsTotal + _measuredBottomCaptionsTotal;
            return _measuredContentHeight;
        }

        // Collect rows in document order (recursing into row groups).
        var rows = new List<Box>();
        CollectRows(grid, rows, cancellationToken);

        if (rows.Count == 0)
        {
            // Captions-only case — fall back to _contentInlineSize.
            _measuredCaptionList = MeasureCaptions(
                _rootBox, _contentInlineSize,
                fragmentainer, ref layout, cancellationToken,
                out _measuredTopCaptionsTotal, out _measuredBottomCaptionsTotal);
            _measureDone = true;
            _measuredContentHeight =
                _measuredTopCaptionsTotal + _measuredBottomCaptionsTotal;
            return _measuredContentHeight;
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
            // No columns → no grid extent; measure captions at the
            // wrapper's content-inline-size + return caption-only
            // total.
            _measuredCaptionList = MeasureCaptions(
                _rootBox, _contentInlineSize,
                fragmentainer, ref layout, cancellationToken,
                out _measuredTopCaptionsTotal, out _measuredBottomCaptionsTotal);
            _measureDone = true;
            _measuredContentHeight =
                _measuredTopCaptionsTotal + _measuredBottomCaptionsTotal;
            return _measuredContentHeight;
        }

        // Per Phase 3 Task 12 sub-cycle 4 + 5 — compute per-column
        // widths. The branch on `table-layout` selects between:
        //   * `fixed` (CSS Tables L3 §3.5) — 4-pass algorithm with
        //     <col> / <colgroup> declarations + first-row cell widths
        //     + Pass D reconciliation.
        //   * `auto` (CSS Tables L3 §3) — shrink-to-fit via per-cell
        //     min/max-content intrinsic widths + per-column
        //     aggregation + linear-interpolation distribution.
        //     Sub-cycle 5 hardening Finding 2 also incorporates
        //     <col> / first-row widths as min/max-content floors.
        //
        // Sub-cycle 4 hardening (Finding 1) — fixed-layout's Pass D
        // distributes leftover wrapper space equally across columns
        // when the column sum < contentInlineSize, or records the
        // inline-overflow when it exceeds. The grid's used inline-size
        // (_measuredUsedInlineSize) is consumed by the emit pass to
        // size the row + caption fragments to the ACTUAL column extent
        // instead of the wrapper's content-inline-size (the pre-fix
        // mismatch left visual gaps when columnSum < contentInlineSize).
        var columnWidths = ComputeColumnWidths(
            _rootBox, rows, columnCount, _contentInlineSize, placements,
            fragmentainer, ref layout, cancellationToken);
        _measuredColumnWidths = columnWidths;

        // Pre-compute per-column inline OFFSETS as a prefix-sum so the
        // emit pass + the measure pass below can do O(1) lookups.
        var columnOffsets = new double[columnCount + 1];
        for (var c = 0; c < columnCount; c++)
        {
            columnOffsets[c + 1] = columnOffsets[c] + columnWidths[c];
        }
        _measuredColumnOffsets = columnOffsets;
        // Sub-cycle 4 hardening (Finding 1) — used inline-size is the
        // post-Pass-D column sum (which ComputeColumnWidths already
        // reconciled against _contentInlineSize for the fixed path).
        // Pre-fix the value was always _contentInlineSize; post-fix it
        // matches the row + cell geometry.
        _measuredUsedInlineSize = columnOffsets[columnCount];

        // Defensive: if every column resolved to 0 inline-size (e.g.,
        // contentInlineSize ≤ 0), bail like the prior scalar-zero path.
        var anyPositiveWidth = false;
        for (var c = 0; c < columnCount; c++)
        {
            if (columnWidths[c] > 0) { anyPositiveWidth = true; break; }
        }
        if (!anyPositiveWidth)
        {
            // All columns resolved to zero — degenerate grid; measure
            // captions at the wrapper's content-inline-size + return
            // caption-only total.
            _measuredCaptionList = MeasureCaptions(
                _rootBox, _contentInlineSize,
                fragmentainer, ref layout, cancellationToken,
                out _measuredTopCaptionsTotal, out _measuredBottomCaptionsTotal);
            _measureDone = true;
            _measuredContentHeight =
                _measuredTopCaptionsTotal + _measuredBottomCaptionsTotal;
            return _measuredContentHeight;
        }

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 6 — now
        // that the grid's used inline-size is known, measure captions
        // at that size (CSS Tables L3 §11.5.1 — caption inline-size
        // matches the grid's outer inline-size). The cells haven't
        // been measured yet but the grid's INLINE extent is already
        // settled. Cell content measurement happens AFTER (below) and
        // uses _measuredColumnWidths which the row-stack-driven
        // pagination logic consumes.
        var captionInlineSize = _measuredUsedInlineSize > 0
            ? _measuredUsedInlineSize
            : _contentInlineSize;
        _measuredCaptionList = MeasureCaptions(
            _rootBox, captionInlineSize,
            fragmentainer, ref layout, cancellationToken,
            out _measuredTopCaptionsTotal, out _measuredBottomCaptionsTotal);

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
            // Per Phase 3 Task 12 sub-cycle 4 — cell inline-offset is
            // the column-offset prefix-sum at `OriginCol`; cell inline-
            // size is the sum of `columnWidths[OriginCol..OriginCol+ColSpan]`
            // (= `columnOffsets[OriginCol + ColSpan] - columnOffsets[OriginCol]`).
            // Pre-sub-cycle-4 this was `placement.ColSpan * columnWidth`.
            var cellInlineOffset = _contentInlineOffset
                + columnOffsets[placement.OriginCol];
            var cellInlineSize =
                columnOffsets[placement.OriginCol + placement.ColSpan]
                - columnOffsets[placement.OriginCol];
            var (buffer, diagBuffer, _) = MeasureCellContent(
                cellBox: placement.Cell,
                cellInlineOffset: cellInlineOffset,
                cellInlineSize: cellInlineSize,
                fragmentainer: fragmentainer,
                layout: ref layout,
                cancellationToken: cancellationToken);
            // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 3 —
            // the cell's content extent includes the cell's block-
            // axis box-model edges (border-block-start + padding-
            // block-start + padding-block-end + border-block-end)
            // so the row height covers the inner content + the
            // cell's own padding/border. MeasuringFragmentSink's
            // tracker measures the inner content extent only.
            var cellBlockEdges =
                placement.Cell.Style.ReadLengthPxOrZero(PropertyId.PaddingTop)
                + placement.Cell.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom)
                + placement.Cell.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth)
                + placement.Cell.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            placements[pIdx] = new CellPlacement(
                cell: placement.Cell,
                originRow: placement.OriginRow,
                originCol: placement.OriginCol,
                rowSpan: placement.RowSpan,
                colSpan: placement.ColSpan,
                contentBuffer: buffer,
                diagnosticsBuffer: diagBuffer,
                contentBlockExtent: buffer.MaxBlockExtentFromCellOrigin + cellBlockEdges);
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
        // sub-cycle 4+ work (see
        // docs/deferrals.md#table-auto-fixed-spans-borders) — but
        // it's deterministic + simple.
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
        var rowStackHeight = 0.0;
        for (var r = 0; r < rows.Count; r++)
        {
            measured.Add(new RowMeasurement(rows[r], rowHeights[r]));
            rowStackHeight += rowHeights[r];
        }

        _measuredRows = measured;
        // Per Phase 3 Task 12 sub-cycle 3 — the table's total content
        // height includes the top-caption stack + row stack + bottom-
        // caption stack. The wrapper's auto-height resolution in
        // BlockLayouter consumes this single total via the existing
        // MeasureContentHeight contract; the emit phase splits it
        // back into per-section offsets via _measuredTopCaptionsTotal.
        _measuredContentHeight = _measuredTopCaptionsTotal
            + rowStackHeight
            + _measuredBottomCaptionsTotal;
        _measureDone = true;
        return _measuredContentHeight;
    }

    /// <summary>Per Finding 4 — DoS-resistant cap on the cumulative
    /// <c>rowspan × colspan</c> slot count for one table. Legal HTML
    /// attribute values give a single cell up to
    /// <c>65534 × 1000 = ~65M</c> slots; without a cap a hostile
    /// author can force unbounded CPU + memory work in the placement
    /// pass. 1M is generous for legitimate documents (a 1000-row × 1000-
    /// column matrix) yet keeps placement bounded.</summary>
    private const long MaxOccupiedSlots = 1_000_000;

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 —
    /// per-table cap on the cumulative auto-table-layout intrinsic-
    /// measurement op count. Each cell costs 2 ops (one min-content
    /// + one max-content speculative <see cref="BlockLayouter"/>
    /// dispatch). 10,000 ops comfortably handles real invoice /
    /// report tables (5,000 cells covers a 100-row × 50-column
    /// matrix) while capping pathological inputs at a known bound.
    /// Beyond the cap, remaining cells fall back to
    /// <c>(minContent=0, maxContent=contentInlineSize)</c> + the
    /// <c>LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001</c>
    /// diagnostic surfaces.</summary>
    private const long MaxIntrinsicMeasurementOps = 10_000;

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
        // cells. Sub-cycle 3 — the caption deferral diagnostic is also
        // gone; captions are laid out per CSS Tables L3 §11.5 (see
        // MeasureCaptions + Phase 0 / Phase D emits). The
        // LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 code stays in the
        // diagnostic catalog because the missing-TableGrid anomaly +
        // the rowspan="0" / colspan="0" deferral still emit it.

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

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 —
        // intrinsic-measurement-budget exceeded; ComputeColumnWidthsAuto
        // fell back to (0, contentInlineSize) for the cells past the
        // cap. The table still renders but the column min/max-content
        // aggregation for those cells didn't reflect their content.
        if (_intrinsicMeasurementBudgetExceeded)
        {
            OptimizingBreakResolver.SafeEmit(
                sink,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutTableIntrinsicMeasurementBudgetExceeded001,
                    $"TableLayouter: cumulative auto-table-layout intrinsic-"
                    + $"measurement op count exceeded the {MaxIntrinsicMeasurementOps} "
                    + $"DoS budget. {_intrinsicMeasurementCellsMeasured} cells "
                    + $"were fully measured (min + max-content speculative passes) "
                    + $"+ {_intrinsicMeasurementCellsFellBack} cells fell back to "
                    + $"(minContent=0, maxContent=contentInlineSize) — column "
                    + $"widths for those cells degenerate toward an equal-split-like "
                    + "distribution. Defends against hostile HTML with very "
                    + "large tables of pathologically deep cell content trees. "
                    + "See docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }

        // Per Phase 3 Task 12 sub-cycle 3 — captions are now laid out
        // for real (see MeasureCaptions + Phase 0 / Phase D emits in
        // AttemptLayout). The caption-emits-LAYOUT-TABLE-FEATURE-
        // UNSUPPORTED-001 path is gone; the code itself stays in the
        // diagnostic catalog (still triggered by missing-TableGrid +
        // span=0 deferral above). Caption-internal diagnostics are
        // flushed via the per-caption BufferingDiagnosticsSink at
        // emit-commit time (mirrors the cell-content contract).
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

    /// <summary>Per Phase 3 Task 12 sub-cycle 3 — walk the wrapper's
    /// direct children for <see cref="BoxKind.TableCaption"/> boxes,
    /// laying each one out via a nested <see cref="BlockLayouter"/>
    /// into a buffered <see cref="MeasuringFragmentSink"/>. The
    /// returned list preserves document order; each entry carries the
    /// caption Box, its resolved <see cref="CaptionSide"/>, its
    /// buffered content fragments, and its measured block-extent.
    /// The two <c>out</c> totals sum the block extents of all top-
    /// side / bottom-side captions for the emit-pass offset arithmetic.
    ///
    /// <para><b>Per BoxBuilder Rec 5</b> captions stay as DIRECT
    /// children of the wrapper (NOT inside the grid) so this walks
    /// the wrapper, not the grid.</para>
    ///
    /// <para><b>Per CSS Tables L3 §11.5.1.</b> Caption inline-size =
    /// wrapper content-inline-size. Sub-cycle 3 doesn't yet ship
    /// auto-table-layout, so the grid's outer inline-size and the
    /// wrapper's content-inline-size are equal; this matches the
    /// spec's requirement that "the caption box's used inline-size is
    /// equal to the outer inline-size of the table grid". When auto-
    /// table-layout lands (sub-cycle 4+) this helper will need to
    /// switch to the grid's outer inline-size — captured here as a
    /// TODO inline.</para>
    ///
    /// <para><b>Per Finding 3 (sub-cycle 1).</b> Each caption-content
    /// layout dispatches against a FRESH <see cref="BreakResolver"/>
    /// scoped to the caption so the outer resolver's checkpoint state
    /// is preserved untouched — same isolation contract as cells.</para>
    /// </summary>
    private List<CaptionMeasurement>? MeasureCaptions(
        Box wrapper,
        double captionInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken,
        out double topCaptionsTotal,
        out double bottomCaptionsTotal)
    {
        topCaptionsTotal = 0;
        bottomCaptionsTotal = 0;

        List<CaptionMeasurement>? list = null;
        for (var i = 0; i < wrapper.Children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ch = wrapper.Children[i];
            if (ch.Kind != BoxKind.TableCaption) continue;

            // CSS Tables L3 §11.5.2 — caption-side is inherited;
            // default is `top`. CSS Logical Properties 1 §4.4 admits
            // writing-mode-relative `block-start` / `block-end`
            // (mapped to top/bottom under LTR horizontal writing
            // mode by ReadCaptionSide; RTL + vertical modes deferred
            // to sub-cycle 4+).
            //
            // Finding 3 (sub-cycle 3 hardening) — the keyword resolver
            // (KeywordResolver) admits `inline-start` / `inline-end`
            // because they're valid CSS values for caption-side. At
            // resolution time NO diagnostic fires (resolver-side
            // accepts the keyword). At LAYOUT time the inline-axis
            // keywords map to `top` under LTR horizontal writing mode
            // — sub-cycle 3 doesn't yet route through the writing-
            // mode resolver. The fallback is surfaced as a
            // LAYOUT-TABLE-FEATURE-UNSUPPORTED-001 Warning here so
            // authors who set `caption-side: inline-start` see why
            // their caption renders at the top instead of the side.
            var captionSideKeyword = ch.Style.ReadKeywordOrDefault(
                PropertyId.CaptionSide, defaultIndex: 0);
            var side = ch.Style.ReadCaptionSide();
            if (captionSideKeyword == 4 || captionSideKeyword == 5)
            {
                var keywordName = captionSideKeyword == 4
                    ? "inline-start"
                    : "inline-end";
                OptimizingBreakResolver.SafeEmit(
                    layout.Diagnostics ?? _diagnostics,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                        $"TableLayouter: caption-side: {keywordName} on "
                        + "<caption> is currently unsupported — only physical "
                        + "`top` / `bottom` + the writing-mode-relative "
                        + "`block-start` / `block-end` are routed through to "
                        + "layout. Inline-axis caption placement requires "
                        + "writing-mode + direction resolution (deferred to "
                        + "sub-cycle 4+; see "
                        + "docs/deferrals.md#table-auto-fixed-spans-borders). "
                        + "Caption falls back to `top` under LTR horizontal "
                        + "writing mode.",
                        PaginateDiagnosticSeverity.Warning));
            }

            // The caption is laid out at the grid's used inline-size.
            // CSS Tables L3 §11.5.1 — caption inline-size equals the
            // outer inline-size of the table grid; under auto-table-
            // layout the grid extent can differ from the wrapper's
            // content-inline-size. Per Phase 3 Task 12 sub-cycle 5
            // hardening Finding 6, the caller passes the post-column-
            // widths used inline-size. The fallback for missing-grid /
            // empty-grid paths is the wrapper's content-inline-size
            // (which is what the caller passes when the grid hasn't
            // produced a usable inline extent).
            //
            // Inline offset for buffered fragments is the wrapper's
            // content-inline-offset so the caption text aligns with
            // the cell grid below / above.
            //
            // Finding 1 — MeasureCaptionContent now returns the full
            // box-model metric set; CaptionMeasurement stores them
            // for the emit pass to consume.
            var m = MeasureCaptionContent(
                captionBox: ch,
                captionInlineOffset: _contentInlineOffset,
                captionInlineSize: captionInlineSize,
                fragmentainer: fragmentainer,
                layout: ref layout,
                cancellationToken: cancellationToken);

            list ??= new List<CaptionMeasurement>(capacity: 2);
            var measurement = new CaptionMeasurement(
                caption: ch,
                side: side,
                contentBuffer: m.Buffer,
                diagnosticsBuffer: m.DiagnosticsBuffer,
                contentBlockExtent: m.BlockExtent,
                marginBlockStart: m.MarginBlockStart,
                marginBlockEnd: m.MarginBlockEnd,
                borderBlockStart: m.BorderBlockStart,
                borderBlockEnd: m.BorderBlockEnd,
                paddingBlockStart: m.PaddingBlockStart,
                paddingBlockEnd: m.PaddingBlockEnd,
                borderInlineStart: m.BorderInlineStart,
                borderInlineEnd: m.BorderInlineEnd,
                paddingInlineStart: m.PaddingInlineStart,
                paddingInlineEnd: m.PaddingInlineEnd,
                declaredBlockSize: m.DeclaredBlockSize);
            list.Add(measurement);

            // Per Finding 1 — the contribution to the stacking total
            // is the MARGIN-BOX block-size (margin + border + padding
            // + content), NOT just the content extent. Adjacent
            // caption margin-collapse (CSS 2.1 §8.3.1) is out of
            // scope for sub-cycle 3 hardening — captions stack by
            // margin SUM, not by collapse; sub-cycle 4+ may revisit.
            var marginBoxBlockSize = measurement.MarginBoxBlockSize;
            if (side == CaptionSide.Bottom)
            {
                bottomCaptionsTotal += marginBoxBlockSize;
            }
            else
            {
                topCaptionsTotal += marginBoxBlockSize;
            }
        }

        return list;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 3 — lay out
    /// <paramref name="captionBox"/>'s inner content via a nested
    /// <see cref="BlockLayouter"/>, BUFFERING the translated
    /// fragments in a <see cref="MeasuringFragmentSink"/> for later
    /// flush. Mirrors <see cref="MeasureCellContent"/>'s contract —
    /// buffered diagnostics sink, deferred block translation — the
    /// only differences are (1) the inline translation = caption's
    /// content-box inline-offset (caption inline content sits inside
    /// the caption's padding/border edges), (2) no rowspan
    /// distribution is involved so the buffered block translation can
    /// be applied directly by FlushTo with the caption's final
    /// content-box origin.
    ///
    /// <para><b>Sub-cycle 3 hardening (Finding 1).</b> The caption's
    /// CSS box model is now read from
    /// <paramref name="captionBox"/>.Style — margin / border /
    /// padding on all four edges + explicit <c>height</c>. Returned
    /// to the caller for storage on the <see cref="CaptionMeasurement"/>
    /// record. The nested BlockLayouter sees only the caption's
    /// CONTENT BOX (inline-size = caption inline-size minus inline
    /// borders + paddings); the buffered fragments are flushed with
    /// the caption's content-box block origin at FlushTo time.</para>
    ///
    /// <para><b>Sub-cycle 3 hardening (Finding 2 — caption truncation).</b>
    /// The nested BlockLayouter is dispatched against a
    /// <see cref="NoBreakBreakResolver"/> that ALWAYS returns
    /// <see cref="BreakAction.Continue"/>. The caption is treated as a
    /// single indivisible block — even if its content would exceed
    /// the outer fragmentainer's block-size, the nested layouter walks
    /// the full subtree. The caller (<see cref="MeasureCaptions"/>)
    /// then surfaces a <see cref="PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
    /// at commit time if the measured caption + table overflows the
    /// outer page. Multi-fragmentainer caption splitting is deferred
    /// (see docs/deferrals.md#table-auto-fixed-spans-borders).</para>
    /// </summary>
    private (MeasuringFragmentSink Buffer,
        BufferingDiagnosticsSink? DiagnosticsBuffer,
        double BlockExtent,
        double MarginBlockStart, double MarginBlockEnd,
        double BorderBlockStart, double BorderBlockEnd,
        double PaddingBlockStart, double PaddingBlockEnd,
        double BorderInlineStart, double BorderInlineEnd,
        double PaddingInlineStart, double PaddingInlineEnd,
        double DeclaredBlockSize) MeasureCaptionContent(
        Box captionBox,
        double captionInlineOffset,
        double captionInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        // Finding 1 — read the caption's CSS box model. Mirrors the
        // same pattern BlockLayouter uses for normal-flow children
        // (margin/border/padding/height per edge). The values default
        // to 0 when not set or for non-LengthPx slots (Auto /
        // Percentage / Unset) per ReadLengthPxOrZero's contract;
        // percentage resolution + Auto margin centering remain
        // deferred per CSS 2.1 §10.3.3 (Phase 3 Task 7+ scope —
        // documented in ComputedStyleLayoutExtensions).
        var style = captionBox.Style;
        var marginBlockStart = style.ReadLengthPxOrZero(PropertyId.MarginTop);
        var marginBlockEnd = style.ReadLengthPxOrZero(PropertyId.MarginBottom);
        var borderBlockStart = style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var borderBlockEnd = style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var paddingBlockStart = style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var paddingBlockEnd = style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
        var borderInlineStart = style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var borderInlineEnd = style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingInlineStart = style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var paddingInlineEnd = style.ReadLengthPxOrZero(PropertyId.PaddingRight);
        var declaredBlockSize = style.ReadLengthPxOrZero(PropertyId.Height);

        // The caption's CONTENT BOX inline-size is the caller's
        // inline-size (= wrapper content-inline-size for sub-cycle 3;
        // sub-cycle 4 will switch to grid outer-inline-size once
        // auto-table-layout ships) MINUS the caption's own inline
        // borders + paddings. Clamp to at least 1 px so the inner
        // BlockLayouter doesn't divide by zero.
        var contentInlineSize = captionInlineSize
            - borderInlineStart - paddingInlineStart
            - borderInlineEnd - paddingInlineEnd;
        if (contentInlineSize < 1) contentInlineSize = 1;

        // The inline OFFSET passed to the buffering sink is the
        // caption's content-box inline-start edge (= caller's caption
        // inline offset + the caption's own border-inline-start +
        // padding-inline-start). Pre-Finding-1 this was the caption's
        // border-box inline-start; inner content then sat AT the
        // border instead of INSIDE the padding.
        var contentInlineOffset = captionInlineOffset
            + borderInlineStart + paddingInlineStart;

        // Pass 0 for the block translation — the caption's final
        // block origin is known only after the row-stack heights
        // settle (because the row stack's offset depends on the top-
        // caption total, which is THIS measure pass). The emit pass
        // calls FlushTo with the caption's finalized CONTENT-BOX
        // block origin (Finding 1: content-box not border-box, so
        // content lands inside the caption's padding).
        var measuringSink = new MeasuringFragmentSink(
            outerSinkBaselineCursor: _sink.Cursor,
            inlineOffsetTranslation: contentInlineOffset,
            blockOffsetTranslation: 0);

        // The caption's content area is what the inner BlockLayouter
        // sees as its own page. Sub-cycle 3 + hardening expose
        // contentInlineSize MINUS the caption's inline borders +
        // paddings (Finding 1). The inner fragmentainer's block-size
        // matches the outer fragmentainer's so the inner break
        // resolver — Finding 2's NoBreakBreakResolver — never sees
        // a chunk-too-tall condition that would otherwise force a
        // BreakHere. Multi-fragmentainer caption splitting is
        // deferred — see docs/deferrals.md#table-auto-fixed-spans-borders.
        var captionFragmentainer = new FragmentainerContext(
            contentInlineSize: contentInlineSize,
            blockSize: Math.Max(fragmentainer.BlockSize, 1));

        // Per Finding 5 (sub-cycle 1) — wrap the ambient diagnostic
        // sink in a BufferingDiagnosticsSink so caption-internal
        // diagnostics don't leak when the outer resolver discards the
        // table. Same flushing contract as cells: FlushTo on commit;
        // Discard on rewind.
        BufferingDiagnosticsSink? diagBuffer = null;
        IPaginateDiagnosticsSink? captionDiagnosticSink = layout.Diagnostics ?? _diagnostics;
        if (captionDiagnosticSink is not null)
        {
            diagBuffer = new BufferingDiagnosticsSink();
            captionDiagnosticSink = diagBuffer;
        }

        var innerLayout = new LayoutContext(captionFragmentainer)
        {
            Diagnostics = captionDiagnosticSink,
            WritingMode = layout.WritingMode,
            IsRtl = layout.IsRtl,
        };

        using var captionLayouter = new BlockLayouter(
            rootBox: captionBox,
            sink: measuringSink,
            incomingContinuation: null,
            diagnostics: captionDiagnosticSink,
            shaperResolver: _shaperResolver);

        // Per sub-cycle 3 hardening (Finding 2) — caption-content
        // layout uses a NoBreakBreakResolver so the inner
        // BlockLayouter NEVER returns PageComplete. Captions are
        // single indivisible boxes (CSS Tables L3 §11.5 treats
        // them as a block-level box in the table's principal box
        // formatting context). The outer fragmentainer-overflow
        // check at commit time handles the rare "caption taller
        // than the page" case by emitting
        // PAGINATION-FORCED-OVERFLOW-001.
        //
        // The Dispose path on NoBreakBreakResolver returns its held
        // checkpoint lease (if any) to the pool — same contract as
        // BreakResolver.
        using var captionResolver = new NoBreakBreakResolver();

        // Defense-in-depth: NoBreakBreakResolver guarantees Continue,
        // but if the inner BlockLayouter's forced-overflow forward
        // progress path ever bypasses ConsiderBreakAt + returns
        // PageComplete on its own, the assert below surfaces the
        // contract violation as a deferral diagnostic. The caller
        // (MeasureCaptions) ALSO checks the outer overflow at commit
        // time + emits PAGINATION-FORCED-OVERFLOW-001 there.
        var attemptResult = captionLayouter.AttemptLayout(
            captionFragmentainer, ref innerLayout, captionResolver,
            LayoutAttemptStrategy.LastResort, cancellationToken);
        if (attemptResult.Outcome == LayoutAttemptOutcome.PageComplete)
        {
            // Surface via the buffered diagnostic sink — picked up at
            // commit by the outer FlushTo. The buffered content the
            // BlockLayouter already emitted PRE-PageComplete is still
            // in the measuringSink + still gets rendered.
            OptimizingBreakResolver.SafeEmit(
                captionDiagnosticSink,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    "TableLayouter: caption content's nested BlockLayouter "
                    + "returned PageComplete despite NoBreakBreakResolver. "
                    + "Caption content past the inner overflow forward-"
                    + "progress threshold may have been truncated. "
                    + "Multi-fragmentainer caption splitting is deferred — "
                    + "see docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }

        return (measuringSink, diagBuffer, measuringSink.MaxBlockExtentFromCellOrigin,
            marginBlockStart, marginBlockEnd,
            borderBlockStart, borderBlockEnd,
            paddingBlockStart, paddingBlockEnd,
            borderInlineStart, borderInlineEnd,
            paddingInlineStart, paddingInlineEnd,
            declaredBlockSize);
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

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 + 5 — compute the
    /// per-column width array for the table. CSS Tables L3 §3.5
    /// (fixed-table-layout): when <c>table-layout: fixed</c> is set,
    /// column widths derive from author declarations; CSS Tables L3 §3
    /// (auto-table-layout): when <c>table-layout: auto</c> (default),
    /// column widths derive from per-cell min/max-content intrinsic
    /// widths via speculative cell-content layouts.
    ///
    /// <para><b>Fixed-layout pipeline (sub-cycle 4):</b></para>
    /// <list type="number">
    ///   <item><b>Pass A</b> — walk <see cref="BoxKind.TableColumn"/>
    ///     / <see cref="BoxKind.TableColumnGroup"/> direct children of
    ///     the table wrapper in document order. Each <c>&lt;col&gt;</c>
    ///     contributes its declared <c>width</c> to <c>span</c>
    ///     consecutive columns (default span = 1). A
    ///     <c>&lt;colgroup&gt;</c> with <c>width</c> + no
    ///     <c>&lt;col&gt;</c> children also contributes; explicit
    ///     <c>&lt;col&gt;</c> children take priority over their
    ///     parent group's width.</item>
    ///   <item><b>Pass B</b> — walk first-row cells. For each cell at
    ///     <c>(0, c)</c> with colspan <c>S</c>, when none of
    ///     <c>columnWidths[c..c+S]</c> were set by Pass A, read the
    ///     cell's <c>width</c> property + HTML <c>width</c> attribute;
    ///     a declared width is distributed equally across the
    ///     <c>S</c> columns.</item>
    ///   <item><b>Pass C</b> — count columns still at 0; equal-
    ///     distribute <c>max(0, contentInlineSize − sum(declared))</c>
    ///     across them. Columns with no declaration AND no remainder
    ///     stay at 0.</item>
    ///   <item><b>Pass D (hardening Finding 1)</b> — reconcile the
    ///     column sum with the wrapper's content-inline-size: if
    ///     less, distribute the leftover equally; if greater, keep
    ///     declared widths intact + record an inline-overflow
    ///     diagnostic for the emit pass.</item>
    /// </list>
    ///
    /// <para><b>Auto-layout pipeline (sub-cycle 5):</b> delegates to
    /// <see cref="ComputeColumnWidthsAuto"/>, which runs the CSS Tables
    /// L3 §3 shrink-to-fit algorithm (per-cell min/max-content via
    /// speculative cell-content layouts, per-column aggregation,
    /// linear-interpolation distribution). Sub-cycle 5 hardening
    /// Finding 2 also folds declared <c>&lt;col&gt;</c> / first-row
    /// cell widths in as per-column min/max floors. The fragmentainer
    /// + layout context are threaded in so the speculative
    /// measurements can run nested BlockLayouter instances.</para>
    ///
    /// <para><b>Percentage widths are NOT supported.</b>
    /// <c>&lt;col width="20%"&gt;</c> is treated as 0 (the column falls
    /// through). Sub-cycle 6+ work — see
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.</para>
    ///
    /// <para>Instance method (was static pre-Pass-D — promoted so
    /// cancellation-token threading + inline-overflow flag recording
    /// can read/write the instance state). Reads
    /// <paramref name="placements"/> for Pass B's first-row colspan
    /// partial-declare semantics + Step 0 of the auto branch.</para>
    /// </summary>
    private double[] ComputeColumnWidths(
        Box wrapper, List<Box> rows, int columnCount, double contentInlineSize,
        IList<CellPlacement> placements,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var widths = new double[columnCount];
        var hasDeclared = new bool[columnCount];

        var mode = wrapper.Style.ReadTableLayout();

        if (mode == TableLayoutMode.Fixed)
        {
            // Pass A — walk <col> / <colgroup> direct children of the
            // wrapper. Per BoxBuilder Rec 5 captions are direct wrapper
            // children but columns + column-groups are children of the
            // TableGrid (see GridLevelFixup). Sub-cycle 4 walks BOTH the
            // wrapper AND the grid because authors may place <col> /
            // <colgroup> as siblings of <tr> in the source HTML, which
            // BoxBuilder slots under the synthesized TableGrid.
            var colCursor = 0;
            CollectColumnsFromGridLevel(
                wrapper, columnCount, widths, hasDeclared, ref colCursor,
                cancellationToken);

            // Pass B — walk first-row placements. Sub-cycle 4 hardening
            // (Finding 2): use the placement list (authoritative origin
            // column from PlaceCellsOntoGrid) instead of re-deriving a
            // local cursor that re-parsed spans. For each first-row
            // cell at OriginCol with ColSpan S, distribute the cell's
            // declared width across the UNDECLARED columns in
            // [OriginCol, OriginCol+S). When some columns in the span
            // are already declared by Pass A, only the REMAINING
            // (cellWidth - alreadyDeclared) is split across the
            // undeclared columns — spec-correct partial-declare
            // (the prior `width / colSpan` divided by the full span
            // even when some columns were pre-declared).
            ApplyFirstRowCellWidths(
                placements, widths, hasDeclared, columnCount,
                cancellationToken);

            // Pass C — equal-distribute the remaining inline-size
            // across columns not declared by Pass A or Pass B. Columns
            // with no declared width AND no remainder stay at 0 (the
            // sum-of-declared-widths exceeded contentInlineSize — the
            // table overflows in the inline-axis; Pass D below records
            // a diagnostic for the emit pass to surface).
            cancellationToken.ThrowIfCancellationRequested();
            var declaredTotal = 0.0;
            var undeclaredCount = 0;
            for (var c = 0; c < columnCount; c++)
            {
                if (hasDeclared[c]) declaredTotal += widths[c];
                else undeclaredCount++;
            }
            if (undeclaredCount > 0)
            {
                var remainder = contentInlineSize - declaredTotal;
                if (remainder < 0) remainder = 0;
                var perUndeclared = remainder / undeclaredCount;
                for (var c = 0; c < columnCount; c++)
                {
                    if (!hasDeclared[c])
                    {
                        widths[c] = perUndeclared;
                        // Mark as declared after Pass C — Pass D treats
                        // every column as "fixed" once Pass A+B+C have
                        // run; Pass D distributes leftover or records
                        // overflow without re-touching individual
                        // declared-flags.
                        hasDeclared[c] = true;
                    }
                }
            }

            // Pass D (Finding 1) — reconcile the column-sum with the
            // wrapper's content-inline-size. CSS 2.1 §17.5.2.1: if the
            // column sum is less than the table width, the extra space
            // is distributed equally across the columns. If the column
            // sum exceeds the wrapper, the table grid overflows in the
            // inline axis (we keep declared widths intact + record an
            // inline-overflow diagnostic for the emit pass). When the
            // sum matches contentInlineSize, no-op.
            ReconcileColumnWidthsFixed(
                widths, columnCount, contentInlineSize, cancellationToken);

            return widths;
        }

        // Per Phase 3 Task 12 sub-cycle 5 — table-layout: auto now runs
        // the CSS Tables L3 §3 shrink-to-fit algorithm. Per-cell min/
        // max-content widths are measured via speculative cell-content
        // layouts at extreme inline sizes (1px → min-content, 1e6 →
        // max-content); per-column min/max are aggregated (colspan
        // distribution mirrors the Pass B partial-declare pattern); the
        // table's effective width is clamped to
        // [sum(min), sum(max)]; columns are distributed via linear
        // interpolation.
        //
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 2 — the
        // auto branch now also consults <col> / <colgroup> +
        // first-row cell widths as inputs to per-column sizing
        // (declared widths floor both min AND max). The wrapper is
        // passed in so Step 0 can walk the column declarations.
        return ComputeColumnWidthsAuto(
            wrapper, columnCount, contentInlineSize, placements,
            fragmentainer, ref layout, cancellationToken);
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 — CSS Tables L3 §3
    /// auto-table-layout column-width algorithm. Computes per-cell
    /// min-content + max-content intrinsic widths via speculative
    /// cell-content layouts, aggregates to per-column min/max arrays
    /// (distributing across colspan>1 cells the same way Pass B
    /// distributes declared widths), then computes the table's
    /// effective width as
    /// <c>clamp(contentInlineSize, sum(colMinContent), sum(colMaxContent))</c>
    /// and distributes via:
    /// <list type="bullet">
    ///   <item>If <c>tableWidth &gt;= sum(colMaxContent)</c>: each
    ///     column gets <c>colMaxContent[c]</c>; extra space is
    ///     distributed equally across all columns.</item>
    ///   <item>If <c>tableWidth &lt;= sum(colMinContent)</c>: each
    ///     column gets <c>colMinContent[c]</c>; the table overflows
    ///     the wrapper inline-axis (records inline-overflow flags for
    ///     the emit pass to surface <c>LAYOUT-TABLE-INLINE-OVERFLOW-001</c>).</item>
    ///   <item>Otherwise: linear interpolation —
    ///     <c>widths[c] = colMin[c] + (tableWidth - sumMin) * (colMax[c] - colMin[c]) / (sumMax - sumMin)</c>.</item>
    /// </list>
    ///
    /// <para><b>Sub-cycle 5 simplification.</b> A single speculative
    /// pass per cell at <c>cellInlineSize = 1.0</c> (forces line-wrap
    /// at every UAX #14 break opportunity, yielding the widest line as
    /// min-content) + another at <c>cellInlineSize = 1e6</c> (no wrap
    /// pressure, yielding the natural inline extent as max-content).
    /// The speculative buffers are discarded — only their
    /// <see cref="MeasuringFragmentSink.MaxInlineExtentFromCellOrigin"/>
    /// trackers are read. Block-level / replaced cell content treats
    /// these the same way (the inner BlockLayouter's emitted
    /// fragments' inline-axis extents drive the tracker regardless of
    /// whether the content is text-only or block-only). The main
    /// measure pass then re-runs <see cref="MeasureCellContent"/> with
    /// the FINAL column widths to harvest the row-height-driving
    /// block extent + the buffered fragments for emit.</para>
    ///
    /// <para><b>Cancellation.</b> Honored per cell + per column.</para>
    /// </summary>
    private double[] ComputeColumnWidthsAuto(
        Box wrapper,
        int columnCount, double contentInlineSize,
        IList<CellPlacement> placements,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var widths = new double[columnCount];
        if (columnCount == 0) return widths;

        // ============================================================
        //  Step 0 — collect declared <col> / <colgroup> + first-row
        //  cell widths so auto-table-layout can FLOOR the per-column
        //  min/max with author declarations.
        //  (Phase 3 Task 12 sub-cycle 5 hardening Finding 2.)
        // ============================================================
        // Per CSS Tables L3 §3 auto-table-layout treats author column
        // / cell width declarations as INPUTS to per-column sizing —
        // pre-fix the auto branch ignored them entirely + only the
        // intrinsic content drove widths. The locked design (sub-cycle
        // 5 hardening Finding 2) uses the simpler approximation
        // "declared widths floor both colMin AND colMax" — a declared
        // width is the AUTHOR'S preferred minimum + when the content's
        // intrinsic widths exceed the declaration the algorithm
        // upgrades (max(declared, intrinsic)). The spec-strict
        // "declared width is preferred for max-content but not
        // necessarily a floor for min-content" lands in sub-cycle 6+
        // — see docs/deferrals.md#table-auto-fixed-spans-borders.
        var declaredWidths = new double[columnCount];
        var columnHasDeclared = new bool[columnCount];
        var colCursor = 0;
        CollectColumnsFromGridLevel(
            wrapper, columnCount, declaredWidths, columnHasDeclared,
            ref colCursor, cancellationToken);
        ApplyFirstRowCellWidths(
            placements, declaredWidths, columnHasDeclared, columnCount,
            cancellationToken);

        // ============================================================
        //  Step 1 — per-cell intrinsic widths via speculative layouts.
        // ============================================================
        // For each placement, run two nested cell layouts: one at a
        // very small inline-size (1.0 px) to force every UAX #14 break
        // opportunity to wrap (yielding min-content as the widest
        // resulting line/atom advance), and one at a very large inline-
        // size (1e6 px) to suppress any wrap (yielding max-content as
        // the natural inline extent). The buffered fragments + their
        // diagnostics are discarded — these are speculative
        // measurements that do not appear in the final layout. Cached
        // in arrays parallel to `placements` so the per-column
        // aggregation below can read them.
        //
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 — DoS-
        // resistant budget. Each cell consumes 2 ops (min-content +
        // max-content speculative passes). When the cumulative ops
        // exceed MaxIntrinsicMeasurementOps the remaining cells fall
        // back to (minContent=0, maxContent=contentInlineSize), and
        // the layouter emits LAYOUT-TABLE-INTRINSIC-MEASUREMENT-
        // BUDGET-EXCEEDED-001 from AttemptLayout's deferral pass.
        var cellMinContent = new double[placements.Count];
        var cellMaxContent = new double[placements.Count];
        var measuredCells = 0;
        var fallbackCells = 0;
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Each cell normally costs 2 ops (min + max passes).
            if (_intrinsicMeasurementOps + 2 > MaxIntrinsicMeasurementOps)
            {
                // Budget exceeded — fall back to a degenerate
                // (0, contentInlineSize) range so the column gets
                // up to the full available width as max + can shrink
                // to 0 as min. Combined with the interpolation
                // distribution path this produces a degenerate-equal-
                // split-like result without dispatching more
                // speculative passes.
                _intrinsicMeasurementBudgetExceeded = true;
                cellMinContent[pIdx] = 0;
                cellMaxContent[pIdx] = contentInlineSize;
                fallbackCells++;
                continue;
            }
            var cell = placements[pIdx].Cell;
            var (cellMin, cellMax) = MeasureCellIntrinsicWidths(
                cell, fragmentainer, ref layout, cancellationToken);
            cellMinContent[pIdx] = cellMin;
            cellMaxContent[pIdx] = cellMax;
            _intrinsicMeasurementOps += 2;
            measuredCells++;
        }
        _intrinsicMeasurementCellsMeasured = measuredCells;
        _intrinsicMeasurementCellsFellBack = fallbackCells;

        // ============================================================
        //  Step 2 — per-column min/max aggregation.
        // ============================================================
        // For colspan=1 placements: column's min/max = max(cell.min /
        // cell.max) over cells anchored at that column. For colspan>1
        // placements: distribute the cell's intrinsic width across the
        // spanned columns ONLY IF the cell's intrinsic width exceeds
        // the sum already attributed to those columns by colspan=1
        // cells. The excess is split equally across the spanned
        // columns (a deterministic, naive split — the CSS Tables L3 §3
        // distribution-proportional algorithm with proportional column-
        // weight allocation is deferred; this matches sub-cycle 2's
        // rowspan "extra-to-last" simplification, and the equal split
        // across spanned columns is symmetric to fixed-layout Pass B's
        // partial-declare distribution).
        var colMin = new double[columnCount];
        var colMax = new double[columnCount];

        // First pass: colspan=1 placements set per-column min/max
        // directly (= max of cells anchored at that column).
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = placements[pIdx];
            if (p.ColSpan != 1) continue;
            var c = p.OriginCol;
            if (cellMinContent[pIdx] > colMin[c]) colMin[c] = cellMinContent[pIdx];
            if (cellMaxContent[pIdx] > colMax[c]) colMax[c] = cellMaxContent[pIdx];
        }

        // Second pass: colspan>1 placements top up the columns they
        // span if the cell's intrinsic width exceeds the sum already
        // attributed to those columns. Equal-split across the spanned
        // columns of the excess.
        for (var pIdx = 0; pIdx < placements.Count; pIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = placements[pIdx];
            if (p.ColSpan <= 1) continue;
            var spanEnd = p.OriginCol + p.ColSpan;
            if (spanEnd > columnCount) spanEnd = columnCount;
            var span = spanEnd - p.OriginCol;
            if (span <= 0) continue;

            // Aggregate already-attributed min / max across the spanned
            // columns; distribute any excess equally.
            var existingMinSum = 0.0;
            var existingMaxSum = 0.0;
            for (var c = p.OriginCol; c < spanEnd; c++)
            {
                existingMinSum += colMin[c];
                existingMaxSum += colMax[c];
            }
            if (cellMinContent[pIdx] > existingMinSum)
            {
                var perColumn = (cellMinContent[pIdx] - existingMinSum) / span;
                for (var c = p.OriginCol; c < spanEnd; c++) colMin[c] += perColumn;
            }
            if (cellMaxContent[pIdx] > existingMaxSum)
            {
                var perColumn = (cellMaxContent[pIdx] - existingMaxSum) / span;
                for (var c = p.OriginCol; c < spanEnd; c++) colMax[c] += perColumn;
            }
        }

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 2 —
        // floor per-column min/max with declared widths from
        // <col> / <colgroup> + first-row cells. The locked design
        // ("declared widths floor both min and max") yields:
        //   colMin[c] = max(intrinsicMin[c], declaredWidth[c])
        //   colMax[c] = max(intrinsicMax[c], declaredWidth[c])
        // when the column carries a declaration. Pre-fix the auto
        // branch ignored declarations entirely, producing widths
        // narrower than the author intended. The simpler approximation
        // matches author expectation (declared width is a preferred
        // minimum) without the spec-strict "preferred = declared only
        // for max-content" rule (sub-cycle 6+ — see
        // docs/deferrals.md#table-auto-fixed-spans-borders).
        for (var c = 0; c < columnCount; c++)
        {
            if (columnHasDeclared[c] && declaredWidths[c] > 0)
            {
                if (declaredWidths[c] > colMin[c]) colMin[c] = declaredWidths[c];
                if (declaredWidths[c] > colMax[c]) colMax[c] = declaredWidths[c];
            }
        }

        // Ensure max >= min per column (defensive — speculative
        // measurements at 1px CAN exceed those at 1e6 if a replaced
        // element forced an intrinsic-width floor in the small case;
        // float rounding can also flip the order by a hair).
        for (var c = 0; c < columnCount; c++)
        {
            if (colMax[c] < colMin[c]) colMax[c] = colMin[c];
        }

        // ============================================================
        //  Step 3 — table effective width via clamp.
        // ============================================================
        cancellationToken.ThrowIfCancellationRequested();
        var sumMin = 0.0;
        var sumMax = 0.0;
        for (var c = 0; c < columnCount; c++)
        {
            sumMin += colMin[c];
            sumMax += colMax[c];
        }

        // tableWidth = clamp(contentInlineSize, sumMin, sumMax).
        var tableWidth = contentInlineSize;
        if (tableWidth < sumMin) tableWidth = sumMin;
        else if (tableWidth > sumMax) tableWidth = sumMax;

        // ============================================================
        //  Step 4 — distribute width to columns.
        // ============================================================
        const double Tolerance = 1e-9;

        if (sumMin > contentInlineSize + Tolerance)
        {
            // Overflow path: the table's min-content sum exceeds the
            // wrapper's content-inline-size. Every column gets its
            // min-content; the table grid overflows the wrapper in the
            // inline axis. Record overflow flags so the emit pass
            // surfaces LAYOUT-TABLE-INLINE-OVERFLOW-001 (mirrors the
            // fixed-layout Pass D contract).
            for (var c = 0; c < columnCount; c++) widths[c] = colMin[c];
            _measuredInlineOverflowed = true;
            _measuredInlineOverflowColumnSum = sumMin;
            _measuredInlineOverflowContentSize = contentInlineSize;
            return widths;
        }

        if (contentInlineSize + Tolerance >= sumMax)
        {
            // Saturated path: contentInlineSize >= sumMax — every
            // column reaches its max-content; the extra space is
            // distributed equally across all columns (per the §3 spec
            // step "if the table-width is greater than the
            // max-content-table-width, the difference [...] is
            // distributed equally between all columns").
            for (var c = 0; c < columnCount; c++) widths[c] = colMax[c];
            var extra = contentInlineSize - sumMax;
            if (extra > Tolerance)
            {
                var perColumn = extra / columnCount;
                for (var c = 0; c < columnCount; c++) widths[c] += perColumn;
            }
            return widths;
        }

        // Interpolation path: sumMin < contentInlineSize < sumMax.
        // Linear interpolation per column:
        //   widths[c] = colMin[c]
        //             + (tableWidth - sumMin)
        //               * (colMax[c] - colMin[c]) / (sumMax - sumMin)
        // The §3 spec calls this "all columns get min-content; then the
        // available extra is distributed proportional to (max - min) of
        // each column" — the closed-form interpolation is equivalent.
        var range = sumMax - sumMin;
        // Guard against pathological all-columns-have-identical-min-max
        // (range ≈ 0 implies sumMin ≈ sumMax ≈ contentInlineSize given
        // the clamp; equal-split the excess to be safe).
        if (range <= Tolerance)
        {
            for (var c = 0; c < columnCount; c++) widths[c] = colMin[c];
            var extra = contentInlineSize - sumMin;
            if (extra > Tolerance && columnCount > 0)
            {
                var perColumn = extra / columnCount;
                for (var c = 0; c < columnCount; c++) widths[c] += perColumn;
            }
            return widths;
        }

        var available = tableWidth - sumMin;
        for (var c = 0; c < columnCount; c++)
        {
            widths[c] = colMin[c] + available * (colMax[c] - colMin[c]) / range;
        }
        return widths;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 — speculative cell-
    /// content layout pair used by <see cref="ComputeColumnWidthsAuto"/>
    /// to derive per-cell min-content + max-content intrinsic widths.
    /// Runs two nested cell layouts:
    /// <list type="number">
    ///   <item>At <c>cellInlineSize = 1.0</c> — the inner inline
    ///     content force-wraps at every UAX #14 break opportunity;
    ///     the widest single line-advance becomes the cell's
    ///     min-content (i.e., the narrowest cell width that doesn't
    ///     require glyph-level splitting).</item>
    ///   <item>At <c>cellInlineSize = 1e6</c> — no wrap pressure; the
    ///     full natural inline extent of the cell's content becomes
    ///     the max-content.</item>
    /// </list>
    /// Both buffers + their diagnostic sinks are DISCARDED — these are
    /// speculative measurements that don't appear in the final layout.
    /// Only <see cref="MeasuringFragmentSink.MaxInlineExtentFromCellOrigin"/>
    /// is harvested.
    ///
    /// <para><b>Performance.</b> Each call dispatches two nested
    /// BlockLayouter passes. For a table with N cells the total cost
    /// is O(2N × per-cell-content). Sub-cycle 5+ may cache or short-
    /// circuit when content is trivially measurable (single literal
    /// without breaks → min = max = naturalWidth without
    /// dispatching).</para>
    ///
    /// <para><b>Edge cases.</b> Cells with no inner content (empty
    /// AnonymousBlock / no children) yield (0, 0). The clamp in
    /// <see cref="ComputeColumnWidthsAuto"/> ensures
    /// <c>colMax &gt;= colMin</c>.</para>
    /// </summary>
    /// <returns>Tuple <c>(MinContent, MaxContent)</c> in CSS px. Both
    /// values are non-negative; <c>MaxContent &gt;= MinContent</c> is
    /// enforced by the speculative pass order (the larger inline-size
    /// pass typically produces the wider extent, but the §3 algorithm
    /// is tolerant of unusual content where they're reversed; the
    /// caller's clamp handles it).</returns>
    private (double MinContent, double MaxContent) MeasureCellIntrinsicWidths(
        Box cell,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Min-content pass — cellInlineSize = 1.0 forces wrap at every
        // UAX #14 break opportunity. The widest resulting line / atom
        // advance is the min-content. We discard the buffer because
        // the main measure pass below re-runs MeasureCellContent with
        // the final column width.
        //
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
        // intrinsicSizingMode=true so the speculative wrap pass
        // downgrades break-word opportunities to Normal per CSS Text
        // L3 §5.1 (break-word's soft opportunities don't count for
        // min-content sizing). Anywhere opportunities continue to
        // fire.
        var (minBuffer, _, inlineEdgesMin) = MeasureCellContent(
            cellBox: cell,
            cellInlineOffset: 0,
            cellInlineSize: 1.0,
            fragmentainer: fragmentainer,
            layout: ref layout,
            cancellationToken: cancellationToken,
            intrinsicSizingMode: true);
        var minContent = minBuffer.MaxInlineExtentFromCellOrigin;

        // Max-content pass — cellInlineSize = 1e6 (effectively
        // unbounded). The inner inline content lays out without
        // wrapping; the rightmost inner inline-axis cursor is the
        // max-content.
        var (maxBuffer, _, _) = MeasureCellContent(
            cellBox: cell,
            cellInlineOffset: 0,
            cellInlineSize: 1e6,
            fragmentainer: fragmentainer,
            layout: ref layout,
            cancellationToken: cancellationToken,
            intrinsicSizingMode: false);
        var maxContent = maxBuffer.MaxInlineExtentFromCellOrigin;

        // Defensive: clamp max-content to 1e6 in case the inner layout
        // ever exceeds the speculative ceiling. The clamp in
        // ComputeColumnWidthsAuto independently enforces max >= min.
        if (maxContent > 1e6) maxContent = 1e6;
        if (minContent < 0) minContent = 0;
        if (maxContent < 0) maxContent = 0;

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 3 — the
        // cell's intrinsic widths INCLUDE its inline box-model edges
        // (border + padding both sides). Pre-fix the min/max content
        // reflected the INNER content only; the column min/max would
        // then under-allocate the cell's space relative to its
        // padding/border. CSS Tables L3 §3 says the auto-table-layout
        // min/max-content per column aggregates the OUTER cell extent
        // (the border-box inline-size).
        return (minContent + inlineEdgesMin, maxContent + inlineEdgesMin);
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 —
    /// Pass D of the fixed-table-layout column-width algorithm. After
    /// Pass A+B+C have populated every column, compare the column-sum
    /// to <paramref name="contentInlineSize"/>:
    /// <list type="bullet">
    ///   <item>If columnSum &lt; contentInlineSize: distribute the
    ///     leftover (contentInlineSize - columnSum) equally across ALL
    ///     columns. Per CSS 2.1 §17.5.2.1 "if the total width of the
    ///     columns is less than the width of the table, the extra
    ///     space should be distributed over the columns".</item>
    ///   <item>If columnSum &gt; contentInlineSize: keep declared
    ///     widths intact (the table grid overflows the wrapper in the
    ///     inline axis). Record <c>_measuredInlineOverflowed</c> +
    ///     <c>_measuredInlineOverflowColumnSum</c> +
    ///     <c>_measuredInlineOverflowContentSize</c> so the emit pass
    ///     surfaces <c>LAYOUT-TABLE-INLINE-OVERFLOW-001</c>.</item>
    ///   <item>If columnSum == contentInlineSize: no-op.</item>
    /// </list>
    /// </summary>
    private void ReconcileColumnWidthsFixed(
        double[] widths, int columnCount, double contentInlineSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var columnSum = 0.0;
        for (var c = 0; c < columnCount; c++) columnSum += widths[c];

        // Floating-point tolerance — exact equality is fragile when
        // Pass C's equal-distribute introduces rounding. Treat sums
        // within 1e-9 as "equal" to skip needless arithmetic.
        const double Tolerance = 1e-9;
        if (columnSum + Tolerance < contentInlineSize)
        {
            // columnSum < contentInlineSize → distribute leftover
            // equally across ALL columns. columnCount > 0 guaranteed
            // by the caller (ComputeColumnWidths skips fixed-layout
            // when columnCount == 0 via the same branch that returns
            // the empty widths array).
            var leftover = contentInlineSize - columnSum;
            var perColumn = leftover / columnCount;
            for (var c = 0; c < columnCount; c++) widths[c] += perColumn;
        }
        else if (columnSum > contentInlineSize + Tolerance)
        {
            // columnSum > contentInlineSize → keep declared widths
            // intact; record overflow for the AttemptLayout emit pass.
            // The row + cell + caption fragments will use the column
            // sum as their inline extent (see _measuredUsedInlineSize
            // in MeasureContentHeight) so author intent is preserved.
            _measuredInlineOverflowed = true;
            _measuredInlineOverflowColumnSum = columnSum;
            _measuredInlineOverflowContentSize = contentInlineSize;
        }
        // columnSum == contentInlineSize → no-op.
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — recursive walk that
    /// visits the wrapper's direct children + the TableGrid's direct
    /// children for <c>&lt;col&gt;</c> / <c>&lt;colgroup&gt;</c>
    /// declarations. Authors may place column declarations as either
    /// (1) direct children of the wrapper (less common; BoxBuilder
    /// keeps them where they were authored unless table fixup pulls
    /// them inside the grid — see <c>GridLevelFixup</c>'s pass-through
    /// branch for TableColumn / TableColumnGroup), or (2) children of
    /// the synthesized TableGrid (most common — they sit alongside row
    /// groups inside the grid box).
    /// <para>Walks in document order; advances a shared
    /// <paramref name="colCursor"/> so a <c>&lt;col span="2"&gt;</c>
    /// declaration claims columns [cursor, cursor+span) regardless of
    /// whether it lives at the wrapper level or inside the grid. Stops
    /// once the cursor reaches <paramref name="columnCount"/> (extra
    /// declarations are silently ignored per CSS Tables L3 §11.1).</para>
    /// </summary>
    private static void CollectColumnsFromGridLevel(
        Box wrapper, int columnCount, double[] widths, bool[] hasDeclared,
        ref int colCursor, CancellationToken cancellationToken)
    {
        // Wrapper-level children first (rare but valid per Display 3 if
        // an author authored <col> as a direct wrapper child + BoxBuilder
        // didn't relocate it).
        for (var i = 0; i < wrapper.Children.Count && colCursor < columnCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VisitColumnishChild(
                wrapper.Children[i], columnCount, widths, hasDeclared,
                ref colCursor, cancellationToken);
        }

        // Find the TableGrid + walk its children for <col> / <colgroup>.
        var grid = FindTableGrid(wrapper);
        if (grid is null) return;
        for (var i = 0; i < grid.Children.Count && colCursor < columnCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VisitColumnishChild(
                grid.Children[i], columnCount, widths, hasDeclared,
                ref colCursor, cancellationToken);
        }
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — visit one direct
    /// child of the wrapper / grid. If it's a <c>&lt;col&gt;</c>, claim
    /// <c>span</c> columns at the cursor. If it's a
    /// <c>&lt;colgroup&gt;</c> with <c>&lt;col&gt;</c> children, recurse
    /// into them (the colgroup acts as a structural parent — its own
    /// <c>width</c> is overridden by explicit children). If it's a
    /// <c>&lt;colgroup&gt;</c> with NO <c>&lt;col&gt;</c> children, the
    /// colgroup's own <c>width</c> + <c>span</c> applies to
    /// <c>span</c> consecutive columns.
    /// </summary>
    private static void VisitColumnishChild(
        Box child, int columnCount, double[] widths, bool[] hasDeclared,
        ref int colCursor, CancellationToken cancellationToken)
    {
        switch (child.Kind)
        {
            case BoxKind.TableColumn:
            {
                var span = ParseColumnSpan(child);
                var width = ReadColumnWidthPx(child);
                ApplyDeclaredWidthToColumns(
                    width, span, columnCount, widths, hasDeclared,
                    ref colCursor, cancellationToken);
                break;
            }
            case BoxKind.TableColumnGroup:
            {
                // Per HTML §4.9.3 a <colgroup> with <col> children
                // ignores its own width attribute + delegates to the
                // children. Otherwise its own span + width applies.
                var hasColChildren = false;
                for (var i = 0; i < child.Children.Count; i++)
                {
                    if (child.Children[i].Kind == BoxKind.TableColumn)
                    {
                        hasColChildren = true;
                        break;
                    }
                }
                if (hasColChildren)
                {
                    for (var i = 0; i < child.Children.Count && colCursor < columnCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var inner = child.Children[i];
                        if (inner.Kind != BoxKind.TableColumn) continue;
                        var span = ParseColumnSpan(inner);
                        var width = ReadColumnWidthPx(inner);
                        ApplyDeclaredWidthToColumns(
                            width, span, columnCount, widths, hasDeclared,
                            ref colCursor, cancellationToken);
                    }
                }
                else
                {
                    var span = ParseColumnSpan(child);
                    var width = ReadColumnWidthPx(child);
                    ApplyDeclaredWidthToColumns(
                        width, span, columnCount, widths, hasDeclared,
                        ref colCursor, cancellationToken);
                }
                break;
            }
            default:
                // Not a columnish child — ignore (row groups + rows are
                // walked elsewhere).
                break;
        }
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — claim
    /// <paramref name="span"/> columns at <paramref name="colCursor"/>
    /// for the declared <paramref name="width"/>. The cursor always
    /// advances by <paramref name="span"/> — the
    /// <c>&lt;col&gt;</c> claims the column slots in document order
    /// regardless of whether it had a usable width. When
    /// <paramref name="width"/> ≤ 0 the column slots stay undeclared
    /// (Pass B / Pass C can still claim them), so a
    /// <c>&lt;col width="20%"&gt;</c> (percentage clamped to 0 by
    /// sub-cycle 4) falls through to Pass C's equal-distribute.
    /// Pre-clamp span to remaining columns so the loop stays in-
    /// bounds.</summary>
    private static void ApplyDeclaredWidthToColumns(
        double width, int span, int columnCount,
        double[] widths, bool[] hasDeclared, ref int colCursor,
        CancellationToken cancellationToken)
    {
        if (span < 1) span = 1;
        var end = colCursor + span;
        if (end > columnCount) end = columnCount;
        if (width > 0)
        {
            for (var c = colCursor; c < end; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                widths[c] = width;
                hasDeclared[c] = true;
            }
        }
        // The cursor always advances — the <col> claims the slot
        // positionally even when its width contribution was 0.
        colCursor = end;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — read a column-related
    /// box's declared width in CSS px. Tries (in order):
    /// <list type="number">
    ///   <item>CSS <c>width</c> property via
    ///     <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>.
    ///     Returns 0 for <c>auto</c>, percentages, and unset slots.</item>
    ///   <item>HTML <c>width</c> attribute on the source element
    ///     (<see cref="Box.SourceElement"/>). Per HTML §4.9.3 the
    ///     attribute is parsed as a non-negative integer in CSS px;
    ///     non-integer / negative values are ignored.</item>
    /// </list>
    /// Returns 0 when both are absent / invalid. Percentage column
    /// widths (e.g. <c>20%</c>) are NOT supported — sub-cycle 5+ work,
    /// see <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>.
    /// </summary>
    private static double ReadColumnWidthPx(Box box)
    {
        // CSS width (resolved-px). Percentages + auto map to 0 per
        // ReadLengthPxOrZero's contract — that's the documented sub-
        // cycle-4 simplification.
        var cssWidth = box.Style.ReadLengthPxOrZero(PropertyId.Width);
        if (cssWidth > 0) return cssWidth;

        // Sub-cycle 4 hardening (Finding 3) — KNOWN LIMITATION:
        // ideally per CSS 2.1 §17.5 the HTML `width` attribute is a
        // low-specificity PRESENTATIONAL hint; explicit author CSS
        // (including `width: auto` / percent) should win. Current
        // limitation: NetPdf's cascade pipeline (BoxBuilder.ApplyDefaults
        // → PropertyResolverDispatch.Resolve) eagerly fills every
        // ComputedStyle slot with the property's INITIAL value (so
        // `width: auto` is set even when no author rule fired); this
        // collapses the distinction between "author wrote width:auto"
        // and "no author rule, defaulted to auto" — both report
        // `IsSet(PropertyId.Width)=true` with a Keyword(auto) slot.
        //
        // Until the cascade exposes a way to query "explicit-author-
        // rule applied" (sub-cycle 5+ — likely a separate bitmap layer
        // or a side declaration table consulted PRE-defaults), Pass A
        // falls back to the HTML attribute whenever CSS resolved to 0.
        // This means an authored `<col width="100" style="width: auto">`
        // will still pick up the HTML attribute's 100 — incorrect per
        // spec but a deliberate trade-off until the cascade exposes
        // explicit-author detection. The HTML width attribute path is
        // documented in docs/deferrals.md#table-auto-fixed-spans-borders
        // as the open work item.
        var el = box.SourceElement;
        if (el is null) return 0;
        var raw = el.GetAttribute("width");
        if (string.IsNullOrEmpty(raw)) return 0;
        // TODO sub-cycle 5+ — handle "%" suffix (currently dropped to
        // 0, falling back to Pass B / Pass C; see
        // docs/deferrals.md#table-auto-fixed-spans-borders).
        if (raw.EndsWith('%')) return 0;
        // HTML §2.4.4.2 — parse a non-negative integer. int.TryParse
        // with NumberStyles.Integer ACCEPTS leading "+" / "-" — so we
        // explicitly reject negative parsed values below (Copilot #4
        // — the pre-fix comment claimed leading sign was rejected,
        // which was false: a "-100" parsed to -100 + the cell got a
        // negative width). A leading "+100" still parses to +100 (a
        // valid non-negative integer).
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }
        if (n < 0) return 0;
        return n;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — parse the
    /// <c>span</c> HTML attribute on a <c>&lt;col&gt;</c> /
    /// <c>&lt;colgroup&gt;</c>. Per HTML §4.9.3 the attribute defaults
    /// to 1; valid range <c>[1, 1000]</c>; non-numeric / 0 / negative
    /// values fall back to 1.
    /// <para>Distinct from <see cref="ParseSpanAttribute"/> (which
    /// handles cell <c>rowspan</c> / <c>colspan</c>'s
    /// <c>0 = "remainder"</c> semantic). Column <c>span</c> doesn't
    /// admit the remainder semantic, so the helpers stay separate.
    /// </para></summary>
    private static int ParseColumnSpan(Box columnishBox)
    {
        var el = columnishBox.SourceElement;
        if (el is null) return 1;
        var raw = el.GetAttribute("span");
        if (string.IsNullOrEmpty(raw)) return 1;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return 1;
        }
        if (n < 1) return 1;
        if (n > 1000) return 1000;
        return n;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — Pass B of the fixed-
    /// table-layout algorithm: read declared widths off the first row's
    /// cells. Per CSS Tables L3 §3.5 a first-row cell's <c>width</c>
    /// fills its column ONLY when <c>&lt;col&gt;</c> didn't claim it.
    ///
    /// <para>Sub-cycle 4 hardening (Finding 2) — for cells with
    /// <c>colspan &gt; 1</c> that PARTIALLY overlap Pass-A declared
    /// columns, the cell's declared width minus the sum of
    /// already-declared columns is distributed across the REMAINING
    /// undeclared columns. Example: col 0 has <c>&lt;col width="100"&gt;</c>,
    /// first-row cell <c>colspan=2 width=400</c> → col 0 stays 100,
    /// col 1 gets <c>400 - 100 = 300</c> (NOT <c>400 / 2 = 200</c> like
    /// the pre-Finding-2 path).</para>
    ///
    /// <para>Sub-cycle 4 hardening (Finding 2 + 5) — operates on the
    /// authoritative placement list (the prior version re-walked the
    /// row + re-parsed spans). Cancellation checked per first-row
    /// placement so long tables respond to cancellation.</para>
    /// </summary>
    private void ApplyFirstRowCellWidths(
        IList<CellPlacement> placements, double[] widths, bool[] hasDeclared,
        int columnCount, CancellationToken cancellationToken)
    {
        for (var i = 0; i < placements.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placement = placements[i];
            if (placement.OriginRow != 0) continue;
            var colOrigin = placement.OriginCol;
            var colSpan = placement.ColSpan;
            // Defensive clamp (the placement algorithm already clamped
            // but we re-confirm in case future caller passes a stale
            // list).
            var spanEnd = colOrigin + colSpan;
            if (spanEnd > columnCount) spanEnd = columnCount;

            // Count undeclared columns in span + sum already-declared
            // widths. Two reads of `hasDeclared[]` per column avoided
            // by doing both in one loop.
            var undeclaredCount = 0;
            var alreadyDeclared = 0.0;
            for (var c = colOrigin; c < spanEnd; c++)
            {
                if (hasDeclared[c]) alreadyDeclared += widths[c];
                else undeclaredCount++;
            }

            // No undeclared columns to fill — skip.
            if (undeclaredCount == 0) continue;

            // Read the cell's declared width (CSS width OR HTML width
            // attribute per Finding 3 cascade-precedence semantics in
            // ReadColumnWidthPx).
            var cellWidth = ReadColumnWidthPx(placement.Cell);
            if (cellWidth <= 0) continue;

            // remainingWidth = cellWidth - alreadyDeclared. When the
            // already-declared columns soak up the cell's width
            // (cellWidth ≤ alreadyDeclared), leave the undeclared
            // columns as undeclared so Pass C can handle them — author
            // intent (the smaller cell width) wins versus expanding the
            // table to fit.
            var remainingWidth = cellWidth - alreadyDeclared;
            if (remainingWidth <= 0) continue;

            var perUndeclared = remainingWidth / undeclaredCount;
            for (var c = colOrigin; c < spanEnd; c++)
            {
                if (!hasDeclared[c])
                {
                    widths[c] = perUndeclared;
                    hasDeclared[c] = true;
                }
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
    /// cell's column-start edge in fragmentainer coordinates (= the
    /// cell's OUTER edge — the inner content-box edge is shifted by
    /// border-inline-start + padding-inline-start per Finding 3
    /// hardening).</param>
    /// <param name="cellInlineSize">Inline extent of the cell's
    /// column (colspan-aware — colspan=N gives N × columnWidth). This
    /// is the cell's BORDER-BOX inline-size; the inner fragmentainer's
    /// content-inline-size is this value minus the cell's inline
    /// edges (border + padding both sides).</param>
    /// <param name="fragmentainer">The outer fragmentainer; the nested
    /// layouter uses a scoped temporary to keep its own pagination
    /// accounting separate from the outer.</param>
    /// <param name="layout">The outer layout context (carries the
    /// diagnostic sink + counter state).</param>
    /// <param name="cancellationToken">Propagated to the inner
    /// layouter.</param>
    /// <param name="intrinsicSizingMode">Per Phase 3 Task 12 sub-cycle
    /// 5 hardening Finding 5 — when <see langword="true"/>, the inner
    /// <see cref="BlockLayouter"/> downgrades
    /// <c>overflow-wrap: break-word</c> opportunities to
    /// <c>OverflowWrap.Normal</c> for the speculative min-content pass
    /// (CSS Text L3 §5.1 — break-word's soft opportunities don't
    /// count for min-content sizing). <c>overflow-wrap: anywhere</c>
    /// opportunities continue to fire. Defaults to
    /// <see langword="false"/> for the final main-measure pass.</param>
    /// <returns>Tuple of the buffered fragments, the buffered cell-
    /// internal diagnostic sink, and the cell's box-model inline edges
    /// (= border-inline-start + padding-inline-start +
    /// padding-inline-end + border-inline-end). The inline-edges value
    /// is used by <see cref="MeasureCellIntrinsicWidths"/> to add the
    /// edge contribution to the cell's intrinsic widths per CSS Tables
    /// L3 §3 — Finding 3 hardening; the EMIT path doesn't need it
    /// directly (the inner translation already accounts for the inner
    /// content-box start edge).</returns>
    private (MeasuringFragmentSink Buffer, BufferingDiagnosticsSink? DiagnosticsBuffer, double InlineEdges) MeasureCellContent(
        Box cellBox,
        double cellInlineOffset,
        double cellInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        CancellationToken cancellationToken,
        bool intrinsicSizingMode = false)
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

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 3 —
        // read the cell's box-model inline edges (border + padding,
        // both sides) so the inner fragmentainer's content-inline-
        // size is the actual content-box width, and the buffered
        // fragments are translated by the inner content-box start
        // edge (not the outer cell edge). Padding/border on the
        // block axis isn't fully wired in sub-cycle 5 hardening —
        // the existing rowspan distribution model assumes the cell's
        // outer block edge equals the row's top; sub-cycle 6+ may
        // revisit. (The block-axis padding still contributes to the
        // cell's content extent because the inner BlockLayouter
        // measures content from offset 0 + the outer row-height
        // accumulation already covers the inner extent.)
        var paddingInlineStart = cellBox.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var paddingInlineEnd = cellBox.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
        var borderInlineStart = cellBox.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var borderInlineEnd = cellBox.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingBlockStart = cellBox.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var borderBlockStart = cellBox.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var inlineEdges = paddingInlineStart + paddingInlineEnd
            + borderInlineStart + borderInlineEnd;
        var innerInlineOffset = cellInlineOffset + paddingInlineStart + borderInlineStart;
        var innerBlockOffsetWithinCell = paddingBlockStart + borderBlockStart;

        // Sub-cycle 2 — pass 0 for the block translation. The block
        // origin can't be finalized until row-height distribution
        // completes (because rowspan cells may extend row heights
        // after the measure pass observes all content extents). The
        // emit pass passes the final cellBlockOffset to FlushTo,
        // which adds it to each buffered fragment's BlockOffset.
        //
        // Per Finding 3 hardening — the buffered fragments' inline
        // translation is the inner content-box start (cellOrigin +
        // borderInlineStart + paddingInlineStart). The block
        // translation gets the inner content-box-top contribution
        // (paddingBlockStart + borderBlockStart) baked in at Emit
        // time so FlushTo can add the row's final block origin
        // uniformly.
        var measuringSink = new MeasuringFragmentSink(
            outerSinkBaselineCursor: _sink.Cursor,
            inlineOffsetTranslation: innerInlineOffset,
            blockOffsetTranslation: innerBlockOffsetWithinCell);

        // Per CSS Tables L3 §11.5.3 — cell content lays out within
        // the cell's content area. Inner fragmentainer's content-
        // inline-size = max(1.0, cellInlineSize - inlineEdges).
        // Defensive clamp because FragmentainerContext rejects
        // zero/negative widths + auto-table-layout's interpolation
        // can produce a near-zero column width when every cell
        // anchored at that column is empty.
        var innerContentInlineSize = cellInlineSize - inlineEdges;
        if (innerContentInlineSize < 1.0) innerContentInlineSize = 1.0;
        var cellFragmentainer = new FragmentainerContext(
            contentInlineSize: innerContentInlineSize,
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

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
        // propagate the intrinsic-sizing-mode flag into the nested
        // BlockLayouter so it can downgrade BreakWord opportunities
        // for the speculative min-content pass.
        cellLayouter.SetIntrinsicSizingMode(intrinsicSizingMode);

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

        return (measuringSink, diagBuffer, inlineEdges);
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

        /// <summary>Per Phase 3 Task 12 sub-cycle 5 — maximum inline
        /// extent (InlineOffset + InlineSize) observed across forwarded
        /// fragments, expressed RELATIVE TO THE CELL'S inline origin
        /// (NOT the fragmentainer origin). Drives the auto-table-layout
        /// min/max-content per-cell intrinsic width measurement:
        /// callers run two speculative cell-content layouts at extreme
        /// inline sizes (1px → min-content, 1e6 → max-content) and read
        /// this property to harvest the natural inline extent each
        /// produced.
        ///
        /// <para>Tracked at <see cref="Emit"/> time alongside
        /// <see cref="MaxBlockExtentFromCellOrigin"/>; the inner
        /// BlockLayouter emits fragments in INNER coordinates (inner
        /// inline-offset starts at 0 = cell's content-box inline-start
        /// edge), so the inner inline rightmost edge equals
        /// <c>fragment.InlineOffset + fragment.InlineSize</c> directly
        /// — the cell's translation isn't baked in at this stage. The
        /// outer translation IS still applied to the buffered fragment
        /// for later flush; only the intrinsic-width tracker reads the
        /// inner-coordinate value.</para></summary>
        public double MaxInlineExtentFromCellOrigin { get; private set; }

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
            // Per Phase 3 Task 12 sub-cycle 5 — track inner inline right
            // edge so MeasureCellIntrinsicWidths can derive min/max-
            // content widths. The inner coordinate space starts at 0 =
            // cell's content-box inline-start edge; the cell's outer
            // translation isn't baked in here (it IS baked into the
            // buffered fragment for FlushTo's benefit, but the intrinsic
            // measurement reads the cell-relative value).
            //
            // Two contribution paths:
            //   1) When the fragment carries an InlineLayout (= it's an
            //      inline-only-block fragment with shaped lines), the
            //      widest LINE.TotalAdvance — picks up the ACTUAL text
            //      content extent regardless of the wrapper's border-
            //      box width (which is the AVAILABLE size, not the
            //      natural text width). Without this, text content
            //      gets the same min/max-content (both = wrapper
            //      available width), defeating the §3 algorithm.
            //   2) Otherwise (block-level fragments WITHOUT inline
            //      layout), the fragment's BORDER-BOX inline right
            //      edge — block-level content that lacks an inline
            //      layout reflects its container's resolved width
            //      (which IS the available size in the current cycle's
            //      recursive in-flow path; sub-cycle 6+ may revise once
            //      block-level shrink-to-fit per CSS 2.2 §10.3.5 lands).
            //
            // Sub-cycle 5 known limitation: when the inline-only-block
            // fragment exists AND lacks line wrapping data
            // (Lines.Length == 0 — e.g., all-whitespace collapsed
            // input), the contribution falls back to (1)'s zero, then
            // (2) picks up the border-box. This is rare in practice but
            // a potential source of over-estimation; documented + left
            // for sub-cycle 6+.
            if (fragment.InlineLayout is { } inlineLayout && inlineLayout.Lines.Length > 0)
            {
                var lines = inlineLayout.Lines;
                for (var i = 0; i < lines.Length; i++)
                {
                    var lineRight = innerInlineOffset + lines[i].TotalAdvance;
                    if (lineRight > MaxInlineExtentFromCellOrigin)
                    {
                        MaxInlineExtentFromCellOrigin = lineRight;
                    }
                }
            }
            else
            {
                var innerRight = innerInlineOffset + fragment.InlineSize;
                if (innerRight > MaxInlineExtentFromCellOrigin)
                {
                    MaxInlineExtentFromCellOrigin = innerRight;
                }
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
            // Same staleness applies to
            // MaxInlineExtentFromCellOrigin per sub-cycle 5.
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

    /// <summary>Per Phase 3 Task 12 sub-cycle 3 hardening (Finding 2) —
    /// <see cref="IBreakResolver"/> that ALWAYS returns
    /// <see cref="BreakAction.Continue"/> — even for chunks that don't
    /// fit on the current page. Captions are atomic block-level boxes
    /// (CSS Tables L3 §11.5); the nested BlockLayouter that walks the
    /// caption subtree must traverse the whole subtree in a single
    /// pass without ever returning <c>BreakHere</c>.
    ///
    /// <para>Pre-Finding-2, caption-content measurement used a vanilla
    /// <see cref="BreakResolver"/>. If a caption's content was taller
    /// than the fragmentainer, the resolver returned
    /// <see cref="BreakAction.BreakHere"/>; BlockLayouter then returned
    /// <see cref="LayoutAttemptOutcome.PageComplete"/> with a continuation
    /// and the rest of the caption's children were silently dropped.
    /// Post-fix: the no-break resolver guarantees Continue, the nested
    /// BlockLayouter walks the full subtree, and the OUTER overflow
    /// detector at commit time emits
    /// <see cref="PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
    /// when the caption + table exceeds the fragmentainer block-size.</para>
    ///
    /// <para><b>ResolveBreaks contract.</b> The batched path is unused
    /// for captions (BlockLayouter inline content uses the streaming
    /// path through <see cref="ConsiderBreakAt"/>); we still implement
    /// it for completeness — always returns
    /// <see cref="OptimizerResult.Empty"/>, meaning "no breaks
    /// needed".</para>
    ///
    /// <para><b>Checkpoint lifecycle.</b> The resolver still rents +
    /// holds checkpoint leases for callers that register them — same
    /// contract as <see cref="BreakResolver"/>'s lease handling.
    /// Captions don't currently register checkpoints, so the lease
    /// is never replaced in practice, but the no-break resolver
    /// honors the contract so future caption-internal work that does
    /// register checkpoints doesn't leak leases.</para></summary>
    /// <summary>Per Phase 3 Task 13 cycle 1 — exposed at internal scope
    /// so <see cref="BlockLayouter"/>'s nested-recursion path can use
    /// the same "always Continue" semantics for nested tables. The
    /// nested path can't propagate <see cref="TableContinuation"/>
    /// back up through the recursion (cycle 1 deferral); using the
    /// no-break resolver makes nested tables behave atomically + the
    /// existing forced-overflow diagnostic fires for over-tall ones.
    /// </summary>
    internal sealed class NoBreakBreakResolver : IBreakResolver
    {
        private CheckpointLease _lastLease;

        public BreakDecision ConsiderBreakAt(
            BreakOpportunity opportunity, FragmentainerContext ctx)
        {
            // Always Continue — the caption is an atomic block.
            // BreakOpportunity.ForceBreak still wins per the
            // BreakResolver convention; the caller (caption-internal
            // BlockLayouter) shouldn't be emitting forced breaks
            // anyway because captions don't model break-before/after,
            // but the contract violation surfaces here if it does.
            // Cost = 0 — no break committed.
            if (opportunity.ForceBreak)
            {
                // Defensive: a forced break inside a caption is a
                // structural anomaly. Emit BreakHere so the outer
                // diagnostics surface the issue rather than silently
                // ignoring the author's request.
                return new BreakDecision(BreakAction.BreakHere, 0, RewindTo: null);
            }
            return new BreakDecision(BreakAction.Continue, 0, RewindTo: null);
        }

        public OptimizerResult ResolveBreaks(
            IReadOnlyList<BreakOpportunity> opportunities,
            FragmentainerContext ctx,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(opportunities);
            cancellationToken.ThrowIfCancellationRequested();
            // No breaks — captions are atomic.
            return OptimizerResult.Empty;
        }

        public void RegisterCheckpoint(CheckpointLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease.Checkpoint);
            if (_lastLease.Checkpoint is not null
                && !ReferenceEquals(_lastLease.Checkpoint, lease.Checkpoint))
            {
                LayoutCheckpointPool.Return(_lastLease);
            }
            _lastLease = lease;
        }

        public LayoutCheckpoint? GetLastCheckpoint() => _lastLease.Checkpoint;

        public void Dispose()
        {
            if (_lastLease.Checkpoint is not null)
            {
                LayoutCheckpointPool.Return(_lastLease);
                _lastLease = default;
            }
        }
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
