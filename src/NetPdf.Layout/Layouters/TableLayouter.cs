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
/// <para><b>Invariant.</b> The outer wrapper's
/// <see cref="BoxFragment"/> is emitted by the parent
/// <see cref="BlockLayouter"/> using the standard block-flow path
/// (the placeholder-emit at the
/// <see cref="BoxKind.Table"/> / <see cref="BoxKind.InlineTable"/>
/// outer-display contract). This layouter is dispatched AFTER that
/// emit + adds the inner row/cell fragments at fragmentainer-relative
/// offsets so the painter sees the complete table geometry.</para>
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
///   <see cref="BoxKind.TableRow"/> children. Captions, column groups,
///   columns are SKIPPED (sub-cycle 1 deferral —
///   see <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>).</item>
///   <item>Compute the column count = max number of
///   <see cref="BoxKind.TableCell"/> children across all collected
///   rows. Sub-cycle 1 assumes no <c>colspan</c>: cell count == column
///   count for each row.</item>
///   <item>Split the available inline-size equally:
///   <c>columnWidth = contentInlineSize / columnCount</c>. No
///   author column widths, no shrink-to-fit, no min/max content
///   sizing (auto + fixed layout algorithms deferred).</item>
///   <item>For each row in document order: measure each cell's
///   content extent via a nested <see cref="BlockLayouter"/> with a
///   <see cref="MeasuringFragmentSink"/> wrapping the outer sink;
///   the row height is the maximum measured cell extent. Emit one
///   row <see cref="BoxFragment"/> spanning the full inline width +
///   computed row height, then one cell <see cref="BoxFragment"/> per
///   cell at the column offset + with the row height. The cell's
///   inner content fragments have already been written to the outer
///   sink by the nested measurement pass.</item>
///   <item>Advance <see cref="FragmentainerContext.UsedBlockSize"/> by
///   the row height as the row is committed.</item>
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
///   <item>Captions (<see cref="BoxKind.TableCaption"/>) — skipped
///   silently in sub-cycle 1.</item>
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
/// <para><b>Cancellation.</b> The token is propagated to the nested
/// <see cref="BlockLayouter"/> for cell content + checked between
/// rows + cells. A long-running deeply-nested cell layout responds
/// to cancellation through the inner BlockLayouter's own checks.</para>
/// </summary>
internal sealed class TableLayouter : ILayouter, IDisposable
{
    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    private readonly LayoutContinuation? _incomingContinuation;
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
        _incomingContinuation = incomingContinuation;
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

        // Locate the TableGrid child. Per CSS Tables L3 §2.1 the
        // wrapper always carries exactly one TableGrid + zero-or-more
        // TableCaption children; BoxBuilder's table fixup synthesizes
        // the grid even when the source HTML has no explicit one.
        var grid = FindTableGrid(_rootBox);
        if (grid is null)
        {
            // Defensive — should not happen in well-formed trees. The
            // outer fragment was already emitted; we just have no inner
            // content to add.
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    "TableLayouter: Table wrapper has no TableGrid child. "
                    + "Box-generation invariant violated — the inner grid "
                    + "should always be synthesized by BoxBuilder's table "
                    + "fixup. Emitting the outer wrapper only; no rows "
                    + "produced.",
                    PaginateDiagnosticSeverity.Warning));
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Collect rows in document order (recursing into row groups).
        var rows = new List<Box>();
        CollectRows(grid, rows, cancellationToken);

        if (rows.Count == 0)
        {
            // Empty table — no rows. AllDone, nothing emitted beyond
            // the wrapper.
            return LayoutAttemptResult.AllDone(cost: 0);
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
                    // Defer colspan/rowspan — emit a single diagnostic
                    // when any cell carries the attribute. See
                    // docs/deferrals.md#table-auto-fixed-spans-borders.
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

        if (sawColspanAttribute)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutTableFeatureUnsupported001,
                    "TableLayouter: a TableCell carries a colspan or "
                    + "rowspan attribute, which sub-cycle 1 ignores (each "
                    + "cell occupies exactly one column / one row). See "
                    + "docs/deferrals.md#table-auto-fixed-spans-borders.",
                    PaginateDiagnosticSeverity.Warning));
        }

        if (columnCount == 0)
        {
            // Rows exist but contain no cells. Nothing useful to emit.
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Equal-split column width. Sub-cycle 1 — no <col> widths, no
        // auto algorithm, no fixed algorithm.
        var columnWidth = _contentInlineSize / columnCount;
        if (columnWidth <= 0)
        {
            // Defensive — wrapper has zero content area. Nothing to
            // emit (rows would be 0-width).
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Walk rows + emit row + cell fragments.
        var rowCursorBlock = _contentBlockOffset;
        var emittedRowExtent = 0.0;
        var overflowDiagnosed = false;
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[r];

            // Compute each cell's content extent via a nested
            // BlockLayouter measure pass; track the row height as the
            // max across cells. Reserve a per-row buffer + walk twice
            // (measure → emit) to keep the per-cell offset arithmetic
            // simple. Sub-cycle 2 may inline the two passes when
            // pagination kicks in.
            var cellCount = CountTableCells(row);
            if (cellCount == 0)
            {
                // Empty row — no cells. Skip without advancing the
                // cursor (sub-cycle 1 simplification; CSS would still
                // reserve some min-height per Tables L3 §11.5.4).
                continue;
            }

            // Measure each cell's content extent into a per-cell array,
            // forwarding the cell's INNER fragments to the outer sink.
            // The measuring sink applies the cell's offsets to every
            // emitted fragment so the painter receives them in
            // fragmentainer coordinates.
            //
            // Per sub-cycle 1 algorithm (step 5): the measure pass
            // RUNS the cell's content layout into the outer sink. We
            // capture the max-block extent so the row height can wrap
            // around the tallest cell.
            var cellMeasurements = new double[cellCount];
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
                var cellBlockOffset = rowCursorBlock;
                cellMeasurements[visibleCellIndex] = LayoutCellContent(
                    cellBox: ch,
                    cellInlineOffset: cellInlineOffset,
                    cellBlockOffset: cellBlockOffset,
                    cellInlineSize: columnWidth,
                    fragmentainer: fragmentainer,
                    layout: ref layout,
                    resolver: resolver,
                    cancellationToken: cancellationToken);
                visibleCellIndex++;
            }

            // Row height = max measured cell extent. Sub-cycle 1 — a
            // zero-height row is legal (an empty cell row), so don't
            // clamp to a minimum.
            var rowHeight = 0.0;
            for (var c = 0; c < cellMeasurements.Length; c++)
            {
                if (cellMeasurements[c] > rowHeight)
                {
                    rowHeight = cellMeasurements[c];
                }
            }

            // Emit row fragment first (the row's geometry spans the
            // full content-inline-size + the computed row height).
            _sink.Emit(new BoxFragment(
                Box: row,
                InlineOffset: _contentInlineOffset,
                BlockOffset: rowCursorBlock,
                InlineSize: _contentInlineSize,
                BlockSize: rowHeight));

            // Emit each cell fragment with the computed row height
            // (cells stretch to the row's height per CSS Tables L3
            // §11.5.5, which sub-cycle 1 approximates as "row height
            // == max cell content extent" without separate cell
            // intrinsic-height resolution).
            visibleCellIndex = 0;
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
                visibleCellIndex++;
            }

            // Advance cursors.
            rowCursorBlock += rowHeight;
            emittedRowExtent += rowHeight;

            // Forced-overflow detection (sub-cycle 1: emit anyway).
            // The dispatch site (BlockLayouter) reserved page space
            // for the wrapper's own border-box height; if the
            // measured cells push the table BEYOND the page bottom,
            // the rest of the rows still emit (atomic table semantics
            // for sub-cycle 1) but a diagnostic fires so consumers
            // know the table overflowed. Multi-page splitting is
            // sub-cycle 2.
            //
            // The threshold is the fragmentainer's absolute block-axis
            // bottom (= BlockSize, since fragmentainer block-axis
            // starts at 0). `rowCursorBlock` is in the same
            // fragmentainer-relative space (we anchored it at
            // _contentBlockOffset which itself is fragmentainer-
            // relative).
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

        // Sub-cycle 1 — the outer BlockLayouter already advanced
        // UsedBlockSize by the wrapper's own border-box block size.
        // Adding the row extent on top would double-count the
        // wrapper's height-from-style (when set) or expand correctly
        // for an auto-height table. Sub-cycle 1 takes the SIMPLER
        // path: leave UsedBlockSize as the outer BlockLayouter set
        // it. This matches the BlockLayouter cycle-2c "subtree-aware
        // measure" semantic — the wrapper itself reports its own
        // border-box size + leaves the per-cell extent for the
        // dedicated layouter. Sub-cycle 2 will refine the wrapper's
        // auto-height resolution to track the row extent.

        return LayoutAttemptResult.AllDone(cost: 0);
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

    /// <summary>Sub-cycle 1 — walk <paramref name="grid"/>'s children
    /// in document order, appending every <see cref="BoxKind.TableRow"/>
    /// (including those nested inside <see cref="BoxKind.TableRowGroup"/>
    /// / <see cref="BoxKind.TableHeaderGroup"/> /
    /// <see cref="BoxKind.TableFooterGroup"/>) to
    /// <paramref name="rows"/>. Captions, column groups, columns are
    /// skipped — those are sub-cycle 2+ work.</summary>
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
    /// forwarding the emitted fragments to the outer sink with an
    /// offset translation so the cell's content lands inside the
    /// column. Returns the cell's measured content block-axis extent
    /// (= max <c>BlockOffset + BlockSize</c> across the cell's emitted
    /// fragments, relative to <paramref name="cellBlockOffset"/>).</summary>
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
    /// <param name="resolver">The outer break resolver — sub-cycle 1
    /// uses the same instance for the inner layout (cells don't
    /// trigger page breaks; the inner layouter consults but the
    /// resolver's verdicts don't change the outer table's
    /// pagination).</param>
    /// <param name="cancellationToken">Propagated to the inner
    /// layouter.</param>
    private double LayoutCellContent(
        Box cellBox,
        double cellInlineOffset,
        double cellBlockOffset,
        double cellInlineSize,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
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
            outerSink: _sink,
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

        // Sub-cycle 1 — best-effort layout into the cell's column.
        // The inner LastResort strategy keeps the inner layouter
        // from returning NeedsRewind (which sub-cycle 1 has no path
        // to handle inside a cell). Outer pagination is unchanged
        // since the cell content's overflow is the outer table's
        // problem, not the cell's.
        _ = cellLayouter.AttemptLayout(
            cellFragmentainer, ref innerLayout, resolver,
            LayoutAttemptStrategy.LastResort, cancellationToken);

        // The measuring sink tracked the max block extent (relative
        // to the cell's origin) across every forwarded fragment.
        return measuringSink.MaxBlockExtentFromCellOrigin;
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 — fragment sink wrapper
    /// that (a) translates inline + block offsets from the cell's
    /// origin space into fragmentainer coordinates, (b) tracks the
    /// maximum block extent observed (for row-height computation), and
    /// (c) forwards translated fragments to the outer sink.
    ///
    /// <para><b>Rollback contract.</b> The inner <see cref="BlockLayouter"/>
    /// may call <see cref="IFragmentSink.RollbackTo"/> if it rewinds.
    /// We translate the inner cursor into the outer sink's cursor by
    /// remembering the outer cursor at our creation + restoring to
    /// that baseline plus the inner offset on rollback. Sub-cycle 1
    /// doesn't drive rewinds inside cells (LastResort strategy
    /// suppresses them), but the wrapper still honors the contract
    /// for forwards compatibility.</para></summary>
    private sealed class MeasuringFragmentSink : IBlockFragmentSink
    {
        private readonly IBlockFragmentSink _outerSink;
        private readonly double _inlineTranslation;
        private readonly double _blockTranslation;
        private readonly int _outerCursorBaseline;
        private int _innerEmitCount;

        public MeasuringFragmentSink(
            IBlockFragmentSink outerSink,
            double inlineOffsetTranslation,
            double blockOffsetTranslation)
        {
            _outerSink = outerSink;
            _inlineTranslation = inlineOffsetTranslation;
            _blockTranslation = blockOffsetTranslation;
            _outerCursorBaseline = outerSink.Cursor;
        }

        /// <summary>Maximum block extent (BlockOffset + BlockSize)
        /// observed across forwarded fragments, expressed relative to
        /// the CELL'S origin (not the fragmentainer origin). Drives
        /// the row-height computation.</summary>
        public double MaxBlockExtentFromCellOrigin { get; private set; }

        public int Cursor => _innerEmitCount;

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
            _outerSink.Emit(translated);
            _innerEmitCount++;
        }

        public void RollbackTo(int cursor)
        {
            // Sub-cycle 1 — the inner LastResort strategy suppresses
            // NeedsRewind, so this path isn't reached in practice. The
            // outer sink's RollbackTo accepts a cursor in OUTER index
            // space; translate via the baseline captured at
            // construction.
            if (cursor < 0 || cursor > _innerEmitCount)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor),
                    $"MeasuringFragmentSink.RollbackTo: inner cursor "
                    + $"{cursor} out of range [0, {_innerEmitCount}].");
            }
            _outerSink.RollbackTo(_outerCursorBaseline + cursor);
            _innerEmitCount = cursor;
            // Recomputing MaxBlockExtentFromCellOrigin after a partial
            // rollback would require re-scanning forwarded fragments —
            // not worth it for the sub-cycle-1 unreachable path; we
            // leave the value stale, accepting that a rolled-back cell
            // measure is over-estimated. Documented for sub-cycle 2.
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
