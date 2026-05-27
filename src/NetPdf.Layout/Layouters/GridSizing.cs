// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 17 cycle 3 + post-PR-#94 review hardening (F1 + F6)
/// — the shared track-sizing + placement service used by both
/// <see cref="GridLayouter"/> emission AND
/// <c>BlockLayouter.PreMeasureGridRowExtent</c> wrapper measurement.
///
/// <para><b>Pre-extraction history</b>: cycle 1 shipped GridLayouter
/// with sizing inside <see cref="GridLayouter.AttemptLayout"/>; cycle 2
/// added fr distribution; cycle 3 added intrinsic resolution. PR-#94
/// review F1 + F6 flagged that BlockLayouter's pre-measure was still
/// length-only — auto-height grids with intrinsic rows would have
/// wrappers that didn't reserve the right block extent, so following
/// siblings overlapped grid content. The reviewer recommended
/// extracting a shared sizing service so wrapper measurement +
/// emission agree.</para>
///
/// <para><b>Cycle 3 pipeline</b>:</para>
/// <list type="number">
///   <item><b>Classify tracks</b>: Length tracks → declared px; Fr +
///   intrinsic → 0 placeholder; unsupported (minmax / fit-content) →
///   0 + diagnostic.</item>
///   <item><b>Place items</b> via the CSS Grid §8.5 4-pass algorithm
///   (both-explicit / row-locked / col-locked + both-auto merged
///   sparse pass).</item>
///   <item><b>Resolve intrinsic tracks</b> from placed items (L19
///   approximation: max declared chrome-inclusive dimension across
///   items at that track).</item>
///   <item><b>Re-resolve fr distribution</b> since intrinsic now
///   contributes non-zero bases.</item>
///   <item><b>Compute positions</b> + validate finite cumulative
///   geometry.</item>
/// </list>
///
/// <para><b>What this DOESN'T do</b>: emit fragments (= layouter's
/// job) + dispatch item inner content (= layouter's job). The shared
/// service stops at sizing + placement; consumers use the result to
/// either measure (= pre-measure path: sum row sizes) or emit
/// (= AttemptLayout path: emit per-item fragments + inner content).</para>
///
/// <para><b>Diagnostic callback</b>: the service takes an optional
/// <see cref="EmitDiagnostic"/> delegate so callers can route
/// diagnostics through their own sinks. <see cref="GridLayouter"/>
/// passes its <c>SafeEmit</c> wrapper; pre-measure may pass null
/// (= dry-run; diagnostics emit only at the real emission pass).</para>
/// </summary>
internal static class GridSizing
{
    /// <summary>Per-diagnostic callback that the caller wires to its
    /// preferred sink. Null = silently discard (= dry-run pre-measure
    /// pattern).</summary>
    public delegate void EmitDiagnostic(PaginateDiagnostic diagnostic);

    /// <summary>Full result of <see cref="Resolve"/>. Caller emits
    /// fragments using <see cref="PlacedItems"/> + <see cref="RowPositions"/>
    /// / <see cref="ColPositions"/> / <see cref="RowSizes"/> /
    /// <see cref="ColSizes"/>. Pre-measure callers read just the
    /// <c>RowSizes</c> / <c>ColSizes</c> sums.</summary>
    public sealed class Result
    {
        public required List<double> RowSizes { get; init; }
        public required List<double> ColSizes { get; init; }
        public required List<double> RowPositions { get; init; }
        public required List<double> ColPositions { get; init; }
        public required List<PlacedItem> PlacedItems { get; init; }
        /// <summary>True when cumulative positions + per-track right/
        /// bottom edges are all finite. Caller skips emission when
        /// false (= protect downstream paint from non-finite geometry).</summary>
        public required bool IsGeometryFinite { get; init; }
        /// <summary>True when the grid has at least one row AND one
        /// column track. False signals "empty grid template" (= cycle
        /// 1+ scope; cycle 6 ships implicit-track generation per
        /// §7.5).</summary>
        public bool HasExplicitTracks => RowSizes.Count > 0 && ColSizes.Count > 0;
        /// <summary>Per PR-#94 review F1 — natural block extent the
        /// grid wrapper should reserve (= sum of row sizes). Used by
        /// BlockLayouter pre-measure to grow auto-height wrappers.</summary>
        public double RowExtentSum
        {
            get
            {
                double sum = 0;
                foreach (var s in RowSizes) sum += s;
                return sum;
            }
        }
    }

    /// <summary>Resolve track sizes + place items for one grid
    /// container. Returns null when no items would be laid out
    /// (= no grid-item children) — caller treats as a degenerate
    /// no-op.</summary>
    /// <param name="gridBox">The grid container box. Per cycle 1
    /// the kind must be <see cref="BoxKind.GridContainer"/> or
    /// <see cref="BoxKind.InlineGridContainer"/>; caller verifies.</param>
    /// <param name="contentInlineOffset">Inline-axis offset (CSS px
    /// from the fragmentainer's content-area origin) of the
    /// container's content-box inline-start edge.</param>
    /// <param name="contentBlockOffset">Block-axis offset of the
    /// container's content-box block-start edge.</param>
    /// <param name="contentInlineSize">Container's content-box inline
    /// extent.</param>
    /// <param name="contentBlockSize">Container's content-box block
    /// extent.</param>
    /// <param name="emit">Optional diagnostic callback.</param>
    /// <param name="cancellationToken">Cancellation token for the
    /// item-walking loops.</param>
    public static Result Resolve(
        Box gridBox,
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize,
        EmitDiagnostic? emit,
        CancellationToken cancellationToken)
    {
        // Per PR-#93 review F3 — detect indefinite axes.
        var heightSlot = gridBox.Style.Get(PropertyId.Height);
        var widthSlot = gridBox.Style.Get(PropertyId.Width);
        var isBlockIndefinite =
            heightSlot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
        var isInlineIndefinite =
            widthSlot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;

        var ctx = new SizingContext(emit);

        var rowKinds = new List<GridTrackKind>();
        var colKinds = new List<GridTrackKind>();
        var rowSizes = ResolveTrackSizes(
            gridBox.Style.ReadGridTemplateRows(),
            contentBlockSize, isBlockIndefinite,
            rowKinds, ctx);
        var colSizes = ResolveTrackSizes(
            gridBox.Style.ReadGridTemplateColumns(),
            contentInlineSize, isInlineIndefinite,
            colKinds, ctx);

        // Empty track template → no cells. Per PR-#92 review F6, emit
        // implicit-track diagnostic per dropped grid-item child.
        if (rowSizes.Count == 0 || colSizes.Count == 0)
        {
            foreach (var child in gridBox.Children)
            {
                if (!IsGridItem(child)) continue;
                EmitImplicitTrackDiagnostic(ctx, child);
            }
            return new Result
            {
                RowSizes = rowSizes,
                ColSizes = colSizes,
                RowPositions = new List<double>(),
                ColPositions = new List<double>(),
                PlacedItems = new List<PlacedItem>(),
                IsGeometryFinite = true,
            };
        }

        var rowCount = rowSizes.Count;
        var colCount = colSizes.Count;
        var occupancy = new bool[rowCount, colCount];

        // Gather grid items + classify placement.
        var placedItems = new List<PlacedItem>(gridBox.Children.Count);
        foreach (var child in gridBox.Children)
        {
            if (!IsGridItem(child)) continue;
            CheckEndLineForSpanDiagnostic(child.Style, isRow: true, ctx);
            CheckEndLineForSpanDiagnostic(child.Style, isRow: false, ctx);
            placedItems.Add(new PlacedItem
            {
                Box = child,
                RowSpec = ReadPlacement(child.Style, isRow: true, ctx),
                ColSpec = ReadPlacement(child.Style, isRow: false, ctx),
                Row = -1,
                Col = -1,
            });
        }

        RunPlacement(placedItems, occupancy, rowCount, colCount, ctx, cancellationToken);

        // Intrinsic resolution from placed items.
        var rowIntrinsicChanged = ResolveIntrinsicTracks(
            rowSizes, rowKinds, placedItems, isRowAxis: true);
        var colIntrinsicChanged = ResolveIntrinsicTracks(
            colSizes, colKinds, placedItems, isRowAxis: false);
        if (rowIntrinsicChanged)
        {
            ReResolveFrAfterIntrinsic(
                rowSizes, gridBox.Style.ReadGridTemplateRows(),
                contentBlockSize, ctx);
        }
        if (colIntrinsicChanged)
        {
            ReResolveFrAfterIntrinsic(
                colSizes, gridBox.Style.ReadGridTemplateColumns(),
                contentInlineSize, ctx);
        }

        // Compute positions + finite check.
        var rowPositions = ComputeTrackPositions(rowSizes, contentBlockOffset);
        var colPositions = ComputeTrackPositions(colSizes, contentInlineOffset);
        var isFinite = IsTrackGeometryFinite(rowPositions, rowSizes)
            && IsTrackGeometryFinite(colPositions, colSizes);
        if (!isFinite)
        {
            ctx.Emit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001,
                Message: "Grid container's resolved track positions or "
                    + "per-track (position + size) overflowed to a "
                    + "non-finite value (NaN / ±Infinity). Individual "
                    + "tracks were finite at AST-construction time but "
                    + "their cumulative sums overflowed (= probably "
                    + "hostile CSS with very large track lengths). Item "
                    + "emission is skipped to prevent corrupting "
                    + "downstream paint / PDF geometry.",
                Severity: PaginateDiagnosticSeverity.Warning));
        }

        return new Result
        {
            RowSizes = rowSizes,
            ColSizes = colSizes,
            RowPositions = rowPositions,
            ColPositions = colPositions,
            PlacedItems = placedItems,
            IsGeometryFinite = isFinite,
        };
    }

    // =====================================================================
    //  Sizing context (= diagnostic-emission state for one Resolve call)
    // =====================================================================

    /// <summary>Per-Resolve state. Tracks the one-shot
    /// track-kind-unsupported + fr-under-indefinite diagnostics so
    /// they don't spam per-track.</summary>
    internal sealed class SizingContext
    {
        public EmitDiagnostic? Emitter { get; }
        public bool EmittedTrackKindDiagnostic { get; set; }
        public bool EmittedFrIndefiniteDiagnostic { get; set; }
        public SizingContext(EmitDiagnostic? emitter) { Emitter = emitter; }
        public void Emit(PaginateDiagnostic d) => Emitter?.Invoke(d);
    }

    // =====================================================================
    //  Track-size classification (Length + Fr + intrinsic placeholder)
    // =====================================================================

    private static List<double> ResolveTrackSizes(
        TrackList trackList, double containerExtent, bool isAxisIndefinite,
        List<GridTrackKind> kindsOut, SizingContext ctx)
    {
        var sizes = new List<double>(trackList.Items.Length);
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
                        kindsOut.Add(GridTrackKind.Length);
                        break;
                    case GridTrackKind.Fr:
                        sizes.Add(0);
                        isFr.Add(true);
                        frFactors.Add(entry.Entry.FrValue);
                        kindsOut.Add(GridTrackKind.Fr);
                        break;
                    case GridTrackKind.Auto:
                    case GridTrackKind.MinContent:
                    case GridTrackKind.MaxContent:
                        // Cycle 3 — intrinsic placeholder; filled from
                        // placed items in the post-placement pass. All
                        // three kinds resolve identically under the L19
                        // approximation.
                        sizes.Add(0);
                        isFr.Add(false);
                        frFactors.Add(0);
                        kindsOut.Add(entry.Entry.Kind);
                        break;
                    default:
                        EmitTrackKindDiagnostic(ctx, entry.Entry.Kind);
                        sizes.Add(0);
                        isFr.Add(false);
                        frFactors.Add(0);
                        kindsOut.Add(entry.Entry.Kind);
                        break;
                }
            }
            else if (item is TrackListRepeat)
            {
                EmitTrackKindDiagnostic(ctx, GridTrackKind.Length);
            }
        }

        // Per PR-#93 review F3 — fr under indefinite axis approximated.
        if (isAxisIndefinite)
        {
            for (var i = 0; i < isFr.Count; i++)
            {
                if (isFr[i])
                {
                    EmitFrUnderIndefiniteDiagnostic(ctx);
                    break;
                }
            }
        }

        // §11.7 fr distribution. For indefinite axes leftover is
        // typically 0 → fr collapses to 0.
        ResolveFrTracks(sizes, isFr, frFactors, containerExtent, ctx);
        return sizes;
    }

    // =====================================================================
    //  §11.7 fr distribution
    // =====================================================================

    private static void ResolveFrTracks(
        List<double> sizes, List<bool> isFr, List<double> frFactors,
        double containerExtent, SizingContext ctx)
    {
        double nonFlexBase = 0;
        var hasFr = false;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (isFr[i]) hasFr = true;
            else nonFlexBase += sizes[i];
        }
        if (!hasFr) return;

        var leftover = containerExtent - nonFlexBase;
        if (leftover <= 0 || !double.IsFinite(leftover)) return;

        // Per PR-#93 review F1 — spec-correct flexFactorSum: max(SUM,1)
        // applied to the TOTAL once, NOT per-track.
        double rawFlexFactorSum = 0;
        for (var i = 0; i < frFactors.Count; i++)
        {
            if (isFr[i]) rawFlexFactorSum += frFactors[i];
        }
        // Per PR-#93 review F4 — non-finite sum guard.
        if (!double.IsFinite(rawFlexFactorSum))
        {
            ctx.Emit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001,
                Message: "Grid container's flex-factor sum overflowed to "
                    + "a non-finite value (= individual fr factors finite, "
                    + "but their sum exceeded double.MaxValue). fr "
                    + "distribution is skipped to prevent silent collapse "
                    + "to 0-sized tracks.",
                Severity: PaginateDiagnosticSeverity.Warning));
            return;
        }
        var flexFactorSum = System.Math.Max(rawFlexFactorSum, 1.0);
        if (flexFactorSum <= 0) return;

        var hypoFr = leftover / flexFactorSum;
        if (!double.IsFinite(hypoFr)) return;

        for (var i = 0; i < sizes.Count; i++)
        {
            if (isFr[i]) sizes[i] = hypoFr * frFactors[i];
        }
    }

    // =====================================================================
    //  Cycle 3 — intrinsic resolution (L19 approximation) + fr re-resolve
    // =====================================================================

    private static bool ResolveIntrinsicTracks(
        List<double> sizes, List<GridTrackKind> kinds,
        List<PlacedItem> placedItems, bool isRowAxis)
    {
        var anyChanged = false;
        for (var i = 0; i < sizes.Count; i++)
        {
            var kind = kinds[i];
            if (kind is not (GridTrackKind.Auto
                or GridTrackKind.MinContent
                or GridTrackKind.MaxContent))
            {
                continue;
            }
            double maxContribution = 0;
            foreach (var item in placedItems)
            {
                if (item.Row < 0 || item.Col < 0) continue;
                var matchesTrack = isRowAxis ? (item.Row == i) : (item.Col == i);
                if (!matchesTrack) continue;
                var contribution = ItemOuterContribution(item.Box, isRowAxis);
                if (contribution > maxContribution) maxContribution = contribution;
            }
            if (maxContribution > 0 && maxContribution != sizes[i])
            {
                sizes[i] = maxContribution;
                anyChanged = true;
            }
        }
        return anyChanged;
    }

    /// <summary>Per PR-#94 review F3 — an item's outer contribution
    /// to its intrinsic track INCLUDES border + padding + margin
    /// (= chrome) on the matching axis, not just the raw declared
    /// width/height. Pre-F3 only raw <c>width</c>/<c>height</c> was
    /// counted, so an item with <c>width:100; padding:20; border:5;
    /// margin:10</c> was sized at 100 instead of the spec-correct
    /// 170. KNOWN GAPS:
    /// <list type="bullet">
    ///   <item><b>box-sizing: border-box</b> — assumes content-box
    ///   default; border-box items over-count chrome.</item>
    ///   <item><b>Percentage dimensions</b> (<c>width: 50%</c>) — return
    ///   0 from <c>ReadLengthPxOrZero</c>; resolved against the cell,
    ///   which is what we're sizing (= chicken-and-egg). Future cycle
    ///   may implement two-pass resolution or document specifically.</item>
    /// </list></summary>
    private static double ItemOuterContribution(Box itemBox, bool isRowAxis)
    {
        if (isRowAxis)
        {
            var declared = itemBox.Style.ReadLengthPxOrZero(PropertyId.Height);
            var borderTop = itemBox.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
            var paddingTop = itemBox.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
            var borderBottom = itemBox.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
            var paddingBottom = itemBox.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);
            var marginTop = itemBox.Style.ReadLengthPxOrZero(PropertyId.MarginTop);
            var marginBottom = itemBox.Style.ReadLengthPxOrZero(PropertyId.MarginBottom);
            // Per cycle-3 approximation: declared height + chrome on
            // both sides. Margins included per CSS Sizing's "outer
            // size" semantics for grid item contributions.
            return declared + borderTop + paddingTop + borderBottom + paddingBottom
                + marginTop + marginBottom;
        }
        else
        {
            var declared = itemBox.Style.ReadLengthPxOrZero(PropertyId.Width);
            var borderLeft = itemBox.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
            var paddingLeft = itemBox.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
            var borderRight = itemBox.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
            var paddingRight = itemBox.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
            var marginLeft = itemBox.Style.ReadLengthPxOrZero(PropertyId.MarginLeft);
            var marginRight = itemBox.Style.ReadLengthPxOrZero(PropertyId.MarginRight);
            return declared + borderLeft + paddingLeft + borderRight + paddingRight
                + marginLeft + marginRight;
        }
    }

    private static void ReResolveFrAfterIntrinsic(
        List<double> sizes, TrackList trackList, double containerExtent,
        SizingContext ctx)
    {
        var isFr = new List<bool>(sizes.Count);
        var frFactors = new List<double>(sizes.Count);
        var idx = 0;
        foreach (var item in trackList.Items)
        {
            if (item is TrackListEntry entry)
            {
                if (idx >= sizes.Count) break;
                if (entry.Entry.Kind == GridTrackKind.Fr)
                {
                    isFr.Add(true);
                    frFactors.Add(entry.Entry.FrValue);
                    sizes[idx] = 0;
                }
                else
                {
                    isFr.Add(false);
                    frFactors.Add(0);
                }
                idx++;
            }
        }
        ResolveFrTracks(sizes, isFr, frFactors, containerExtent, ctx);
    }

    // =====================================================================
    //  Placement (CSS Grid §8.5 4-pass algorithm)
    // =====================================================================

    private static void RunPlacement(
        List<PlacedItem> placedItems, bool[,] occupancy,
        int rowCount, int colCount, SizingContext ctx,
        CancellationToken cancellationToken)
    {
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
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            occupancy[rowZero, colZero] = true;
            placedItems[i] = item;
        }

        // Pass 2 — row-locked (definite row, auto col).
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.RowSpec.Kind != PlacementKind.Definite
                || item.ColSpec.Kind != PlacementKind.Auto)
            {
                continue;
            }
            if (item.Row >= 0) continue;
            var rowZero = ResolveDefiniteToZeroBased(item.RowSpec.Line, rowCount);
            if (rowZero < 0 || rowZero >= rowCount)
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }
            var colZero = FindFirstFreeColumnInRow(occupancy, rowZero, colCount);
            if (colZero < 0)
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            occupancy[rowZero, colZero] = true;
            placedItems[i] = item;
        }

        // Pass 3 + 4 — col-locked + both-auto share a cursor (§8.5).
        var cursorRow = 0;
        var cursorCol = 0;
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.Row >= 0) continue;

            if (item.RowSpec.Kind == PlacementKind.Definite
                && item.ColSpec.Kind == PlacementKind.Auto)
            {
                continue;
            }

            if (item.RowSpec.Kind == PlacementKind.Auto
                && item.ColSpec.Kind == PlacementKind.Definite)
            {
                var colZero = ResolveDefiniteToZeroBased(item.ColSpec.Line, colCount);
                if (colZero < 0 || colZero >= colCount)
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }
                var rowZero = FindFirstFreeRowInColumnFrom(
                    occupancy, colZero, rowCount, startRow: cursorRow);
                if (rowZero < 0)
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }
                item.Row = rowZero;
                item.Col = colZero;
                occupancy[rowZero, colZero] = true;
                placedItems[i] = item;
                continue;
            }

            if (item.RowSpec.Kind == PlacementKind.Auto
                && item.ColSpec.Kind == PlacementKind.Auto)
            {
                if (!TryFindNextSparseCell(
                        occupancy, cursorRow, cursorCol, rowCount, colCount,
                        out var foundRow, out var foundCol))
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }
                item.Row = foundRow;
                item.Col = foundCol;
                occupancy[foundRow, foundCol] = true;
                placedItems[i] = item;
                cursorRow = foundRow;
                cursorCol = foundCol + 1;
                if (cursorCol >= colCount)
                {
                    cursorRow++;
                    cursorCol = 0;
                }
                continue;
            }
        }
    }

    // =====================================================================
    //  Placement helpers
    // =====================================================================

    private static int ResolveDefiniteToZeroBased(int lineNumber, int trackCount)
    {
        if (lineNumber == 0) return -1;
        if (lineNumber > 0) return lineNumber - 1;
        return trackCount + 1 + lineNumber;
    }

    private static int FindFirstFreeColumnInRow(bool[,] occupancy, int row, int colCount)
    {
        for (var c = 0; c < colCount; c++)
        {
            if (!occupancy[row, c]) return c;
        }
        return -1;
    }

    private static int FindFirstFreeRowInColumnFrom(
        bool[,] occupancy, int col, int rowCount, int startRow)
    {
        for (var r = startRow; r < rowCount; r++)
        {
            if (!occupancy[r, col]) return r;
        }
        return -1;
    }

    private static bool TryFindNextSparseCell(
        bool[,] occupancy, int startRow, int startCol,
        int rowCount, int colCount,
        out int row, out int col)
    {
        if (startRow >= rowCount) { row = -1; col = -1; return false; }
        var r = startRow;
        var c = startCol;
        while (r < rowCount)
        {
            while (c < colCount)
            {
                if (!occupancy[r, c]) { row = r; col = c; return true; }
                c++;
            }
            c = 0;
            r++;
        }
        row = -1; col = -1;
        return false;
    }

    // =====================================================================
    //  Placement spec reading
    // =====================================================================

    private static PlacementSpec ReadPlacement(
        ComputedStyle style, bool isRow, SizingContext ctx)
    {
        var value = isRow ? style.ReadGridRowStart() : style.ReadGridColumnStart();
        return value.Kind switch
        {
            GridLineKind.Auto => new PlacementSpec(PlacementKind.Auto, 0),
            GridLineKind.LineNumber when value.NamedLine is null
                => new PlacementSpec(PlacementKind.Definite, value.LineNumber),
            _ => EmitPlacementApproximatedAndFallToAuto(ctx, value.Kind),
        };
    }

    private static PlacementSpec EmitPlacementApproximatedAndFallToAuto(
        SizingContext ctx, GridLineKind kind)
    {
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
            Message: $"Grid item placement kind {kind} is not supported in "
                + "cycle 1; falling back to auto-placement. Cycle 6 will "
                + "ship span; cycle 7 will ship named lines + areas.",
            Severity: PaginateDiagnosticSeverity.Warning));
        return new PlacementSpec(PlacementKind.Auto, 0);
    }

    private static void CheckEndLineForSpanDiagnostic(
        ComputedStyle style, bool isRow, SizingContext ctx)
    {
        var endValue = isRow ? style.ReadGridRowEnd() : style.ReadGridColumnEnd();
        if (endValue.Kind != GridLineKind.Auto)
        {
            ctx.Emit(new PaginateDiagnostic(
                Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
                Message: (isRow ? "grid-row-end" : "grid-column-end")
                    + $" = {endValue.Kind} (non-default) — cycle 1 doesn't"
                    + " support multi-cell spans, so the item is placed at"
                    + " its start line as a single cell (= the authored"
                    + " area is shrunk). Cycle 6 ships span semantics.",
                Severity: PaginateDiagnosticSeverity.Warning));
        }
    }

    // =====================================================================
    //  Geometry helpers + diagnostics
    // =====================================================================

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

    private static bool IsTrackGeometryFinite(
        List<double> positions, List<double> sizes)
    {
        for (var i = 0; i < positions.Count; i++)
        {
            if (!double.IsFinite(positions[i])) return false;
            if (!double.IsFinite(positions[i] + sizes[i])) return false;
        }
        return true;
    }

    private static void EmitTrackKindDiagnostic(
        SizingContext ctx, GridTrackKind kind)
    {
        if (ctx.EmittedTrackKindDiagnostic) return;
        ctx.EmittedTrackKindDiagnostic = true;
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001,
            Message: $"Grid track kind {kind} is not yet supported "
                + "(cycle 3 ships <length> + <flex>/fr via §11.7 + "
                + "intrinsic auto / min-content / max-content via the "
                + "L19 approximation; minmax / fit-content / repeat are "
                + "cycle 4-7 scope). The track contributes 0 px to the "
                + "track sum.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    private static void EmitImplicitTrackDiagnostic(SizingContext ctx, Box itemBox)
    {
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridImplicitTrackUnsupported001,
            Message: $"Grid item (kind={itemBox.Kind}) was placed outside "
                + "the explicit grid bounds; cycle 1 doesn't yet generate "
                + "implicit tracks per §7.5. Item dropped (no fragment "
                + "emitted). Cycle 6 will ship implicit-track generation.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    private static void EmitFrUnderIndefiniteDiagnostic(SizingContext ctx)
    {
        if (ctx.EmittedFrIndefiniteDiagnostic) return;
        ctx.EmittedFrIndefiniteDiagnostic = true;
        // Per PR-#94 review F5 — the message no longer says "cycle 3
        // will ship intrinsic resolution" since this IS cycle 3. The
        // actual limitation is true content measurement (= L19) +
        // wrapper-measurement coupling. Cycle 3's L19 approximation
        // would fill intrinsic tracks from item declared dimensions,
        // but for fr-under-indefinite the wrapper's extent itself is
        // unknown (= it's what we're trying to determine), so fr
        // tracks have no leftover to distribute. The proper fix is
        // wrapper-measurement-aware sizing (= future cycle) plus true
        // intrinsic content measurement for the case where items
        // have no explicit dimensions (= L19).
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001,
            Message: "Grid container has fr tracks on an axis with "
                + "indefinite extent (= auto height for rows / auto "
                + "width for columns). Per CSS Grid §11.7 fr under "
                + "indefinite space requires resolving the container's "
                + "extent from intrinsic content first; cycle 3 ships "
                + "the L19 approximation for intrinsic tracks but the "
                + "wrapper-measurement coupling for indefinite-fr is a "
                + "follow-on cycle. fr tracks collapse to 0 here. "
                + "Author intent: declare an explicit height/width on "
                + "the grid container OR wait for true intrinsic +"
                + " wrapper-measurement resolution.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    /// <summary>Per Phase 3 Task 17 cycle 1 — grid item predicate
    /// mirroring <c>GridLayouter.IsGridItem</c>.</summary>
    private static bool IsGridItem(Box box)
    {
        return box.Kind is BoxKind.BlockContainer
            or BoxKind.GridContainer or BoxKind.InlineGridContainer
            or BoxKind.FlexContainer or BoxKind.InlineFlexContainer
            or BoxKind.Table or BoxKind.InlineTable
            or BoxKind.BlockReplacedElement
            or BoxKind.ListItem;
    }
}

/// <summary>Per Phase 3 Task 17 cycle 1 — a single grid item's
/// placement state. Cycle 3 review hardening moved this from
/// <see cref="GridLayouter"/> to <see cref="GridSizing"/> so the
/// shared sizing service can return placement info to consumers.</summary>
internal struct PlacedItem
{
    public Box Box;
    public PlacementSpec RowSpec;
    public PlacementSpec ColSpec;
    /// <summary>0-based final row index after placement. -1 when
    /// unplaced.</summary>
    public int Row;
    /// <summary>0-based final column index after placement. -1 when
    /// unplaced.</summary>
    public int Col;
}

/// <summary>Per Phase 3 Task 17 cycle 1 — classified placement spec.</summary>
internal readonly record struct PlacementSpec(PlacementKind Kind, int Line);

internal enum PlacementKind : byte
{
    Auto = 0,
    Definite = 1,
}
