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

        // Per Phase 3 Task 18 cycle 6 + PR-#103 review F1+F2 — capture
        // the explicit-grid extents NOW (before any implicit growth)
        // so negative line numbers resolve against the
        // author-declared grid (not the post-grown grid) per CSS Grid
        // L1 §8.1.4. Both counts may be 0 (= implicit-only grid).
        var explicitRowCount = rowInfos.Count;
        var explicitColCount = colInfos.Count;

        // Gather grid items + classify placement. Per Phase 3 Task 18
        // cycle 6, the placement spec carries the per-axis span; the
        // initial PlacedItem.RowSpan / ColSpan mirror RowSpec.Span /
        // ColSpec.Span so the layouter's emission loop can size
        // multi-cell rectangles without re-reading the CSS values.
        // Per Phase 3 Task 18 cycle 7a — read the parent's
        // grid-template-areas map so item placement can resolve
        // named-area references (e.g., `grid-row-start: head`).
        var gridAreas = gridBox.Style.ReadGridTemplateAreas();

        // Per Phase 3 Task 18 cycle 7b + 7c + post-PR-#106 review
        // F2 #6 — build per-axis named-line occurrence maps. Combines
        // authored `[name]` lines from grid-template-rows/columns +
        // implicit `<area>-start` / `<area>-end` lines from grid-
        // template-areas per §8.4. Shares container-aware
        // ExpandTrackList with the sizing pass so auto-fill / auto-fit
        // iteration counts + named lines inside auto-repeats agree.
        var rowNamedLines = BuildNamedLineMap(
            gridBox.Style.ReadGridTemplateRows(), gridAreas, isRow: true,
            containerExtent: contentBlockSize, isAxisIndefinite: isBlockIndefinite,
            ctx, cancellationToken);
        var colNamedLines = BuildNamedLineMap(
            gridBox.Style.ReadGridTemplateColumns(), gridAreas, isRow: false,
            containerExtent: contentInlineSize, isAxisIndefinite: isInlineIndefinite,
            ctx, cancellationToken);

        var placedItems = new List<PlacedItem>(gridBox.Children.Count);
        foreach (var child in gridBox.Children)
        {
            if (!IsGridItem(child)) continue;
            var rowSpec = ReadPlacement(
                child.Style, isRow: true, gridAreas, rowNamedLines, ctx);
            var colSpec = ReadPlacement(
                child.Style, isRow: false, gridAreas, colNamedLines, ctx);
            placedItems.Add(new PlacedItem
            {
                Box = child,
                RowSpec = rowSpec,
                ColSpec = colSpec,
                Row = -1,
                Col = -1,
                RowSpan = rowSpec.Span,
                ColSpan = colSpec.Span,
            });
        }

        // Per Phase 3 Task 18 cycle 6 + PR-#103 review F1 — implicit-
        // only grid path: when an axis has zero explicit tracks but
        // there are items to place, seed a single implicit track on
        // that axis from grid-auto-rows / grid-auto-columns so the
        // placement service has somewhere to put things. Per CSS Grid
        // §7.4 the implicit-only grid is well-formed: items render in
        // implicit cells. A grid with NO items + empty templates
        // short-circuits before placement (degenerate no-op).
        var gridAutoRows = gridBox.Style.ReadGridAutoRows();
        var gridAutoColumns = gridBox.Style.ReadGridAutoColumns();
        var gridAutoFlow = gridBox.Style.ReadGridAutoFlow();
        if (placedItems.Count == 0
            && rowInfos.Count == 0 && colInfos.Count == 0)
        {
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
        if (rowInfos.Count == 0)
        {
            AppendImplicitTracks(rowInfos, 1, gridAutoRows,
                explicitCount: 0, ctx);
        }
        if (colInfos.Count == 0)
        {
            AppendImplicitTracks(colInfos, 1, gridAutoColumns,
                explicitCount: 0, ctx);
        }

        var rowCountBeforePlacement = rowInfos.Count;
        var colCountBeforePlacement = colInfos.Count;

        // Per Phase 3 Task 18 cycle 6 — placement may extend the grid
        // with implicit tracks (per CSS Grid §7.4). The placement
        // service consumes grid-auto-rows / grid-auto-columns /
        // grid-auto-flow to grow the track lists in-place; rowInfos /
        // colInfos may have additional entries appended after the
        // call.
        var grownOccupancy = RunPlacementWithSpans(
            placedItems, rowInfos, colInfos,
            explicitRowCount, explicitColCount,
            gridAutoRows, gridAutoColumns, gridAutoFlow,
            ctx, cancellationToken);
        // Per-axis growth may have re-shaped the fr distribution
        // (flexFactorSum changes when implicit tracks appended fr ones,
        // and the leftover space depends on the now-larger non-flex
        // base sum). Re-resolve fr for both axes if the grid grew.
        if (rowInfos.Count > rowCountBeforePlacement)
        {
            ResolveFrTracks(rowInfos, contentBlockSize, ctx, cancellationToken);
        }
        if (colInfos.Count > colCountBeforePlacement)
        {
            ResolveFrTracks(colInfos, contentInlineSize, ctx, cancellationToken);
        }
        _ = grownOccupancy;

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
        // Per Phase 3 Task 18 cycle 7c — defer to the container-aware
        // overload with no extent + indefinite-axis so auto-fill /
        // auto-fit fall through to their cycle-4 placeholder behavior
        // (= kept as TrackListRepeat). Callers with container geometry
        // call the overload directly.
        return ExpandTrackList(trackList, ctx, ct,
            containerExtent: 0.0, isAxisIndefinite: true);
    }

    /// <summary>Per Phase 3 Task 18 cycle 7c — container-aware
    /// expansion that derives the iteration count for
    /// <c>repeat(auto-fill, …)</c> / <c>repeat(auto-fit, …)</c> from
    /// the container extent per CSS Grid L1 §7.2.3.1.
    ///
    /// <para><b>Algorithm</b>:
    /// <list type="number">
    ///   <item>Compute <c>otherSize</c> = sum of FIXED sizes from the
    ///   non-auto-repeat items in <see cref="TrackList.Items"/>
    ///   (= length tracks, explicit-count repeats' length tracks, and
    ///   minmax min-floors).</item>
    ///   <item>Compute <c>patternSize</c> = sum of the auto-repeat
    ///   pattern's fixed sizes.</item>
    ///   <item><c>iterations = floor((containerExtent - otherSize) /
    ///   patternSize)</c>, clamped to ≥ 1 and to
    ///   <see cref="MaxImplicitTracksPerAxis"/>.</item>
    ///   <item>Indefinite axis OR <c>patternSize ≤ 0</c> → 1
    ///   iteration (= the spec's fallback).</item>
    /// </list></para>
    ///
    /// <para><b>auto-fill vs auto-fit</b>: identical expansion in
    /// cycle 7c. Per §7.2.3.1, auto-fit additionally collapses empty
    /// tracks (= those with no placed items) to 0 size AFTER
    /// placement; that collapse pass is tracked under
    /// <c>grid-auto-fit-collapse-empty-tracks-deferral</c>. Cycle 7c
    /// treats both as auto-fill so the pattern at least renders.</para></summary>
    private static List<TrackListItem> ExpandTrackList(
        TrackList trackList, SizingContext ctx, CancellationToken ct,
        double containerExtent, bool isAxisIndefinite)
    {
        var expanded = new List<TrackListItem>(trackList.Items.Length);
        var truncated = false;

        // Pre-compute the auto-repeat iteration count once. Per
        // §7.2.3.1 at most one auto-fill or auto-fit repeat is allowed
        // per track list; if multiple appear we use the cap for the
        // first and pass the rest through as 1 iteration.
        int autoIterations = ComputeAutoRepeatIterations(
            trackList, containerExtent, isAxisIndefinite);

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
                        if (UnrollPattern(tr.Repeat.Pattern, expanded))
                        {
                            truncated = true;
                            break;
                        }
                    }
                    break;
                case TrackListRepeat autoTr:
                    // Per Phase 3 Task 18 cycle 7c — auto-fill (Count=0)
                    // / auto-fit (Count=-1) unroll their pattern
                    // `autoIterations` times.
                    for (var r = 0; r < autoIterations; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (UnrollPattern(autoTr.Repeat.Pattern, expanded))
                        {
                            truncated = true;
                            break;
                        }
                    }
                    break;
            }
            if (truncated) break;
        }
        if (truncated)
        {
            EmitTruncationDiagnostic(ctx);
        }
        return expanded;
    }

    /// <summary>Per Phase 3 Task 18 cycle 7c — unroll one repeat-
    /// pattern iteration into <paramref name="expanded"/>. Returns
    /// <see langword="true"/> when the global truncation cap stops
    /// the inner loop short; caller exits the outer iteration.</summary>
    private static bool UnrollPattern(
        System.Collections.Immutable.ImmutableArray<TrackRepeatItem> pattern,
        List<TrackListItem> expanded)
    {
        foreach (var p in pattern)
        {
            if (expanded.Count >= TrackList.MaxExpandedTrackCount)
            {
                return true;
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
        return false;
    }

    /// <summary>Per Phase 3 Task 18 cycle 7c + CSS Grid L1 §7.2.3.1 —
    /// derive the iteration count for the FIRST auto-fill / auto-fit
    /// repeat in <paramref name="trackList"/>. Returns 1 when the
    /// axis is indefinite or when the pattern has no fixed size
    /// (= the spec's fallback when count cannot be derived).</summary>
    private static int ComputeAutoRepeatIterations(
        TrackList trackList, double containerExtent, bool isAxisIndefinite)
    {
        if (isAxisIndefinite) return 1;
        if (!double.IsFinite(containerExtent) || containerExtent <= 0)
            return 1;

        // Find the first auto-fill / auto-fit repeat and compute its
        // pattern's fixed size. Also accumulate the fixed size of
        // other items to subtract from the available extent.
        TrackRepeat? autoRepeat = null;
        double otherSize = 0;
        foreach (var item in trackList.Items)
        {
            switch (item)
            {
                case TrackListEntry te:
                    otherSize += GetTrackFixedSize(te.Entry);
                    break;
                case TrackListRepeat tr when tr.Repeat.Count > 0:
                    foreach (var p in tr.Repeat.Pattern)
                    {
                        if (p is TrackRepeatEntry re)
                        {
                            otherSize += tr.Repeat.Count * GetTrackFixedSize(re.Entry);
                        }
                    }
                    break;
                case TrackListRepeat atr when autoRepeat is null:
                    autoRepeat = atr.Repeat;
                    break;
                // Additional auto-repeats are invalid per spec but
                // tolerated as 1-iteration each by the main loop.
            }
        }
        if (autoRepeat is null) return 1;

        double patternSize = 0;
        foreach (var p in autoRepeat.Pattern)
        {
            if (p is TrackRepeatEntry re)
            {
                patternSize += GetTrackFixedSize(re.Entry);
            }
        }
        if (patternSize <= 0) return 1;

        var available = containerExtent - otherSize;
        if (available < patternSize) return 1;
        var iterations = (int)Math.Floor(available / patternSize);
        if (iterations < 1) iterations = 1;
        if (iterations > MaxImplicitTracksPerAxis)
            iterations = MaxImplicitTracksPerAxis;
        return iterations;
    }

    /// <summary>Per Phase 3 Task 18 cycle 7c — fixed (= "definite
    /// pixel") size of a track entry for the purposes of the
    /// auto-fill / auto-fit count derivation. Length tracks contribute
    /// their declared px; <c>minmax(min, max)</c> contributes its
    /// min-floor when that min is a fixed length; everything else
    /// (fr, auto, min/max-content, percentage, fit-content) is 0 —
    /// indefinite for count derivation per §7.2.3.1.</summary>
    private static double GetTrackFixedSize(TrackEntry entry)
    {
        switch (entry.Kind)
        {
            case GridTrackKind.Length when !entry.IsPercentage:
                return entry.LengthPx;
            case GridTrackKind.MinMax
                when entry.MinSubKind == GridTrackKind.Length
                    && !entry.MinSubIsPercentage:
                return entry.MinSubLengthPx;
            default:
                return 0;
        }
    }

    private static void ResolveTrackSizes(
        TrackList trackList, double containerExtent, bool isAxisIndefinite,
        List<TrackSizingInfo> infoOut, SizingContext ctx,
        CancellationToken ct)
    {
        // Per Phase 3 Task 18 cycle 7c — pass container extent so
        // auto-fill / auto-fit derive their iteration count.
        var expanded = ExpandTrackList(
            trackList, ctx, ct, containerExtent, isAxisIndefinite);
        foreach (var item in expanded)
        {
            if (item is TrackListEntry entry)
            {
                infoOut.Add(ClassifyEntry(entry.Entry, ctx));
            }
            else if (item is TrackListRepeat)
            {
                // Per Phase 3 Task 18 cycle 7c — an auto-repeat that
                // survives expansion means the count was 0 (= no fit).
                // The 1-iteration fallback should have prevented this,
                // so reaching this branch indicates a degenerate input.
                EmitTrackKindDiagnostic(ctx, GridTrackKind.Auto);
            }
            // TrackListNamedLine — cycle 7b named-line resolution.
            // Kept in the expanded list so the named-line builder
            // can read positionally.
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
                // Per Phase 3 Task 18 cycle 6 — check whether track i
                // is in the item's rectangle. For spanning items,
                // multiple tracks match the same item.
                var itemSpan = isRowAxis
                    ? Math.Max(1, item.RowSpan)
                    : Math.Max(1, item.ColSpan);
                var start = isRowAxis ? item.Row : item.Col;
                var matchesTrack = i >= start && i < start + itemSpan;
                if (!matchesTrack) continue;
                // Cycle 6a per-track contribution from a spanning item:
                // equal-share distribution (= contribution / span).
                // The spec-strict §11.5.1 step 3 algorithm subtracts
                // already-resolved length-track contributions then
                // distributes the remainder across intrinsic tracks
                // proportionally; that's tracked as
                // `grid-spanning-item-intrinsic-distribution-deferral`.
                var rawContribution = ItemOuterContribution(item.Box, isRowAxis);
                var perTrackContribution = itemSpan > 1
                    ? rawContribution / itemSpan
                    : rawContribution;
                if (perTrackContribution > maxContribution)
                {
                    maxContribution = perTrackContribution;
                }
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
    //  Placement (CSS Grid §8.5 4-pass algorithm with span + implicit
    //  tracks + auto-flow direction, per Phase 3 Task 18 cycle 6)
    // =====================================================================

    /// <summary>Per Phase 3 Task 18 cycle 6 + PR-#103 review F2 + F4 +
    /// F5 + F6 — extended placement service that handles span
    /// rectangles, generates implicit tracks per CSS Grid §7.4, and
    /// honors <c>grid-auto-flow: row | column</c>.
    ///
    /// <para><b>Algorithm</b> (parameterized on the major / minor
    /// axis per <paramref name="gridAutoFlow"/>; for
    /// <c>grid-auto-flow: row</c> major = rows + minor = columns;
    /// <c>column</c> swaps. Negative line numbers resolve against the
    /// PRE-PLACEMENT explicit counts per
    /// <paramref name="explicitRowCount"/> /
    /// <paramref name="explicitColCount"/> so a prior item's implicit
    /// growth doesn't shift a later <c>-1</c> resolution.):</para>
    /// <list type="number">
    ///   <item><b>Pass 1 — both definite</b>: marks
    ///   <c>[rowStart, rowStart+rowSpan) × [colStart, colStart+colSpan)</c>
    ///   in the sparse occupancy, growing tracks as needed.</item>
    ///   <item><b>Pass 2 — major-locked</b>: items with definite
    ///   placement on the MAJOR axis (= row in row-flow, col in
    ///   column-flow) + auto on the minor axis. Find the first free
    ///   minor-axis run at the locked major position.</item>
    ///   <item><b>Pass 3+4 — remaining</b>: items with definite
    ///   placement on the MINOR axis OR both-auto. They share the
    ///   auto-placement cursor (= cursor walks in the major axis,
    ///   growing implicit major tracks as needed).</item>
    /// </list>
    ///
    /// <para><b>Per PR-#103 review F6</b>: track growth that hits the
    /// per-axis implicit cap or global expanded cap drops the item
    /// (= leaves <c>Row/Col</c> at <c>-1</c>) with a diagnostic — the
    /// emission loop skips it.</para>
    ///
    /// <para><b>Cycle 6a known gap — atomic-to-row-span pagination</b>
    /// — see <c>docs/deferrals.md#grid-row-span-atomic-pagination-deferral</c>.
    /// A spanning item whose last row would land on a later page
    /// currently emits with its rectangle straddling the page break;
    /// cycle 6b ships the atomic-to-span rewind contract.</para></summary>
    private static GrowableOccupancy RunPlacementWithSpans(
        List<PlacedItem> placedItems,
        List<TrackSizingInfo> rowInfos,
        List<TrackSizingInfo> colInfos,
        int explicitRowCount,
        int explicitColCount,
        TrackList gridAutoRows,
        TrackList gridAutoColumns,
        GridAutoFlowValue gridAutoFlow,
        SizingContext ctx,
        CancellationToken cancellationToken)
    {
        var occupancy = new GrowableOccupancy(rowInfos.Count, colInfos.Count);
        var isRowFlow = gridAutoFlow == GridAutoFlowValue.Row;

        // Pass 1 — both definite. Mark rectangles + grow tracks.
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.RowSpec.Kind != PlacementKind.Definite
                || item.ColSpec.Kind != PlacementKind.Definite)
            {
                continue;
            }

            // Per PR-#103 review F2 — negative line numbers resolve
            // against the explicit grid (NOT the grown grid).
            var rowZero = ResolveDefiniteToZeroBased(
                item.RowSpec.Line, explicitRowCount);
            var colZero = ResolveDefiniteToZeroBased(
                item.ColSpec.Line, explicitColCount);
            if (rowZero < 0 || colZero < 0)
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }

            var rowSpan = Math.Max(1, item.RowSpec.Span);
            var colSpan = Math.Max(1, item.ColSpec.Span);

            // Grow tracks then mark — both can fail when caps trip.
            if (!GrowRowsIfNeeded(rowInfos, occupancy, rowZero + rowSpan,
                    explicitRowCount, gridAutoRows, ctx)
                || !GrowColumnsIfNeeded(colInfos, occupancy, colZero + colSpan,
                    explicitColCount, gridAutoColumns, ctx)
                || !occupancy.MarkRectangle(rowZero, colZero, rowSpan, colSpan))
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }

            item.Row = rowZero;
            item.Col = colZero;
            item.RowSpan = rowSpan;
            item.ColSpan = colSpan;
            placedItems[i] = item;
        }

        // Pass 2 — major-locked. Definite placement on the MAJOR
        // (auto-flow) axis + auto on the minor axis.
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.Row >= 0) continue;
            var majorSpec = isRowFlow ? item.RowSpec : item.ColSpec;
            var minorSpec = isRowFlow ? item.ColSpec : item.RowSpec;
            if (majorSpec.Kind != PlacementKind.Definite
                || minorSpec.Kind != PlacementKind.Auto)
            {
                continue;
            }

            var explicitMajor = isRowFlow ? explicitRowCount : explicitColCount;
            var majorZero = ResolveDefiniteToZeroBased(majorSpec.Line, explicitMajor);
            if (majorZero < 0)
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }
            var majorSpan = Math.Max(1, majorSpec.Span);
            var minorSpan = Math.Max(1, minorSpec.Span);

            // Grow major tracks to fit majorZero + majorSpan.
            var majorGrew = isRowFlow
                ? GrowRowsIfNeeded(rowInfos, occupancy,
                    majorZero + majorSpan, explicitRowCount, gridAutoRows, ctx)
                : GrowColumnsIfNeeded(colInfos, occupancy,
                    majorZero + majorSpan, explicitColCount, gridAutoColumns, ctx);
            if (!majorGrew)
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }

            // Find first minor-axis run of minorSpan free cells.
            var minorZero = FindFirstFreeMinorRun(
                occupancy, majorZero, majorSpan, minorSpan, isRowFlow);
            if (minorZero < 0)
            {
                // No room — start at the current minor extent and grow.
                minorZero = isRowFlow ? occupancy.ColCount : occupancy.RowCount;
                var minorGrew = isRowFlow
                    ? GrowColumnsIfNeeded(colInfos, occupancy,
                        minorZero + minorSpan, explicitColCount, gridAutoColumns, ctx)
                    : GrowRowsIfNeeded(rowInfos, occupancy,
                        minorZero + minorSpan, explicitRowCount, gridAutoRows, ctx);
                if (!minorGrew)
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }
            }

            var (rowZero, colZero) = isRowFlow
                ? (majorZero, minorZero)
                : (minorZero, majorZero);
            var (rowSpan, colSpan) = isRowFlow
                ? (majorSpan, minorSpan)
                : (minorSpan, majorSpan);
            if (!occupancy.MarkRectangle(rowZero, colZero, rowSpan, colSpan))
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            item.RowSpan = rowSpan;
            item.ColSpan = colSpan;
            placedItems[i] = item;
        }

        // Pass 3+4 — remaining items share the auto-placement cursor.
        // Includes minor-locked (definite minor axis + auto major)
        // and both-auto items, in source order. The cursor walks the
        // minor axis (= advances minor coord, wraps to next major).
        //
        // Per PR-#103 review F6 — items where BOTH axes are definite
        // were attempted in Pass 1. If they're still unplaced
        // (Row<0) it means a growth cap tripped + the item was
        // dropped intentionally. Skip them here so we don't silently
        // re-place a dropped item at a different spot.
        var cursorMajor = 0;
        var cursorMinor = 0;
        for (var i = 0; i < placedItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = placedItems[i];
            if (item.Row >= 0) continue;
            if (item.RowSpec.Kind == PlacementKind.Definite
                && item.ColSpec.Kind == PlacementKind.Definite)
            {
                continue;
            }

            var majorSpec = isRowFlow ? item.RowSpec : item.ColSpec;
            var minorSpec = isRowFlow ? item.ColSpec : item.RowSpec;
            var majorSpan = Math.Max(1, majorSpec.Span);
            var minorSpan = Math.Max(1, minorSpec.Span);

            int majorZero, minorZero;

            if (minorSpec.Kind == PlacementKind.Definite)
            {
                // Minor-locked (= definite-minor, auto-major). Per
                // §8.5 step 4.1 — cursor's minor position resets to
                // the item's minor-start; if that goes backward,
                // advance the major position by 1.
                var explicitMinor = isRowFlow ? explicitColCount : explicitRowCount;
                minorZero = ResolveDefiniteToZeroBased(minorSpec.Line, explicitMinor);
                if (minorZero < 0)
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }
                if (minorZero < cursorMinor) cursorMajor++;
                cursorMinor = minorZero;

                // Grow minor tracks to fit minorZero + minorSpan.
                var minorGrew = isRowFlow
                    ? GrowColumnsIfNeeded(colInfos, occupancy,
                        minorZero + minorSpan, explicitColCount, gridAutoColumns, ctx)
                    : GrowRowsIfNeeded(rowInfos, occupancy,
                        minorZero + minorSpan, explicitRowCount, gridAutoRows, ctx);
                if (!minorGrew)
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }

                // Walk major axis from cursorMajor looking for a free
                // (majorSpan × minorSpan) rectangle at this minor.
                majorZero = FindFreeMajorRunAtMinor(
                    occupancy, minorZero, minorSpan, majorSpan,
                    startMajor: cursorMajor, isRowFlow);
                if (majorZero < 0)
                {
                    // No room within current major extent — grow.
                    majorZero = Math.Max(
                        isRowFlow ? occupancy.RowCount : occupancy.ColCount,
                        cursorMajor);
                    var majorGrew = isRowFlow
                        ? GrowRowsIfNeeded(rowInfos, occupancy,
                            majorZero + majorSpan, explicitRowCount, gridAutoRows, ctx)
                        : GrowColumnsIfNeeded(colInfos, occupancy,
                            majorZero + majorSpan, explicitColCount, gridAutoColumns, ctx);
                    if (!majorGrew)
                    {
                        EmitImplicitTrackDiagnostic(ctx, item.Box);
                        continue;
                    }
                }
            }
            else if (majorSpec.Kind == PlacementKind.Auto)
            {
                // Both-auto. Walk minor axis from cursor; wrap when
                // overflowing the explicit-minor extent (= grow major
                // implicit tracks).
                var (foundMajor, foundMinor, ok) = FindNextSparseRectangleAxisAware(
                    occupancy, cursorMajor, cursorMinor,
                    majorSpan, minorSpan, isRowFlow,
                    rowInfos, colInfos, explicitRowCount, explicitColCount,
                    gridAutoRows, gridAutoColumns, ctx);
                if (!ok)
                {
                    EmitImplicitTrackDiagnostic(ctx, item.Box);
                    continue;
                }
                majorZero = foundMajor;
                minorZero = foundMinor;
            }
            else
            {
                // Should not reach: major-definite cases handled by
                // Pass 2 already (item.Row >= 0 would have skipped).
                continue;
            }

            var (rowZero, colZero) = isRowFlow
                ? (majorZero, minorZero)
                : (minorZero, majorZero);
            var (rowSpan, colSpan) = isRowFlow
                ? (majorSpan, minorSpan)
                : (minorSpan, majorSpan);
            if (!occupancy.MarkRectangle(rowZero, colZero, rowSpan, colSpan))
            {
                EmitImplicitTrackDiagnostic(ctx, item.Box);
                continue;
            }
            item.Row = rowZero;
            item.Col = colZero;
            item.RowSpan = rowSpan;
            item.ColSpan = colSpan;
            placedItems[i] = item;

            // Per PR-#103 review F5 — advance the shared cursor past
            // the placed rectangle. Minor-locked items share the
            // cursor with both-auto items per §8.5 step 4.
            cursorMajor = majorZero;
            cursorMinor = minorZero + minorSpan;
            var minorExtent = isRowFlow ? occupancy.ColCount : occupancy.RowCount;
            if (cursorMinor >= minorExtent)
            {
                cursorMinor = 0;
                cursorMajor = majorZero + majorSpan;
            }
        }

        return occupancy;
    }

    // =====================================================================
    //  Placement helpers
    // =====================================================================

    /// <summary>Per Phase 3 Task 18 cycle 6 + PR-#103 review F3 — sparse
    /// occupancy grid backed by a <see cref="HashSet{T}"/> of packed
    /// <c>(row, col)</c> keys. Storage is O(occupied cells) regardless
    /// of grid extent, defending against hostile CSS that requests
    /// far-out grid coordinates (e.g.,
    /// <c>grid-row-start: 50000; grid-column-start: 50000</c> would
    /// have allocated a 2.5-billion-cell dense bool matrix under the
    /// initial cycle-6a implementation).
    ///
    /// <para><b>Cell-count guard per PR-#103 review F3</b>:
    /// <see cref="MarkRectangle"/> rejects rectangles whose total cell
    /// count would push <c>_occupied.Count</c> past
    /// <see cref="MaxOccupiedCells"/>. The caller treats the
    /// <c>false</c> return as a placement failure (= drop + diagnostic),
    /// mirroring <see cref="AppendImplicitTracks"/>'s
    /// truncation contract.</para>
    ///
    /// <para><b>Track-count growth</b>: <see cref="GrowRows"/> /
    /// <see cref="GrowColumns"/> just update the
    /// <see cref="RowCount"/> / <see cref="ColCount"/> bounds — no
    /// allocation is needed since the storage is sparse.</para></summary>
    internal sealed class GrowableOccupancy
    {
        private readonly HashSet<long> _occupied = new();
        public int RowCount { get; private set; }
        public int ColCount { get; private set; }

        /// <summary>Cap on total occupied cells per
        /// <see cref="GridSizing.Resolve"/> call. With the
        /// <see cref="MaxImplicitTracksPerAxis"/> cap also in force the
        /// theoretical worst case for a fully-occupied implicit grid is
        /// ~1M cells (1024 × 1024); this cap matches that ceiling so
        /// the occupancy stays well-bounded.</summary>
        public const int MaxOccupiedCells = 1_048_576;

        public GrowableOccupancy(int initialRows, int initialCols)
        {
            RowCount = initialRows;
            ColCount = initialCols;
        }

        public bool IsOccupied(int row, int col)
        {
            if (row < 0 || col < 0) return false;
            return _occupied.Contains(CellKey(row, col));
        }

        /// <summary>Marks every cell in
        /// <c>[rowStart, rowStart+rowSpan) × [colStart, colStart+colSpan)</c>
        /// as occupied. Returns <see langword="false"/> when the
        /// rectangle's cell count would exceed
        /// <see cref="MaxOccupiedCells"/> — the caller treats this as
        /// placement failure + emits a diagnostic.</summary>
        public bool MarkRectangle(int rowStart, int colStart, int rowSpan, int colSpan)
        {
            if (rowSpan <= 0 || colSpan <= 0) return true;
            // Pre-check the cell-count delta. Spans larger than the
            // available budget short-circuit before any HashSet inserts.
            var newCells = (long)rowSpan * colSpan;
            if (_occupied.Count + newCells > MaxOccupiedCells) return false;
            for (var r = rowStart; r < rowStart + rowSpan; r++)
            {
                for (var c = colStart; c < colStart + colSpan; c++)
                {
                    _occupied.Add(CellKey(r, c));
                }
            }
            return true;
        }

        public void GrowRows(int newRowCount)
        {
            if (newRowCount > RowCount) RowCount = newRowCount;
        }

        public void GrowColumns(int newColCount)
        {
            if (newColCount > ColCount) ColCount = newColCount;
        }

        private static long CellKey(int row, int col)
            => ((long)row << 32) | (uint)col;
    }

    /// <summary>Per PR-#103 review F3 — per-axis cap on implicit track
    /// growth. Significantly tighter than
    /// <see cref="TrackList.MaxExpandedTrackCount"/> because implicit
    /// growth is driven by per-item coordinates (a single
    /// <c>grid-column-start: 50000</c> can request 50000 tracks),
    /// while the larger cap exists to defend
    /// <c>repeat(N, ...)</c> parse-time expansion.</summary>
    private const int MaxImplicitTracksPerAxis = 1024;

    /// <summary>Resolves a 1-based grid line number (possibly negative
    /// per §8.3 for end-relative addressing) to a 0-based track index.
    /// Returns -1 for invalid input (= line 0). Negative inputs resolve
    /// against the explicit grid: <c>-1</c> = line <c>trackCount+1</c>,
    /// <c>-2</c> = line <c>trackCount</c>, etc. The result may equal
    /// <c>trackCount</c> (= an implicit-row index past the explicit
    /// grid; cycle 6's placement service grows the grid to fit).
    /// Negative line numbers that resolve to less than line 1 return a
    /// negative track index — the caller drops the item with a
    /// diagnostic since cycle 6a doesn't generate pre-implicit tracks.</summary>
    private static int ResolveDefiniteToZeroBased(int lineNumber, int trackCount)
    {
        if (lineNumber == 0) return -1;
        if (lineNumber > 0) return lineNumber - 1;
        return trackCount + 1 + lineNumber;
    }

    private static bool IsRectangleFree(
        GrowableOccupancy occupancy, int rowStart, int colStart,
        int rowSpan, int colSpan)
    {
        for (var r = rowStart; r < rowStart + rowSpan; r++)
        {
            for (var c = colStart; c < colStart + colSpan; c++)
            {
                if (occupancy.IsOccupied(r, c)) return false;
            }
        }
        return true;
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 + PR-#103 review F4 — find
    /// the first minor-axis index such that the rectangle anchored at
    /// (majorZero, M) spanning (majorSpan, minorSpan) is fully free.
    /// Returns -1 when no run fits within the current minor extent.
    /// In row-flow major = row, minor = column; in column-flow
    /// major = column, minor = row.</summary>
    private static int FindFirstFreeMinorRun(
        GrowableOccupancy occupancy, int majorZero, int majorSpan,
        int minorSpan, bool isRowFlow)
    {
        var minorExtent = isRowFlow ? occupancy.ColCount : occupancy.RowCount;
        for (var m = 0; m + minorSpan <= minorExtent; m++)
        {
            var (rowStart, colStart) = isRowFlow ? (majorZero, m) : (m, majorZero);
            var (rowSpan, colSpan) = isRowFlow
                ? (majorSpan, minorSpan)
                : (minorSpan, majorSpan);
            if (IsRectangleFree(occupancy, rowStart, colStart, rowSpan, colSpan))
            {
                return m;
            }
        }
        return -1;
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 + PR-#103 review F4 — walks
    /// the major axis from <paramref name="startMajor"/> looking for
    /// a free rectangle at the locked minor position. Returns -1 when
    /// no run fits within the current major extent.</summary>
    private static int FindFreeMajorRunAtMinor(
        GrowableOccupancy occupancy, int minorZero, int minorSpan,
        int majorSpan, int startMajor, bool isRowFlow)
    {
        var majorExtent = isRowFlow ? occupancy.RowCount : occupancy.ColCount;
        for (var M = startMajor; M + majorSpan <= majorExtent; M++)
        {
            var (rowStart, colStart) = isRowFlow ? (M, minorZero) : (minorZero, M);
            var (rowSpan, colSpan) = isRowFlow
                ? (majorSpan, minorSpan)
                : (minorSpan, majorSpan);
            if (IsRectangleFree(occupancy, rowStart, colStart, rowSpan, colSpan))
            {
                return M;
            }
        }
        return -1;
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 + PR-#103 review F4 + F6 —
    /// axis-aware sparse-cursor walk for the both-auto placement case.
    /// The cursor walks the MINOR axis (= advances minor coord, wraps
    /// to next major when minor overflows the explicit-minor extent).
    /// Implicit MAJOR tracks are added on-demand; the minor axis stays
    /// bounded by the explicit grid + any prior definite-minor
    /// extension.
    ///
    /// <para>Returns <c>(major, minor, ok=true)</c> on placement;
    /// <c>(_, _, ok=false)</c> when implicit-track growth would exceed
    /// the per-axis cap (= caller drops the item with a diagnostic).</para></summary>
    private static (int Major, int Minor, bool Ok) FindNextSparseRectangleAxisAware(
        GrowableOccupancy occupancy, int cursorMajor, int cursorMinor,
        int majorSpan, int minorSpan, bool isRowFlow,
        List<TrackSizingInfo> rowInfos, List<TrackSizingInfo> colInfos,
        int explicitRowCount, int explicitColCount,
        TrackList gridAutoRows, TrackList gridAutoColumns,
        SizingContext ctx)
    {
        var minorExtent = isRowFlow ? occupancy.ColCount : occupancy.RowCount;
        // Clamp minorSpan to the minor extent (= the spec's "minor axis
        // is bounded by explicit grid in row mode" rule; matches the
        // pre-refactor cycle-6a clamp).
        if (minorSpan > minorExtent && minorExtent > 0) minorSpan = minorExtent;

        var M = cursorMajor;
        var m = cursorMinor;
        var maxAdvance = TrackList.MaxExpandedTrackCount;
        for (var step = 0; step < maxAdvance; step++)
        {
            if (m + minorSpan > minorExtent)
            {
                // Wrap to next major. Implicit minor tracks are NOT
                // added by both-auto placement (= the explicit-axis
                // contract from §8.5 step 4).
                m = 0;
                M++;
                continue;
            }
            // Ensure enough major tracks for majorSpan starting at M.
            var majorExtent = isRowFlow ? occupancy.RowCount : occupancy.ColCount;
            if (M + majorSpan > majorExtent)
            {
                var grew = isRowFlow
                    ? GrowRowsIfNeeded(rowInfos, occupancy,
                        M + majorSpan, explicitRowCount, gridAutoRows, ctx)
                    : GrowColumnsIfNeeded(colInfos, occupancy,
                        M + majorSpan, explicitColCount, gridAutoColumns, ctx);
                if (!grew) return (0, 0, false);
            }
            var (rowStart, colStart) = isRowFlow ? (M, m) : (m, M);
            var (rowSpan, colSpan) = isRowFlow
                ? (majorSpan, minorSpan)
                : (minorSpan, majorSpan);
            if (IsRectangleFree(occupancy, rowStart, colStart, rowSpan, colSpan))
            {
                return (M, m, true);
            }
            m++;
        }
        return (0, 0, false);
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 + CSS Grid §7.4 + PR-#103
    /// review F6 — extend <paramref name="rowInfos"/> with implicit
    /// row tracks sized per <paramref name="gridAutoRows"/>, cycling
    /// the pattern as needed to reach <paramref name="requiredCount"/>
    /// total tracks. Returns <see langword="false"/> when the request
    /// would exceed
    /// <see cref="MaxImplicitTracksPerAxis"/> beyond the explicit
    /// extent or <see cref="TrackList.MaxExpandedTrackCount"/> — caller
    /// drops the placement.</summary>
    private static bool GrowRowsIfNeeded(
        List<TrackSizingInfo> rowInfos, GrowableOccupancy occupancy,
        int requiredCount, int explicitRowCount,
        TrackList gridAutoRows, SizingContext ctx)
    {
        if (requiredCount <= rowInfos.Count) return true;
        var ok = AppendImplicitTracks(rowInfos,
            requiredCount - rowInfos.Count,
            gridAutoRows, explicitRowCount, ctx);
        occupancy.GrowRows(rowInfos.Count);
        return ok && rowInfos.Count >= requiredCount;
    }

    private static bool GrowColumnsIfNeeded(
        List<TrackSizingInfo> colInfos, GrowableOccupancy occupancy,
        int requiredCount, int explicitColCount,
        TrackList gridAutoColumns, SizingContext ctx)
    {
        if (requiredCount <= colInfos.Count) return true;
        var ok = AppendImplicitTracks(colInfos,
            requiredCount - colInfos.Count,
            gridAutoColumns, explicitColCount, ctx);
        occupancy.GrowColumns(colInfos.Count);
        return ok && colInfos.Count >= requiredCount;
    }

    /// <summary>Per Phase 3 Task 18 cycle 6 + PR-#103 review F3 + F6 —
    /// append up to <paramref name="count"/> implicit tracks to
    /// <paramref name="infos"/>, cycling through
    /// <paramref name="pattern"/> entries. Returns
    /// <see langword="true"/> when the full <paramref name="count"/>
    /// was appended; <see langword="false"/> when either the per-axis
    /// implicit cap (<paramref name="explicitCount"/> +
    /// <see cref="MaxImplicitTracksPerAxis"/>) or the global expanded
    /// cap stopped the loop short.</summary>
    private static bool AppendImplicitTracks(
        List<TrackSizingInfo> infos, int count, TrackList pattern,
        int explicitCount, SizingContext ctx)
    {
        if (count <= 0) return true;
        // Collect just the track entries from the pattern (ignore any
        // named-line items the parser may have preserved).
        var entries = new List<TrackEntry>();
        foreach (var item in pattern.Items)
        {
            if (item is TrackListEntry e) entries.Add(e.Entry);
        }
        // Defensive: if the pattern is empty (= reader returned the
        // none / wrong-typed fallback), use a single Auto track.
        if (entries.Count == 0)
        {
            entries.Add(TrackEntry.ForAuto());
        }
        // Per-axis cap = explicit count + MaxImplicitTracksPerAxis.
        var perAxisCap = explicitCount + MaxImplicitTracksPerAxis;
        for (var i = 0; i < count; i++)
        {
            if (infos.Count >= perAxisCap
                || infos.Count >= TrackList.MaxExpandedTrackCount)
            {
                EmitTruncationDiagnostic(ctx);
                return false;
            }
            var entry = entries[i % entries.Count];
            infos.Add(ClassifyEntry(entry, ctx));
        }
        return true;
    }

    // =====================================================================
    //  Placement spec reading
    // =====================================================================

    /// <summary>Per Phase 3 Task 17 cycle 1 + Task 18 cycle 6 — read
    /// the four grid-line longhands for one axis and reduce them to a
    /// single <see cref="PlacementSpec"/> (kind + start-line +
    /// span). Cycle 6 added span handling per CSS Grid L1 §8.3.
    ///
    /// <para><b>Decision matrix</b> (named-line / negative-line edge
    /// cases route to <see cref="EmitPlacementApproximatedAndFallToAuto"/>
    /// — cycle 7 wires named lines):</para>
    /// <list type="bullet">
    ///   <item>(auto, auto) → Auto / span 1</item>
    ///   <item>(span N, *) → Auto / span N (auto-placed N-span)</item>
    ///   <item>(integer, auto) → Definite / span 1</item>
    ///   <item>(integer, integer) → Definite / span = end - start (with
    ///   §8.3 swap when end ≤ start)</item>
    ///   <item>(integer, span N) → Definite / span N</item>
    ///   <item>(auto, span N) → Auto / span N</item>
    ///   <item>(auto, integer) → Definite at (end - 1) / span 1 (cycle
    ///   6a simplification — the "auto-positioned but ending at line N"
    ///   case is rare in practice + the spec's reverse-auto-placement
    ///   algorithm is cycle 7 scope)</item>
    /// </list></summary>
    /// <summary>Per Phase 3 Task 18 cycle 7b + post-PR-#106 review F1
    /// + F2 + F6 — build a per-axis named → occurrence-list lookup that
    /// <see cref="ReadPlacement"/> consults when a placement uses a
    /// <c>&lt;custom-ident&gt;</c>.
    ///
    /// <para><b>Sources combined</b> per CSS Grid L1 §8.3 + §8.4:</para>
    /// <list type="number">
    ///   <item><b>Author-declared named lines</b>: walk the expanded
    ///   <see cref="TrackList"/> (= same expansion service the sizing
    ///   pass uses, so repeat caps + cancellation match) for
    ///   <see cref="TrackListNamedLine"/> items. Each name occurrence
    ///   appends to its list.</item>
    ///   <item><b>Implicit area-derived lines</b>: for each named area
    ///   in <paramref name="areas"/>, append the area's start line to
    ///   the <c>&lt;name&gt;-start</c> list and the end line to
    ///   <c>&lt;name&gt;-end</c>.</item>
    /// </list>
    ///
    /// <para><b>Per PR-#106 review F1 #2</b>: stored as an occurrence
    /// LIST instead of a single int. Multiple `[foo]` declarations or
    /// authored-plus-implicit collisions all coexist; resolution picks
    /// the first occurrence (= lowest line number after sorting).</para>
    ///
    /// <para><b>Per PR-#106 review F2 #6</b>: shares the container-
    /// aware <c>ExpandTrackList</c> overload with the sizing pass so
    /// repeat expansion respects the same
    /// <see cref="TrackList.MaxExpandedTrackCount"/> cap +
    /// <see cref="CancellationToken"/> contract. Pre-fix this code
    /// duplicated the repeat handler without cancellation checks.</para>
    ///
    /// <para><b>Still deferred to cycle 7c+</b>: <c>repeat(auto-fill,
    /// …)</c> / <c>repeat(auto-fit, …)</c> expansion is layout-time
    /// dynamic (= container-size dependent); named lines inside those
    /// repeats remain unresolved. Tracked under
    /// `grid-implicit-named-area-and-occurrence-syntax-deferral`.</para></summary>
    private static Dictionary<string, List<int>> BuildNamedLineMap(
        TrackList trackList, GridTemplateAreas areas, bool isRow,
        double containerExtent, bool isAxisIndefinite,
        SizingContext ctx, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        // Walk authored items via the shared container-aware expansion
        // (= same auto-fill / auto-fit iteration count as sizing, so
        // names inside auto-repeats land at line numbers matching the
        // actual track positions).
        var expanded = ExpandTrackList(
            trackList, ctx, cancellationToken,
            containerExtent, isAxisIndefinite);
        int currentLine = 1;
        foreach (var item in expanded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item)
            {
                case TrackListNamedLine namedLine:
                    AddLineOccurrence(map, namedLine.Name, currentLine);
                    break;
                case TrackListEntry:
                    currentLine++;
                    break;
                // TrackListRepeat with auto-fill/auto-fit stays as-is;
                // ExpandTrackList passes them through. Names inside
                // those repeats remain unresolved (= cycle 7c scope).
            }
        }

        // Implicit `<area>-start` / `<area>-end` lines per §8.4.
        foreach (var (name, rect) in areas.NameToRect)
        {
            var startLine = isRow ? rect.RowStart : rect.ColumnStart;
            var endLine = isRow ? rect.RowEnd : rect.ColumnEnd;
            AddLineOccurrence(map, $"{name}-start", startLine);
            AddLineOccurrence(map, $"{name}-end", endLine);
        }

        // Sort each occurrence list ascending so the FIRST occurrence
        // (= map[name][0]) is the lowest-numbered line per spec §8.3.
        foreach (var list in map.Values)
        {
            list.Sort();
        }

        return map;
    }

    private static void AddLineOccurrence(
        Dictionary<string, List<int>> map, string name, int line)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = new List<int>(1);
            map[name] = list;
        }
        list.Add(line);
    }

    /// <summary>Per Phase 3 Task 18 cycle 7b + post-PR-#106 review F1
    /// #3 — resolve a <c>&lt;custom-ident&gt;</c> reference for a
    /// START longhand per CSS Grid L1 §8.3. Tries
    /// <c>&lt;ident&gt;-start</c> first (= catches both authored
    /// <c>[foo-start]</c> lines AND implicit <c>foo-start</c> lines
    /// auto-generated from a named area <c>foo</c>), then falls back
    /// to bare <c>&lt;ident&gt;</c>. First occurrence wins (= lowest
    /// line number).</summary>
    private static int? ResolveStartLineFromIdent(
        string ident, Dictionary<string, List<int>> namedLines)
    {
        if (namedLines.TryGetValue(ident + "-start", out var compoundLines)
            && compoundLines.Count > 0)
        {
            return compoundLines[0];
        }
        if (namedLines.TryGetValue(ident, out var bareLines)
            && bareLines.Count > 0)
        {
            return bareLines[0];
        }
        return null;
    }

    /// <summary>Per Phase 3 Task 18 cycle 7b + post-PR-#106 review F1
    /// #3 — mirror of <see cref="ResolveStartLineFromIdent"/> for END
    /// longhands. Tries <c>&lt;ident&gt;-end</c> first, then bare
    /// <c>&lt;ident&gt;</c>.</summary>
    private static int? ResolveEndLineFromIdent(
        string ident, Dictionary<string, List<int>> namedLines)
    {
        if (namedLines.TryGetValue(ident + "-end", out var compoundLines)
            && compoundLines.Count > 0)
        {
            return compoundLines[0];
        }
        if (namedLines.TryGetValue(ident, out var bareLines)
            && bareLines.Count > 0)
        {
            return bareLines[0];
        }
        return null;
    }

    private static PlacementSpec ReadPlacement(
        ComputedStyle style, bool isRow, GridTemplateAreas areas,
        Dictionary<string, List<int>> namedLines, SizingContext ctx)
    {
        var start = isRow ? style.ReadGridRowStart() : style.ReadGridColumnStart();
        var end = isRow ? style.ReadGridRowEnd() : style.ReadGridColumnEnd();

        // (1) start: span N (= auto-positioned N-span)
        if (start.Kind == GridLineKind.Span)
        {
            if (start.NamedLine is not null)
            {
                return EmitPlacementApproximatedAndFallToAuto(ctx,
                    "`span <custom-ident>` syntax (see grid-implicit-"
                    + "named-area-and-occurrence-syntax-deferral)");
            }
            // Per §8.3.1 — `span 0` normalizes to `span 1`.
            var span = Math.Max(1, start.LineNumber);
            return new PlacementSpec(PlacementKind.Auto, 0, span);
        }

        // (2) start: NamedLine (= bare custom-ident). Per CSS Grid L1
        // §8.3 + post-PR-#106 review F1 #3 — try `<ident>-start` first
        // (= matches author's `[foo-start]` line OR implicit foo-start
        // line auto-generated from a named area `foo`), then bare
        // `<ident>` (= author's `[foo]` line). The cycle-7a area-
        // direct lookup is REDUNDANT here because the named-line map
        // already contains the implicit `<area>-start` / `<area>-end`
        // lines for every entry in `grid-template-areas`.
        if (start.Kind == GridLineKind.NamedLine && start.NamedLine is not null)
        {
            var ident = start.NamedLine;
            var resolvedStartLine = ResolveStartLineFromIdent(ident, namedLines);
            if (resolvedStartLine is int startLineNum)
            {
                return CombineLineStartWithEnd(
                    startLineNum, end, isRow, namedLines, ctx);
            }
            return EmitPlacementApproximatedAndFallToAuto(ctx,
                $"<custom-ident> '{ident}' in grid-*-start does not "
                + "match any `<ident>-start` line or bare `<ident>` "
                + "line (see grid-implicit-named-area-and-occurrence-"
                + "syntax-deferral for reverse-implicit-area + "
                + "occurrence forms)");
        }

        // (3) start: LineNumber + NamedLine qualifier (= `foo 2`).
        // Per PR-#106 review F2 #4 — occurrence-aware resolution is
        // tracked in grid-implicit-named-area-and-occurrence-syntax-
        // deferral. Falls back to auto with a deferral-tagged
        // diagnostic so authors see the gap.
        if (start.Kind == GridLineKind.LineNumber && start.NamedLine is not null)
        {
            return EmitPlacementApproximatedAndFallToAuto(ctx,
                "`<integer> <custom-ident>` occurrence syntax (see "
                + "grid-implicit-named-area-and-occurrence-syntax-deferral)");
        }

        // (3) start: auto — let end drive
        if (start.Kind == GridLineKind.Auto)
        {
            if (end.Kind == GridLineKind.Auto)
            {
                return new PlacementSpec(PlacementKind.Auto, 0, 1);
            }
            if (end.Kind == GridLineKind.Span)
            {
                if (end.NamedLine is not null)
                {
                    return EmitPlacementApproximatedAndFallToAuto(ctx,
                        "`span <custom-ident>` syntax in grid-*-end "
                        + "(see grid-implicit-named-area-and-occurrence-"
                        + "syntax-deferral)");
                }
                var span = Math.Max(1, end.LineNumber);
                return new PlacementSpec(PlacementKind.Auto, 0, span);
            }
            if (end.Kind == GridLineKind.NamedLine && end.NamedLine is not null)
            {
                // Per CSS Grid L1 §8.3 + PR-#106 review F1 #3 — try
                // <ident>-end first (= catches authored foo-end OR
                // implicit foo-end from area `foo`), then bare <ident>.
                var resolvedEndLine = ResolveEndLineFromIdent(
                    end.NamedLine, namedLines);
                if (resolvedEndLine is int endLineNum)
                {
                    // auto-start + definite-end uses the cycle-6a
                    // simplification (single cell at end - 1); the
                    // spec's reverse-auto-placement search is tracked
                    // in grid-reverse-auto-placement-deferral.
                    EmitPlacementApproximatedDiagnostic(ctx,
                        $"auto-start with `<custom-ident>` end '{end.NamedLine}' "
                        + "uses single-cell placement at end-1 (= "
                        + "grid-reverse-auto-placement-deferral simplification)");
                    return new PlacementSpec(
                        PlacementKind.Definite, endLineNum - 1, 1);
                }
                return EmitPlacementApproximatedAndFallToAuto(ctx,
                    $"<custom-ident> '{end.NamedLine}' in grid-*-end does "
                    + "not match any `<ident>-end` or bare `<ident>` line "
                    + "(see grid-implicit-named-area-and-occurrence-"
                    + "syntax-deferral)");
            }
            if (end.Kind == GridLineKind.LineNumber
                && end.NamedLine is null
                && end.LineNumber > 0)
            {
                // Per PR-#103 review F7 — cycle 6a's auto-start +
                // definite-end uses a SIMPLIFICATION: single cell at
                // (end - 1). The spec's full reverse-auto-placement
                // searches backward from `end` for an open slot of
                // the right span; that's tracked in
                // `grid-reverse-auto-placement-deferral`. Emit the
                // diagnostic so authors see they're in approximation
                // territory; preserve the simplified placement so
                // common single-cell `grid-row: auto / 3` cases still
                // render at a sensible position.
                EmitPlacementApproximatedDiagnostic(ctx,
                    "auto-start with definite-end uses cycle-6a "
                    + "simplification (single cell at end-1) — full "
                    + "reverse-auto-placement search is deferred "
                    + "(see grid-reverse-auto-placement-deferral)");
                return new PlacementSpec(
                    PlacementKind.Definite, end.LineNumber - 1, 1);
            }
            return EmitPlacementApproximatedAndFallToAuto(ctx,
                $"grid-*-start: auto / grid-*-end: {end.Kind} "
                + "(negative-line / named-line in end — cycle-7 scope)");
        }

        // (4) start: integer line number (no named ident)
        // (cases where start.Kind == LineNumber + NamedLine == null)
        var startLine = start.LineNumber;

        if (end.Kind == GridLineKind.Auto)
        {
            return new PlacementSpec(PlacementKind.Definite, startLine, 1);
        }

        if (end.Kind == GridLineKind.Span)
        {
            if (end.NamedLine is not null)
            {
                EmitPlacementApproximatedAndFallToAuto(ctx,
                    "span <named-line> in grid-*-end — using start with span 1");
                return new PlacementSpec(PlacementKind.Definite, startLine, 1);
            }
            var span = Math.Max(1, end.LineNumber);
            return new PlacementSpec(PlacementKind.Definite, startLine, span);
        }

        if (end.Kind == GridLineKind.LineNumber && end.NamedLine is null)
        {
            // Both positive: span = end - start (swap if end ≤ start per §8.3).
            if (startLine > 0 && end.LineNumber > 0)
            {
                if (end.LineNumber == startLine)
                {
                    // Zero-span via identical lines collapses to single
                    // cell (per §8.3 a zero-span is equivalent to span 1
                    // after normalization).
                    return new PlacementSpec(PlacementKind.Definite, startLine, 1);
                }
                if (end.LineNumber < startLine)
                {
                    var swapStart = end.LineNumber;
                    var swapEnd = startLine;
                    return new PlacementSpec(
                        PlacementKind.Definite, swapStart, swapEnd - swapStart);
                }
                return new PlacementSpec(
                    PlacementKind.Definite, startLine, end.LineNumber - startLine);
            }
            // Negative-line interactions are cycle-7 scope (need the
            // final grid bounds to resolve).
            return EmitPlacementApproximatedAndFallToAuto(ctx,
                "negative line numbers in placement (cycle-7 scope)");
        }

        // end is named — try <ident>-end first, then bare <ident>.
        if (end.Kind == GridLineKind.NamedLine && end.NamedLine is not null)
        {
            var resolvedEnd = ResolveEndLineFromIdent(end.NamedLine, namedLines);
            if (resolvedEnd is int endLineNum)
            {
                return SpanBetweenLines(startLine, endLineNum);
            }
        }
        EmitPlacementApproximatedAndFallToAuto(ctx,
            $"<custom-ident> '{end.NamedLine}' in grid-*-end does not "
            + "resolve — using start line with span 1");
        return new PlacementSpec(PlacementKind.Definite, startLine, 1);
    }

    /// <summary>Per Phase 3 Task 18 cycle 7b + post-PR-#106 review F1
    /// — combine a resolved start line (from a custom-ident lookup)
    /// with the end-clause to build a Definite placement spec. The
    /// end may be auto (= single cell), span N, an integer line, or
    /// another custom-ident (= recursive line lookup).</summary>
    private static PlacementSpec CombineLineStartWithEnd(
        int startLine, GridLineValue end, bool isRow,
        Dictionary<string, List<int>> namedLines, SizingContext ctx)
    {
        if (end.Kind == GridLineKind.Auto)
        {
            return new PlacementSpec(PlacementKind.Definite, startLine, 1);
        }
        if (end.Kind == GridLineKind.Span)
        {
            if (end.NamedLine is not null)
            {
                EmitPlacementApproximatedAndFallToAuto(ctx,
                    "span <named-line> in grid-*-end — using start with span 1");
                return new PlacementSpec(PlacementKind.Definite, startLine, 1);
            }
            var span = Math.Max(1, end.LineNumber);
            return new PlacementSpec(PlacementKind.Definite, startLine, span);
        }
        if (end.Kind == GridLineKind.LineNumber && end.NamedLine is null
            && end.LineNumber > 0)
        {
            return SpanBetweenLines(startLine, end.LineNumber);
        }
        if (end.Kind == GridLineKind.NamedLine && end.NamedLine is not null)
        {
            // Per §8.3 — try <ident>-end first, then bare <ident>.
            var resolvedEnd = ResolveEndLineFromIdent(end.NamedLine, namedLines);
            if (resolvedEnd is int endLineNum)
            {
                return SpanBetweenLines(startLine, endLineNum);
            }
            EmitPlacementApproximatedDiagnostic(ctx,
                $"<custom-ident> '{end.NamedLine}' in grid-*-end does not "
                + "resolve — using single-cell placement at start");
            return new PlacementSpec(PlacementKind.Definite, startLine, 1);
        }
        return new PlacementSpec(PlacementKind.Definite, startLine, 1);
    }

    /// <summary>Build a Definite placement spec from two line numbers,
    /// applying the §8.3 swap when end ≤ start.</summary>
    private static PlacementSpec SpanBetweenLines(int startLine, int endLine)
    {
        if (endLine == startLine)
        {
            return new PlacementSpec(PlacementKind.Definite, startLine, 1);
        }
        if (endLine < startLine)
        {
            return new PlacementSpec(
                PlacementKind.Definite, endLine, startLine - endLine);
        }
        return new PlacementSpec(
            PlacementKind.Definite, startLine, endLine - startLine);
    }

    /// <summary>Per PR-#106 review F3 #8 — message no longer claims
    /// "cycle 7 will ship named lines + areas + dense" since cycles
    /// 7a (areas) and 7b (named lines) have already shipped. The
    /// caller passes the specific unsupported syntax + a deferral
    /// pointer for the reader to find the gap.</summary>
    private static PlacementSpec EmitPlacementApproximatedAndFallToAuto(
        SizingContext ctx, string reason)
    {
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
            Message: $"Grid item placement falls back to auto: {reason}.",
            Severity: PaginateDiagnosticSeverity.Warning));
        return new PlacementSpec(PlacementKind.Auto, 0, 1);
    }

    /// <summary>Per PR-#103 review F7 — emit a placement-approximated
    /// diagnostic without forcing the caller to a specific
    /// <see cref="PlacementSpec"/>. Used when the placement is
    /// preserved (= the simplification still produces a sensible
    /// result) but the contract diverges from the spec.</summary>
    private static void EmitPlacementApproximatedDiagnostic(
        SizingContext ctx, string reason)
    {
        ctx.Emit(new PaginateDiagnostic(
            Code: PaginateDiagnosticCodes.LayoutGridPlacementApproximated001,
            Message: $"Grid item placement is approximated: {reason}.",
            Severity: PaginateDiagnosticSeverity.Warning));
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

/// <summary>Per Phase 3 Task 17 cycle 1 + Task 18 cycle 6 (span +
/// implicit tracks) — a single grid item's placement state. Cycle 3
/// review hardening moved this from <see cref="GridLayouter"/> to
/// <see cref="GridSizing"/> so the shared sizing service can return
/// placement info to consumers. Cycle 6 added the per-axis span
/// fields so the layouter can emit fragments spanning the rectangle
/// <c>[Row, Row+RowSpan) × [Col, Col+ColSpan)</c>.</summary>
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
    /// <summary>Per Phase 3 Task 18 cycle 6 — number of rows occupied
    /// (= 1 for single-cell items, &gt; 1 for span items). The item
    /// occupies <c>[Row, Row+RowSpan)</c>.</summary>
    public int RowSpan;
    /// <summary>Per Phase 3 Task 18 cycle 6 — number of columns
    /// occupied. The item occupies <c>[Col, Col+ColSpan)</c>.</summary>
    public int ColSpan;
}

/// <summary>Per Phase 3 Task 17 cycle 1 + Task 18 cycle 6 — a
/// classified placement spec. <see cref="Span"/> is the multi-cell
/// extent: 1 for single-cell items, &gt; 1 for spanning. Cycle 7 will
/// add a separate named-line placement kind.
///
/// <para><b>Kind interpretation</b>:</para>
/// <list type="bullet">
///   <item><see cref="PlacementKind.Auto"/>: position is auto-determined
///   by the sparse-cursor pass; <see cref="Span"/> may still be ≥ 1
///   (= auto position + span N, e.g., <c>grid-row: span 2</c>).</item>
///   <item><see cref="PlacementKind.Definite"/>: <see cref="Line"/> is
///   the 1-based start line; <see cref="Span"/> is the extent.</item>
/// </list></summary>
internal readonly record struct PlacementSpec(
    PlacementKind Kind, int Line, int Span = 1);

internal enum PlacementKind : byte
{
    Auto = 0,
    Definite = 1,
}
