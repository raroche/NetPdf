// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
///   the named checkpoint + advance to attempt 1.</item>
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
/// <para><b>Why bounded.</b> Per the plan's "Common pitfalls" section
/// (#"Re-layout loop infinite recursion"): without a hard cap, a
/// pathological combination of constraints could loop forever.
/// 2 retries is enough for the CSS Fragmentation L3 §5.2 last-resort
/// algorithm (drop break-inside-avoid → drop break-before/after-avoid
/// → emit overflow); higher values aren't supported by the spec
/// either.</para>
///
/// <para><b>Diagnostic emission.</b> Per Phase 3 Task 4 review fix
/// design — the coordinator takes an optional
/// <see cref="IPaginateDiagnosticsSink"/>; when non-<see langword="null"/>,
/// the LastResort attempt emits <c>PAGINATION-FORCED-OVERFLOW-001</c>
/// before the attempt runs (so consumers see the warning even if the
/// last-resort attempt itself has issues). The facade adapter (in the
/// <c>NetPdf</c> project) translates these to the public
/// <c>NetPdf.IDiagnosticsSink</c>.</para>
///
/// <para><b>State management.</b> The coordinator does NOT take its
/// own checkpoint at the start — the layouter is responsible for
/// capturing checkpoints at appropriate points internally + returning
/// the relevant one in
/// <see cref="LayoutAttemptResult.RewindTo"/>. This matches the
/// existing <see cref="IBreakResolver.RegisterCheckpoint"/> contract:
/// the layouter owns the rent/Capture lifecycle, the resolver
/// references the checkpoint, the coordinator drives retry logic.
/// On rewind, the coordinator calls <see cref="LayoutCheckpoint.RestoreInto"/>
/// — which (per Phase 3 Task 4 review fix #6) reseats
/// <c>layout.Fragmentainer</c> back to the captured one if a
/// speculative swap occurred.</para>
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

    public LayoutRetryCoordinator(IPaginateDiagnosticsSink? diagnostics = null)
    {
        Diagnostics = diagnostics;
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
    public LayoutAttemptResult Run(
        ILayouter layouter,
        FragmentainerContext fragmentainer,
        ref LayoutContext layout,
        IBreakResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(layouter);
        ArgumentNullException.ThrowIfNull(fragmentainer);
        ArgumentNullException.ThrowIfNull(resolver);

        // Track best-cost across attempts so a future enhancement
        // could pick the cheapest attempt's result. Currently the
        // coordinator returns the LAST result (latest attempt wins
        // when LastResort fires); cost tracking is recorded for the
        // diagnostic message + future optimizer integration.
        double bestCost = double.PositiveInfinity;
        LayoutAttemptResult? bestResult = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var strategy = AttemptToStrategy(attempt);

            // Per Phase 3 Task 5 — emit the forced-overflow diagnostic
            // BEFORE running the LastResort attempt. This way consumers
            // see the warning even if the layouter itself has issues
            // during the last attempt (e.g., throws). The diagnostic
            // describes what the coordinator IS ABOUT TO DO, not what
            // happened.
            if (strategy == LayoutAttemptStrategy.LastResort)
            {
                Diagnostics?.Emit(new PaginateDiagnostic(
                    PaginateDiagnosticCodes.PaginationForcedOverflow001,
                    $"Pagination required {MaxRetries} retries on fragmentainer page index "
                    + $"{fragmentainer.PageIndex}; falling back to last-resort layout that "
                    + "drops avoid-break constraints + commits best-effort result.",
                    PaginateDiagnosticSeverity.Warning));
            }

            var result = layouter.AttemptLayout(fragmentainer, ref layout, resolver, strategy);

            if (result.Outcome != LayoutAttemptOutcome.NeedsRewind)
            {
                // Done — or done-enough. Record best-cost for visibility
                // (future enhancement — currently we just return the
                // latest result).
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
                // The layouter returned NeedsRewind on the LastResort
                // attempt — contract violation. The bounded retry
                // contract guarantees no more retries; return a
                // best-effort result anyway. This path is defensive;
                // a well-behaved layouter never reaches here.
                return new LayoutAttemptResult(
                    LayoutAttemptOutcome.PageComplete,
                    Continuation: result.Continuation,
                    RewindTo: null,
                    Cost: result.Cost);
            }

            // Track best-cost from the failed-but-instructive attempt.
            if (result.Cost < bestCost)
            {
                bestCost = result.Cost;
                bestResult = result;
            }

            // Restore from the named checkpoint + advance to next attempt.
            // Per Phase 3 Task 4 review fix #6 — RestoreInto reseats
            // layout.Fragmentainer to the captured one if a speculative
            // swap occurred.
            result.RewindTo?.RestoreInto(fragmentainer, ref layout);
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
