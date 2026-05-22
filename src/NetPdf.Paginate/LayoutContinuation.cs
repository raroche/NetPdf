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
/// <paramref name="LineIndex"/> identifies the next flex line to
/// emit. <paramref name="BaselineState"/> per Phase 3 plan §"Flex
/// baseline alignment across fragments" + review fix #7 — opaque
/// cross-fragment baseline snapshot so a flex line that splits across
/// pages keeps its baseline alignment.
/// <paramref name="EmittedBlockExtent"/> per Phase 3 Task 16 cycle 4e
/// (P2 #5 from PR-#79) — the cross-axis extent the FlexLayouter
/// actually consumed for the lines emitted on THIS fragment (=
/// cumulative sum of <c>LineCrossSize</c> for emitted lines).
/// BlockLayouter's PageComplete propagation uses this for accurate
/// cross-page <c>ConsumedBlockSize</c> accounting; future cycle 4f
/// will use it to shrink the wrapper's BoxFragment block-size when
/// the cycle-4b paginatable-flex clamp's budget exceeded what
/// actually got emitted. <b>Z-order constraint for cycle 4f:</b>
/// the wrapper fragment must precede its children in the sink's
/// fragment list (= painter's draw order), so the wrapper emit
/// can't simply move to post-dispatch; a sink-mutation or
/// pre-emit-with-backfill API is required.</summary>
internal sealed record FlexContinuation(
    int LineIndex,
    object? BaselineState = null,
    double EmittedBlockExtent = 0.0) : LayoutContinuation;

/// <summary>Grid container split between grid rows.
/// <paramref name="RowIndex"/> identifies the next grid row to emit.
/// <paramref name="TrackSizingCache"/> per Phase 3 plan + review fix #7
/// — opaque snapshot of the resolved track-sizing pass so the next-
/// page resume skips the (expensive) two-pass intrinsic + flex-track
/// distribution.</summary>
internal sealed record GridContinuation(
    int RowIndex,
    object? TrackSizingCache = null) : LayoutContinuation;

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
