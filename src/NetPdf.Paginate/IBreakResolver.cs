// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan §"Build the paginator first" — the layouter-facing
/// API for break decisions. Every layouter (block / inline / table /
/// flex / grid / multicol) consults this at every candidate break
/// point. Returning <see cref="BreakAction.Continue"/> means "keep
/// laying out"; <see cref="BreakAction.BreakHere"/> means "commit a
/// page break here"; <see cref="BreakAction.Rewind"/> means "the
/// optimizer wants you to roll back to the named checkpoint + retry".
///
/// <para>The Phase 3 Task 1 implementation (<see cref="BreakResolver"/>)
/// is greedy — break only when the next chunk overflows. Phase 3 Task 4
/// adds <see cref="OptimizingBreakResolver"/> which delegates to
/// <see cref="Optimizer.Optimize"/> for batched candidate windows; the
/// streaming <see cref="ConsiderBreakAt"/> path stays greedy (the DP's
/// 2-page lookahead requires a window — single-candidate streaming
/// degrades to greedy by definition).</para>
///
/// <para><b>Streaming vs batched.</b> Layouters that pre-compute their
/// break candidates for a logical section (a paragraph's lines, a
/// table's rows, a flex container's lines) call <see cref="ResolveBreaks"/>
/// to get the optimizer's globally-best break set. Layouters that
/// can't pre-enumerate (e.g., block layouters with deep recursion +
/// unknown total height) call <see cref="ConsiderBreakAt"/> per
/// candidate; that path is greedy regardless of resolver. The two
/// paths are complementary, not exclusive.</para>
/// </summary>
internal interface IBreakResolver
{
    /// <summary>Ask the resolver what to do at the current candidate
    /// break point. The layouter passes the per-page context (so the
    /// resolver sees <see cref="FragmentainerContext.RemainingBlockSize"/>)
    /// + a <see cref="BreakOpportunity"/> describing the candidate.</summary>
    BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx);

    /// <summary>Per Phase 3 Task 4 — batched cost minimization across
    /// a complete candidate window. The implementation runs the
    /// bounded DP optimizer (<see cref="Optimizer.Optimize"/>) when
    /// the window fits within budget; otherwise falls back to greedy
    /// (the result's <see cref="OptimizerResult.FellBackToGreedy"/>
    /// flag is set so the caller can emit
    /// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c>).
    ///
    /// <para>Per the contract on
    /// <see cref="Optimizer.Optimize"/>, the input opportunities'
    /// <see cref="BreakOpportunity.UsedBlockSize"/> values must be
    /// monotonically non-decreasing. The returned indices are into
    /// <paramref name="opportunities"/>; an empty list means "no
    /// breaks needed for this window".</para></summary>
    OptimizerResult ResolveBreaks(
        IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx);

    /// <summary>Register a checkpoint that the resolver may name in a
    /// subsequent <see cref="BreakAction.Rewind"/> decision. The
    /// caller (typically <c>BlockLayouter</c>) snapshots its mutable
    /// state into the checkpoint + hands it to the resolver; the
    /// resolver returns the checkpoint to the pool when the rewind
    /// frontier passes the registration point.</summary>
    void RegisterCheckpoint(LayoutCheckpoint checkpoint);

    /// <summary>Return the most recently registered checkpoint, or
    /// <see langword="null"/> when none exists. Used by the layouter's
    /// rewind handler to resume from a known-good state.</summary>
    LayoutCheckpoint? GetLastCheckpoint();
}
