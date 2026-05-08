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
/// state (anchored at <c>pageStart</c>):</para>
/// <list type="number">
///   <item>If the remaining tail (from pageStart to end-of-window)
///   fits on a single fragmentainer AND no forced break lies ahead,
///   <i>commit no break</i> + exit. Per Phase 3 Task 4 review fix #1
///   + Copilot review #2 — without this, every non-empty input would
///   pick up at least one break index even when no break is needed.</item>
///   <item>If a forced break (<see cref="BreakOpportunity.ForceBreak"/>)
///   is reachable on the current page, commit it directly without
///   running the (b1, b2) DP. Per Phase 3 Task 4 review fix #4 —
///   author-chosen break points are strong signals; section-boundary
///   rewards must not insert extra breaks earlier on the same page.</item>
///   <item>Otherwise enumerate <c>(b1, b2)</c> candidate pairs where
///   <c>b1</c> ends the current page + <c>b2</c> ends the next; pick
///   the pair minimizing <c>Score(b1) + Score(b2)</c>; commit only
///   <c>b1</c> + slide forward to <c>pageStart =
///   opportunities[b1].UsedBlockSize</c>.</item>
/// </list>
///
/// <para>Per Phase 3 Task 4 review fix #2 + Copilot reviews #1, #8 —
/// the optimizer feeds <c>pageStart</c> into <see cref="CostModel.Score"/>
/// so per-page measurements (the trailing-blank ratio in particular)
/// recover correctly across multi-page windows. Without this, scoring
/// on pages 2+ would compute a negative blank ratio (because
/// <see cref="BreakOpportunity.UsedBlockSize"/> is cumulative-across-window
/// in the batched path) + miss the trailing-blank penalty entirely.</para>
///
/// <para><b>Why slide rather than commit both.</b> Per the Phase 3
/// plan §"DP optimizer worst case" — committing 2-page decisions
/// atomically would let early choices propagate suboptimally when
/// later context (a forced break on page 3, an avoid-break region on
/// page 4) would have changed the page-1 decision. Sliding window
/// with single-page commits accepts a bounded amount of "look ahead
/// but commit one" suboptimality in exchange for stable forward
/// progress.</para>
///
/// <para><b>Runtime.</b> O(n × k²) where n = candidate count and k =
/// candidates-per-page. Per Phase 3 Task 4 review fix #3 — both
/// <see cref="MaxCandidatesBeforeFallback"/> (whole-window cap),
/// <see cref="MaxCandidatesPerPage"/> (per-page cap), and
/// <see cref="MaxPairEvaluations"/> (cumulative pair-eval budget)
/// trip the greedy fallback when exceeded. The combination prevents
/// pathological dense-page inputs (1000 zero-height candidates on
/// one page) from costing O(1M) pair evaluations in a single outer
/// iteration.</para>
///
/// <para><b>Forced + avoid metadata.</b> Per CSS Fragmentation L3
/// §3.1 + Phase 3 review fix #3:</para>
/// <list type="bullet">
///   <item><see cref="BreakOpportunity.ForceBreak"/> — the earliest
///   reachable forced break on the current page is committed
///   directly (review fix #4); a forced break beyond the current
///   page's reach is handled in a subsequent iteration.</item>
///   <item><see cref="BreakOpportunity.AvoidBreak"/> — DP rejects this
///   candidate as <c>b1</c> / <c>b2</c> unless it's also the forced
///   break (forced wins). When every candidate in the window is
///   avoid-break, the DP falls back to greedy + emits
///   <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> via the
///   <see cref="OptimizerResult.FellBackToGreedy"/> flag.</item>
/// </list>
///
/// <para><b>Widow scoring.</b> Per Phase 3 Task 4 review fix #5 —
/// the optimizer counts subsequent line boundaries that would land on
/// the next page (in the same paragraph) using
/// <see cref="BreakOpportunity.ParagraphId"/>. Layouters that supply
/// stable paragraph identifiers get accurate widow penalties; those
/// that leave <see cref="BreakOpportunity.ParagraphId"/> at 0 fall
/// back to a heuristic counting consecutive
/// <see cref="BreakOpportunityClass.LineBoundary"/> opportunities
/// until a different class appears. Both branches feed
/// <see cref="CostModel.Score"/>'s <c>lineCountAfterBreak</c>
/// argument so the widow check fires correctly.</para>
///
/// <para><b>Page parity (<see cref="PageParity"/>).</b> NOT enforced
/// by the optimizer in v1; the layouter is responsible for inserting
/// blank fragmentainers post-DP to satisfy
/// <c>break-before: left/right/recto/verso</c> per CSS Page L3 §3.4.1.</para>
/// </summary>
internal static class Optimizer
{
    /// <summary>Per Phase 3 plan §"DP optimizer worst case" — keep
    /// lookahead at 2 pages. Higher values quickly explode runtime
    /// (per-state work is O(k²); a 3-page lookahead would require
    /// O(k³) per state). The plan explicitly forbids tuning this
    /// above 2.</summary>
    public const int LookaheadPages = 2;

    /// <summary>Per Phase 3 plan — soft cap on whole-window candidate
    /// count before falling back to greedy. 16k candidates already
    /// constitute a multi-hundred-page document with one candidate
    /// per line.</summary>
    public const int MaxCandidatesBeforeFallback = 16_384;

    /// <summary>Per Phase 3 Task 4 review fix #3 — per-page candidate
    /// cap. A single page with more than this many in-page candidates
    /// trips the greedy fallback. 256 is generous (a typical page
    /// has 30-50 lines); the cap protects against pathological inputs
    /// where many zero-height chunks pile onto one page.</summary>
    public const int MaxCandidatesPerPage = 256;

    /// <summary>Per Phase 3 Task 4 review fix #3 — cumulative
    /// pair-evaluation budget across the whole optimization run. Each
    /// (b1, b2) inner-loop iteration counts as one. 65k caps the
    /// total work at well under the perf gate's 200ms p50 budget for
    /// realistic documents.</summary>
    public const int MaxPairEvaluations = 65_536;

    /// <summary>Run the bounded DP optimizer over
    /// <paramref name="opportunities"/> + return the chosen break
    /// indices. See class XML doc for algorithm + invariants.</summary>
    /// <param name="opportunities">Candidate break points in document
    /// order. <see cref="BreakOpportunity.UsedBlockSize"/> values must
    /// be cumulative-across-window + monotonically non-decreasing —
    /// per the
    /// <see cref="IBreakResolver.ResolveBreaks"/> coordinate-space
    /// contract.</param>
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

        // Whole-window budget cap.
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
        var pairEvalsUsed = 0;

        while (i2 < n)
        {
            // ---- Step 1 (review fix #1 + Copilot #2): early exit
            // when the remainder fits + no forced break ahead. ----
            var tailEnd = opportunities[n - 1].UsedBlockSize + opportunities[n - 1].ChunkBlockSize;
            var tailFitsOnePage = (tailEnd - pageStart) <= contentBlockSize;
            var firstForceIdx = -1;
            for (var k = i2; k < n; k++)
            {
                if (opportunities[k].ForceBreak)
                {
                    firstForceIdx = k;
                    break;
                }
            }
            if (firstForceIdx < 0 && tailFitsOnePage)
            {
                // No more breaks needed for this window. The trailing
                // tail past the most recent committed break (or from
                // the start of input if none) fits on a single page.
                break;
            }

            // ---- Find b1 range: opportunities[i2..b1Max] where
            // page (pageStart, b1] fits on one fragmentainer. ----
            var b1Max = -1;
            var inPageCount = 0;
            for (var k = i2; k < n; k++)
            {
                if (opportunities[k].UsedBlockSize - pageStart > contentBlockSize) break;
                b1Max = k;
                inPageCount++;
            }

            // Per review fix #3 — per-page cap. Pathological inputs
            // (many zero-height candidates on one page) trip the
            // greedy fallback rather than blowing the O(k²) inner
            // loop budget.
            if (inPageCount > MaxCandidatesPerPage)
            {
                return Greedy(opportunities, contentBlockSize, orphansRequired, widowsRequired,
                    fallbackReason:
                        $"Per-page candidate count {inPageCount} exceeds MaxCandidatesPerPage="
                        + $"{MaxCandidatesPerPage}; greedy fallback.");
            }

            // ---- Step 2 (review fix #4): if a forced break is
            // REACHABLE on the current page, commit it directly. The
            // (b1, b2) optimizer is skipped — author's choice wins. ----
            if (firstForceIdx >= 0 && firstForceIdx <= b1Max)
            {
                var forcedOpp = opportunities[firstForceIdx];
                // Score is 0 for ForceBreak per CostModel, but call
                // through the full path for consistency + future
                // hooks.
                totalCost += CostModel.Score(
                    forcedOpp, contentBlockSize, orphansRequired, widowsRequired,
                    lineCountAfterBreak: ComputeLinesAfterBreak(opportunities, firstForceIdx, widowsRequired),
                    pageStart: pageStart);
                breaks.Add(firstForceIdx);
                pageStart = forcedOpp.UsedBlockSize;
                i2 = firstForceIdx + 1;
                continue;
            }

            // ---- Step 3: overflow handling — even the first
            // candidate at i2 doesn't fit. Last-resort commit at i2
            // with the BreakInsideAvoidViolation cost. ----
            if (b1Max < 0)
            {
                breaks.Add(i2);
                totalCost += CostModel.BreakInsideAvoidViolation;
                pageStart = opportunities[i2].UsedBlockSize;
                i2++;
                continue;
            }

            // ---- Step 4: regular DP over (b1, b2) pairs. ----
            // Per Phase 3 Task 4 review fix #1 (invariant strengthening):
            // for each candidate b1, also consider the OPTION-A case
            // where committing b1 leaves a fitting tail + no forced
            // break ahead — in that case b1 alone suffices, no second
            // break is needed, total cost is just costB1. Without this
            // check, the DP's myopic 2-page lookahead can pick a 2-break
            // sequence that ties with a 1-break sequence on pair-cost
            // but is strictly worse on TOTAL cost (the 1-break sequence
            // exits early on the next iteration via step 1). The
            // failing test that surfaced this:
            // ops = [(200,50), (400,50), (700,50), (1000,50)] on 800px
            // page — pair (b1=0, b2=3) ties with pair (b1=2, b2=3) at
            // total 200, but committing b1=2 alone yields cost 0
            // because the tail [700..1050] fits on page 2.
            var lastTailEnd = opportunities[n - 1].UsedBlockSize + opportunities[n - 1].ChunkBlockSize;

            int bestB1 = -1;
            double bestPairCost = double.PositiveInfinity;
            double bestB1OwnCost = 0;

            for (var b1 = i2; b1 <= b1Max; b1++)
            {
                var oppB1 = opportunities[b1];

                // Skip avoid-break candidates — DP can't pick them
                // unless every alternative is also avoid (handled by
                // the no-feasible-pair fallback below).
                if (oppB1.AvoidBreak) continue;

                var costB1 = CostModel.Score(
                    oppB1, contentBlockSize, orphansRequired, widowsRequired,
                    lineCountAfterBreak: ComputeLinesAfterBreak(opportunities, b1, widowsRequired),
                    pageStart: pageStart);

                var nextPageStart = oppB1.UsedBlockSize;

                // ---- Option A (review fix #1 invariant): committing
                // b1 alone suffices when the tail past b1 fits on the
                // next page AND no forced break demands another break
                // beyond b1. Score b1's cost as the TOTAL — no b2 is
                // needed. This prevents the DP from picking an extra
                // (rewarded) break that wouldn't strictly be required. ----
                var tailPastB1Fits = (lastTailEnd - nextPageStart) <= contentBlockSize;
                var forcedAheadOfB1 = false;
                for (var k = b1 + 1; k < n; k++)
                {
                    if (opportunities[k].ForceBreak) { forcedAheadOfB1 = true; break; }
                }

                if (tailPastB1Fits && !forcedAheadOfB1)
                {
                    // Option A wins for this b1 — committing b1 alone
                    // is optimal. Skip the b2 lookahead loop entirely
                    // (a second break would be UNNECESSARY).
                    if (costB1 < bestPairCost)
                    {
                        bestPairCost = costB1;
                        bestB1 = b1;
                        bestB1OwnCost = costB1;
                    }
                    continue;
                }

                // ---- Option B: 2-page lookahead — find the best b2
                // given b1 (the tail past b1 doesn't fit OR a forced
                // break demands one, so a second break IS needed). ----
                var b2Max = -1;
                for (var k = b1 + 1; k < n; k++)
                {
                    if (opportunities[k].UsedBlockSize - nextPageStart > contentBlockSize) break;
                    b2Max = k;
                }

                if (b2Max < 0)
                {
                    // No b2 candidate fits on the next page either, but
                    // a second break IS needed (Option A didn't apply).
                    // Last-resort: score b1 alone; the next iteration's
                    // overflow handler picks up the rest.
                    if (costB1 < bestPairCost)
                    {
                        bestPairCost = costB1;
                        bestB1 = b1;
                        bestB1OwnCost = costB1;
                    }
                    continue;
                }

                for (var b2 = b1 + 1; b2 <= b2Max; b2++)
                {
                    pairEvalsUsed++;
                    if (pairEvalsUsed > MaxPairEvaluations)
                    {
                        return Greedy(opportunities, contentBlockSize, orphansRequired, widowsRequired,
                            fallbackReason:
                                $"DP exceeded MaxPairEvaluations={MaxPairEvaluations}; greedy fallback.");
                    }

                    var oppB2 = opportunities[b2];
                    if (oppB2.AvoidBreak) continue;

                    var costB2 = CostModel.Score(
                        oppB2, contentBlockSize, orphansRequired, widowsRequired,
                        lineCountAfterBreak: ComputeLinesAfterBreak(opportunities, b2, widowsRequired),
                        pageStart: nextPageStart);

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
                // No feasible (b1, b2) pair found — every candidate
                // in the page's window was avoid-break (and not
                // forced). Fall back to greedy for the rest of the
                // window; the caller emits the diagnostic.
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

    /// <summary>Per Phase 3 Task 4 review fix #5 — count line
    /// boundaries after <paramref name="idx"/> that would land on the
    /// next page (in the same paragraph). Drives the
    /// <see cref="CostModel.Widow"/> penalty.
    ///
    /// <para><b>Algorithm.</b> Two cases:</para>
    /// <list type="bullet">
    ///   <item>Source candidate is not a line boundary → return
    ///   <paramref name="widowsRequired"/> so the widow check passes
    ///   trivially (widows only matter at line boundaries inside an
    ///   inline formatting context).</item>
    ///   <item>Source candidate has a non-zero
    ///   <see cref="BreakOpportunity.ParagraphId"/> → count subsequent
    ///   opportunities that share the same id. Stops at the first
    ///   non-matching id (cross-paragraph) or any forced break.</item>
    ///   <item>Source candidate has <see cref="BreakOpportunity.ParagraphId"/>
    ///   = 0 (paragraph identity not supplied) → fall back to a
    ///   heuristic counting consecutive
    ///   <see cref="BreakOpportunityClass.LineBoundary"/> opportunities
    ///   until a non-line / different-class candidate appears.</item>
    /// </list>
    /// <para>Caps at <paramref name="widowsRequired"/> + 1 — once we
    /// know the count meets the requirement, exact value past that
    /// doesn't change the cost.</para>
    /// </summary>
    private static int ComputeLinesAfterBreak(
        IReadOnlyList<BreakOpportunity> opportunities, int idx, int widowsRequired)
    {
        var src = opportunities[idx];
        if (src.Class != BreakOpportunityClass.LineBoundary)
        {
            // Not a paragraph break — widow check doesn't apply.
            // Return widowsRequired to suppress the penalty.
            return widowsRequired;
        }

        var cap = widowsRequired + 1;
        var lines = 0;

        if (src.ParagraphId > 0)
        {
            // Per fix #5 — paragraph-identity-aware counting.
            for (var k = idx + 1; k < opportunities.Count && lines < cap; k++)
            {
                var next = opportunities[k];
                if (next.ForceBreak) break;
                if (next.Class != BreakOpportunityClass.LineBoundary) break;
                if (next.ParagraphId != src.ParagraphId) break;
                lines++;
            }
        }
        else
        {
            // Heuristic — consecutive LineBoundary opportunities.
            // Same-paragraph approximation when the layouter doesn't
            // supply ParagraphId.
            for (var k = idx + 1; k < opportunities.Count && lines < cap; k++)
            {
                var next = opportunities[k];
                if (next.ForceBreak) break;
                if (next.Class != BreakOpportunityClass.LineBoundary) break;
                lines++;
            }
        }

        return lines;
    }

    /// <summary>Per Phase 3 plan + class XML doc — last-resort greedy
    /// pass when the DP can't / shouldn't run. Walks the candidate
    /// list once + commits a break whenever the next chunk would
    /// overflow OR a <see cref="BreakOpportunity.ForceBreak"/> demands
    /// it.
    ///
    /// <para>Per Phase 3 Task 4 Copilot reviews #3, #4 — also commits
    /// a break + applies the overflow penalty when
    /// <see cref="BreakOpportunity.ChunkBlockSize"/> exceeds
    /// <paramref name="contentBlockSize"/> (a chunk taller than a
    /// fragmentainer can't fit on any page; the greedy resolver must
    /// at least signal the overflow in the cost).</para>
    ///
    /// <para><see cref="OptimizerResult.FellBackToGreedy"/> is set on
    /// the returned result so the caller can emit
    /// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c>.</para>
    /// </summary>
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

            var pageSoFar = opp.UsedBlockSize - pageStart;
            var wouldOverflow = (pageSoFar + opp.ChunkBlockSize) > contentBlockSize;
            // Per Copilot reviews #3, #4 — single-chunk-too-tall case.
            var chunkTooTall = opp.ChunkBlockSize > contentBlockSize;

            if (opp.ForceBreak || wouldOverflow || chunkTooTall)
            {
                var cost = CostModel.Score(
                    opp, contentBlockSize, orphansRequired, widowsRequired,
                    lineCountAfterBreak: ComputeLinesAfterBreak(opportunities, i, widowsRequired),
                    pageStart: pageStart);
                // Per Copilot review #3 — explicit overflow penalty
                // when the page itself overflowed (not just the chunk).
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
