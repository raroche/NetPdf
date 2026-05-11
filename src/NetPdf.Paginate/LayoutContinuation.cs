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
/// (0-based index into the table's collected row list).
/// <paramref name="ConsumedBlockSize"/> per Phase 3 Task 13 cycle 1
/// hardening — cumulative block-axis size committed across PRIOR pages,
/// matching <see cref="BlockContinuation.ConsumedBlockSize"/>'s
/// semantics. The outer dispatcher uses this to position fragments on
/// the resume page consistent with the page-relative origin contract;
/// future cycles may consume it for cost-model lookahead.
/// <paramref name="ColumnLayoutCache"/> per Phase 3 plan + review fix #7
/// — opaque cache of the table's resolved column widths so the next-
/// page resume doesn't re-run the auto-layout pass (Task 13 cycle 1
/// re-runs <see cref="LayoutContinuation"/> the measure pass on resume
/// for simplicity; cycle 2 may plumb the cache here).</summary>
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
/// pages keeps its baseline alignment.</summary>
internal sealed record FlexContinuation(
    int LineIndex,
    object? BaselineState = null) : LayoutContinuation;

/// <summary>Grid container split between grid rows.
/// <paramref name="RowIndex"/> identifies the next grid row to emit.
/// <paramref name="TrackSizingCache"/> per Phase 3 plan + review fix #7
/// — opaque snapshot of the resolved track-sizing pass so the next-
/// page resume skips the (expensive) two-pass intrinsic + flex-track
/// distribution.</summary>
internal sealed record GridContinuation(
    int RowIndex,
    object? TrackSizingCache = null) : LayoutContinuation;
