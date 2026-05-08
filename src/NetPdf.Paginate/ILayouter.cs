// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 Task 5 + plan §"Re-layout loop discipline" — the
/// layouter-facing contract that <see cref="LayoutRetryCoordinator"/>
/// orchestrates. Real layouters (Phase 3 Task 7+'s <c>BlockLayouter</c>,
/// <c>InlineLayouter</c>, <c>TableLayouter</c>, <c>FlexLayouter</c>,
/// <c>GridLayouter</c>) implement this interface so the bounded retry
/// loop can drive any layouter uniformly.
///
/// <para><b>The retry contract.</b> A layouter may need MULTIPLE
/// attempts at one fragmentainer when constraints conflict — e.g., a
/// <c>break-inside: avoid</c> region doesn't fit on the current page.
/// The coordinator gives the layouter up to 2 retries (Phase 3 plan
/// pin), passing a progressively-relaxed
/// <see cref="LayoutAttemptStrategy"/> on each attempt. The layouter
/// MUST honor the strategy:</para>
/// <list type="bullet">
///   <item><see cref="LayoutAttemptStrategy.Strict"/> — full
///   constraints (avoid-break, avoid-page, etc. all in effect).
///   The layouter may return <see cref="LayoutAttemptOutcome.NeedsRewind"/>
///   if a constraint can't be satisfied.</item>
///   <item><see cref="LayoutAttemptStrategy.DropAvoidInside"/> —
///   drop <c>break-inside: avoid</c> constraints. The layouter
///   may still return <see cref="LayoutAttemptOutcome.NeedsRewind"/>
///   for stronger constraints (<c>break-before/after: avoid</c>).</item>
///   <item><see cref="LayoutAttemptStrategy.LastResort"/> — the
///   coordinator's final attempt. The layouter MUST commit a
///   best-effort result; returning <see cref="LayoutAttemptOutcome.NeedsRewind"/>
///   here is a contract violation. The coordinator emits
///   <see cref="Diagnostics.PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
///   when this attempt is invoked.</item>
/// </list>
///
/// <para>Layouters consult the <see cref="IBreakResolver"/> at every
/// candidate break point during a single <see cref="AttemptLayout"/>
/// call. The resolver may return <see cref="BreakAction.Rewind"/> to
/// signal "this configuration can't satisfy constraints; restore +
/// retry"; the layouter then propagates that decision up via
/// <see cref="LayoutAttemptResult.Outcome"/> =
/// <see cref="LayoutAttemptOutcome.NeedsRewind"/>.</para>
/// </summary>
internal interface ILayouter
{
    /// <summary>Lay out content into the current fragmentainer. The
    /// layouter consults <paramref name="resolver"/> at every candidate
    /// break point + writes mutations to <paramref name="fragmentainer"/>
    /// + <paramref name="layout"/> as it progresses.
    ///
    /// <para>Returns when:</para>
    /// <list type="bullet">
    ///   <item>The current page is filled →
    ///   <see cref="LayoutAttemptOutcome.PageComplete"/> with
    ///   <see cref="LayoutAttemptResult.Continuation"/> describing
    ///   where the next page should resume.</item>
    ///   <item>All content has been laid out →
    ///   <see cref="LayoutAttemptOutcome.AllDone"/> with
    ///   <see cref="LayoutAttemptResult.Continuation"/> = <see langword="null"/>.</item>
    ///   <item>The resolver returned <see cref="BreakAction.Rewind"/> →
    ///   <see cref="LayoutAttemptOutcome.NeedsRewind"/> with
    ///   <see cref="LayoutAttemptResult.RewindTo"/> naming the
    ///   checkpoint to restore from. The coordinator handles the
    ///   restore + retry. Forbidden when <paramref name="strategy"/>
    ///   is <see cref="LayoutAttemptStrategy.LastResort"/>.</item>
    /// </list>
    /// </summary>
    LayoutAttemptResult AttemptLayout(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy);
}

/// <summary>
/// Per Phase 3 Task 5 — outcome categorization for a single
/// <see cref="ILayouter.AttemptLayout"/> call. The coordinator's
/// retry loop branches on this value.
/// </summary>
internal enum LayoutAttemptOutcome
{
    /// <summary>The current fragmentainer was filled; the layouter
    /// emitted its fragments. <see cref="LayoutAttemptResult.Continuation"/>
    /// names where the next page should resume.</summary>
    PageComplete = 0,

    /// <summary>All content has been laid out. No more pages needed.
    /// <see cref="LayoutAttemptResult.Continuation"/> is
    /// <see langword="null"/>.</summary>
    AllDone = 1,

    /// <summary>The resolver requested a rewind (typically because a
    /// constraint cannot be satisfied under the current
    /// <see cref="LayoutAttemptStrategy"/>). The coordinator restores
    /// from <see cref="LayoutAttemptResult.RewindTo"/> + retries with
    /// a relaxed strategy. Forbidden when the current strategy is
    /// <see cref="LayoutAttemptStrategy.LastResort"/>.</summary>
    NeedsRewind = 2,
}

/// <summary>
/// Per Phase 3 Task 5 + plan §"Re-layout loop discipline" — the
/// progressive-relaxation strategy the coordinator passes on each
/// attempt. Strategies are ordered: each higher-numbered strategy
/// drops more constraints than the lower one. Layouters honor the
/// strategy by suppressing the corresponding
/// <see cref="LayoutAttemptOutcome.NeedsRewind"/> path.
/// </summary>
internal enum LayoutAttemptStrategy
{
    /// <summary>Honor all constraints (<c>break-before</c>,
    /// <c>break-inside</c>, <c>break-after</c> at all values; avoid
    /// + parity + force). The layouter may emit
    /// <see cref="LayoutAttemptOutcome.NeedsRewind"/> when a
    /// constraint cannot be satisfied.</summary>
    Strict = 0,

    /// <summary>Drop <c>break-inside: avoid</c> constraints. Per
    /// CSS Fragmentation L3 §3.2 — when an avoid-region is taller
    /// than the fragmentainer, splitting becomes necessary. The
    /// layouter still honors <c>break-before/after</c> avoid +
    /// forced-break + parity.</summary>
    DropAvoidInside = 1,

    /// <summary>Last resort — commit a best-effort result regardless
    /// of constraint violations. The coordinator emits
    /// <see cref="Diagnostics.PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
    /// when this attempt is invoked. Layouters MUST NOT return
    /// <see cref="LayoutAttemptOutcome.NeedsRewind"/> on this
    /// strategy — there is no further retry.</summary>
    LastResort = 2,
}

/// <summary>
/// Per Phase 3 Task 5 — result of a single
/// <see cref="ILayouter.AttemptLayout"/> call.
/// </summary>
/// <param name="Outcome">Category of result; the coordinator branches
/// on this value.</param>
/// <param name="Continuation">For <see cref="LayoutAttemptOutcome.PageComplete"/>,
/// the continuation token describing where the next page should resume.
/// <see langword="null"/> for <see cref="LayoutAttemptOutcome.AllDone"/>
/// + <see cref="LayoutAttemptOutcome.NeedsRewind"/>.</param>
/// <param name="RewindTo">For <see cref="LayoutAttemptOutcome.NeedsRewind"/>,
/// the checkpoint to restore state from before retrying.
/// <see langword="null"/> for the other outcomes.</param>
/// <param name="Cost">Total <see cref="CostModel"/> cost accumulated
/// during this attempt; used by the coordinator to track best-cost
/// across attempts. 0 when the attempt didn't run any cost-model
/// scoring (e.g., trivial early-return).</param>
internal readonly record struct LayoutAttemptResult(
    LayoutAttemptOutcome Outcome,
    LayoutContinuation? Continuation,
    LayoutCheckpoint? RewindTo,
    double Cost)
{
    /// <summary>Convenience: a "page complete + zero cost" result
    /// for trivial-fit cases.</summary>
    public static LayoutAttemptResult PageComplete(LayoutContinuation? continuation, double cost) =>
        new(LayoutAttemptOutcome.PageComplete, continuation, RewindTo: null, cost);

    /// <summary>Convenience: an "all done" result for layouters that
    /// finished all content within the current fragmentainer.</summary>
    public static LayoutAttemptResult AllDone(double cost) =>
        new(LayoutAttemptOutcome.AllDone, Continuation: null, RewindTo: null, cost);

    /// <summary>Convenience: a "needs rewind" result naming the
    /// checkpoint to restore from.</summary>
    public static LayoutAttemptResult NeedsRewind(LayoutCheckpoint rewindTo, double cost) =>
        new(LayoutAttemptOutcome.NeedsRewind, Continuation: null, rewindTo, cost);
}
