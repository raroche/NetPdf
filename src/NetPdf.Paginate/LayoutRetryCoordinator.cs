// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 Task 5 + plan §"Re-layout loop discipline" — the
/// bounded-retry orchestrator that drives an <see cref="ILayouter"/>
/// through up to 2 retries with progressively-relaxed
/// <see cref="LayoutAttemptStrategy"/>, emitting
/// <see cref="PaginateDiagnosticCodes.PaginationForcedOverflow001"/>
/// when the constraints can't be satisfied + a last-resort attempt
/// is required.
///
/// <para><b>Algorithm.</b> Per the plan's pseudocode:</para>
/// <list type="number">
///   <item>Attempt 0 — <see cref="LayoutAttemptStrategy.Strict"/>.
///   Honor all constraints. If layouter returns
///   <see cref="LayoutAttemptOutcome.NeedsRewind"/>, restore from
///   the named checkpoint + sync <c>fragmentainer</c> from
///   <c>layout.Fragmentainer</c> (PR #21 review fix #2) + advance
///   to attempt 1.</item>
///   <item>Attempt 1 — <see cref="LayoutAttemptStrategy.DropAvoidInside"/>.
///   Drop <c>break-inside: avoid</c> constraints. If layouter still
///   returns <see cref="LayoutAttemptOutcome.NeedsRewind"/>, restore
///   + advance to attempt 2.</item>
///   <item>Attempt 2 — <see cref="LayoutAttemptStrategy.LastResort"/>.
///   Emit <c>PAGINATION-FORCED-OVERFLOW-001</c> + run the layouter
///   once more. The layouter MUST commit a result on this attempt
///   (returning <see cref="LayoutAttemptOutcome.NeedsRewind"/> is a
///   contract violation).</item>
/// </list>
///
/// <para><b>PR #21 review fixes.</b></para>
/// <list type="bullet">
///   <item><b>#1 — NeedsRewind requires non-null RewindTo.</b> The
///   coordinator calls
///   <see cref="LayoutAttemptResult.ValidateOrThrow"/> on every
///   layouter response. A NeedsRewind result with null RewindTo
///   throws <see cref="InvalidOperationException"/> rather than
///   silently retrying from dirty state.</item>
///   <item><b>#2 — Fragmentainer sync after restore.</b>
///   <see cref="LayoutCheckpoint.RestoreInto"/> may reseat
///   <c>layout.Fragmentainer</c> back to the captured one (per Task 4
///   review fix #6 — speculative-swap undo). The coordinator now
///   reassigns its local <c>fragmentainer</c> from
///   <c>layout.Fragmentainer</c> after each restore, so the next
///   attempt receives the correct instance.</item>
///   <item><b>#3 — Fragment-output rollback.</b> Optional
///   <see cref="IFragmentSink"/> — when supplied, the coordinator
///   calls <see cref="IFragmentSink.RollbackTo"/> with the
///   checkpoint's <see cref="LayoutCheckpoint.FragmentOutputCursor"/>
///   on rewind. Layouters that emit only on
///   <see cref="LayoutAttemptOutcome.PageComplete"/> can leave the
///   sink <see langword="null"/>.</item>
///   <item><b>#6 — Cancellation.</b> <see cref="CancellationToken"/>
///   threaded through; checked before each attempt so a slow
///   document doesn't burn through retries past the caller's
///   deadline.</item>
/// </list>
///
/// <para><b>Why bounded.</b> Per the plan's "Common pitfalls" section
/// (#"Re-layout loop infinite recursion"): without a hard cap, a
/// pathological combination of constraints could loop forever.
/// 2 retries is enough for the CSS Fragmentation L3 §5.2 last-resort
/// algorithm (drop break-inside-avoid → drop break-before/after-avoid
/// → emit overflow); higher values aren't supported by the spec
/// either.</para>
/// </summary>
internal sealed class LayoutRetryCoordinator
{
    /// <summary>Per Phase 3 plan §"Re-layout loop bound" — max 2
    /// retries per fragmentainer. Pinned at 2; the plan's "Common
    /// pitfalls" explicitly cites this value as the safety bound.</summary>
    public const int MaxRetries = 2;

    /// <summary>Optional diagnostic sink. When non-<see langword="null"/>,
    /// receives <c>PAGINATION-FORCED-OVERFLOW-001</c> when the
    /// LastResort attempt is invoked.</summary>
    public IPaginateDiagnosticsSink? Diagnostics { get; }

    /// <summary>Per Phase 3 Task 5 PR #21 review fix #3 — optional
    /// fragment-output rollback sink. When non-<see langword="null"/>,
    /// the coordinator calls <see cref="IFragmentSink.RollbackTo"/>
    /// with the checkpoint's
    /// <see cref="LayoutCheckpoint.FragmentOutputCursor"/> on rewind
    /// before the next attempt runs.</summary>
    public IFragmentSink? FragmentSink { get; }

    public LayoutRetryCoordinator(
        IPaginateDiagnosticsSink? diagnostics = null,
        IFragmentSink? fragmentSink = null)
    {
        Diagnostics = diagnostics;
        FragmentSink = fragmentSink;
    }

    /// <summary>Run <paramref name="layouter"/> against
    /// <paramref name="fragmentainer"/> + <paramref name="layout"/>
    /// with bounded retry. Returns the final result — either
    /// <see cref="LayoutAttemptOutcome.PageComplete"/>,
    /// <see cref="LayoutAttemptOutcome.AllDone"/>, or (after
    /// 2 retries + LastResort) the layouter's best-effort result.
    /// <see cref="LayoutAttemptOutcome.NeedsRewind"/> is never returned
    /// — the coordinator either resolves it via retry or escalates
    /// to LastResort.</summary>
    /// <exception cref="InvalidOperationException">A layouter
    /// returned <see cref="LayoutAttemptOutcome.NeedsRewind"/> with
    /// a null <see cref="LayoutAttemptResult.RewindTo"/> — contract
    /// violation per PR #21 review fix #1.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired before or during a
    /// retry attempt.</exception>
    public LayoutAttemptResult Run(
        ILayouter layouter,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layouter);
        ArgumentNullException.ThrowIfNull(fragmentainer);
        ArgumentNullException.ThrowIfNull(resolver);

        // Per post-Task-7 review (recommendation P1 #2) — thread the
        // coordinator's diagnostic sink into the layout context so the
        // layouter's forced-overflow path emits via the same sink.
        // Pre-fix, the coordinator's sink + each layouter's sink were
        // wired separately; a composition root that wired only the
        // coordinator could miss PAGINATION-FORCED-OVERFLOW-001 from
        // a layouter's Strict-attempt forward-progress path.
        //
        // Only sets layout.Diagnostics when it's null on entry —
        // respects the caller's choice to override (e.g., a test
        // wrapping with a recording sink).
        if (Diagnostics is not null && layout.Diagnostics is null)
        {
            layout.Diagnostics = Diagnostics;
        }

        // Capture the original page index for diagnostic messaging —
        // RestoreInto on a partially-captured checkpoint can reset
        // PageIndex to 0, which would mislead the consumer about
        // which page had problems.
        var originalPageIndex = fragmentainer.PageIndex;

        // Track best-cost across attempts so a future enhancement
        // could pick the cheapest attempt's result. Currently the
        // coordinator returns the latest result.
        double bestCost = double.PositiveInfinity;
        LayoutAttemptResult? bestResult = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            // Per PR #21 review fix #6 — cancellation check before
            // each attempt. Layouters SHOULD check inside their long-
            // running inner loops too.
            cancellationToken.ThrowIfCancellationRequested();

            var strategy = AttemptToStrategy(attempt);

            // Per Phase 3 Task 5 — emit the forced-overflow diagnostic
            // BEFORE running the LastResort attempt. This way consumers
            // see the warning even if the layouter itself has issues
            // during the last attempt (e.g., throws). The diagnostic
            // describes what the coordinator IS ABOUT TO DO, not what
            // happened.
            if (strategy == LayoutAttemptStrategy.LastResort)
            {
                // Per PR #24 review pass — guard against sink throws.
                // The IPaginateDiagnosticsSink contract says
                // implementations MUST NOT throw, but a misbehaving
                // sink (e.g., a hostile host adapter) shouldn't be
                // able to take down the layout pipeline + leave the
                // coordinator in an inconsistent retry state.
                OptimizingBreakResolver.SafeEmit(Diagnostics, new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    $"Pagination required {MaxRetries} retries on fragmentainer page index "
                    + $"{originalPageIndex}; falling back to last-resort layout that "
                    + "drops avoid-break constraints + commits best-effort result.",
                    PaginateDiagnosticSeverity.Warning));
            }

            var result = layouter.AttemptLayout(
                fragmentainer, ref layout, resolver, strategy, cancellationToken);

            // Per PR #21 review fix #1 — enforce the NeedsRewind
            // invariant. A null RewindTo here means the layouter
            // wanted to retry without restoring state — which would
            // silently corrupt the next attempt.
            result.ValidateOrThrow();

            if (result.Outcome != LayoutAttemptOutcome.NeedsRewind)
            {
                if (result.Cost < bestCost)
                {
                    bestCost = result.Cost;
                    bestResult = result;
                }
                return result;
            }

            // ---- NeedsRewind path. ----

            if (strategy == LayoutAttemptStrategy.LastResort)
            {
                // Per PR #24 review pass — a layouter returning
                // NeedsRewind on the LastResort attempt is a HARD
                // contract violation. The strategy explicitly
                // forbids it (see ILayouter XML doc). Pre-fix the
                // coordinator silently fabricated a PageComplete
                // result, but that:
                //   (a) returned a PageComplete with a potentially
                //       null Continuation (the new ValidateOrThrow
                //       would reject this construction), and
                //   (b) hid a real layouter bug that could drop
                //       content + corrupt downstream pagination.
                // Throw fail-fast so the caller (test or layout
                // pipeline) sees the violation immediately.
                throw new InvalidOperationException(
                    "ILayouter contract violation: layouter returned "
                    + "LayoutAttemptOutcome.NeedsRewind on the LastResort "
                    + "attempt. LastResort is the final retry — the "
                    + "layouter MUST commit a result (PageComplete or "
                    + "AllDone) regardless of constraint violations. "
                    + "See ILayouter.AttemptLayout XML doc for the "
                    + "contract. RewindTo checkpoint = "
                    + (result.RewindTo is not null ? "<set>" : "<null>") + ".");
            }

            // Track best-cost from the failed-but-instructive attempt.
            if (result.Cost < bestCost)
            {
                bestCost = result.Cost;
                bestResult = result;
            }

            // Per PR #21 review fix #3 — roll back any fragments
            // emitted past the checkpoint's cursor. The optional sink
            // is the layouter's responsibility to register; layouters
            // that emit only on PageComplete can leave it null.
            FragmentSink?.RollbackTo(result.RewindTo!.FragmentOutputCursor);

            // Restore from the named checkpoint. Per Phase 3 Task 4
            // review fix #6 — RestoreInto reseats layout.Fragmentainer
            // to the captured one if a speculative swap occurred.
            // RewindTo is guaranteed non-null by ValidateOrThrow above.
            result.RewindTo!.RestoreInto(fragmentainer, ref layout);

            // Per PR #21 review fix #2 — synchronize the local
            // fragmentainer with layout.Fragmentainer. RestoreInto
            // may have reseated layout.Fragmentainer to the captured
            // (original) instance; without this sync the next
            // AttemptLayout would receive a different fragmentainer
            // than what layout.Fragmentainer points at.
            fragmentainer = layout.Fragmentainer;
        }

        // Unreachable in well-behaved control flow — the LastResort
        // branch above always returns. Defensive fallback returns the
        // best-cost result observed (or a synthetic PageComplete if
        // somehow no result was captured).
        return bestResult ?? new LayoutAttemptResult(
            LayoutAttemptOutcome.PageComplete,
            Continuation: null,
            RewindTo: null,
            Cost: 0);
    }

    /// <summary>Map an attempt index (0..<see cref="MaxRetries"/>) to
    /// the corresponding <see cref="LayoutAttemptStrategy"/>. Encodes
    /// the progressive-relaxation order from the plan's
    /// "Re-layout loop discipline" section.</summary>
    internal static LayoutAttemptStrategy AttemptToStrategy(int attempt) => attempt switch
    {
        0 => LayoutAttemptStrategy.Strict,
        1 => LayoutAttemptStrategy.DropAvoidInside,
        _ => LayoutAttemptStrategy.LastResort,
    };
}
