// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;

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
/// <see cref="LayoutAttemptOutcome.NeedsRewind"/> with a non-null
/// <see cref="LayoutAttemptResult.RewindTo"/>.</para>
///
/// <para><b>Fragment emission contract (Phase 3 Task 5 PR #21 review
/// fix #3 + Copilot resolution).</b> Two valid emission patterns:
/// <list type="bullet">
///   <item><b>All-or-nothing (no rollback hook).</b> The layouter
///   buffers its fragments internally and only flushes to the sink
///   on <see cref="LayoutAttemptOutcome.PageComplete"/> /
///   <see cref="LayoutAttemptOutcome.AllDone"/>. Returning
///   <see cref="LayoutAttemptOutcome.NeedsRewind"/> MUST NOT have
///   produced any visible side effects on the sink.</item>
///   <item><b>Incremental with rollback (opt-in).</b> The layouter
///   emits fragments as it goes, and registers an
///   <see cref="IFragmentSink"/> with the
///   <see cref="LayoutRetryCoordinator"/>; on rewind, the coordinator
///   calls <see cref="IFragmentSink.RollbackTo"/> with the
///   checkpoint's
///   <see cref="LayoutCheckpoint.FragmentOutputCursor"/> to discard
///   the speculative fragments. The layouter's
///   <see cref="LayoutAttemptOutcome.NeedsRewind"/> result is
///   permitted because the rollback hook will undo the emission.</item>
/// </list>
/// A layouter MAY NOT mix the two patterns: emitting incrementally
/// without registering an <see cref="IFragmentSink"/> leaves the
/// sink in a corrupted state on rewind (retry restores layout state
/// but cannot un-emit fragments without the explicit rollback hook).</para>
///
/// <para><b>Cancellation (Phase 3 Task 5 PR #21 review fix #6).</b>
/// The <see cref="CancellationToken"/> threads through to layouters
/// for cooperative cancellation. The coordinator checks the token
/// before each attempt; layouters SHOULD check it inside long-running
/// inner loops (per-element / per-line) so a slow document doesn't
/// burn through retries past the caller's deadline.</para>
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
    ///   <see cref="LayoutAttemptOutcome.NeedsRewind"/> with a
    ///   non-<see langword="null"/> <see cref="LayoutAttemptResult.RewindTo"/>
    ///   naming the checkpoint to restore from. Per PR #21 review
    ///   fix #1 the coordinator throws
    ///   <see cref="InvalidOperationException"/> when this invariant
    ///   is violated. Forbidden when <paramref name="strategy"/> is
    ///   <see cref="LayoutAttemptStrategy.LastResort"/>.</item>
    /// </list>
    /// </summary>
    LayoutAttemptResult AttemptLayout(
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        LayoutAttemptStrategy strategy,
        CancellationToken cancellationToken = default);
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
    /// a relaxed strategy. <see cref="LayoutAttemptResult.RewindTo"/>
    /// MUST be non-<see langword="null"/> per PR #21 review fix #1 —
    /// the coordinator throws <see cref="InvalidOperationException"/>
    /// when the invariant is violated. Forbidden when the current
    /// strategy is <see cref="LayoutAttemptStrategy.LastResort"/>.</summary>
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
///
/// <para><b>Invariants (PR #21 review fix #1).</b></para>
/// <list type="bullet">
///   <item><see cref="Outcome"/> = <see cref="LayoutAttemptOutcome.NeedsRewind"/>
///   ⟹ <see cref="RewindTo"/> non-<see langword="null"/>. The
///   coordinator throws <see cref="InvalidOperationException"/> when
///   this invariant is violated to prevent silent dirty-state retry.</item>
///   <item><see cref="Outcome"/> = <see cref="LayoutAttemptOutcome.AllDone"/>
///   ⟹ <see cref="Continuation"/> = <see langword="null"/>.</item>
/// </list>
/// Use <see cref="ValidateOrThrow"/> at any boundary that consumes a
/// raw result struct.
/// </summary>
/// <param name="Outcome">Category of result; the coordinator branches
/// on this value.</param>
/// <param name="Continuation">For <see cref="LayoutAttemptOutcome.PageComplete"/>,
/// the continuation token describing where the next page should resume.
/// <see langword="null"/> for <see cref="LayoutAttemptOutcome.AllDone"/>
/// + <see cref="LayoutAttemptOutcome.NeedsRewind"/>.</param>
/// <param name="RewindTo">For <see cref="LayoutAttemptOutcome.NeedsRewind"/>,
/// the checkpoint to restore state from before retrying. MUST be
/// non-<see langword="null"/> for the rewind outcome (PR #21 review
/// fix #1). <see langword="null"/> for the other outcomes.</param>
/// <param name="Cost"><see cref="CostModel"/> cost accumulated during
/// this attempt; used by the coordinator to track best-cost across
/// attempts (currently informational; Phase 3 Task 7+ may use it for
/// best-attempt selection across retries). Can be 0 for trivial
/// fast-path results, non-zero for layouts that ran cost-model
/// scoring.</param>
internal readonly record struct LayoutAttemptResult(
    LayoutAttemptOutcome Outcome,
    LayoutContinuation? Continuation,
    LayoutCheckpoint? RewindTo,
    double Cost)
{
    /// <summary>Per PR #21 review fix #1 + PR #24 review pass — verify
    /// the result honors the documented invariants. Throws
    /// <see cref="InvalidOperationException"/> when violated; the
    /// coordinator calls this on every layouter response so a
    /// misbehaving layouter is caught immediately rather than
    /// silently retrying from dirty state OR returning ambiguous
    /// outcomes.
    ///
    /// <para><b>Invariants checked:</b></para>
    /// <list type="bullet">
    ///   <item><see cref="LayoutAttemptOutcome.NeedsRewind"/> ⟹
    ///   <see cref="RewindTo"/> non-<see langword="null"/>. Without
    ///   the checkpoint the coordinator can't restore state for
    ///   the retry.</item>
    ///   <item><b>PR #24 review pass — <see cref="LayoutAttemptOutcome.PageComplete"/>
    ///   ⟹ <see cref="Continuation"/> non-<see langword="null"/>.</b>
    ///   "PageComplete" means "the page is full; the next page must
    ///   resume from the carried continuation". A null continuation
    ///   would mean "the next page has no resume target" which is
    ///   the <see cref="LayoutAttemptOutcome.AllDone"/> semantic.
    ///   Allowing both <c>PageComplete(null)</c> + <c>AllDone</c>
    ///   creates an ambiguity where the coordinator + caller can't
    ///   tell whether more pages are needed. Pre-fix the cycle-1
    ///   factory let layouters construct <c>PageComplete(null)</c>
    ///   + the test doubles relied on this; this revision fails
    ///   fast on the construction path.</item>
    ///   <item><see cref="LayoutAttemptOutcome.AllDone"/> ⟹
    ///   <see cref="Continuation"/> <see langword="null"/>. The
    ///   inverse of the PageComplete invariant.</item>
    /// </list></summary>
    public void ValidateOrThrow()
    {
        if (Outcome == LayoutAttemptOutcome.NeedsRewind && RewindTo is null)
        {
            throw new InvalidOperationException(
                "LayoutAttemptResult invariant violation: "
                + "Outcome=NeedsRewind requires non-null RewindTo. "
                + "A layouter that returns NeedsRewind without naming a "
                + "checkpoint causes the coordinator to retry from dirty "
                + "state — silently corrupting downstream pagination. "
                + "Use LayoutAttemptResult.NeedsRewind(checkpoint, cost) "
                + "factory or capture a checkpoint before declining.");
        }
        if (Outcome == LayoutAttemptOutcome.PageComplete && Continuation is null)
        {
            // Per PR #24 review pass — PageComplete must carry a
            // continuation. AllDone is the no-continuation success
            // outcome.
            throw new InvalidOperationException(
                "LayoutAttemptResult invariant violation: "
                + "Outcome=PageComplete requires non-null Continuation. "
                + "PageComplete means the next page must know where to resume; "
                + "use AllDone for the no-more-pages-needed outcome.");
        }
        if (Outcome == LayoutAttemptOutcome.AllDone && Continuation is not null)
        {
            throw new InvalidOperationException(
                "LayoutAttemptResult invariant violation: "
                + "Outcome=AllDone requires null Continuation. AllDone "
                + "means no more pages are needed; a continuation token "
                + "would imply otherwise.");
        }
    }

    /// <summary>Convenience: a "page complete" result with the named
    /// continuation + cost.
    ///
    /// <para>Per PR #24 review pass — <paramref name="continuation"/>
    /// is non-nullable. PageComplete means "this page is full; the
    /// next page must know where to resume" — a null continuation
    /// is the AllDone semantic. The factory enforces the invariant
    /// at the construction boundary; <see cref="ValidateOrThrow"/>
    /// catches direct-record-construction violations.</para></summary>
    public static LayoutAttemptResult PageComplete(LayoutContinuation continuation, double cost)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return new(LayoutAttemptOutcome.PageComplete, continuation, RewindTo: null, cost);
    }

    /// <summary>Convenience: an "all done" result for layouters that
    /// finished all content within the current fragmentainer.</summary>
    public static LayoutAttemptResult AllDone(double cost) =>
        new(LayoutAttemptOutcome.AllDone, Continuation: null, RewindTo: null, cost);

    /// <summary>Convenience: a "needs rewind" result naming the
    /// checkpoint to restore from. Per PR #21 review fix #1 — the
    /// <paramref name="rewindTo"/> parameter is non-nullable, so
    /// constructing a NeedsRewind via this factory cannot violate
    /// the invariant.</summary>
    public static LayoutAttemptResult NeedsRewind(LayoutCheckpoint rewindTo, double cost)
    {
        ArgumentNullException.ThrowIfNull(rewindTo);
        return new(LayoutAttemptOutcome.NeedsRewind, Continuation: null, rewindTo, cost);
    }
}

/// <summary>
/// Per Phase 3 Task 5 PR #21 review fix #3 — fragment-output rollback
/// hook. The coordinator passes this to layouters that emit fragments
/// incrementally; on rewind the coordinator calls
/// <see cref="RollbackTo"/> with the checkpoint's
/// <see cref="LayoutCheckpoint.FragmentOutputCursor"/> so any fragments
/// emitted past that cursor are discarded before the retry.
///
/// <para>Layouters that emit fragments only on
/// <see cref="LayoutAttemptOutcome.PageComplete"/> (i.e., never
/// before they're sure they won't rewind) don't need this — the
/// coordinator's <c>fragmentSink</c> can stay
/// <see langword="null"/>. The plan section "Re-layout loop bound"
/// + the <see cref="LayoutCheckpoint.FragmentOutputCursor"/> docstring
/// describe this contract; the hook is the wire that lets it work.</para>
/// </summary>
internal interface IFragmentSink
{
    /// <summary>Discard any fragments emitted past
    /// <paramref name="cursor"/>. Called by the coordinator on rewind
    /// before the layouter retries. After this call the sink's state
    /// MUST match what it was when the checkpoint was captured (i.e.,
    /// the next emission appends starting from position
    /// <paramref name="cursor"/>).</summary>
    void RollbackTo(int cursor);
}
