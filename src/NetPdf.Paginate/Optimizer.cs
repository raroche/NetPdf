// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 Task 4 + plan §"Bounded DP optimizer" — Knuth-Plass-style
/// cost minimization with bounded 2-page lookahead. The optimizer
/// consumes an ordered sequence of <see cref="BreakOpportunity"/>
/// candidates and returns the indices where fragmentainer breaks
/// should fire to minimize total <see cref="CostModel"/> cost.
///
/// <para><b>Algorithm.</b> Sliding-window DP. For each "current page"
/// state (anchored at <c>pageStart</c>), the optimizer enumerates:</para>
/// <list type="bullet">
///   <item>All candidates <c>b1</c> that could end the current page
///   (i.e., <c>opportunities[b1].UsedBlockSize - pageStart &lt;= contentBlockSize</c>).</item>
///   <item>For each <c>b1</c>, all candidates <c>b2</c> that could end
///   the next page given the b1 choice
///   (<c>opportunities[b2].UsedBlockSize - opportunities[b1].UsedBlockSize &lt;= contentBlockSize</c>).</item>
///   <item>The <c>(b1, b2)</c> pair minimizing
///   <c>Score(b1) + Score(b2)</c> wins. Only <c>b1</c> commits;
///   the optimizer slides forward + re-evaluates from <c>pageStart =
///   opportunities[b1].UsedBlockSize</c>.</item>
/// </list>
///
/// <para><b>Why slide rather than commit both.</b> Per the Phase 3 plan
/// §"DP optimizer worst case" — committing 2-page decisions atomically
/// would let early choices propagate suboptimally when later context
/// (a forced break on page 3, an avoid-break region revealed on page 4)
/// would have changed the page-1 decision. Sliding window with single-
/// page commits accepts a bounded amount of "look ahead but commit one"
/// suboptimality in exchange for stable forward progress — a property
/// the bounded retry loop (Task 5) leans on.</para>
///
/// <para><b>Runtime.</b> O(n × k²) where n = candidate count and k =
/// candidates-per-page. For typical documents (10-50 lines per page),
/// k ≈ 50 and the inner loop's 2500 ops × n total = ~2.5M ops per 1000
/// candidates. Well within the Phase 3 perf gate (3-page invoice ≤ 200
/// ms p50). The <see cref="MaxCandidatesBeforeFallback"/> budget cap
/// hard-limits worst-case n.</para>
///
/// <para><b>Forced + avoid metadata.</b> Per CSS Fragmentation L3 §3.1
/// + Phase 3 review fix #3:</para>
/// <list type="bullet">
///   <item><see cref="BreakOpportunity.ForceBreak"/> — when an
///   opportunity inside the current page's window has this set, the DP
///   restricts <c>b1</c> to be at-or-before the forced break. The
///   forced break MUST be selected (its cost is 0 per
///   <see cref="CostModel.Score"/>).</item>
///   <item><see cref="BreakOpportunity.AvoidBreak"/> — DP rejects this
///   candidate as <c>b1</c> / <c>b2</c> unless it's also the forced
///   break (forced wins). When every candidate in the window is
///   avoid-break, the DP falls back to greedy + emits
///   <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> via the
///   <see cref="OptimizerResult.FellBackToGreedy"/> flag.</item>
/// </list>
///
/// <para><b>Page parity (<see cref="PageParity"/>).</b> NOT enforced by
/// the optimizer in v1; the layouter is responsible for inserting
/// blank fragmentainers post-DP to satisfy
/// <c>break-before: left/right/recto/verso</c> per CSS Page L3 §3.4.1.
/// Future work (Phase 3 follow-up) may push parity into the DP if
/// real corpus shows it matters.</para>
/// </summary>
internal static class Optimizer
{
    /// <summary>Per Phase 3 plan §"DP optimizer worst case" — keep
    /// lookahead at 2 pages. Higher values quickly explode runtime
    /// (the per-state inner loop is O(k²); a 3-page lookahead would
    /// require O(k³) per state). The plan explicitly forbids tuning
    /// this above 2.</summary>
    public const int LookaheadPages = 2;

    /// <summary>Per Phase 3 plan — soft cap on candidate-set size
    /// before falling back to greedy. 16k candidates would already
    /// constitute a multi-hundred-page document with one candidate
    /// per line. The fallback emits
    /// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> via
    /// <see cref="OptimizerResult.FellBackToGreedy"/>.</summary>
    public const int MaxCandidatesBeforeFallback = 16_384;

    /// <summary>Run the bounded DP optimizer over
    /// <paramref name="opportunities"/> + return the chosen break
    /// indices. See class XML doc for algorithm + invariants.</summary>
    /// <param name="opportunities">Candidate break points in document
    /// order. The <see cref="BreakOpportunity.UsedBlockSize"/> values
    /// MUST be monotonically non-decreasing — they represent
    /// cumulative block-axis position assuming no breaks fire. Pass
    /// per-fragmentainer-relative values when batching across pages
    /// is undesirable; the optimizer will reset per chosen break.</param>
    /// <param name="contentBlockSize">Fragmentainer block-axis extent
    /// (CSS px). Must be finite + positive.</param>
    /// <param name="orphansRequired">Author's <c>orphans</c> property
    /// (CSS Fragmentation L3 §4.2). Defaults to 2 in the spec.</param>
    /// <param name="widowsRequired">Author's <c>widows</c> property.
    /// Defaults to 2.</param>
    public static OptimizerResult Optimize(
        IReadOnlyList<BreakOpportunity> opportunities,
        double contentBlockSize,
        int orphansRequired,
        int widowsRequired)
    {
        ArgumentNullException.ThrowIfNull(opportunities);
        if (!double.IsFinite(contentBlockSize) || contentBlockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentBlockSize),
                $"contentBlockSize must be finite + positive; got {contentBlockSize}");
        if (orphansRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(orphansRequired));
        if (widowsRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(widowsRequired));

        var n = opportunities.Count;
        if (n == 0)
        {
            return OptimizerResult.Empty;
        }

        // Per-opportunity validation. EnsureValid catches NaN /
        // negative geometry — the DP's arithmetic depends on finite
        // values to compare costs deterministically.
        for (var i = 0; i < n; i++)
        {
            opportunities[i].EnsureValid();
        }

        // Budget cap — fall back to greedy when the candidate set
        // exceeds what the bounded DP can usefully optimize. The
        // diagnostic emission lives at the layouter level; we just
        // flag it on the result.
        if (n > MaxCandidatesBeforeFallback)
        {
            return Greedy(opportunities, contentBlockSize, orphansRequired, widowsRequired,
                fallbackReason:
                    $"Candidate set size {n} exceeds DP budget {MaxCandidatesBeforeFallback}; greedy fallback.");
        }

        // Monotonicity check — UsedBlockSize must be non-decreasing.
        // Violations indicate the caller batched across pages without
        // resetting the cumulative measure, which the DP can't reason
        // about. Fall back to greedy with a clear reason.
        for (var i = 1; i < n; i++)
        {
            if (opportunities[i].UsedBlockSize < opportunities[i - 1].UsedBlockSize)
            {
                return Greedy(opportunities, contentBlockSize, orphansRequired, widowsRequired,
                    fallbackReason:
                        "UsedBlockSize is not monotonically non-decreasing; the DP requires "
                        + "cumulative block-axis position. Greedy fallback.");
            }
        }

        var breaks = new List<int>(capacity: Math.Min(64, n));
        double totalCost = 0;
        double pageStart = 0;
        var i2 = 0;

        while (i2 < n)
        {
            // Find the inclusive range of b1 candidates: opportunities[i2..b1Max]
            // such that the page (pageStart, b1] fits on one fragmentainer.
            var b1Max = -1;
            for (var k = i2; k < n; k++)
            {
                if (opportunities[k].UsedBlockSize - pageStart > contentBlockSize) break;
                b1Max = k;
            }

            // Overflow handling — even the first candidate (i2) doesn't
            // fit on the current page. This means a single chunk
            // exceeded the fragmentainer's block extent. Last-resort:
            // commit a break at i2 with the overflow penalty.
            // Diagnostic PAGINATION-FORCED-OVERFLOW-001 is the
            // layouter's job (Task 5+); the DP just expresses the
            // penalty in the cost.
            if (b1Max < 0)
            {
                breaks.Add(i2);
                totalCost += CostModel.BreakInsideAvoidViolation;
                pageStart = opportunities[i2].UsedBlockSize;
                i2++;
                continue;
            }

            // Forced-break detection — if a ForceBreak opportunity is
            // inside [i2..b1Max], b1 MUST be at-or-before that index.
            // (The DP cannot skip a forced break.)
            var b1ForceCap = b1Max;
            for (var k = i2; k <= b1Max; k++)
            {
                if (opportunities[k].ForceBreak)
                {
                    b1ForceCap = k;
                    break;
                }
            }

            int bestB1 = -1;
            double bestPairCost = double.PositiveInfinity;
            // Track the best b1's standalone cost so we can attribute
            // it to totalCost (b2's cost re-evaluates next iteration).
            double bestB1OwnCost = 0;

            for (var b1 = i2; b1 <= b1ForceCap; b1++)
            {
                var oppB1 = opportunities[b1];

                // Skip avoid-break candidates UNLESS they're also the
                // forced break (forced wins per Fragmentation L3 §3.1).
                if (oppB1.AvoidBreak && !oppB1.ForceBreak) continue;

                var costB1 = CostModel.Score(
                    oppB1, contentBlockSize, orphansRequired, widowsRequired,
                    // Stub: assume widows is satisfied. A future
                    // refinement could plumb paragraph identity into
                    // BreakOpportunity for precise widow counting.
                    lineCountAfterBreak: widowsRequired);

                // ---- 2-page lookahead: find the best b2 given b1 ----
                // Range: opportunities[b1+1..b2Max] where the page
                // (b1, b2] fits.
                var b2Max = -1;
                for (var k = b1 + 1; k < n; k++)
                {
                    if (opportunities[k].UsedBlockSize - oppB1.UsedBlockSize > contentBlockSize) break;
                    b2Max = k;
                }

                // No valid b2 (b1 is at or near the end of the input).
                // Score b1 alone — the trailing tail past b1 is the
                // caller's next-window problem.
                if (b2Max < 0)
                {
                    if (costB1 < bestPairCost)
                    {
                        bestPairCost = costB1;
                        bestB1 = b1;
                        bestB1OwnCost = costB1;
                    }
                    continue;
                }

                var b2ForceCap = b2Max;
                for (var k = b1 + 1; k <= b2Max; k++)
                {
                    if (opportunities[k].ForceBreak)
                    {
                        b2ForceCap = k;
                        break;
                    }
                }

                for (var b2 = b1 + 1; b2 <= b2ForceCap; b2++)
                {
                    var oppB2 = opportunities[b2];
                    if (oppB2.AvoidBreak && !oppB2.ForceBreak) continue;

                    var costB2 = CostModel.Score(
                        oppB2, contentBlockSize, orphansRequired, widowsRequired,
                        lineCountAfterBreak: widowsRequired);

                    var pair = costB1 + costB2;
                    if (pair < bestPairCost)
                    {
                        bestPairCost = pair;
                        bestB1 = b1;
                        bestB1OwnCost = costB1;
                    }
                }
            }

            if (bestB1 < 0)
            {
                // No feasible (b1, b2) pair found within the lookahead
                // window — every candidate inside [i2..b1ForceCap] was
                // an avoid-break (and not also a forced break). Fall
                // back to greedy from here for the caller's window;
                // the caller emits the diagnostic.
                return Greedy(opportunities, contentBlockSize, orphansRequired, widowsRequired,
                    fallbackReason:
                        $"DP could not find a feasible (b1, b2) pair starting at index {i2}: "
                        + "every in-window candidate was avoid-break + not forced. Greedy fallback.");
            }

            breaks.Add(bestB1);
            totalCost += bestB1OwnCost;
            pageStart = opportunities[bestB1].UsedBlockSize;
            i2 = bestB1 + 1;
        }

        return new OptimizerResult(breaks, totalCost, FellBackToGreedy: false, FallbackReason: null);
    }

    /// <summary>Per Phase 3 plan + class XML doc — last-resort greedy
    /// pass when the DP can't / shouldn't run. Walks the candidate
    /// list once + commits a break whenever the next chunk would
    /// overflow OR a <see cref="BreakOpportunity.ForceBreak"/> demands
    /// it. <see cref="OptimizerResult.FellBackToGreedy"/> is set on
    /// the returned result so the caller can emit
    /// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c>.</summary>
    private static OptimizerResult Greedy(
        IReadOnlyList<BreakOpportunity> opportunities,
        double contentBlockSize,
        int orphansRequired,
        int widowsRequired,
        string fallbackReason)
    {
        var breaks = new List<int>(capacity: Math.Min(64, opportunities.Count));
        double totalCost = 0;
        double pageStart = 0;

        for (var i = 0; i < opportunities.Count; i++)
        {
            var opp = opportunities[i];
            opp.EnsureValid();

            // Block-axis extent the chunk after this opportunity
            // would consume on the current page if we DON'T break.
            var pageSoFar = opp.UsedBlockSize - pageStart;
            var wouldOverflow = (pageSoFar + opp.ChunkBlockSize) > contentBlockSize;

            if (opp.ForceBreak || wouldOverflow)
            {
                var cost = CostModel.Score(
                    opp, contentBlockSize, orphansRequired, widowsRequired,
                    lineCountAfterBreak: widowsRequired);
                // Greedy includes the overflow penalty when the chunk
                // itself doesn't fit. Per CSS Fragmentation L3 §3.2
                // last-resort path; the layouter emits
                // PAGINATION-FORCED-OVERFLOW-001 separately when the
                // committed page actually overflows.
                if (pageSoFar > contentBlockSize)
                {
                    cost += CostModel.BreakInsideAvoidViolation;
                }
                breaks.Add(i);
                totalCost += cost;
                pageStart = opp.UsedBlockSize;
            }
        }

        return new OptimizerResult(breaks, totalCost,
            FellBackToGreedy: true, FallbackReason: fallbackReason);
    }
}
