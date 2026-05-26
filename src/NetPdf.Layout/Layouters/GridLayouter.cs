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
/// <para><b>Cycle 1 scope</b>:</para>
/// <list type="bullet">
///   <item>Explicit <c>&lt;length&gt;</c> tracks only via
///   <c>grid-template-rows</c> + <c>grid-template-columns</c>.
///   Track entries with any other kind (fr, auto, min-content,
///   max-content, minmax, fit-content, repeat) emit
///   <see cref="PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001"/>
///   + contribute 0 px to the track sum.</item>
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
        // grid-template-columns. Cycle 1 supports only Length tracks;
        // other kinds contribute 0 + emit a one-shot diagnostic.
        var rowSizes = ResolveTrackSizes(_rootBox.Style.ReadGridTemplateRows());
        var colSizes = ResolveTrackSizes(_rootBox.Style.ReadGridTemplateColumns());

        // No explicit tracks → no cells to place into. Cycle 1 doesn't
        // generate implicit tracks (§7.5); emit a diagnostic + return
        // AllDone with no per-item fragments. The wrapper fragment is
        // already on the sink from the dispatcher.
        if (rowSizes.Count == 0 || colSizes.Count == 0)
        {
            return LayoutAttemptResult.AllDone(cost: 0.0);
        }

        // Step 2 — compute track positions (cumulative sums starting
        // at the content-area origin).
        var rowPositions = ComputeTrackPositions(rowSizes, _contentBlockOffset);
        var colPositions = ComputeTrackPositions(colSizes, _contentInlineOffset);

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

        // Pass 3 — column-locked (auto row, definite column).
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.RowSpec.Kind != PlacementKind.Auto
                || item.ColSpec.Kind != PlacementKind.Definite)
            {
                continue;
            }
            if (item.Row >= 0) continue;
            var colZero = ResolveDefiniteToZeroBased(item.ColSpec.Line, colCount);
            if (colZero < 0 || colZero >= colCount)
            {
                EmitImplicitTrackDiagnostic(item.Box);
                continue;
            }
            var rowZero = FindFirstFreeRowInColumn(occupancy, colZero, rowCount);
            if (rowZero < 0)
            {
                EmitImplicitTrackDiagnostic(item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            occupancy[rowZero, colZero] = true;
            placedItems[i] = item;
        }

        // Pass 4 — both auto (sparse auto-placement per §8.5). Cursor
        // starts at (0, 0) and walks row-major; after placing an item
        // at (r, c), the cursor moves to (r, c+1) (wrapping to (r+1, 0)
        // when c+1 == colCount).
        var cursorRow = 0;
        var cursorCol = 0;
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.Row >= 0) continue; // already placed by earlier pass
            // Only process items where BOTH specs are Auto. Items that
            // had a Definite spec but failed placement (= out-of-range
            // line numbers per §7.5 implicit-track gap) were diagnosed
            // by their respective pass + must NOT fall back to sparse
            // auto-placement; that would silently place them in cells
            // the author never asked for.
            if (item.RowSpec.Kind != PlacementKind.Auto
                || item.ColSpec.Kind != PlacementKind.Auto)
            {
                continue;
            }
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
        }

        // Step 5 — emit each placed item as a BoxFragment at its cell's
        // top-left + sized to the cell's extents. Items the placement
        // algorithm couldn't place (= dropped to implicit-track
        // diagnostic) skip emission.
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
        }

        return LayoutAttemptResult.AllDone(cost: 0.0);
    }

    public void Dispose()
    {
        // No unmanaged resources; matches FlexLayouter's pattern of a
        // trivial Dispose for symmetry with the ILayouter consumers'
        // `using` declarations.
    }

    // =====================================================================
    //  Track sizing (cycle 1: Length tracks only)
    // =====================================================================

    /// <summary>Per cycle 1 — flatten the parsed <see cref="TrackList"/>
    /// AST into a list of px track sizes. Each
    /// <see cref="TrackListEntry"/> with a
    /// <see cref="GridTrackKind.Length"/> entry contributes its pixel
    /// value; non-length kinds contribute 0 + emit
    /// <see cref="PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001"/>
    /// (once per AttemptLayout). <see cref="TrackListNamedLine"/> +
    /// <see cref="TrackListRepeat"/> entries are SKIPPED in cycle 1
    /// (no track contributed); future cycles will handle repeat() +
    /// named-line position resolution.</summary>
    private List<double> ResolveTrackSizes(TrackList trackList)
    {
        var sizes = new List<double>(trackList.Items.Length);
        foreach (var item in trackList.Items)
        {
            if (item is TrackListEntry entry)
            {
                if (entry.Entry.Kind == GridTrackKind.Length
                    && !entry.Entry.IsPercentage)
                {
                    sizes.Add(entry.Entry.LengthPx);
                }
                else
                {
                    EmitTrackKindDiagnostic(entry.Entry.Kind);
                    // Contribute 0 — keeps the track count but with no
                    // pixels. Cycle 2+ will replace this branch with
                    // real fr / intrinsic / etc. sizing.
                    sizes.Add(0.0);
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
        return sizes;
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

    private void EmitTrackKindDiagnostic(GridTrackKind kind)
    {
        if (_emittedTrackKindDiagnostic) return;
        _emittedTrackKindDiagnostic = true;
        _diagnostics?.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001,
            Message: $"Grid track kind {kind} is not supported in cycle 1 "
                + "(Hello World); only <length> tracks contribute. The "
                + "track contributes 0 px to the track sum. Cycle 2+ will "
                + "ship fr / intrinsic / minmax / fit-content / repeat.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    private void EmitImplicitTrackDiagnostic(Box itemBox)
    {
        _diagnostics?.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001,
            Message: $"Grid item (kind={itemBox.Kind}) was placed outside "
                + "the explicit grid bounds; cycle 1 doesn't yet generate "
                + "implicit tracks per §7.5. Item dropped (no fragment "
                + "emitted). Cycle 6 will ship implicit-track generation.",
            Severity: PaginateDiagnosticSeverity.Warning));
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
        _diagnostics?.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
            Message: $"Grid item placement kind {kind} is not supported in "
                + "cycle 1; falling back to auto-placement. Cycle 6 will "
                + "ship span; cycle 7 will ship named lines + areas.",
            Severity: PaginateDiagnosticSeverity.Warning));
        return new PlacementSpec(PlacementKind.Auto, 0);
    }

    /// <summary>Resolve a CSS Grid line number (1-based, with negatives
    /// counting from the end per §8.3) to a 0-based row/column index.
    /// Returns -1 when the resolved index falls outside [0, trackCount).</summary>
    private static int ResolveDefiniteToZeroBased(int lineNumber, int trackCount)
    {
        if (lineNumber == 0)
        {
            // Per §8.3 line number 0 is invalid; cycle-0b parser rejects
            // it but defense in depth.
            return -1;
        }
        int zeroBased;
        if (lineNumber > 0)
        {
            // Line numbers are 1-based, but they identify the LINE not
            // the TRACK. Per CSS Grid §8 a line-N value means "the
            // item's edge is at line N"; the track between lines N and
            // N+1 is track N (1-based) = track index N-1 (0-based).
            // grid-row-start: 2 → track index 1.
            zeroBased = lineNumber - 1;
        }
        else
        {
            // Negative — count from the end. -1 means the LAST line
            // (= the line AFTER the last track), so -1 maps to track
            // index trackCount-1 (the last track). -2 → trackCount-2.
            zeroBased = trackCount + lineNumber;
        }
        return zeroBased;
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
