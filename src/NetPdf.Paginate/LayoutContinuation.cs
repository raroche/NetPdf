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
///   (with thead / tfoot repetition).</item>
///   <item><see cref="FlexContinuation"/> — multi-line flex container
///   split between flex lines.</item>
///   <item><see cref="GridContinuation"/> — grid container split between
///   grid rows.</item>
/// </list>
///
/// <para>Continuations are immutable + pooled where lifetime allows.
/// The layouter that produces a continuation is the one that consumes
/// it on the next-page pass — the type discriminates the resume strategy.</para>
/// </summary>
internal abstract record LayoutContinuation;

/// <summary>Block container split between children. Resume at child
/// <paramref name="ResumeAtChild"/> with <paramref name="ConsumedHeight"/>
/// already emitted on prior pages.</summary>
internal sealed record BlockContinuation(
    int ResumeAtChild,
    double ConsumedHeight) : LayoutContinuation;

/// <summary>Inline run / cluster split inside a line. Resume at run
/// <paramref name="RunIndex"/>, glyph cluster
/// <paramref name="ClusterIndex"/>.</summary>
internal sealed record InlineContinuation(
    int RunIndex,
    int ClusterIndex) : LayoutContinuation;

/// <summary>Table split between rows. <paramref name="RepeatHead"/> +
/// <paramref name="RepeatFoot"/> control whether <c>&lt;thead&gt;</c> /
/// <c>&lt;tfoot&gt;</c> are re-emitted on the new page.
/// <paramref name="NextRowIndex"/> identifies the next row to lay out.</summary>
internal sealed record TableContinuation(
    bool RepeatHead,
    bool RepeatFoot,
    int NextRowIndex) : LayoutContinuation;

/// <summary>Multi-line flex container split between flex lines. Carries
/// the cross-axis baseline state so the next-page resume can keep
/// alignment consistent with the on-page lines.</summary>
internal sealed record FlexContinuation(
    int LineIndex) : LayoutContinuation;

/// <summary>Grid container split between grid rows. Track-sizing cache
/// is recomputed on resume per the Phase 3 plan; <paramref name="RowIndex"/>
/// identifies the next row.</summary>
internal sealed record GridContinuation(
    int RowIndex) : LayoutContinuation;
