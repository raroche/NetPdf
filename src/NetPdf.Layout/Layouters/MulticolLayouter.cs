// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 14 cycles 1-2 — multi-column layout per
/// CSS Multi-column L1 (<see href="https://www.w3.org/TR/css-multicol-1/"/>).
/// A block container with a positive integer <c>column-count: N</c>
/// becomes a multicol container; its in-flow content flows through
/// N parallel columns (sub-fragmentainers), each the same block-axis
/// extent as the container's content area + an inline-axis extent of
/// <c>(containerContentInlineSize - (N-1) × columnGap) / N</c>.
///
/// <para><b>Cycle 1 contract (baseline).</b> The layouter:</para>
/// <list type="bullet">
///   <item>Reads <c>column-count</c> + <c>column-gap</c> from the
///   container's <see cref="ComputedStyle"/>. <c>column-count: 1</c>
///   is NOT a multicol container — the BlockLayouter dispatch path
///   skips MulticolLayouter for any container with
///   <c>ReadColumnCount() &lt; 2</c>.</item>
///   <item>For each of the N columns (left-to-right), constructs a
///   nested <see cref="BlockLayouter"/> with the SAME root box (the
///   multicol container) + a per-column sub-fragmentainer
///   (<see cref="FragmentainerContext"/> with content-inline-size =
///   per-column inline size + block-size = container's content block
///   extent). The first column starts emission at child 0; each
///   subsequent column resumes from the
///   <see cref="BlockContinuation"/> the previous column returned (=
///   "where the previous column left off").</item>
///   <item>Wraps the nested layouter's sink in a
///   <see cref="ColumnFragmentSink"/> that translates each emitted
///   <see cref="BoxFragment.InlineOffset"/> by
///   <c>columnInlineOffset</c> + <c>columnIndex × (perColumnInlineSize
///   + columnGap)</c>. The block-axis offset is also translated by
///   <c>columnBlockOffset</c> (the multicol container's content-box
///   block-start in the outer fragmentainer's coordinate space).</item>
///   <item>Does NOT emit the outer multicol container's fragment —
///   the dispatching <see cref="BlockLayouter"/> emits that fragment
///   first + then invokes MulticolLayouter to emit the column
///   content INSIDE the container's content-box. (Pattern mirrors
///   how <see cref="TableLayouter"/> relies on the caller to emit
///   the table wrapper fragment first.)</item>
/// </list>
///
/// <para><b>Cycle 2 — multi-page multicol via
/// <see cref="MulticolContinuation"/>.</b> When in-flow content
/// exceeds N × per-column block-size on the current page, the layouter
/// returns <see cref="LayoutAttemptOutcome.PageComplete"/> with a
/// <see cref="MulticolContinuation"/> capturing the next-child index
/// + the LAST-column-on-this-page's nested
/// <see cref="BlockContinuation"/> (if any) as
/// <c>PerChildLayouterState</c>. The dispatching
/// <see cref="BlockLayouter"/> wraps this in a
/// <see cref="BlockContinuation"/> whose <c>LayouterState</c> carries
/// the <see cref="MulticolContinuation"/>; on the next page the
/// dispatch re-constructs <c>MulticolLayouter</c> with the carried
/// continuation, and the FIRST column on the resumed page picks up
/// content emission at the captured child (with the captured nested
/// state, if any). This parallels Task 13 cycle 1's row-pagination
/// pattern for tables.</para>
///
/// <para><b>Cycle 2 contract — when the diagnostic still fires.</b>
/// The original <c>LAYOUT-MULTICOL-FORCED-OVERFLOW-001</c> code is
/// retained but with NARROWER semantics: a clean multi-page split is
/// no longer an error (the layouter returns PageComplete +
/// continuation instead of emitting the diagnostic). The diagnostic
/// fires only when:</para>
/// <list type="bullet">
///   <item>A SINGLE child of the multicol container is taller than the
///   per-page multicol block-size (cycle 1's
///   <c>column-block-size = container's content block extent</c>); the
///   child can't be split across pages by multicol-level pagination
///   alone (intra-child fragmentation is the inner BlockLayouter's
///   responsibility + its absence at the multicol level forces a
///   forward-progress truncation). Analog to TableLayouter's single-
///   oversized-row case.</item>
///   <item>Multi-page splitting itself can't make forward progress
///   (e.g., resume child index exactly equals the prior page's child
///   index AND no nested-layouter state advanced — the layouter would
///   loop). Defensive guard against pathological inputs.</item>
/// </list>
///
/// <para><b>Cycle 2 limitations + deferrals</b> per
/// <c>docs/deferrals.md#multicol-balancing-pagination</c>:</para>
/// <list type="bullet">
///   <item>No column balancing (<c>column-fill: balance</c>). Columns
///   fill left-to-right serially; the LAST column may be shorter
///   than the rest.</item>
///   <item>No <c>column-width</c>-derived automatic column count.</item>
///   <item>No <c>column-span: all</c>.</item>
///   <item>No column rules (<c>column-rule-*</c> properties parse +
///   cascade but have no painted effect).</item>
///   <item>No avoid-column / break-before-column / break-after-column
///   honoring (only regular block-level break properties apply
///   inside columns, via the inner <see cref="BlockLayouter"/>).</item>
/// </list>
///
/// <para><b>Cancellation.</b> The token is propagated to each
/// per-column nested <see cref="BlockLayouter"/> + checked between
/// columns. A long-running content layout responds through the inner
/// layouter's own checks.</para>
/// </summary>
internal sealed class MulticolLayouter : ILayouter, IDisposable
{
    private const double DefaultColumnGapPx = 16.0;

    // Hard cap on column count. CSS spec admits any positive integer
    // but real layouts rarely exceed ~10; this cap is purely a DoS
    // guard against `column-count: 999999`. Per-column inline-size
    // would still be positive (FragmentainerContext's positivity
    // check would otherwise throw on `inlineSize / 999999` rounding
    // to zero for narrow containers).
    private const int MaxColumnCount = 1000;

    private readonly Box _rootBox;
    private readonly IBlockFragmentSink _sink;
    // Per Phase 3 Task 14 cycle 2 — multi-page multicol resume state.
    // Non-null only when the dispatching BlockLayouter constructed
    // this layouter with a MulticolContinuation captured by a prior
    // page's AttemptLayout (= the multicol container's columnized
    // content exceeded N columns on the prior page). The first column
    // on the resumed page reads NextChildIndex (+ PerChildLayouterState
    // for the prior page's overflowing nested BlockContinuation) and
    // picks up emission there. Cycle 1's hardening Finding 5
    // rejection of any non-null continuation is RELAXED in cycle 2;
    // a MulticolContinuation is now accepted, other LayoutContinuation
    // subtypes still throw.
    private readonly MulticolContinuation? _incomingContinuation;
    private readonly IPaginateDiagnosticsSink? _diagnostics;
    private readonly IShaperResolver? _shaperResolver;

    private double _contentInlineOffset;
    private double _contentBlockOffset;
    private double _contentInlineSize;
    private double _contentBlockSize;
    private bool _emissionConfigured;

    /// <summary>Construct a layouter for the multicol container
    /// <paramref name="rootBox"/>. The box MUST have a positive
    /// <c>column-count</c> declared on its computed style (= a
    /// <see cref="ComputedStyleLayoutExtensions.ReadColumnCount"/>
    /// result &gt;= 1); otherwise the constructor throws.
    ///
    /// <para>Per the cycle 1 plan the dispatch gate in
    /// <see cref="BlockLayouter"/> only invokes this layouter when
    /// <c>ReadColumnCount() &gt;= 2</c> — <c>column-count: 1</c> is
    /// handled by the regular block path. The constructor's lower
    /// bound is 1 (not 2) to permit direct-construction tests of the
    /// edge case + so future sub-cycles using
    /// <c>column-width</c>-derived counting can still construct a
    /// layouter when the derivation yields 1.</para></summary>
    /// <param name="rootBox">The multicol container box (a
    /// <see cref="BoxKind.BlockContainer"/> /
    /// <see cref="BoxKind.AnonymousBlock"/> / <see cref="BoxKind.ListItem"/>
    /// with <c>column-count: N</c>).</param>
    /// <param name="sink">The same sink the caller uses; the per-
    /// column fragments append after the multicol wrapper fragment
    /// (which the caller has already emitted).</param>
    /// <param name="incomingContinuation">Per Phase 3 Task 14 cycle 2
    /// — accepts <see langword="null"/> (first page) OR a
    /// <see cref="MulticolContinuation"/> (resume page). Cycle 1
    /// hardening Finding 5's blanket non-null rejection has been
    /// RELAXED to accept the multi-page resume contract; other
    /// <see cref="LayoutContinuation"/> subtypes (e.g.,
    /// <see cref="BlockContinuation"/>) still throw so callers don't
    /// accidentally mis-route a wrong-kind continuation into
    /// MulticolLayouter. Mirrors <see cref="TableLayouter"/>'s
    /// cycle 1 → cycle 2 progression for
    /// <see cref="TableContinuation"/>.</param>
    /// <param name="diagnostics">Diagnostic sink for the
    /// <c>LAYOUT-MULTICOL-FORCED-OVERFLOW-001</c> +
    /// <c>LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001</c> codes.</param>
    /// <param name="shaperResolver">Optional inline shaper resolver
    /// threaded into the nested <see cref="BlockLayouter"/> for
    /// inline-only child layout.</param>
    /// <exception cref="ArgumentNullException">When
    /// <paramref name="rootBox"/> or <paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException">When
    /// <paramref name="rootBox"/> does not declare a positive
    /// integer <c>column-count</c>, OR when
    /// <paramref name="incomingContinuation"/> is non-null but not a
    /// <see cref="MulticolContinuation"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When
    /// <paramref name="incomingContinuation"/> is a
    /// <see cref="MulticolContinuation"/> whose
    /// <see cref="MulticolContinuation.NextChildIndex"/> is &lt; 0
    /// or &gt; <c>rootBox.Children.Count</c>.</exception>
    public MulticolLayouter(
        Box rootBox,
        IBlockFragmentSink sink,
        LayoutContinuation? incomingContinuation = null,
        IPaginateDiagnosticsSink? diagnostics = null,
        IShaperResolver? shaperResolver = null)
    {
        ArgumentNullException.ThrowIfNull(rootBox);
        ArgumentNullException.ThrowIfNull(sink);

        var columnCount = rootBox.Style.ReadColumnCount();
        if (columnCount is null or < 1)
        {
            throw new ArgumentException(
                "MulticolLayouter expects a block container with "
                + "`column-count: <positive integer>`. The "
                + $"ReadColumnCount() extension returned {columnCount} "
                + "for the supplied rootBox (= null/auto/invalid/zero/"
                + "negative). The dispatching BlockLayouter is the "
                + "guard for this contract — the wrong kind would "
                + "silently emit zero columns + drop all content, "
                + "hiding the integration bug.",
                nameof(rootBox));
        }

        // Per Phase 3 Task 14 cycle 2 — accept MulticolContinuation
        // for the multi-page resume; reject other LayoutContinuation
        // kinds so misrouted continuations surface loudly. Mirrors
        // BlockLayouter / TableLayouter's per-kind validation pattern.
        MulticolContinuation? typedIncoming = null;
        if (incomingContinuation is not null)
        {
            if (incomingContinuation is not MulticolContinuation mcCont)
            {
                throw new ArgumentException(
                    "MulticolLayouter expects a MulticolContinuation "
                    + $"for incomingContinuation; got "
                    + $"{incomingContinuation.GetType().Name}. The "
                    + "wrong continuation type would silently restart "
                    + "from column 0 + duplicate content. Per Phase 3 "
                    + "Task 14 cycle 2's resume contract — the "
                    + "dispatching BlockLayouter unpacks "
                    + "BlockContinuation.LayouterState + passes the "
                    + "MulticolContinuation here.",
                    nameof(incomingContinuation));
            }
            if (mcCont.NextChildIndex < 0
                || mcCont.NextChildIndex > rootBox.Children.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(incomingContinuation),
                    $"MulticolContinuation.NextChildIndex={mcCont.NextChildIndex} "
                    + $"is outside the root's child range [0, "
                    + $"{rootBox.Children.Count}]. Out-of-range values "
                    + "would silently return AllDone with no fragments, "
                    + "hiding caller bugs.");
            }
            // Per Phase 3 Task 14 cycle 2 hardening (Finding #2) —
            // fail-fast invariant validation. Mirrors
            // TableLayouter's per-field range checks at
            // TableLayouter.cs:300-319. Each check guards a silent-
            // restart / silent-content-loss failure mode that would
            // otherwise hide producer-side bugs.
            if (!double.IsFinite(mcCont.ConsumedBlockSize)
                || mcCont.ConsumedBlockSize < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(incomingContinuation),
                    $"MulticolContinuation.ConsumedBlockSize="
                    + $"{mcCont.ConsumedBlockSize} must be finite + non-"
                    + "negative. NaN / Infinity / negative values "
                    + "indicate a producer bug — they would propagate "
                    + "into pagination math (RemainingBlockSize "
                    + "comparisons, cost calculations) and silently "
                    + "corrupt every downstream page-break decision.");
            }
            if (mcCont.PerChildLayouterState is not null
                and not BlockContinuation)
            {
                throw new ArgumentException(
                    "MulticolContinuation.PerChildLayouterState must be "
                    + "null or a BlockContinuation (the nested per-"
                    + "child layouter resume state). Got "
                    + $"{mcCont.PerChildLayouterState.GetType().Name}. "
                    + "Per cycle 2's contract, per-child resume state "
                    + "is always a BlockContinuation produced by the "
                    + "inner BlockLayouter that runs each multicol "
                    + "column. Other types would silently bypass the "
                    + "resume dispatch and restart the child from "
                    + "scratch, duplicating content already emitted "
                    + "on the prior page.",
                    nameof(incomingContinuation));
            }
            if (mcCont.NextChildIndex == rootBox.Children.Count
                && mcCont.PerChildLayouterState is not null)
            {
                throw new ArgumentException(
                    $"MulticolContinuation has NextChildIndex="
                    + $"{mcCont.NextChildIndex} == "
                    + $"rootBox.Children.Count ({rootBox.Children.Count}) "
                    + "(= terminal index, no more children to emit) BUT "
                    + "PerChildLayouterState is not null. A terminal "
                    + "index means the prior page completed every "
                    + "child; there is no nested per-child state to "
                    + "resume. This combination indicates a producer "
                    + "bug — likely a missing 'consume' of the "
                    + "PerChildLayouterState when advancing past the "
                    + "last child.",
                    nameof(incomingContinuation));
            }
            if (mcCont.PerChildLayouterState is BlockContinuation bc
                && bc.ResumeAtChild != mcCont.NextChildIndex)
            {
                throw new ArgumentException(
                    "MulticolContinuation has PerChildLayouterState = "
                    + $"BlockContinuation(ResumeAtChild={bc.ResumeAtChild}) "
                    + $"but the outer NextChildIndex is "
                    + $"{mcCont.NextChildIndex}. The two indices must "
                    + "agree — they refer to the SAME multicol child "
                    + "(NextChildIndex from the multicol's perspective; "
                    + "ResumeAtChild from the nested BlockLayouter's "
                    + "perspective on the same child's inner block "
                    + "subtree). A mismatch indicates a producer bug "
                    + "and would cause the resume to misroute the "
                    + "nested state to a different child, silently "
                    + "duplicating or skipping content.",
                    nameof(incomingContinuation));
            }
            typedIncoming = mcCont;
        }

        _rootBox = rootBox;
        _sink = sink;
        _incomingContinuation = typedIncoming;
        _diagnostics = diagnostics;
        _shaperResolver = shaperResolver;
    }

    /// <summary>Per cycle 1 — set the multicol container's content-
    /// box geometry in the outer fragmentainer's coordinate space.
    /// The dispatching <see cref="BlockLayouter"/> has already
    /// emitted the wrapper fragment; this method tells the layouter
    /// WHERE to anchor the per-column content fragments inside that
    /// wrapper's content area.
    ///
    /// <para>MUST be called before <see cref="AttemptLayout"/>;
    /// otherwise AttemptLayout throws.</para></summary>
    /// <param name="contentInlineOffset">Inline-axis offset (CSS px
    /// from the fragmentainer's content-area origin) of the
    /// container's content-box inline-start edge.</param>
    /// <param name="contentBlockOffset">Block-axis offset of the
    /// container's content-box block-start edge.</param>
    /// <param name="contentInlineSize">Container's content-box
    /// inline extent.</param>
    /// <param name="contentBlockSize">Container's content-box block
    /// extent (= the per-column block-size).</param>
    public void ConfigureEmission(
        double contentInlineOffset,
        double contentBlockOffset,
        double contentInlineSize,
        double contentBlockSize)
    {
        if (!double.IsFinite(contentInlineSize) || contentInlineSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentInlineSize),
                $"contentInlineSize must be finite + positive; got {contentInlineSize}");
        }
        if (!double.IsFinite(contentBlockSize) || contentBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentBlockSize),
                $"contentBlockSize must be finite + positive; got {contentBlockSize}");
        }
        _contentInlineOffset = contentInlineOffset;
        _contentBlockOffset = contentBlockOffset;
        _contentInlineSize = contentInlineSize;
        _contentBlockSize = contentBlockSize;
        _emissionConfigured = true;
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
                "MulticolLayouter.AttemptLayout was called before "
                + "ConfigureEmission. The dispatching BlockLayouter "
                + "must supply the wrapper's content-area geometry so "
                + "the layouter knows where to anchor the columns.");
        }

        // Re-read column-count + column-gap. ReadColumnCount() was
        // validated >= 1 in the constructor; the upper-bound cap is
        // a DoS guard against `column-count: 999999`.
        var columnCount = _rootBox.Style.ReadColumnCount() ?? 1;
        if (columnCount > MaxColumnCount)
        {
            // Per Phase 3 Task 14 cycle 2 hardening (Finding #3) —
            // surface the silent clamp via a Warning diagnostic.
            // Without this, an author with `column-count: 10000` sees
            // a rendered output with 1000 columns that visually
            // disagrees with the stylesheet but no warning explains
            // why. The clamp itself is intentional (the per-column
            // arithmetic is O(N) per child; uncapped N is a DoS
            // vector for adversarial / generated input). Mirrors the
            // non-finite-geometry emission pattern below
            // (`SafeEmit(layout.Diagnostics ?? _diagnostics, ...)`).
            var requestedColumnCount = columnCount;
            columnCount = MaxColumnCount;
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutMulticolColumnCountClamped001,
                    $"MulticolLayouter: requested column-count="
                    + $"{requestedColumnCount} exceeds the layouter's "
                    + $"safety cap (MaxColumnCount={MaxColumnCount}); "
                    + "clamping to "
                    + $"{columnCount}. The cap protects against the "
                    + "per-column arithmetic's O(N)-per-child cost on "
                    + "adversarial inputs (uncapped N is a DoS vector). "
                    + "The rendered output will have at most "
                    + $"{columnCount} columns.",
                    PaginateDiagnosticSeverity.Warning));
        }
        var columnGap = _rootBox.Style.ReadColumnGap();
        if (!double.IsFinite(columnGap) || columnGap < 0)
        {
            // Defensive — the resolver should already reject negative
            // length values for column-gap, but a future change might
            // not. Fall back to the cycle-1 default.
            columnGap = DefaultColumnGapPx;
        }

        // Per-column inline size. The total gap budget is
        // (N-1) × columnGap; the remaining inline-axis space splits
        // equally across N columns. Clamp to a small positive value
        // so FragmentainerContext's positivity check doesn't trip
        // for pathological inputs (e.g., a 10 px container with
        // column-count: 100). Cycle 1 intentionally doesn't surface
        // a diagnostic for this clamp — sub-cycle 2 may add one
        // alongside the column-width path.
        //
        // Per Phase 3 Task 14 cycle 1 hardening (Finding 4) —
        // non-finite geometry guard. Per-column inline-size is
        // clamped to a positive finite value below (the existing
        // `< 1.0` floor handles the underflow case), but `totalGap`
        // and the per-column inline-offset arithmetic
        // (`c * (perColumnInlineSize + columnGap)`) can blow up to
        // ±Infinity for individually-finite operands when columnGap
        // is astronomically large (e.g., column-gap: 1e300 with
        // 100 columns). Pre-fix the layouter happily computed those
        // and propagated ±Inf through the column emission cascade,
        // which then NaN-poisoned downstream pagination arithmetic.
        // Post-fix: detect the blow-up at this entry point + clamp
        // columnGap so totalGap stays below half the container's
        // inline size; emit
        // LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001 (Warning) so
        // authors / integrators see the clamp.
        var totalGap = (columnCount - 1) * columnGap;
        if (!double.IsFinite(totalGap)
            || totalGap >= _contentInlineSize)
        {
            // Clamp columnGap so totalGap < contentInlineSize / 2.
            // For columnCount==1 there's no gap arithmetic; the
            // outer `columnCount > 1` guard means columnCount >= 2
            // in this branch (the constructor's >= 1 check passes
            // through 1 + the BlockLayouter dispatch only fires for
            // >= 2; defensive `(columnCount - 1)` denominator handles
            // the 1 case anyway).
            var divisor = Math.Max(1, columnCount - 1);
            var clampedGap = (_contentInlineSize / 2.0) / divisor;
            // Ensure the clamped gap is itself finite + non-negative.
            if (!double.IsFinite(clampedGap) || clampedGap < 0)
            {
                clampedGap = 0;
            }
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutMulticolNonFiniteGeometry001,
                    "MulticolLayouter: non-finite column geometry "
                    + $"(columnGap={columnGap}, columnCount="
                    + $"{columnCount}, contentInlineSize="
                    + $"{_contentInlineSize:0.##}, totalGap="
                    + $"{totalGap}). Clamping columnGap to "
                    + $"{clampedGap:0.##} so per-column inline geometry "
                    + "stays finite. Rendered output may differ "
                    + "visually from author intent.",
                    PaginateDiagnosticSeverity.Warning));
            columnGap = clampedGap;
            totalGap = (columnCount - 1) * columnGap;
        }
        var perColumnInlineSize = (_contentInlineSize - totalGap) / columnCount;
        if (!double.IsFinite(perColumnInlineSize) || perColumnInlineSize < 1.0)
        {
            perColumnInlineSize = 1.0;
        }

        // The per-column block-size is the container's content block
        // extent. Cycle 1 doesn't shrink the last column when it's
        // partially filled (= no column balancing).
        var perColumnBlockSize = _contentBlockSize;

        // Per Phase 3 Task 14 cycle 3 — column balancing per CSS Multi-
        // column L1 §3.4. Activate when:
        //   1. `column-fill` is `balance` or `balance-all` (= the spec
        //      default — cycle 1+2 silently ignored this; cycle 3
        //      honors it).
        //   2. The multicol container has `height: auto` (= an
        //      explicit height pins the per-column block-size to the
        //      author's chosen value, mirroring conservative Prince /
        //      WeasyPrint behavior + avoiding the over-shrink case
        //      where a balanced ideal would drop content out of a
        //      fixed-height container).
        //
        // 2-pass algorithm:
        //   PASS 1 (pre-measure): serial-fill the content into a
        //     single notional tall column; capture the resulting block
        //     extent (= `totalSerialExtent`).
        //   PASS 2 (layout): re-run the column-fill loop with
        //     `effectiveColumnBlockSize = min(ceil(totalSerial /
        //     columnCount), perColumnBlockSize)`. The ceiling rounds
        //     up so the balanced columns aren't shorter than they need
        //     to be (and any remainder lands in the last column,
        //     visually similar to author intent). The min(...,
        //     perColumnBlockSize) clamp preserves the per-page block-
        //     size bound — balancing never grows a column past the
        //     container's content extent. The floor at 1.0 avoids
        //     pathological divide-by-zero edge cases for tiny content.
        //
        // `column-fill: balance-all` per the spec applies balancing to
        // ALL fragmentainers including the last; cycle 3 treats it
        // identically to `balance` (the last-fragmentainer special-
        // case is sub-cycle 2+ scope; see
        // docs/deferrals.md#multicol-balancing-pagination).
        var columnFill = _rootBox.Style.ReadColumnFill();
        var balancingActive =
            columnFill is ColumnFillValue.Balance or ColumnFillValue.BalanceAll
            && _rootBox.IsHeightAuto();
        var effectiveColumnBlockSize = perColumnBlockSize;
        if (balancingActive && columnCount > 1)
        {
            var totalSerialExtent = PreMeasureTotalSerialExtent(
                perColumnInlineSize: perColumnInlineSize,
                perColumnBlockSize: perColumnBlockSize,
                columnCount: columnCount,
                carriedContinuation: null,
                cancellationToken: cancellationToken);
            // Defensive: a non-finite extent (e.g., pathological
            // content with non-finite block-size) would NaN-poison
            // the divide. Fall back to the unbalanced perColumnBlockSize.
            if (double.IsFinite(totalSerialExtent) && totalSerialExtent > 0)
            {
                var idealBlockSize = Math.Ceiling(totalSerialExtent / columnCount);
                effectiveColumnBlockSize = Math.Min(idealBlockSize, perColumnBlockSize);
                // Floor at 1.0 — both as a divide-by-zero guard +
                // because FragmentainerContext rejects non-positive
                // block-sizes (the per-column FragmentainerContext
                // below uses this value).
                effectiveColumnBlockSize = Math.Max(effectiveColumnBlockSize, 1.0);
            }
        }

        // Per Phase 3 Task 14 cycle 2 — multi-page multicol resume.
        // When the constructor stashed a MulticolContinuation, the
        // FIRST column on THIS page picks up content emission at the
        // captured NextChildIndex with the captured
        // PerChildLayouterState as its nested incomingContinuation.
        // Subsequent columns continue from the prior column's
        // PageComplete result, same as cycle 1.
        //
        // Resume protocol:
        //   - If _incomingContinuation.PerChildLayouterState is a
        //     BlockContinuation, hand it directly to the first
        //     column's nested BlockLayouter — that captures BOTH the
        //     resume-child-index + any nested layouter state from a
        //     prior page's overflowing column.
        //   - If PerChildLayouterState is null AND NextChildIndex > 0,
        //     synthesize a BlockContinuation(ResumeAtChild=NextChildIndex,
        //     ConsumedBlockSize=0). This case arises when the prior
        //     page committed a whole-child boundary (= the last column
        //     of the prior page ended exactly at a child boundary;
        //     PerChildLayouterState was null but the next-child cursor
        //     advanced).
        //   - Otherwise (null + NextChildIndex == 0): first-page
        //     equivalent — start cleanly.
        LayoutContinuation? carriedContinuation = null;
        if (_incomingContinuation is not null)
        {
            if (_incomingContinuation.PerChildLayouterState is BlockContinuation perChildBlockCont)
            {
                carriedContinuation = perChildBlockCont;
            }
            else if (_incomingContinuation.NextChildIndex > 0)
            {
                carriedContinuation = new BlockContinuation(
                    ResumeAtChild: _incomingContinuation.NextChildIndex,
                    ConsumedBlockSize: 0);
            }
            // (NextChildIndex == 0 AND PerChildLayouterState == null)
            // is the degenerate "first-page-equivalent" case — leave
            // carriedContinuation null + start at child 0.
        }

        // Column-fill pass. Walk columns left-to-right; the previous
        // column's PageComplete continuation feeds the next column's
        // resume point.
        var contentExhausted = false;
        var sinkCursorAtStart = _sink.Cursor;
        for (var columnIdx = 0; columnIdx < columnCount; columnIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per-column inline offset in the outer fragmentainer's
            // coordinate space.
            //
            // Per Phase 3 Task 14 cycle 1 hardening (Finding 4) —
            // defensive finite guard. With the columnGap clamp above
            // both columnGap + perColumnInlineSize are finite, so the
            // product is finite; the defensive guard catches any
            // future change that could introduce a non-finite
            // operand here.
            var columnInlineOffset =
                _contentInlineOffset + columnIdx * (perColumnInlineSize + columnGap);
            if (!double.IsFinite(columnInlineOffset))
            {
                columnInlineOffset = _contentInlineOffset;
            }

            // Translating sink: each fragment the nested BlockLayouter
            // emits with InlineOffset relative to its OWN sub-
            // fragmentainer (= column-local) is translated to land at
            // the column's absolute inline position. Block offsets
            // are also translated to the container's content-block
            // origin (the sub-fragmentainer starts at UsedBlockSize=0,
            // = the column's block-start = _contentBlockOffset).
            var translatingSink = new ColumnFragmentSink(
                outerSink: _sink,
                inlineOffsetTranslation: columnInlineOffset,
                blockOffsetTranslation: _contentBlockOffset);

            // Sub-fragmentainer: same content-block extent as the
            // outer multicol box (each column is a sub-fragmentainer
            // of the SAME block-axis size). Inline extent is the per-
            // column inline size.
            //
            // Per Phase 3 Task 14 cycle 3 — column balancing: when
            // active, `effectiveColumnBlockSize` is the
            // balancing-derived ideal (ceil(serial total / N), clamped
            // ≤ perColumnBlockSize); otherwise it equals
            // perColumnBlockSize (= cycle 1+2 serial fill).
            var columnFragmentainer = new FragmentainerContext(
                contentInlineSize: perColumnInlineSize,
                blockSize: effectiveColumnBlockSize);

            // Nested BlockLayouter for the column. The root is the
            // SAME multicol container — the nested layouter walks
            // its children. On the first column the incoming
            // continuation comes from the multicol resume protocol
            // above (null for a clean first page); on subsequent
            // columns it's the previous column's PageComplete result
            // (resume at the deferred child).
            //
            // Per cycle 1 plan — the nested resolver is a fresh
            // column-scoped BreakResolver (below). The inner
            // BlockLayouter's per-column pagination uses it for
            // break decisions inside the column; the sub-
            // fragmentainer's block-size acts as the column boundary.
            // Cycle 3+ may introduce a column-scoped break protocol
            // to honor avoid-column + forced break-before-column.
            using var innerLayouter = new BlockLayouter(
                rootBox: _rootBox,
                sink: translatingSink,
                incomingContinuation: carriedContinuation,
                diagnostics: _diagnostics,
                shaperResolver: _shaperResolver);

            // Per Phase 3 Task 14 cycle 1 — use a fresh column-scoped
            // BreakResolver so the outer table-aware resolver isn't
            // polluted by per-column checkpoints. Mirrors
            // TableLayouter's per-cell resolver isolation (Finding 3
            // hardening on Task 12).
            using var columnResolver = new BreakResolver();
            var columnLayoutCtx = new LayoutContext(columnFragmentainer)
            {
                Diagnostics = layout.Diagnostics ?? _diagnostics,
            };

            var columnResult = innerLayouter.AttemptLayout(
                columnFragmentainer,
                ref columnLayoutCtx,
                columnResolver,
                // Per cycle 1 — LastResort so the inner layouter
                // commits a best-effort result on overflow rather
                // than asking for a rewind (the outer multicol
                // dispatch already passed its checkpoint frontier;
                // we have no further retry budget here).
                LayoutAttemptStrategy.LastResort,
                cancellationToken);

            if (columnResult.Outcome == LayoutAttemptOutcome.AllDone)
            {
                contentExhausted = true;
                carriedContinuation = null;
                break;
            }
            if (columnResult.Outcome == LayoutAttemptOutcome.PageComplete)
            {
                carriedContinuation = columnResult.Continuation;
                // Continue to the next column with the carried
                // continuation as the resume point.
                continue;
            }

            // NeedsRewind under LastResort is a layouter contract
            // violation per ILayouter.AttemptLayout's docs. The
            // inner BlockLayouter MUST NOT return this on
            // LastResort; defensive fall-through treats it as
            // overflow.
            carriedContinuation = columnResult.Continuation;
        }

        if (contentExhausted)
        {
            // Clean exit — all content committed on this page.
            return LayoutAttemptResult.AllDone(cost: 0);
        }

        // Per Phase 3 Task 14 cycle 2 — multi-page multicol via
        // MulticolContinuation. When the LAST column STILL has a non-
        // null BlockContinuation (= the N columns weren't enough),
        // package the resume state into a MulticolContinuation +
        // return PageComplete so the dispatching BlockLayouter
        // continues the multicol on the next page.
        //
        // Detection of the "single-oversized-child" case (cycle 2
        // continues to surface LAYOUT-MULTICOL-FORCED-OVERFLOW-001):
        // a single child block that exceeds the per-page multicol
        // block-size can't be split across pages by multicol-level
        // pagination alone — the inner BlockLayouter's forced-
        // overflow path already committed the oversized child on
        // THIS page (= it appears among the emitted fragments) +
        // returned a BlockContinuation pointing at the NEXT child.
        // The forward-progress check is: did anything emit between
        // the multicol's entry + exit (= sink cursor advanced)? AND
        // did the resume-child-index advance past the entry index?
        // If neither advanced, we're in the loop case; emit the
        // diagnostic + return AllDone (= truncate) for forward
        // progress, mirroring cycle 1's behavior.
        if (carriedContinuation is BlockContinuation blockCont)
        {
            var entryNextChildIndex = _incomingContinuation?.NextChildIndex ?? 0;
            var noFragmentsEmitted = _sink.Cursor == sinkCursorAtStart;
            var resumeIndexDidNotAdvance = blockCont.ResumeAtChild <= entryNextChildIndex;
            // Additional progress signal: the prior page's
            // PerChildLayouterState is also a BlockContinuation; if
            // the new blockCont matches (same ResumeAtChild + null /
            // identical layouter state) the resume isn't making
            // progress through the same child either.
            var nestedStateDidNotAdvance =
                _incomingContinuation?.PerChildLayouterState is BlockContinuation priorNested
                && blockCont.ResumeAtChild == priorNested.ResumeAtChild
                && blockCont.LayouterState is null
                && priorNested.LayouterState is null;

            if (noFragmentsEmitted
                && (resumeIndexDidNotAdvance || nestedStateDidNotAdvance))
            {
                // Forced-overflow fallback — the multicol can't make
                // forward progress at the multicol level. Emit the
                // diagnostic + truncate the remainder (= AllDone) so
                // the outer pagination doesn't loop forever.
                OptimizingBreakResolver.SafeEmit(
                    layout.Diagnostics ?? _diagnostics,
                    new PaginateDiagnostic(
                        PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                        $"MulticolLayouter: in-flow content of multicol container "
                        + $"(column-count={columnCount}, per-column block-size="
                        + $"{perColumnBlockSize:0.##}) cannot make forward "
                        + $"progress across pages — a single child at index "
                        + $"{blockCont.ResumeAtChild} (of "
                        + $"{_rootBox.Children.Count}) is taller than the per-"
                        + $"page multicol block-size + cannot be split at the "
                        + $"multicol level. The remaining content is truncated "
                        + $"to avoid pagination loop. Intra-child fragmentation "
                        + $"at the multicol level is sub-cycle 3+ scope. See "
                        + $"docs/deferrals.md#multicol-balancing-pagination.",
                        PaginateDiagnosticSeverity.Warning));
                return LayoutAttemptResult.AllDone(cost: 0);
            }

            // Clean multi-page split — capture the resume state into
            // a MulticolContinuation + return PageComplete.
            var priorConsumed = _incomingContinuation?.ConsumedBlockSize ?? 0.0;
            return LayoutAttemptResult.PageComplete(
                new MulticolContinuation(
                    NextChildIndex: blockCont.ResumeAtChild,
                    ConsumedBlockSize: priorConsumed + perColumnBlockSize,
                    PerChildLayouterState: blockCont),
                cost: 0);
        }

        // Defensive — carriedContinuation is non-null AND not a
        // BlockContinuation. Cycle 1's invariant: the nested
        // BlockLayouter only returns BlockContinuation. If a future
        // change introduced another kind, surface the diagnostic +
        // truncate so we don't return a malformed PageComplete.
        if (carriedContinuation is not null)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                    $"MulticolLayouter: nested BlockLayouter returned an "
                    + $"unexpected continuation kind "
                    + $"({carriedContinuation.GetType().Name}); the multicol "
                    + $"can't make forward progress + truncates the remainder. "
                    + $"Cycle 2's resume contract expects BlockContinuation "
                    + $"from the per-column nested layouter.",
                    PaginateDiagnosticSeverity.Warning));
        }
        return LayoutAttemptResult.AllDone(cost: 0);
    }

    /// <summary>Per Phase 3 Task 14 cycle 3 — dry-run pre-measure
    /// for <c>column-fill: balance</c>. Runs a single nested
    /// <see cref="BlockLayouter"/> over the multicol's children with
    /// a very tall block-size (= <paramref name="perColumnBlockSize"/>
    /// × <paramref name="columnCount"/> × 2, clamped to a finite upper
    /// bound) so all content fits in one notional "tall column". The
    /// returned value is the maximum <c>BlockOffset + BlockSize</c>
    /// reached by any emitted fragment in that single-column flow —
    /// the "serial extent" the balancing math then divides across
    /// <paramref name="columnCount"/> columns.
    ///
    /// <para>Uses a <see cref="DiscardingMeasureSink"/> that drops
    /// fragments but tracks the max bottom edge. The dry-run pass
    /// uses <see cref="LayoutAttemptStrategy.LastResort"/> so any
    /// content that doesn't fit (= extremely pathological inputs
    /// where even the very-tall sentinel block-size is exceeded)
    /// commits in place rather than asking for a rewind. A fresh
    /// <see cref="BreakResolver"/> isolates the measure's
    /// checkpoints from the outer column-fill loop's resolver.</para>
    ///
    /// <para><b>Caveat — 2× cost</b> when balancing is active. The
    /// pre-measure runs the children's full block-layout once
    /// (against a discarding sink) + the subsequent layout pass
    /// re-runs it against the real outer sink with the balanced
    /// block-size. Acceptable for cycle 3 Hello World; the cycle 3
    /// hardening pass may cache the result per Box.</para></summary>
    /// <param name="perColumnInlineSize">The cycle-2-derived per-
    /// column inline-axis extent. Re-used for the dry-run's notional
    /// single tall column so the measured extent reflects the actual
    /// post-balancing inline width (= line-wrap counts match).</param>
    /// <param name="perColumnBlockSize">The unbalanced per-page block-
    /// size bound. Multiplied by <paramref name="columnCount"/> × 2 to
    /// give the dry-run a generous block-axis budget.</param>
    /// <param name="columnCount">The active column count (after the
    /// MaxColumnCount clamp). Used as the multiplier on
    /// <paramref name="perColumnBlockSize"/> for the dry-run's block
    /// budget.</param>
    /// <param name="carriedContinuation">Per Phase 3 Task 14 cycle 3
    /// — Hello World scope DOES NOT propagate resume state into the
    /// pre-measure (= passes null). The cycle 3 hardening pass may
    /// fold in the resume contract; here it's a placeholder for
    /// signature symmetry with the layout pass.</param>
    /// <param name="cancellationToken">Threaded into the dry-run
    /// layouter so a long-running content layout responds to
    /// cancellation.</param>
    /// <returns>The serial-fill block-axis extent. Returns 0 when the
    /// multicol has no in-flow content; returns a finite-clamped
    /// value when the measurement produced a non-finite result
    /// (caller treats 0 as "no balancing needed", falling back to
    /// perColumnBlockSize).</returns>
    private double PreMeasureTotalSerialExtent(
        double perColumnInlineSize,
        double perColumnBlockSize,
        int columnCount,
        BlockContinuation? carriedContinuation,
        CancellationToken cancellationToken)
    {
        if (perColumnInlineSize <= 0 || perColumnBlockSize <= 0
            || columnCount <= 0 || _rootBox.Children.Count == 0)
        {
            return 0;
        }

        // Generous block-axis budget: 2× the natural budget (=
        // columns × per-page block-size). Clamp to a finite ceiling
        // to avoid Infinity propagation when perColumnBlockSize is
        // astronomically large.
        var rawBudget = perColumnBlockSize * columnCount * 2.0;
        if (!double.IsFinite(rawBudget) || rawBudget <= 0)
        {
            rawBudget = perColumnBlockSize;
        }
        var measureBudget = rawBudget;

        var discardingSink = new DiscardingMeasureSink();
        using var innerLayouter = new BlockLayouter(
            rootBox: _rootBox,
            sink: discardingSink,
            incomingContinuation: carriedContinuation,
            diagnostics: null,
            shaperResolver: _shaperResolver);

        var measureFragmentainer = new FragmentainerContext(
            contentInlineSize: perColumnInlineSize,
            blockSize: measureBudget);
        var measureLayoutCtx = new LayoutContext(measureFragmentainer)
        {
            Diagnostics = null,
        };
        using var measureResolver = new BreakResolver();

        _ = innerLayouter.AttemptLayout(
            measureFragmentainer,
            ref measureLayoutCtx,
            measureResolver,
            LayoutAttemptStrategy.LastResort,
            cancellationToken);

        return discardingSink.MaxBlockExtent;
    }

    /// <summary>Per cycle 1 — release resources held by the
    /// layouter. Currently a no-op (no per-instance pools / buffers
    /// to clean up); the placeholder maintains the
    /// <see cref="IDisposable"/> contract that mirrors
    /// <see cref="TableLayouter"/> + <see cref="BlockLayouter"/>.</summary>
    public void Dispose()
    {
        // No-op for cycle 1.
    }

    /// <summary>Per Phase 3 Task 14 cycle 3 — discarding sink for the
    /// balancing pre-measure pass. Records the maximum
    /// <c>BlockOffset + BlockSize</c> seen for any emitted fragment;
    /// drops the fragments themselves. The maximum is the "serial
    /// extent" (= the bottom edge of the deepest fragment when all
    /// content flows into one notional tall column).
    ///
    /// <para>Distinct from
    /// <c>BlockLayouter.MulticolDiscardingMeasureSink</c> — that
    /// sibling measures a multicol's COLUMNIZED extent (driven by a
    /// MulticolLayouter dry-run); this one measures the SERIAL
    /// extent (driven by a BlockLayouter dry-run with a single tall
    /// column). Both follow the same column-relative anchor pattern
    /// (= the dry-run's
    /// <see cref="MulticolLayouter.ConfigureEmission"/>-equivalent
    /// uses 0,0 as the inline + block-axis origin, so the measured
    /// max <c>BlockOffset + BlockSize</c> is the natural block
    /// extent).</para></summary>
    private sealed class DiscardingMeasureSink : IBlockFragmentSink
    {
        private int _cursor;

        public double MaxBlockExtent { get; private set; }

        public int Cursor => _cursor;

        public void Emit(BoxFragment fragment)
        {
            _cursor++;
            // The dry-run BlockLayouter emits with absolute BlockOffsets
            // measured from the fragmentainer's content-area origin
            // (= 0 since the dry-run uses a fresh FragmentainerContext).
            var bottom = fragment.BlockOffset + fragment.BlockSize;
            if (bottom > MaxBlockExtent)
            {
                MaxBlockExtent = bottom;
            }
        }

        public void RollbackTo(int cursor)
        {
            // The dry-run uses a fresh BreakResolver so the resolver
            // never names a pre-existing checkpoint; this method is
            // unreachable in practice. Defensive no-op for forward-
            // compat with future multi-step measure paths.
            if (cursor < _cursor)
            {
                _cursor = cursor;
            }
        }
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 — decorator over
    /// <see cref="IBlockFragmentSink"/> that translates each emitted
    /// fragment's <see cref="BoxFragment.InlineOffset"/> +
    /// <see cref="BoxFragment.BlockOffset"/> by fixed deltas. Each
    /// per-column nested <see cref="BlockLayouter"/> emits with
    /// offsets relative to its OWN sub-fragmentainer (column-local);
    /// the translating sink shifts them into the outer multicol
    /// container's coordinate space so the painter lands them at the
    /// right column position.
    ///
    /// <para>Cursor + rollback pass through to the wrapped outer
    /// sink unchanged — the rollback semantics for the inner
    /// BlockLayouter (which may rewind speculatively-emitted
    /// fragments) remain consistent with the outer sink's view.</para>
    ///
    /// <para>Pattern mirrors <see cref="TableLayouter"/>'s
    /// <c>MeasuringFragmentSink</c> — a buffered/translating
    /// decorator inserted between the inner layouter + the outer
    /// painter sink without disturbing the inner layouter's
    /// emission contract.</para></summary>
    private sealed class ColumnFragmentSink : IBlockFragmentSink
    {
        private readonly IBlockFragmentSink _outerSink;
        private readonly double _inlineOffsetTranslation;
        private readonly double _blockOffsetTranslation;

        public ColumnFragmentSink(
            IBlockFragmentSink outerSink,
            double inlineOffsetTranslation,
            double blockOffsetTranslation)
        {
            _outerSink = outerSink;
            _inlineOffsetTranslation = inlineOffsetTranslation;
            _blockOffsetTranslation = blockOffsetTranslation;
        }

        public int Cursor => _outerSink.Cursor;

        public void Emit(BoxFragment fragment) =>
            _outerSink.Emit(fragment with
            {
                InlineOffset = fragment.InlineOffset + _inlineOffsetTranslation,
                BlockOffset = fragment.BlockOffset + _blockOffsetTranslation,
            });

        public void RollbackTo(int cursor) => _outerSink.RollbackTo(cursor);
    }
}
