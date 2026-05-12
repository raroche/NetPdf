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
/// Per Phase 3 Task 14 cycle 1 — Hello World multi-column layout per
/// CSS Multi-column L1 (<see href="https://www.w3.org/TR/css-multicol-1/"/>).
/// A block container with a positive integer <c>column-count: N</c>
/// becomes a multicol container; its in-flow content flows through
/// N parallel columns (sub-fragmentainers), each the same block-axis
/// extent as the container's content area + an inline-axis extent of
/// <c>(containerContentInlineSize - (N-1) × columnGap) / N</c>.
///
/// <para><b>Cycle 1 contract.</b> The layouter:</para>
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
///   <item>When any non-last column returns
///   <see cref="LayoutAttemptOutcome.PageComplete"/> (= content
///   overflows the current column), the layouter advances to the next
///   column carrying the previous column's
///   <see cref="BlockContinuation"/> as the resume point. When the
///   LAST column STILL returns PageComplete (= even N columns
///   weren't enough), surfaces
///   <c>LAYOUT-MULTICOL-FORCED-OVERFLOW-001</c> + discards the
///   continuation. Multi-page multicol (the outer multicol box
///   fragmenting across pages so the overflow continues on the next
///   page) is sub-cycle 2+ scope per
///   <c>docs/deferrals.md#multicol-balancing-pagination</c>.</item>
///   <item>Does NOT emit the outer multicol container's fragment —
///   the dispatching <see cref="BlockLayouter"/> emits that fragment
///   first + then invokes MulticolLayouter to emit the column
///   content INSIDE the container's content-box. (Pattern mirrors
///   how <see cref="TableLayouter"/> relies on the caller to emit
///   the table wrapper fragment first.)</item>
/// </list>
///
/// <para><b>Cycle 1 limitations + deferrals</b> per
/// <c>docs/deferrals.md#multicol-balancing-pagination</c>:</para>
/// <list type="bullet">
///   <item>No column balancing (<c>column-fill: balance</c>). Columns
///   fill left-to-right serially; the LAST column may be shorter
///   than the rest.</item>
///   <item>No <c>column-width</c>-derived automatic column count.</item>
///   <item>No <c>column-span: all</c>.</item>
///   <item>No column rules (<c>column-rule-*</c> properties parse +
///   cascade but have no painted effect).</item>
///   <item>No multi-page multicol — overflow truncates + emits the
///   forced-overflow diagnostic.</item>
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
    // Per Phase 3 Task 14 cycle 1 hardening (Finding 5) — the
    // constructor REJECTS non-null continuations until sub-cycle 2
    // ships multi-page multicol. Cycle 1 doesn't carry a stored
    // continuation field; sub-cycle 2 will re-introduce one + read
    // it inside AttemptLayout's resume path.
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
    /// <param name="incomingContinuation">Per cycle 1 hardening
    /// (Finding 5) — null only. Sub-cycle 2 will accept a
    /// <see cref="MulticolContinuation"/> for the multi-page multicol
    /// resume; until then a non-null value (of ANY type) throws so
    /// callers don't accidentally restart from column 0 + duplicate
    /// content. Mirrors <see cref="TableLayouter"/>'s cycle 1
    /// non-null-continuation rejection pattern.</param>
    /// <param name="diagnostics">Diagnostic sink for the
    /// <c>LAYOUT-MULTICOL-FORCED-OVERFLOW-001</c> code.</param>
    /// <param name="shaperResolver">Optional inline shaper resolver
    /// threaded into the nested <see cref="BlockLayouter"/> for
    /// inline-only child layout.</param>
    /// <exception cref="ArgumentNullException">When
    /// <paramref name="rootBox"/> or <paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException">When
    /// <paramref name="rootBox"/> does not declare a positive
    /// integer <c>column-count</c>, OR when
    /// <paramref name="incomingContinuation"/> is non-null
    /// (cycle 1 does not yet support resume).</exception>
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

        // Per Phase 3 Task 14 cycle 1 hardening (Finding 5) — REJECT
        // any non-null continuation until sub-cycle 2 wires up
        // multi-page multicol. Pre-fix the constructor accepted
        // MulticolContinuation instances + silently discarded them
        // in AttemptLayout (the stored field was never read), so an
        // accidental resume would restart from column 0 +
        // duplicate / drop content. Mirrors TableLayouter's cycle 1
        // pattern (which initially rejected non-null
        // TableContinuation before sub-cycle 2 implemented resume).
        if (incomingContinuation is not null)
        {
            throw new ArgumentException(
                "Sub-cycle 1 does not yet support resume after "
                + "PageComplete. MulticolLayouter rejects any non-null "
                + "incomingContinuation (got "
                + $"{incomingContinuation.GetType().Name}) so callers "
                + "don't accidentally restart from column 0 + duplicate "
                + "content. Multi-page multicol via "
                + "MulticolContinuation is sub-cycle 2+ scope. See "
                + "docs/deferrals.md#multicol-balancing-pagination.",
                nameof(incomingContinuation));
        }

        _rootBox = rootBox;
        _sink = sink;
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
            columnCount = MaxColumnCount;
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

        // Column-fill pass. Walk columns left-to-right; the previous
        // column's PageComplete continuation feeds the next column's
        // resume point.
        LayoutContinuation? carriedContinuation = null;
        var contentExhausted = false;
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
            var columnFragmentainer = new FragmentainerContext(
                contentInlineSize: perColumnInlineSize,
                blockSize: perColumnBlockSize);

            // Nested BlockLayouter for the column. The root is the
            // SAME multicol container — the nested layouter walks
            // its children. On the first column the incoming
            // continuation is null (start at child 0); on subsequent
            // columns it's the previous column's PageComplete result
            // (resume at the deferred child).
            //
            // Per cycle 1 plan — the nested resolver is the OUTER
            // resolver. The inner BlockLayouter's per-column
            // pagination uses it for break decisions inside the
            // column; the sub-fragmentainer's block-size acts as the
            // column boundary. This is acceptable for cycle 1
            // because the outer BlockLayouter has already committed
            // its dispatch to MulticolLayouter (= the multicol
            // container is single-page atomic from the outer's view)
            // + the inner per-column pagination is governed by the
            // sub-fragmentainer's bounds. Sub-cycle 2 will introduce
            // a column-scoped BreakResolver to honor avoid-column +
            // forced break-before-column properties.
            using var innerLayouter = new BlockLayouter(
                rootBox: _rootBox,
                sink: translatingSink,
                incomingContinuation: carriedContinuation,
                diagnostics: _diagnostics,
                shaperResolver: _shaperResolver);

            // Per Phase 3 Task 14 cycle 1 — use a fresh column-scoped
            // BreakResolver so the outer table-aware resolver isn't
            // polluted by per-column checkpoints. The column emits
            // atomically from the outer view (single multicol
            // fragment). Mirrors TableLayouter's per-cell resolver
            // isolation (Finding 3 hardening on Task 12).
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

        // Per cycle 1 — when the LAST column STILL has a non-null
        // continuation (= the N columns weren't enough), surface the
        // forced-overflow diagnostic + truncate the remaining
        // content. Multi-page multicol is sub-cycle 2+ scope; we
        // return AllDone so the outer BlockLayouter treats the
        // multicol as fully committed for this page.
        if (!contentExhausted && carriedContinuation is BlockContinuation blockCont)
        {
            OptimizingBreakResolver.SafeEmit(
                layout.Diagnostics ?? _diagnostics,
                new PaginateDiagnostic(
                    PaginateDiagnosticCodes.LayoutMulticolForcedOverflow001,
                    $"MulticolLayouter: in-flow content of multicol container "
                    + $"(column-count={columnCount}, per-column block-size="
                    + $"{perColumnBlockSize:0.##}) overflowed the columns. "
                    + $"Remaining content starts at child index "
                    + $"{blockCont.ResumeAtChild} (of "
                    + $"{_rootBox.Children.Count}); the remaining content is "
                    + "truncated. Multi-page multicol (the outer multicol box "
                    + "fragmenting across pages so the overflow continues on "
                    + "the next page) is sub-cycle 2+ scope. See "
                    + "docs/deferrals.md#multicol-balancing-pagination.",
                    PaginateDiagnosticSeverity.Warning));
        }

        return LayoutAttemptResult.AllDone(cost: 0);
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
