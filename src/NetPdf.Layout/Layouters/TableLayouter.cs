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
/// Per Phase 3 Task 12 sub-cycle 1 + plan §"TableLayouter" — Hello-World
/// table layouter. Walks the inner content of a <see cref="BoxKind.Table"/>
/// (or <see cref="BoxKind.InlineTable"/>) wrapper: finds the
/// <see cref="BoxKind.TableGrid"/>, collects table rows (recursing into
/// row groups), splits the content-inline-size equally across the
/// columns implied by the maximum row width, stacks rows vertically,
/// dispatches each cell's content through a nested
/// <see cref="BlockLayouter"/> for recursive layout, and emits one
/// <see cref="BoxFragment"/> per row + per cell into the same
/// <see cref="IBlockFragmentSink"/> the outer block layouter uses.
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
/// <para><b>Sub-cycle 1 algorithm (equal-column "Hello World"):</b></para>
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
///   what's being dropped (sub-cycle 1 deferral — see
///   <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>).
///   Column groups and columns are skipped silently.</item>
///   <item>Compute the column count = max number of
///   <see cref="BoxKind.TableCell"/> children across all collected
///   rows. Sub-cycle 1 assumes no <c>colspan</c>: cell count == column
///   count for each row.</item>
///   <item>Split the available inline-size equally:
///   <c>columnWidth = contentInlineSize / columnCount</c>. No
///   author column widths, no shrink-to-fit, no min/max content
///   sizing (auto + fixed layout algorithms deferred).</item>
///   <item>Measure pass: for each row, lay out each cell's content
///   into a per-cell <see cref="MeasuringFragmentSink"/> that BUFFERS
///   the translated fragments. Track the per-cell maximum block
///   extent; the row height is the maximum across cells. Cache the
///   measurements + buffers on the layouter.</item>
///   <item>Emit pass (when <see cref="AttemptLayout"/> runs): emit the
///   row fragment, then for each cell emit the cell fragment, then
///   drain the cell's buffered content into the outer sink. Advance
///   the row cursor.</item>
/// </list>
///
/// <para><b>Sub-cycle 1 deferrals</b> (see
/// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>):</para>
/// <list type="bullet">
///   <item>CSS Tables L3 §3 auto-table-layout column-width algorithm
///   (shrink-to-fit / intrinsic min/max content).</item>
///   <item>CSS Tables L3 §3.5 fixed-table-layout algorithm (column
///   widths from <c>&lt;col&gt;</c> + first-row cell widths).</item>
///   <item>Border-collapse model + <c>border-spacing</c> per
///   §6.3.</item>
///   <item><c>colspan</c> / <c>rowspan</c> cell merging.</item>
///   <item><c>&lt;thead&gt;</c> / <c>&lt;tfoot&gt;</c> repetition
///   across pages.</item>
///   <item>Captions (<see cref="BoxKind.TableCaption"/>) — content is
///   skipped + a diagnostic emits with a snippet of the caption text
///   so authors see what is being dropped.</item>
///   <item><c>&lt;col&gt;</c> / <c>&lt;colgroup&gt;</c> column-specific
///   widths.</item>
///   <item>Multi-fragmentainer table splitting (rows that cross
///   pages). If the table doesn't fit on the current fragmentainer,
///   sub-cycle 1 emits all rows anyway + a forced-overflow
///   diagnostic.</item>
///   <item>Right-to-left tables / writing-mode flips.</item>
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

    /// <summary>Per-row measurement record produced by
    /// <see cref="MeasureContentHeight"/> and consumed by
    /// <see cref="AttemptLayout"/>. Stores the row's height + per-cell
    /// content buffers in document order (TableCell-only).</summary>
    private readonly struct RowMeasurement
    {
        public RowMeasurement(
            Box row,
            double rowHeight,
            List<MeasuringFragmentSink> cellBuffers)
        {
            Row = row;
            RowHeight = rowHeight;
            CellBuffers = cellBuffers;
        }
        public Box Row { get; }
        public double RowHeight { get; }
        public List<MeasuringFragmentSink> CellBuffers { get; }
    }

    private bool _measureDone;
    private double _measuredContentHeight;
    private int _measuredColumnCount;
    private double _measuredColumnWidth;
    private List<RowMeasurement>? _measuredRows;
    private bool _measuredSawColspan;
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
        var rowCursorBlock = _contentBlockOffset;
        var overflowDiagnosed = false;

        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowMeasure = rows[r];
            var row = rowMeasure.Row;
            var rowHeight = rowMeasure.RowHeight;

            // Emit row fragment FIRST (paint-safe order: backgrounds
            // before content). Sub-cycle 1 simplification — a zero-cell
            // row produces no measurement entry so we don't reach here
            // for an empty row.
            _sink.Emit(new BoxFragment(
                Box: row,
                InlineOffset: _contentInlineOffset,
                BlockOffset: rowCursorBlock,
                InlineSize: _contentInlineSize,
                BlockSize: rowHeight));

            // For each cell in document order: emit the cell fragment,
            // then drain its buffered content fragments via FlushTo.
            // This produces the paint-safe order row → cell → cell-
            // content (text under cell backgrounds/borders).
            var cellBuffers = rowMeasure.CellBuffers;
            var visibleCellIndex = 0;
            for (var i = 0; i < row.Children.Count; i++)
            {
                var ch = row.Children[i];
                if (ch.Kind != BoxKind.TableCell)
                {
                    continue;
                }
                var cellInlineOffset = _contentInlineOffset
                    + (visibleCellIndex * columnWidth);
                _sink.Emit(new BoxFragment(
                    Box: ch,
                    InlineOffset: cellInlineOffset,
                    BlockOffset: rowCursorBlock,
                    InlineSize: columnWidth,
                    BlockSize: rowHeight));

                // Drain the cell's buffered content fragments. The
                // measure pass already translated them into
                // fragmentainer coordinates relative to the cell's
                // origin captured at measure time — which we deliberately
                // anchored to (_contentInlineOffset + visibleCellIndex *
                // columnWidth, rowCursorBlock). Since the row cursor
                // matches the measure-time anchor by construction
                // (we walk rows in the same order MeasureContentHeight
                // did), the translated offsets are correct.
                var buffer = cellBuffers[visibleCellIndex];
                buffer.FlushTo(_sink);
                visibleCellIndex++;
            }

            rowCursorBlock += rowHeight;

            // Forced-overflow detection (sub-cycle 1: emit anyway).
            // Sub-cycle 2 will split rows across pages.
            if (!overflowDiagnosed && rowCursorBlock > fragmentainer.BlockSize)
            {
                overflowDiagnosed = true;
                OptimizingBreakResolver.SafeEmit(
                    layout.Diagnostics ?? _diagnostics,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.PaginationForcedOverflow001,
                        $"TableLayouter: table on fragmentainer page index "
                        + $"{fragmentainer.PageIndex} overflows page block-"
                        + $"size {fragmentainer.BlockSize:0.##} (rows extend to "
                        + $"{rowCursorBlock:0.##}). Sub-cycle 1 commits all "
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

        // Column count = max number of TableCell children across rows.
        // Sub-cycle 1 assumes no colspan, so this equals row.Children
        // count filtered by Kind == TableCell.
        var columnCount = 0;
        var sawColspanAttribute = false;
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cellCount = 0;
            var row = rows[r];
            for (var i = 0; i < row.Children.Count; i++)
            {
                var ch = row.Children[i];
                if (ch.Kind == BoxKind.TableCell)
                {
                    cellCount++;
                    if (!sawColspanAttribute && HasSpanAttribute(ch))
                    {
                        sawColspanAttribute = true;
                    }
                }
            }
            if (cellCount > columnCount)
            {
                columnCount = cellCount;
            }
        }

        _measuredColumnCount = columnCount;
        _measuredSawColspan = sawColspanAttribute;

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

        // Per-row measure: layout each cell into a buffering
        // MeasuringFragmentSink, derive row height = max cell extent.
        var measured = new List<RowMeasurement>(capacity: rows.Count);
        var totalContentHeight = 0.0;
        var rowAnchorBlock = _contentBlockOffset;
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[r];
            var cellCount = CountTableCells(row);
            if (cellCount == 0)
            {
                // Empty row — no measurement recorded (skipped during
                // emit). Sub-cycle 1 simplification; CSS would still
                // reserve some min-height per Tables L3 §11.5.4.
                continue;
            }

            var cellBuffers = new List<MeasuringFragmentSink>(capacity: cellCount);
            var rowHeight = 0.0;
            var visibleCellIndex = 0;
            for (var i = 0; i < row.Children.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ch = row.Children[i];
                if (ch.Kind != BoxKind.TableCell)
                {
                    continue;
                }
                var cellInlineOffset = _contentInlineOffset
                    + (visibleCellIndex * columnWidth);
                var cellBlockOffset = rowAnchorBlock;
                var buffer = MeasureCellContent(
                    cellBox: ch,
                    cellInlineOffset: cellInlineOffset,
                    cellBlockOffset: cellBlockOffset,
                    cellInlineSize: columnWidth,
                    fragmentainer: fragmentainer,
                    layout: ref layout,
                    cancellationToken: cancellationToken);
                cellBuffers.Add(buffer);
                if (buffer.MaxBlockExtentFromCellOrigin > rowHeight)
                {
                    rowHeight = buffer.MaxBlockExtentFromCellOrigin;
                }
                visibleCellIndex++;
            }

            measured.Add(new RowMeasurement(row, rowHeight, cellBuffers));
            totalContentHeight += rowHeight;
            rowAnchorBlock += rowHeight;
        }

        _measuredRows = measured;
        _measuredContentHeight = totalContentHeight;
        _measureDone = true;
        return totalContentHeight;
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

        if (_measuredSawColspan)
        {
            OptimizingBreakResolver.SafeEmit(
                sink,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                    "TableLayouter: a TableCell carries a colspan or "
                    + "rowspan attribute, which sub-cycle 1 ignores (each "
                    + "cell occupies exactly one column / one row). See "
                    + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }

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

    /// <summary>Sub-cycle 1 — detect whether <paramref name="cell"/>'s
    /// source element carries a <c>colspan</c> or <c>rowspan</c>
    /// attribute. The attribute is read directly from the DOM (the
    /// CSS counterparts <c>table-column-span</c> /
    /// <c>table-row-span</c> aren't part of the cascade yet — they're
    /// HTML attribute mapped). Used purely for the deferral
    /// diagnostic.</summary>
    private static bool HasSpanAttribute(Box cell)
    {
        var el = cell.SourceElement;
        if (el is null)
        {
            return false;
        }
        // AngleSharp surfaces HTML attributes via GetAttribute.
        var colspan = el.GetAttribute("colspan");
        if (!string.IsNullOrEmpty(colspan) && colspan != "1")
        {
            return true;
        }
        var rowspan = el.GetAttribute("rowspan");
        if (!string.IsNullOrEmpty(rowspan) && rowspan != "1")
        {
            return true;
        }
        return false;
    }

    /// <summary>Sub-cycle 1 — lay out <paramref name="cellBox"/>'s
    /// inner content via a nested <see cref="BlockLayouter"/>,
    /// BUFFERING the translated fragments in a
    /// <see cref="MeasuringFragmentSink"/> for later flush. Returns
    /// the buffer (carrying both the measured extent + the buffered
    /// fragments).
    ///
    /// <para>Per Finding 3 — the nested <see cref="BlockLayouter"/>
    /// runs against a FRESH <see cref="BreakResolver"/> scoped to the
    /// cell so the outer resolver's checkpoint state is preserved.
    /// </para></summary>
    /// <param name="cellBox">The <see cref="BoxKind.TableCell"/>
    /// box. The nested BlockLayouter treats this as a fresh root —
    /// its children lay out within the cell's allocated column.</param>
    /// <param name="cellInlineOffset">Inline-axis position of the
    /// cell's column-start edge in fragmentainer coordinates.</param>
    /// <param name="cellBlockOffset">Block-axis position of the
    /// cell's top edge in fragmentainer coordinates.</param>
    /// <param name="cellInlineSize">Inline extent of the cell's
    /// column.</param>
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
        double cellBlockOffset,
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

        var measuringSink = new MeasuringFragmentSink(
            outerSinkBaselineCursor: _sink.Cursor,
            inlineOffsetTranslation: cellInlineOffset,
            blockOffsetTranslation: cellBlockOffset);

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
        /// content).</summary>
        public void FlushTo(IBlockFragmentSink target)
        {
            ArgumentNullException.ThrowIfNull(target);
            for (var i = 0; i < _buffered.Count; i++)
            {
                target.Emit(_buffered[i]);
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
