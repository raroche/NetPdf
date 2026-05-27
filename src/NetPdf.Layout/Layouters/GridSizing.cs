// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        // Per PR-#95 review Q1 — kindsOut/rowKinds/colKinds were dead
        // code (populated but never read; TrackSizingInfo.Kind already
        // carries the value). Removed to drop 2 List allocations + the
        // unused parameter from ResolveTrackSizes.
        var rowInfos = new List<TrackSizingInfo>();
        var colInfos = new List<TrackSizingInfo>();
        ResolveTrackSizes(
            gridBox.Style.ReadGridTemplateRows(),
            contentBlockSize, isBlockIndefinite,
            rowInfos, ctx, cancellationToken);
        ResolveTrackSizes(
            gridBox.Style.ReadGridTemplateColumns(),
            contentInlineSize, isInlineIndefinite,
            colInfos, ctx, cancellationToken);

        // Empty track template → no cells. Per PR-#92 review F6, emit
        // implicit-track diagnostic per dropped grid-item child.
        if (rowInfos.Count == 0 || colInfos.Count == 0)
        {
            foreach (var child in gridBox.Children)
            {
                if (!IsGridItem(child)) continue;
                EmitImplicitTrackDiagnostic(ctx, child);
            }
            return new Result
            {
                RowSizes = new List<double>(),
                ColSizes = new List<double>(),
                RowPositions = new List<double>(),
                ColPositions = new List<double>(),
                PlacedItems = new List<PlacedItem>(),
                IsGeometryFinite = true,
            };
        }

        var rowCount = rowInfos.Count;
        var colCount = colInfos.Count;
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
            rowInfos, placedItems, isRowAxis: true);
        var colIntrinsicChanged = ResolveIntrinsicTracks(
            colInfos, placedItems, isRowAxis: false);
        if (rowIntrinsicChanged)
        {
            ResolveFrTracks(rowInfos, contentBlockSize, ctx, cancellationToken);
        }
        if (colIntrinsicChanged)
        {
            ResolveFrTracks(colInfos, contentInlineSize, ctx, cancellationToken);
        }

        // Per Phase 3 Task 17 cycle 4 + post-PR-#95 review C1 — §11.6
        // "Maximize Tracks": equal-share distribution to non-fr tracks
        // with finite GrowthLimit > BaseSize, freezing each as it
        // reaches its growth limit. Per CSS Grid §11.5.1 + §11.6 (NOT
        // proportional-to-headroom; cycle-4 initial impl had it wrong
        // and matched flexbox semantics instead of grid).
        MaximizeTracks(rowInfos, contentBlockSize);
        MaximizeTracks(colInfos, contentInlineSize);

        // Per PR-#95 review P3 — single materialization pass (was 2x;
        // ResolveTrackSizes used to materialize prematurely then we
        // overwrote post-Maximize).
        var rowSizes = MaterializeSizes(rowInfos);
        var colSizes = MaterializeSizes(colInfos);

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

    /// <summary>Per-Resolve state. Tracks one-shot diagnostic guards so
    /// each KIND of warning fires at most once per axis-pair (= per
    /// grid container), avoiding spam from `repeat(N, ...)`.
    /// <para><b>Per PR-#95 review R5</b>: shared across rows + columns
    /// by design — one grid → one diagnostic per cause. If row + col
    /// both trip the same kind, only the first fires. Per cause type
    /// is its own one-shot, so a row unsupported-kind + col percentage
    /// emit two diagnostics (not just one).</para>
    /// <para><b>Kept as <c>internal sealed class</c></b> (not ref struct)
    /// to keep the call signature stable + because the SafeEmit delegate
    /// captured by GridLayouter is more natural with reference semantics.
    /// The single allocation per Resolve is negligible vs. the layout
    /// work that follows.</para></summary>
    internal sealed class SizingContext
    {
        public EmitDiagnostic? Emitter { get; }
        public bool EmittedTrackKindDiagnostic { get; set; }
        public bool EmittedFrIndefiniteDiagnostic { get; set; }
        public bool EmittedPercentageDiagnostic { get; set; }
        public bool EmittedTruncationDiagnostic { get; set; }
        public SizingContext(EmitDiagnostic? emitter) { Emitter = emitter; }
        public void Emit(PaginateDiagnostic d) => Emitter?.Invoke(d);
    }

    /// <summary>Per PR-#95 review H1 — epsilon-based double comparison
    /// for "did this size change" checks. Exact <c>!=</c> on doubles
    /// is fragile under accumulated FP drift from fr distribution +
    /// Maximize iteration. <see cref="SizeEpsilon"/> = 1e-9 px, well
    /// below sub-pixel paint precision but above FP rounding noise
    /// for layout sizes up to ~1e6 px.</summary>
    private const double SizeEpsilon = 1e-9;

    /// <summary>Per Phase 3 Task 17 cycle 5 — public accessor for
    /// <see cref="GridLayouter"/>'s row-budget check (= same epsilon
    /// across the sizing service + the layouter so float drift
    /// doesn't cause off-by-one row decisions at the page boundary).</summary>
    internal const double SizeEpsilonPublic = SizeEpsilon;

    private static bool SizesDiffer(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return true;
        if (a == b) return false;
        return Math.Abs(a - b) > SizeEpsilon;
    }

    // =====================================================================
    //  Track-size classification (Length + Fr + intrinsic + minmax + fit-content)
    // =====================================================================

    /// <summary>Per Phase 3 Task 17 cycle 4 — per-track sizing state.
    /// Carries the (baseSize, growthLimit, isFr, frFactor) tuple that
    /// the §11.5 / §11.6 / §11.7 algorithms consume.
    ///
    /// <para><b>Lifecycle</b>: populated by <see cref="ResolveTrackSizes"/>
    /// from the expanded <see cref="TrackList"/>; updated in-place by
    /// fr distribution (§11.7), intrinsic resolution (§11.5), Maximize
    /// (§11.6).</para>
    ///
    /// <para><b>GrowthLimit = +∞</b> for tracks with no max cap (=
    /// pure fr, auto/min-content/max-content, minmax with intrinsic
    /// max). Maximize distributes free space among finite-growth-limit
    /// tracks first; +∞ growth tracks absorb any remainder.</para></summary>
    private struct TrackSizingInfo
    {
        public double BaseSize;
        /// <summary>The "minimum base" floor — the value
        /// <see cref="BaseSize"/> resets to at the start of each
        /// <see cref="ResolveFrTracks"/> pass. For fr tracks this
        /// is 0 (= plain Fr) or the min-arg value (= minmax(min, fr)).
        /// For non-fr tracks <see cref="MinBaseSize"/> tracks
        /// <see cref="BaseSize"/> directly (the floor doesn't move
        /// independently). Cycle 4 — without this floor, a second fr
        /// distribution pass after intrinsic resolution would treat
        /// the first pass's distributed value as the floor + trigger
        /// the §11.7 removal step incorrectly.</summary>
        public double MinBaseSize;
        public double GrowthLimit;
        public bool IsFr;
        public double FrFactor;
        /// <summary>The authored track kind (Length / Fr / Auto /
        /// MinContent / MaxContent / MinMax / FitContent). Used by
        /// the diagnostic emitter + intrinsic resolution targeting.</summary>
        public GridTrackKind Kind;
        /// <summary>For MinMax tracks: the min arg's kind. Intrinsic
        /// (Auto/MinContent/MaxContent) → base grows from placed
        /// items. Length → base is fixed (= the px value already in
        /// BaseSize). Per §7.2.4 Fr is INVALID in min.</summary>
        public GridTrackKind MinSubKind;
        /// <summary>For MinMax tracks: the max arg's kind. Intrinsic
        /// → growthLimit grows from placed items (then Maximize uses
        /// it). Length → growthLimit is fixed. Fr → IsFr=true with
        /// factor.</summary>
        public GridTrackKind MaxSubKind;
        /// <summary>For FitContent tracks: the limit value (px).
        /// Per §7.2.2 the final size = max(auto-min, min(limit,
        /// max-content)). Cycle 4 with L19 approximation simplifies
        /// to min(limit, max-content) since auto-min = 0.</summary>
        public double FitContentLimit;
    }

    /// <summary>Per Phase 3 Task 17 cycle 4 — expand <c>repeat(N, ...)</c>
    /// groups inline. Positive-count repeats unroll into N pattern
    /// copies; <c>auto-fill</c> (count 0) and <c>auto-fit</c> (count
    /// -1) remain as <see cref="TrackListRepeat"/> placeholders that
    /// the classify pass treats as unsupported (cycle 7 ships those).
    ///
    /// <para><b>DoS guard</b>: total expanded item count caps at
    /// <see cref="TrackList.MaxExpandedTrackCount"/>; on overflow the
    /// expansion truncates + emits
    /// <see cref="PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001"/>
    /// (per PR-#95 review R2 — was previously the misleading
    /// "track kind Length unsupported" diagnostic).</para>
    ///
    /// <para><b>Named lines preserved</b>: <see cref="TrackRepeatNamedLine"/>
    /// items inside the pattern become <see cref="TrackListNamedLine"/>
    /// items in the expanded list, repeating with each iteration per
    /// CSS Grid §7.2.3.</para>
    ///
    /// <para><b>Cancellation</b>: per PR-#95 review P6, the inner repeat
    /// loop checks the cancellation token at iteration boundaries since
    /// hostile CSS with combinations of large repeats can spin up to
    /// <see cref="TrackList.MaxExpandedTrackCount"/> iterations.</para></summary>
    private static List<TrackListItem> ExpandTrackList(
        TrackList trackList, SizingContext ctx, CancellationToken ct)
    {
        var expanded = new List<TrackListItem>(trackList.Items.Length);
        var truncated = false;
        foreach (var item in trackList.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (expanded.Count >= TrackList.MaxExpandedTrackCount)
            {
                truncated = true;
                break;
            }
            switch (item)
            {
                case TrackListEntry:
                case TrackListNamedLine:
                    expanded.Add(item);
                    break;
                case TrackListRepeat tr when tr.Repeat.Count > 0:
                    // Explicit integer count: unroll pattern N times.
                    for (var r = 0; r < tr.Repeat.Count; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var p in tr.Repeat.Pattern)
                        {
                            if (expanded.Count >= TrackList.MaxExpandedTrackCount)
                            {
                                truncated = true;
                                break;
                            }
                            switch (p)
                            {
                                case TrackRepeatEntry re:
                                    expanded.Add(new TrackListEntry(re.Entry));
                                    break;
                                case TrackRepeatNamedLine nl:
                                    expanded.Add(TrackListNamedLine.Create(nl.Name));
                                    break;
                            }
                        }
                        if (truncated) break;
                    }
                    break;
                case TrackListRepeat:
                    // auto-fill (count=0) / auto-fit (count=-1) —
                    // unsupported in cycle 4; keep as-is so classify
                    // emits the unsupported diagnostic.
                    expanded.Add(item);
                    break;
            }
        }
        if (truncated)
        {
            EmitTruncationDiagnostic(ctx);
        }
        return expanded;
    }

    private static void ResolveTrackSizes(
        TrackList trackList, double containerExtent, bool isAxisIndefinite,
        List<TrackSizingInfo> infoOut, SizingContext ctx,
        CancellationToken ct)
    {
        var expanded = ExpandTrackList(trackList, ctx, ct);
        foreach (var item in expanded)
        {
            if (item is TrackListEntry entry)
            {
                infoOut.Add(ClassifyEntry(entry.Entry, ctx));
            }
            else if (item is TrackListRepeat)
            {
                // auto-fill / auto-fit — cycle 7 scope. Per PR-#95
                // review R2 — emit the unsupported-kind diagnostic with
                // the actual kind (was hardcoded to Length, misleading).
                EmitTrackKindDiagnostic(ctx, GridTrackKind.Auto);
            }
            // TrackListNamedLine — cycle 7 named-line resolution.
            // Kept in the expanded list so cycle 7 can read positionally.
        }

        // Per PR-#93 review F3 — fr under indefinite axis approximated.
        if (isAxisIndefinite)
        {
            for (var i = 0; i < infoOut.Count; i++)
            {
                if (infoOut[i].IsFr)
                {
                    EmitFrUnderIndefiniteDiagnostic(ctx);
                    break;
                }
            }
        }

        // §11.7 fr distribution (cycle 4 — with iterative removal step).
        ResolveFrTracks(infoOut, containerExtent, ctx, ct);
    }

    /// <summary>Per Phase 3 Task 17 cycle 4 — classify one
    /// <see cref="TrackEntry"/> into a <see cref="TrackSizingInfo"/>.
    /// Handles Length / Fr / intrinsic / MinMax / FitContent. Returns
    /// a fully populated info struct (subkind fields default to
    /// <see cref="GridTrackKind.Auto"/> for non-MinMax tracks).
    ///
    /// <para><b>Per PR-#95 review H3 + C3</b>: percentage tracks
    /// (top-level OR inside minmax/fit-content sub-args) emit
    /// <c>LayoutGridPercentageTrackApproximated001</c> and collapse
    /// to 0 instead of being silently misinterpreted as pixels.</para></summary>
    private static TrackSizingInfo ClassifyEntry(TrackEntry entry, SizingContext ctx)
    {
        switch (entry.Kind)
        {
            case GridTrackKind.Length when !entry.IsPercentage:
                return new TrackSizingInfo
                {
                    BaseSize = entry.LengthPx,
                    MinBaseSize = entry.LengthPx,
                    GrowthLimit = entry.LengthPx,
                    Kind = GridTrackKind.Length,
                };
            case GridTrackKind.Length:
                // Per PR-#95 review H3 — Length with IsPercentage=true.
                // Cycle-4 initial impl fell into the default branch with
                // the misleading "kind Length unsupported" diagnostic.
                // Now emits the dedicated percentage diagnostic + 0
                // placeholder.
                EmitPercentageDiagnostic(ctx);
                return new TrackSizingInfo
                {
                    BaseSize = 0,
                    MinBaseSize = 0,
                    GrowthLimit = 0,
                    Kind = GridTrackKind.Length,
                };
            case GridTrackKind.Fr:
                return new TrackSizingInfo
                {
                    BaseSize = 0,
                    MinBaseSize = 0,
                    GrowthLimit = double.PositiveInfinity,
                    IsFr = true,
                    FrFactor = entry.FrValue,
                    Kind = GridTrackKind.Fr,
                };
            case GridTrackKind.Auto:
            case GridTrackKind.MinContent:
            case GridTrackKind.MaxContent:
                // Cycle 3 — intrinsic placeholder; filled from placed
                // items in the post-placement pass.
                return new TrackSizingInfo
                {
                    BaseSize = 0,
                    MinBaseSize = 0,
                    GrowthLimit = double.PositiveInfinity,
                    Kind = entry.Kind,
                };
            case GridTrackKind.MinMax:
                // Cycle 4 — minmax(min, max). Per §7.2.4: min is
                // Length/Auto/MinContent/MaxContent (NOT Fr); max
                // is additionally Fr-allowed. Base from min; growth
                // (+ fr if applicable) from max.
                return ClassifyMinMax(entry, ctx);
            case GridTrackKind.FitContent:
                // Cycle 4 — fit-content(limit). Per §7.2.2 the size
                // is max(auto-min, min(limit, max-content)). Cycle 4
                // L19 approximation: auto-min = 0 + max-content =
                // L19 declared contribution. Stored as base=0,
                // growth=limit; intrinsic resolution will clamp via
                // min(limit, contribution).
                if (entry.IsPercentage)
                {
                    // Per PR-#95 review C3 — fit-content(50%) silently
                    // becoming fit-content(50px) is worse than emitting.
                    EmitPercentageDiagnostic(ctx);
                    return new TrackSizingInfo
                    {
                        BaseSize = 0,
                        MinBaseSize = 0,
                        GrowthLimit = 0,
                        Kind = GridTrackKind.FitContent,
                        FitContentLimit = 0,
                    };
                }
                return new TrackSizingInfo
                {
                    BaseSize = 0,
                    MinBaseSize = 0,
                    GrowthLimit = entry.LengthPx,
                    Kind = GridTrackKind.FitContent,
                    FitContentLimit = entry.LengthPx,
                };
            default:
                EmitTrackKindDiagnostic(ctx, entry.Kind);
                return new TrackSizingInfo
                {
                    BaseSize = 0,
                    MinBaseSize = 0,
                    GrowthLimit = double.PositiveInfinity,
                    Kind = entry.Kind,
                };
        }
    }

    /// <summary>Per Phase 3 Task 17 cycle 4 — classify a MinMax entry.
    /// Base from min (Length pinned, intrinsic = 0 placeholder);
    /// growth from max (Length pinned, Fr → IsFr=true uncapped,
    /// intrinsic = ∞ placeholder).
    /// <para><b>Per PR-#95 review C3</b>: percentage sub-args
    /// (<c>minmax(50%, 1fr)</c>) emit a diagnostic and collapse to 0
    /// for the percentage side instead of being silently treated as
    /// pixels.</para></summary>
    private static TrackSizingInfo ClassifyMinMax(TrackEntry entry, SizingContext ctx)
    {
        // Min arg — base size.
        double baseSize = 0;
        if (entry.MinSubKind == GridTrackKind.Length)
        {
            if (entry.MinSubIsPercentage)
            {
                EmitPercentageDiagnostic(ctx);
                // baseSize stays 0 — the percentage is treated as 0
                // until cycle 5+ ships percentage resolution.
            }
            else
            {
                baseSize = entry.MinSubLengthPx;
            }
        }
        // Intrinsic min (Auto/MinContent/MaxContent) → base stays 0;
        // intrinsic resolution will fill from placed items.

        // Max arg — growth limit + fr.
        double growthLimit;
        var isFr = false;
        double frFactor = 0;
        if (entry.MaxSubKind == GridTrackKind.Length)
        {
            if (entry.MaxSubIsPercentage)
            {
                EmitPercentageDiagnostic(ctx);
                growthLimit = 0;
            }
            else
            {
                growthLimit = entry.MaxSubLengthPx;
            }
        }
        else if (entry.MaxSubKind == GridTrackKind.Fr)
        {
            growthLimit = double.PositiveInfinity;
            isFr = true;
            frFactor = entry.MaxSubFrValue;
        }
        else
        {
            // Intrinsic max — growth uncapped; intrinsic resolution
            // sets a real cap from placed items.
            growthLimit = double.PositiveInfinity;
        }

        // Per §11.5: growth-limit must be ≥ base-size. If a fixed-
        // min exceeds a fixed-max (= invalid CSS like minmax(200px,
        // 100px)), max is treated as min (= track sits at 200).
        if (growthLimit < baseSize) growthLimit = baseSize;

        return new TrackSizingInfo
        {
            BaseSize = baseSize,
            MinBaseSize = baseSize,
            GrowthLimit = growthLimit,
            IsFr = isFr,
            FrFactor = frFactor,
            Kind = GridTrackKind.MinMax,
            MinSubKind = entry.MinSubKind,
            MaxSubKind = entry.MaxSubKind,
        };
    }

    /// <summary>Per Phase 3 Task 17 cycle 4 + post-PR-#95 review P3 —
    /// extract the per-track final size (= BaseSize) into a
    /// List&lt;double&gt; for the downstream position-computation +
    /// emission paths that still consume the legacy List&lt;double&gt;
    /// shape. Called ONCE after Maximize (was twice in cycle-4 initial
    /// impl — once eagerly inside ResolveTrackSizes, once again after
    /// Maximize overwrote the values).</summary>
    private static List<double> MaterializeSizes(List<TrackSizingInfo> infos)
    {
        var sizes = new List<double>(infos.Count);
        var span = CollectionsMarshal.AsSpan(infos);
        for (var i = 0; i < span.Length; i++) sizes.Add(span[i].BaseSize);
        return sizes;
    }

    // =====================================================================
    //  §11.7 fr distribution (cycle 4 — with iterative removal)
    // =====================================================================

    /// <summary>Per Phase 3 Task 17 cycle 2 (initial) + cycle 4
    /// (iterative removal step) — CSS Grid §11.7.1 "Find the Size
    /// of an fr".
    ///
    /// <para><b>Algorithm</b>:</para>
    /// <list type="number">
    ///   <item>leftover = containerExtent - SUM(baseSize) over all
    ///   tracks (= space available for fr distribution).</item>
    ///   <item>If leftover ≤ 0 → fr tracks pin at their current
    ///   baseSize (= the §11.7 "minimum base" floor).</item>
    ///   <item>flexFactorSum = max(SUM(factor), 1.0) over fr tracks.
    ///   (Per PR-#93 F1: floor applies to TOTAL once, NOT per-track.)</item>
    ///   <item>hypoFr = leftover / flexFactorSum.</item>
    ///   <item><b>Iterative removal step (cycle 4)</b>: any fr track
    ///   with baseSize > hypoFr × factor freezes at its base (= the
    ///   "minimum prevents shrinkage" rule). Its factor leaves the
    ///   fr pool + its base shifts to the non-flex sum; recompute
    ///   leftover + hypoFr. Iterate until no more removals.</item>
    ///   <item>Final per-fr-track size = max(baseSize, hypoFr × factor).</item>
    /// </list>
    ///
    /// <para>Pre-cycle-4 fr tracks always had baseSize=0 (= the
    /// removal check never fired). Cycle 4's <c>minmax(min, fr)</c>
    /// introduces non-zero fr bases (= the min sets a floor), so the
    /// removal step now matters.</para></summary>
    private static void ResolveFrTracks(
        List<TrackSizingInfo> infos, double containerExtent,
        SizingContext ctx, CancellationToken ct)
    {
        // Per PR-#95 review P1 — single Span aliasing the List for
        // zero-copy ref-mutation across the rest of the method.
        var span = CollectionsMarshal.AsSpan(infos);
        var hasFr = false;
        var frTrackCount = 0;
        // Reset fr-track BaseSize to MinBaseSize (= floor) so a
        // re-distribution pass after intrinsic resolution starts
        // from the floor + recomputes. Without this, the prior
        // pass's distributed value would be misread as the floor
        // and trigger the §11.7 removal step incorrectly. Per cycle
        // 4 — fr tracks inside <c>minmax(min, fr)</c> with intrinsic
        // mins get their floor updated by intrinsic resolution.
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].IsFr)
            {
                hasFr = true;
                frTrackCount++;
                span[i].BaseSize = span[i].MinBaseSize;
            }
        }
        if (!hasFr) return;

        // Per-track "frozen" flag — fr tracks whose base exceeds
        // their proportional share get treated as fixed for the
        // remaining iterations. Per PR-#95 review P2 — stackalloc
        // for the common case (≤ 64 tracks) avoids the heap alloc.
        Span<bool> frozen = span.Length <= 64
            ? stackalloc bool[span.Length]
            : new bool[span.Length];
        frozen.Clear();

        // Per PR-#95 review H2 — bound matches the spec (each pass
        // freezes ≥1 fr track or terminates; +1 for the final
        // converged-distribution pass). Was infos.Count (= total
        // tracks, looser than needed).
        var maxPasses = frTrackCount + 1;
        for (var pass = 0; pass <= maxPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();
            double nonFlexBase = 0;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsFr && !frozen[i]) continue;
                // Frozen fr OR non-fr tracks contribute their current
                // baseSize to the non-flex sum.
                nonFlexBase += span[i].BaseSize;
            }
            var leftover = containerExtent - nonFlexBase;
            if (leftover <= 0 || !double.IsFinite(leftover))
            {
                // No room for the unfrozen fr tracks to grow above
                // their bases; they stay at baseSize.
                return;
            }

            // Per PR-#93 F1 — spec-correct flexFactorSum.
            double rawSum = 0;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i].IsFr && !frozen[i]) rawSum += span[i].FrFactor;
            }
            if (!double.IsFinite(rawSum))
            {
                // Per PR-#93 F4 — non-finite sum guard.
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
            if (rawSum <= 0)
            {
                // No fr factors left (= all frozen). Nothing more to do.
                return;
            }
            var flexFactorSum = Math.Max(rawSum, 1.0);
            var hypoFr = leftover / flexFactorSum;
            if (!double.IsFinite(hypoFr)) return;

            // Iterative removal — freeze any unfrozen fr track whose
            // base > hypoFr × factor (= the minimum prevents shrinkage).
            var anyFrozenThisPass = false;
            for (var i = 0; i < span.Length; i++)
            {
                if (!span[i].IsFr || frozen[i]) continue;
                if (span[i].BaseSize > hypoFr * span[i].FrFactor)
                {
                    frozen[i] = true;
                    anyFrozenThisPass = true;
                }
            }

            if (!anyFrozenThisPass)
            {
                // Converged — apply the final distribution to all
                // unfrozen fr tracks.
                for (var i = 0; i < span.Length; i++)
                {
                    if (!span[i].IsFr || frozen[i]) continue;
                    var distributed = hypoFr * span[i].FrFactor;
                    if (distributed > span[i].BaseSize)
                    {
                        span[i].BaseSize = distributed;
                    }
                }
                return;
            }
        }

        // Per PR-#95 review H2 — exhausted the safety bound without
        // converging. This shouldn't be reachable since each iteration
        // either freezes ≥1 track OR returns; emit a diagnostic to
        // surface the impossible state.
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridNonFiniteGeometry001,
            Message: "Grid §11.7 fr distribution failed to converge "
                + $"within {maxPasses + 1} passes (= one per fr track "
                + "plus a final distribution pass). This indicates an "
                + "internal algorithm bug or a degenerate input that "
                + "produces oscillating freeze decisions; fr tracks "
                + "stay at their pre-distribution floor values to "
                + "avoid corrupting downstream paint.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    // =====================================================================
    //  §11.6 Maximize step (cycle 4)
    // =====================================================================

    /// <summary>Per Phase 3 Task 17 cycle 4 + post-PR-#95 review C1 —
    /// CSS Grid §11.6 "Maximize Tracks".
    ///
    /// <para>Per W3C spec
    /// (<see href="https://www.w3.org/TR/css-grid-1/#algo-grow-tracks"/>):
    /// "If the free space is positive, distribute it EQUALLY to the
    /// base sizes of all tracks, freezing tracks as they reach their
    /// growth limits (and continuing to grow the unfrozen tracks as
    /// needed)."</para>
    ///
    /// <para><b>This is NOT proportional-to-headroom</b> (= flexbox
    /// semantics). The cycle-4 INITIAL impl used `ratio = freeSpace /
    /// totalHeadroom` then `headroom × ratio` per track — that's wrong.
    /// PR-#95 review C1 caught the divergence with counter-examples
    /// like <c>minmax(50px, 100px) minmax(50px, 1000px)</c> in 400px:
    /// spec says A=100 (frozen) B=300; old impl said A=65 B=335.</para>
    ///
    /// <para><b>Algorithm</b> (mirrors §11.7.1's iterative freezing):</para>
    /// <list type="number">
    ///   <item>Mark non-eligible tracks frozen up-front (fr; infinite
    ///   GrowthLimit; GrowthLimit ≤ BaseSize).</item>
    ///   <item>While unfrozen tracks remain + free space > 0:
    ///   share = freeSpace / unfrozenCount. Each unfrozen track tries
    ///   to grow by share. Tracks whose new BaseSize would meet/exceed
    ///   GrowthLimit are clamped + frozen.</item>
    ///   <item>If no track froze this pass, the share fits — apply +
    ///   return.</item>
    /// </list>
    ///
    /// <para><b>Per PR-#95 review H4 known-gap</b>: tracks with infinite
    /// GrowthLimit (e.g., auto/min-content/max-content with no placed
    /// items) are excluded. Per §11.5.1 step 3 the spec distributes
    /// leftover to "extra-space-receiver" tracks (intrinsic max-content
    /// then fr) AFTER finite-limit tracks freeze. Cycle 4 doesn't
    /// implement that second pass; documented in deferrals.md as
    /// `grid-maximize-extra-space-receiver-deferred`. In practice,
    /// intrinsic-with-items always sets growthLimit=base so the gap
    /// only manifests for degenerate empty-track-with-no-items cases.</para>
    ///
    /// <para><b>Per PR-#95 review C2</b>: FitContent tracks have their
    /// GrowthLimit pinned to <c>effective</c> after intrinsic
    /// resolution (ResolveIntrinsicTracks now sets GrowthLimit =
    /// BaseSize for FitContent), so Maximize correctly leaves them
    /// alone.</para></summary>
    private static void MaximizeTracks(
        List<TrackSizingInfo> infos, double containerExtent)
    {
        if (!double.IsFinite(containerExtent) || containerExtent <= 0) return;

        var span = CollectionsMarshal.AsSpan(infos);
        if (span.Length == 0) return;

        // Per PR-#95 review P1+P2 — Span ref-access + stackalloc for
        // small-track-count case.
        Span<bool> frozen = span.Length <= 64
            ? stackalloc bool[span.Length]
            : new bool[span.Length];
        frozen.Clear();

        // Mark non-eligible tracks frozen up-front.
        var unfrozenCount = 0;
        for (var i = 0; i < span.Length; i++)
        {
            ref readonly var t = ref span[i];
            if (t.IsFr || !double.IsFinite(t.GrowthLimit) || t.GrowthLimit <= t.BaseSize)
            {
                frozen[i] = true;
            }
            else
            {
                unfrozenCount++;
            }
        }
        if (unfrozenCount == 0) return;

        // Iterative equal-distribute + freeze. Bound: at most
        // unfrozenCount passes (each freezes ≥1 OR converges).
        for (var pass = 0; pass <= unfrozenCount; pass++)
        {
            double totalBase = 0;
            for (var i = 0; i < span.Length; i++) totalBase += span[i].BaseSize;
            var freeSpace = containerExtent - totalBase;
            if (freeSpace <= SizeEpsilon || !double.IsFinite(freeSpace)) return;

            // Count currently unfrozen.
            var liveCount = 0;
            for (var i = 0; i < span.Length; i++) if (!frozen[i]) liveCount++;
            if (liveCount == 0) return;

            var share = freeSpace / liveCount;
            var anyFrozenThisPass = false;
            for (var i = 0; i < span.Length; i++)
            {
                if (frozen[i]) continue;
                var newBase = span[i].BaseSize + share;
                if (newBase >= span[i].GrowthLimit)
                {
                    // Clamp + freeze.
                    span[i].BaseSize = span[i].GrowthLimit;
                    frozen[i] = true;
                    anyFrozenThisPass = true;
                }
                else
                {
                    span[i].BaseSize = newBase;
                }
            }

            if (!anyFrozenThisPass) return;
        }
    }

    // =====================================================================
    //  Cycle 3 — intrinsic resolution (L19 approximation) + fr re-resolve
    //  Cycle 4 — extended to MinMax (intrinsic min/max) + FitContent
    // =====================================================================

    /// <summary>Per Phase 3 Task 17 cycle 3 (initial) + cycle 4 (MinMax
    /// + FitContent extensions) — resolve intrinsic track sizes from
    /// placed items per the L19 approximation.
    ///
    /// <para><b>Per kind</b>:</para>
    /// <list type="bullet">
    ///   <item><b>Auto / MinContent / MaxContent</b>: baseSize =
    ///   max(item contribution). All three resolve identically
    ///   under the L19 approximation (cycle 3 known-gap).</item>
    ///   <item><b>MinMax with intrinsic min</b> (= min is Auto /
    ///   MinContent / MaxContent): baseSize = max(item contribution)
    ///   — same as a plain intrinsic track for the min side.</item>
    ///   <item><b>MinMax with intrinsic max</b> (= max is Auto /
    ///   MinContent / MaxContent): growthLimit = max(baseSize,
    ///   max(item contribution)) — Maximize will then grow base
    ///   up to this limit.</item>
    ///   <item><b>FitContent(limit)</b>: per §7.2.2 formula
    ///   <c>max(auto-min, min(limit, max-content))</c>. Cycle 4 L19
    ///   approximation: auto-min = 0 → effective = min(limit,
    ///   max-content). baseSize = min(growthLimit, max(item
    ///   contribution)); growthLimit stays at the original limit so
    ///   Maximize won't grow it further.</item>
    /// </list>
    ///
    /// <para>Returns true when at least one track's baseSize OR
    /// growthLimit changed (= triggers fr re-distribution since the
    /// non-flex base sum has changed).</para></summary>
    private static bool ResolveIntrinsicTracks(
        List<TrackSizingInfo> infos,
        List<PlacedItem> placedItems, bool isRowAxis)
    {
        var anyChanged = false;
        // Per PR-#95 review P1 — Span ref-access for mutation.
        var span = CollectionsMarshal.AsSpan(infos);
        for (var i = 0; i < span.Length; i++)
        {
            ref var info = ref span[i];
            var needsContribution =
                info.Kind is GridTrackKind.Auto
                    or GridTrackKind.MinContent
                    or GridTrackKind.MaxContent
                    or GridTrackKind.FitContent
                || (info.Kind == GridTrackKind.MinMax
                    && (IsIntrinsicKind(info.MinSubKind)
                        || IsIntrinsicKind(info.MaxSubKind)));
            if (!needsContribution) continue;

            double maxContribution = 0;
            foreach (var item in placedItems)
            {
                if (item.Row < 0 || item.Col < 0) continue;
                var matchesTrack = isRowAxis ? (item.Row == i) : (item.Col == i);
                if (!matchesTrack) continue;
                var contribution = ItemOuterContribution(item.Box, isRowAxis);
                if (contribution > maxContribution) maxContribution = contribution;
            }

            if (maxContribution <= 0) continue;

            switch (info.Kind)
            {
                case GridTrackKind.Auto:
                case GridTrackKind.MinContent:
                case GridTrackKind.MaxContent:
                    // Per PR-#95 review H1 — epsilon comparison.
                    if (SizesDiffer(maxContribution, info.BaseSize))
                    {
                        info.BaseSize = maxContribution;
                        info.MinBaseSize = maxContribution;
                        // For uncapped intrinsic tracks, growthLimit
                        // tracks baseSize (= no headroom for Maximize).
                        info.GrowthLimit = maxContribution;
                        anyChanged = true;
                    }
                    break;
                case GridTrackKind.MinMax:
                    var changed = false;
                    if (IsIntrinsicKind(info.MinSubKind)
                        && maxContribution > info.BaseSize + SizeEpsilon)
                    {
                        info.BaseSize = maxContribution;
                        info.MinBaseSize = maxContribution;
                        changed = true;
                    }
                    if (IsIntrinsicKind(info.MaxSubKind))
                    {
                        var newGrowth = Math.Max(info.BaseSize, maxContribution);
                        if (SizesDiffer(newGrowth, info.GrowthLimit))
                        {
                            info.GrowthLimit = newGrowth;
                            changed = true;
                        }
                    }
                    // Per §11.5: growthLimit ≥ baseSize invariant.
                    if (info.GrowthLimit < info.BaseSize)
                    {
                        info.GrowthLimit = info.BaseSize;
                        changed = true;
                    }
                    if (changed) anyChanged = true;
                    break;
                case GridTrackKind.FitContent:
                    // Per §7.2.2: effective = min(limit, max-content).
                    // L19 approximation: max-content = maxContribution.
                    var effective = Math.Min(info.FitContentLimit, maxContribution);
                    if (SizesDiffer(effective, info.BaseSize))
                    {
                        info.BaseSize = effective;
                        // Per PR-#95 review H7 — only update MinBaseSize
                        // for fr-participating tracks. FitContent never
                        // is. Keeps MinBaseSize semantics clean (= the
                        // fr re-distribution floor).
                        // Per PR-#95 review C2 — pin GrowthLimit so
                        // Maximize doesn't subsequently grow this track
                        // past its §7.2.2 result. Before the fix,
                        // GrowthLimit stayed at FitContentLimit and
                        // Maximize would grow the track to the LIMIT
                        // even though the formula said it should sit at
                        // <c>effective</c> = min(limit, content).
                        info.GrowthLimit = effective;
                        anyChanged = true;
                    }
                    break;
            }
        }
        return anyChanged;
    }

    /// <summary>Predicate for the three intrinsic kinds (Auto /
    /// MinContent / MaxContent). Used by MinMax classification to
    /// detect whether the min OR max arg needs intrinsic resolution
    /// from placed items.</summary>
    private static bool IsIntrinsicKind(GridTrackKind kind) =>
        kind is GridTrackKind.Auto
            or GridTrackKind.MinContent
            or GridTrackKind.MaxContent;

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
                + "(cycle 4 ships <length> + <flex>/fr via §11.7 + "
                + "intrinsic auto / min-content / max-content via the "
                + "L19 approximation + minmax() with intrinsic-aware "
                + "base/growth + fit-content(limit) per §7.2.2 + "
                + "repeat(<integer>, ...) expansion; auto-fill / "
                + "auto-fit / percentage tracks + named-line lookup "
                + "are cycle 5-7 scope). The track contributes 0 px "
                + "to the track sum or truncates at the MaxExpanded "
                + "cap.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    /// <summary>Per PR-#95 review C3 + H3 — emitted when a percentage
    /// track is encountered (top-level Length, or as a minmax/
    /// fit-content sub-arg). One-shot per Resolve call.</summary>
    private static void EmitPercentageDiagnostic(SizingContext ctx)
    {
        if (ctx.EmittedPercentageDiagnostic) return;
        ctx.EmittedPercentageDiagnostic = true;
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridPercentageTrackApproximated001,
            Message: "Grid track uses a <percentage> value (top-level "
                + "or inside minmax()/fit-content() sub-args). Cycle 4 "
                + "doesn't yet resolve percentages against the "
                + "container's extent; the track contributes 0 px so "
                + "the author sees a clearly broken layout instead of "
                + "an authoritative-looking-but-wrong pixel value. "
                + "Cycle 5+ ships percentage resolution against the "
                + "container's definite extent.",
            Severity: PaginateDiagnosticSeverity.Warning));
    }

    /// <summary>Per PR-#95 review R2 — emitted when repeat() expansion
    /// hits <see cref="TrackList.MaxExpandedTrackCount"/>. Distinguished
    /// from the generic unsupported-kind diagnostic so authors can
    /// understand "your CSS was too large + truncated" vs. "you used
    /// a feature we don't support".</summary>
    private static void EmitTruncationDiagnostic(SizingContext ctx)
    {
        if (ctx.EmittedTruncationDiagnostic) return;
        ctx.EmittedTruncationDiagnostic = true;
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridMaxExpandedTracksTruncated001,
            Message: "Grid track list's repeat() expansion would exceed "
                + $"TrackList.MaxExpandedTrackCount = {TrackList.MaxExpandedTrackCount} "
                + "entries. Expansion truncates at the cap to prevent "
                + "unbounded memory allocation from hostile CSS like "
                + "repeat(10000, 1px 1fr 1px 1fr 1fr 1px). Items placed "
                + "at indices in the truncated tail are silently dropped.",
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
