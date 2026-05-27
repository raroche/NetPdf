// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan §"Continuation tokens" — the result of a layouter
/// being interrupted by a fragmentainer break. The continuation captures
/// "where to resume" so the next-page invocation lays out the rest of
/// the box without re-running the work that already fit. Concrete
/// subtypes per layouter:
/// <list type="bullet">
///   <item><see cref="BlockContinuation"/> — block container split between
///   children.</item>
///   <item><see cref="InlineContinuation"/> — inline run/cluster split
///   inside a line.</item>
///   <item><see cref="TableContinuation"/> — table split between rows
///   (with thead / tfoot repetition + cached column layout).</item>
///   <item><see cref="FlexContinuation"/> — multi-line flex container
///   split between flex lines (with cross-fragment baseline state).</item>
///   <item><see cref="GridContinuation"/> — grid container split between
///   grid rows (with track-sizing cache).</item>
/// </list>
///
/// <para>Continuations are immutable + pooled where lifetime allows.
/// The layouter that produces a continuation is the one that consumes
/// it on the next-page pass — the type discriminates the resume strategy.</para>
///
/// <para><b>Per Phase 3 review fix #7</b> — continuations carry an
/// optional <c>LayouterState</c> field of an opaque
/// <see cref="object"/> type. Layouters that need to hand state across
/// the page boundary (table column-layout cache; flex baseline state;
/// grid track-sizing cache; inline shaping cache) stash a layouter-
/// private record there + cast it back on resume. This avoids burning
/// a per-layouter sealed subtype every time a new piece of cached
/// state is needed; per the plan, "the type discriminates the resume
/// strategy" — <i>what's</i> handed across is up to that layouter.</para>
/// </summary>
internal abstract record LayoutContinuation;

/// <summary>Block container split between children. Resume at child
/// <paramref name="ResumeAtChild"/> with <paramref name="ConsumedBlockSize"/>
/// already emitted on prior pages.
/// <paramref name="LayouterState"/> per Phase 3 review fix #7 — opaque
/// layouter-owned state (e.g., margin-collapsing summary state).</summary>
internal sealed record BlockContinuation(
    int ResumeAtChild,
    double ConsumedBlockSize,
    object? LayouterState = null) : LayoutContinuation;

/// <summary>Inline run / cluster split inside a line. Resume at run
/// <paramref name="RunIndex"/>, glyph cluster
/// <paramref name="ClusterIndex"/>.
/// <paramref name="LayouterState"/> per Phase 3 review fix #7 — opaque
/// state (e.g., the in-progress shaped run buffer that needs to be
/// carried into the next-page line builder).</summary>
internal sealed record InlineContinuation(
    int RunIndex,
    int ClusterIndex,
    object? LayouterState = null) : LayoutContinuation;

/// <summary>Table split between rows. <paramref name="RepeatHead"/> +
/// <paramref name="RepeatFoot"/> control whether <c>&lt;thead&gt;</c> /
/// <c>&lt;tfoot&gt;</c> are re-emitted on the new page (Task 13 cycle 2
/// will set these; cycle 1 leaves them at <see langword="false"/>).
/// <paramref name="NextRowIndex"/> identifies the next row to lay out
/// (0-based index into the table's collected row list; the valid
/// range on entry is <c>[0, rows.Count]</c> where the upper bound is
/// the "all rows committed; emit bottom captions only" case).
/// <paramref name="ConsumedBlockSize"/> per Phase 3 Task 13 cycle 1
/// hardening — cumulative block-axis size committed across PRIOR pages,
/// matching <see cref="BlockContinuation.ConsumedBlockSize"/>'s
/// semantics. Currently informational (recorded for cost-model
/// lookahead in future cycles); the resume-page TableLayouter
/// recomputes its own page-relative offsets from
/// <paramref name="NextRowIndex"/> alone.
/// <paramref name="ColumnLayoutCache"/> per Phase 3 plan + review fix #7
/// + Task 13 cycle 1 hardening (Finding 8) — opaque cache of the
/// table's resolved column widths + per-cell placements + intrinsic
/// widths so the resume-page TableLayouter skips the (expensive) auto-
/// layout pass. When non-null on resume, the layouter loads the cached
/// values + jumps straight to the cell-content emit pass for the
/// resumed rows.</summary>
internal sealed record TableContinuation(
    bool RepeatHead,
    bool RepeatFoot,
    int NextRowIndex,
    double ConsumedBlockSize = 0.0,
    object? ColumnLayoutCache = null) : LayoutContinuation;

/// <summary>Multi-line flex container split between flex lines.
/// <c>LineIndex</c> identifies the next flex line to emit.
/// <c>BaselineState</c> per Phase 3 plan §"Flex baseline alignment
/// across fragments" + review fix #7 — opaque cross-fragment
/// baseline snapshot so a flex line that splits across pages keeps
/// its baseline alignment.
/// <c>EmittedBlockExtent</c> per Phase 3 Task 16 cycle 4e (P2 #5
/// from PR-#79) — the TRUE occupied cross-axis extent the
/// FlexLayouter consumed for the lines emitted on THIS fragment.
/// Computed in FlexLayouter's emission loop as the content-cross-box
/// 0-based bottom of the deepest emitted line (= INCLUDES
/// align-content's <c>lineStartOffset</c> + <c>lineBetweenSpacing</c>;
/// NOT a naive sum of LineCrossSize). Cycle 4f will use this value
/// to shrink the wrapper's BoxFragment block-size when the cycle-4b
/// paginatable-flex clamp's budget exceeded what actually got
/// emitted; BlockLayouter does NOT yet consume the field — current
/// production code keeps the wrapper at the clamped budget per the
/// z-order constraint below.
/// <b>Z-order constraint for cycle 4f:</b> the wrapper fragment must
/// precede its children in the sink's fragment list (= painter's
/// draw order), so the wrapper emit can't simply move to
/// post-dispatch; a sink-mutation or pre-emit-with-backfill API is
/// required.</summary>
internal sealed record FlexContinuation : LayoutContinuation
{
    public int LineIndex { get; }
    public object? BaselineState { get; }
    public double EmittedBlockExtent { get; }

    /// <summary>Per Phase 3 Task 16 cycle 4e post-PR-#86 review P2 #2
    /// — defensive validation. <c>LineIndex</c> validation lives on
    /// the FlexLayouter resume path (= it must fall within the
    /// container's packed line count). <c>EmittedBlockExtent</c> is
    /// validated here at construction so cycle 4f's consumer side
    /// can trust the value as finite + non-negative. NaN, ±Infinity,
    /// or negative values would poison page accounting + fragment
    /// geometry; surface a contract violation at the source instead
    /// of silently corrupting downstream layout.</summary>
    public FlexContinuation(
        int LineIndex,
        object? BaselineState = null,
        double EmittedBlockExtent = 0.0)
    {
        if (!double.IsFinite(EmittedBlockExtent) || EmittedBlockExtent < 0)
        {
            throw new System.ArgumentOutOfRangeException(
                nameof(EmittedBlockExtent),
                $"FlexContinuation.EmittedBlockExtent must be finite + "
                + $"non-negative; got {EmittedBlockExtent}. NaN / ±Infinity / "
                + "negative values would corrupt cycle 4f's wrapper-resize "
                + "+ ConsumedBlockSize accounting.");
        }
        this.LineIndex = LineIndex;
        this.BaselineState = BaselineState;
        this.EmittedBlockExtent = EmittedBlockExtent;
    }
}

/// <summary>Per Phase 3 Task 17 cycle 5 — grid container split between
/// grid rows. Resume at row <paramref name="RowIndex"/> on the next
/// page, reusing <paramref name="Cache"/> so the resume page doesn't
/// re-resolve track sizes (= expensive multi-pass §11 algorithm) and
/// doesn't re-place items via sparse auto-placement (= would yield a
/// different placement if items had been partially emitted on the
/// prior page).
///
/// <para><b>Per Phase 3 plan + review fix #7</b> — the
/// <paramref name="Cache"/> field was originally typed <c>object?</c>;
/// cycle 5 promoted it to <see cref="GridResumeCache"/>, a concrete
/// internal record carrying the row/column sizes + positions +
/// per-item placement. Per CLAUDE.md "AOT-clean" rules the cache is
/// strongly typed (no runtime cast); the layout pipeline reads it
/// without reflection.</para>
///
/// <para><b>Row-spanning items (cycle 6 scope)</b>: cycle 5 ships
/// pagination assuming each item occupies exactly one row (= cycle 1
/// contract still holds). Cycle 6 introduces <c>span N</c> placement;
/// at that point the row-K computation in
/// <c>GridLayouter.AttemptLayout</c> must account for spanning items
/// (= no item straddles the page break per design doc § cycle 5
/// "spanning items are ATOMIC to their row span").</para>
///
/// <para><b>Force-overflow per CSS Fragmentation L3 §4.4</b>: if a
/// single row + its contained items is taller than the entire
/// fragmentainer, the layouter emits the row anyway + emits a
/// diagnostic. The continuation is still valid + the resume page
/// continues with row K+1; the overflowing row's content "leaks" into
/// the fragmentainer-block-end region (per the progress rule —
/// authoring such CSS is unusual).</para></summary>
internal sealed record GridContinuation(
    int RowIndex,
    GridResumeCache? Cache = null) : LayoutContinuation;

/// <summary>Per Phase 3 Task 17 cycle 5 — opaque snapshot of the
/// first-page Resolve pass that the resume page reuses verbatim.
/// Skipping the resolution + placement passes is a real perf win
/// (= the §11 algorithm is N+M passes over the track count for fr +
/// intrinsic + Maximize, plus the 4-pass §8.5 placement). It is ALSO
/// a correctness requirement: sparse auto-placement is order-
/// sensitive + would yield a DIFFERENT placement on resume if items
/// were partially emitted on the prior page.
///
/// <para><see cref="RowBaseSizes"/> / <see cref="ColumnBaseSizes"/>
/// carry the post-Maximize final track sizes (= the
/// <c>RowSizes</c>/<c>ColSizes</c> the layouter's emit loop reads).
/// <see cref="RowPositions"/> / <see cref="ColumnPositions"/> carry
/// the cumulative offsets in the wrapper's content-box coordinates
/// (= what emit sums to get per-fragment offsets).</para>
///
/// <para><see cref="ItemPlacements"/> per design doc § cycle 5 — the
/// per-item (Row, Col) tuples from the sparse auto-placement pass,
/// in DOM order. The resume layouter iterates this list verbatim +
/// emits only items whose Row ≥ continuation.RowIndex.</para></summary>
internal sealed record GridResumeCache(
    System.Collections.Immutable.ImmutableArray<double> RowBaseSizes,
    System.Collections.Immutable.ImmutableArray<double> ColumnBaseSizes,
    System.Collections.Immutable.ImmutableArray<double> RowPositions,
    System.Collections.Immutable.ImmutableArray<double> ColumnPositions,
    System.Collections.Immutable.ImmutableArray<GridItemPlacement> ItemPlacements);

/// <summary>Per Phase 3 Task 17 cycle 5 — one grid item's placement
/// + a reference to the source box so the resume layouter can
/// re-emit fragments without re-walking the placement algorithm.
///
/// <para><b>Why <see cref="Box"/> is typed <see cref="object"/></b>:
/// <c>NetPdf.Paginate</c> cannot reference <c>NetPdf.Layout</c>
/// without a circular dependency (Layout references Paginate for
/// continuation types). The Layout-side <c>GridLayouter</c> casts
/// back to its <c>Box</c> type. The cast is a single layouter-
/// internal seam + matches the same opaque-payload pattern used by
/// <see cref="BlockContinuation.LayouterState"/> /
/// <see cref="MulticolContinuation.PerChildLayouterState"/> /
/// <see cref="FlexContinuation.BaselineState"/>.</para>
///
/// <para><see cref="Row"/> and <see cref="Col"/> are 0-based final
/// indices; negative values indicate unplaced (= dropped per the
/// implicit-tracks-unsupported diagnostic).</para></summary>
internal sealed record GridItemPlacement(
    object Box,
    int Row,
    int Col);

/// <summary>Per Phase 3 Task 14 cycles 1-2 — multicol container split
/// across pages. CSS Multi-column L1 §2 defines the multicol container
/// as a block formatting context whose in-flow children flow through
/// N parallel columns (sub-fragmentainers) before continuing to the
/// next page. Cycle 1 ships a Hello World multicol with explicit
/// <c>column-count</c>; cycle 2 (this revision) implements multi-page
/// multicol (the outer multicol box fragmenting across pages so
/// overflowing content continues on the next page) per CSS
/// Multi-column L1 §3.5 + CSS Fragmentation L3 §3. Column balancing +
/// <c>column-width</c> auto-count + <c>column-span: all</c> + column
/// rules remain sub-cycle 3+ scope per
/// <c>docs/deferrals.md#multicol-balancing-pagination</c>.
///
/// <para><b>Cycle 2 contract.</b> The continuation captures
/// "where to resume" so the next-page MulticolLayouter picks up content
/// emission at the exact child the previous page ran out of room for.
/// Mirrors <see cref="BlockContinuation"/>'s semantics — the resume
/// page constructs a fresh <c>MulticolLayouter</c> with this
/// continuation; the LAST column's overflowing
/// <see cref="BlockContinuation"/> (if any) lives in
/// <paramref name="PerChildLayouterState"/>, and the FIRST column on
/// the next page resumes that nested <c>BlockLayouter</c> at the same
/// place.</para>
///
/// <para><paramref name="NextChildIndex"/> identifies the next child
/// of the multicol container to start emitting content from when the
/// outer multicol box resumes on the next page. The valid range on
/// entry is <c>[0, container.Children.Count]</c>; the upper bound is
/// the degenerate "all children committed" case (no continuation
/// would actually be produced for that case — the layouter returns
/// AllDone — but the bound is included for symmetry with
/// <see cref="BlockContinuation.ResumeAtChild"/>).</para>
///
/// <para><paramref name="ConsumedBlockSize"/> per Phase 3 Task 14
/// cycle 2 — cumulative block-axis size committed across PRIOR pages,
/// matching <see cref="BlockContinuation.ConsumedBlockSize"/>'s
/// semantics. Currently informational (recorded for cost-model
/// lookahead in future cycles); the resume-page MulticolLayouter
/// recomputes its own page-relative offsets from
/// <paramref name="NextChildIndex"/> alone.</para>
///
/// <para><paramref name="PerChildLayouterState"/> per Phase 3 review
/// fix #7 + Task 14 cycle 2 — opaque carrier for the LAST-column-on-
/// prior-page nested layouter's resume state (e.g., a
/// <see cref="BlockContinuation"/> from the column that overflowed
/// mid-child). When non-null on resume, the FIRST column on the
/// resumed page hands this back to the nested <c>BlockLayouter</c>
/// as <c>incomingContinuation</c>; the column picks up at the right
/// place inside <paramref name="NextChildIndex"/> rather than
/// re-starting that child's emission. Cycle 2 stores
/// <see cref="BlockContinuation"/> instances here; future cycles may
/// also carry nested table / flex / grid continuations through the
/// same seam (mirrors how <see cref="BlockContinuation.LayouterState"/>
/// carries <see cref="TableContinuation"/>).</para>
/// </summary>
internal sealed record MulticolContinuation(
    int NextChildIndex,
    double ConsumedBlockSize = 0.0,
    object? PerChildLayouterState = null) : LayoutContinuation;
