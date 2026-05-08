// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;

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
///
/// <para><b>UsedBlockSize coordinate space</b> — per Phase 3 Task 4
/// review fix #2 + Copilot review #8, opportunities passed in two
/// different shapes depending on which method consumes them:</para>
/// <list type="bullet">
///   <item><b><see cref="ConsiderBreakAt"/> (streaming).</b>
///   <see cref="BreakOpportunity.UsedBlockSize"/> is the live
///   <i>per-fragmentainer</i> cumulative block-axis size at the moment
///   the candidate was offered. Resets to 0 at each page break.
///   Matches the original CSS-px-from-page-top semantics.</item>
///   <item><b><see cref="ResolveBreaks"/> (batched).</b>
///   <see cref="BreakOpportunity.UsedBlockSize"/> is
///   <i>cumulative-across-the-window</i> — monotonically non-decreasing
///   so the DP can subtract <c>pageStart</c> to recover per-page
///   measurements. The optimizer hands the page-relative value into
///   <see cref="CostModel.Score"/> via the <c>pageStart</c> argument
///   so trailing-blank scoring stays correct on pages 2+.</item>
/// </list>
/// <para>The two coordinate spaces coincide on the first page. The
/// distinction matters only when batching across multiple pages.</para>
/// </summary>
internal interface IBreakResolver : IDisposable
{
    /// <summary>Ask the resolver what to do at the current candidate
    /// break point. The layouter passes the per-page context (so the
    /// resolver sees <see cref="FragmentainerContext.RemainingBlockSize"/>)
    /// + a <see cref="BreakOpportunity"/> describing the candidate.
    /// <see cref="BreakOpportunity.UsedBlockSize"/> is per-fragmentainer
    /// in this path (see class XML doc).</summary>
    BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx);

    /// <summary>Per Phase 3 Task 4 — batched cost minimization across
    /// a complete candidate window. The implementation runs the
    /// bounded DP optimizer (<see cref="Optimizer.Optimize"/>) when
    /// the window fits within budget; otherwise falls back to greedy
    /// (the result's <see cref="OptimizerResult.FellBackToGreedy"/>
    /// flag is set so the caller can emit
    /// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c>).
    ///
    /// <para><b>Coordinate-space contract.</b> Per Phase 3 Task 4
    /// review fix #2 + Copilot #8, this method requires
    /// <see cref="BreakOpportunity.UsedBlockSize"/> values to be
    /// <i>monotonically non-decreasing across the window</i> — i.e.,
    /// cumulative as if no breaks were committed. The optimizer
    /// recovers per-page measurements by subtracting the running
    /// <c>pageStart</c> (the cumulative position of the most recent
    /// committed break, or 0 for the first page). This matches the
    /// Knuth-Plass DP's natural framing where line widths feed the
    /// DP cumulatively + the optimizer chooses where to break.</para>
    ///
    /// <para>The returned <see cref="OptimizerResult.BreakIndices"/>
    /// are indices into <paramref name="opportunities"/>; an empty
    /// list means "no breaks needed for this window — the entire
    /// candidate range fits on a single fragmentainer + no forced
    /// break demands one" (per Phase 3 Task 4 review fix #1 +
    /// Copilot #2).</para></summary>
    /// <remarks>Per PR #24 review pass — accepts a
    /// <see cref="CancellationToken"/> so batched candidate windows
    /// (inline / table / flex / grid layouters that pre-compute
    /// hundreds-to-thousands of opportunities) can be cancelled mid-
    /// optimization. Attempt-level cancellation in
    /// <see cref="LayoutRetryCoordinator.Run"/> isn't sufficient on
    /// its own — a single <c>ResolveBreaks</c> call on a
    /// pathological input can run for seconds.</remarks>
    OptimizerResult ResolveBreaks(
        IReadOnlyList<BreakOpportunity> opportunities,
        FragmentainerContext ctx,
        CancellationToken cancellationToken = default);

    /// <summary>Register a checkpoint that the resolver may name in a
    /// subsequent <see cref="BreakAction.Rewind"/> decision. Per
    /// Phase 3 Task 4 review fix #7 — takes a
    /// <see cref="CheckpointLease"/> rather than a raw checkpoint;
    /// the resolver internally returns the prior lease (if any) to
    /// the pool when a new one is registered, using the saved token
    /// to reject stale-after-rerent races.
    ///
    /// <para>The caller (typically <c>BlockLayouter</c>) rents a
    /// checkpoint via <see cref="LayoutCheckpointPool.Rent"/>, calls
    /// <see cref="LayoutCheckpoint.Capture"/>, and hands the lease
    /// to this method. The resolver's internal storage of the lease
    /// keeps the rental alive until either (a) a new checkpoint is
    /// registered (the prior is returned), or (b) the resolver is
    /// disposed / discarded (no automatic return — the caller is
    /// expected to drive the rewind frontier explicitly).</para></summary>
    void RegisterCheckpoint(CheckpointLease lease);

    /// <summary>Return the most recently registered checkpoint, or
    /// <see langword="null"/> when none exists. Used by the layouter's
    /// rewind handler to resume from a known-good state. Returns the
    /// underlying <see cref="LayoutCheckpoint"/> only — the lease
    /// token stays inside the resolver so callers can't accidentally
    /// double-Return through this read path.</summary>
    LayoutCheckpoint? GetLastCheckpoint();

    // Per Phase 3 Task 5 PR #21 review fix #4 — Dispose is inherited
    // from IDisposable. Implementations release the final held
    // checkpoint lease so the last registered checkpoint doesn't pin
    // its referenced graphs (continuation, named-strings snapshot,
    // float state) past the render. Pre-fix the resolver held its
    // final checkpoint forever — leaking one checkpoint per render.
    // Dispose must be idempotent: a second call after the first is
    // a no-op (default-struct lease has null Checkpoint).
}
