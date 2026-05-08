// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// <para>The default implementation, <see cref="BreakResolver"/>, runs
/// a bounded DP optimizer over the candidate set seen so far. Layouters
/// only see the resolver through this interface so future cost-model
/// experiments + greedy fallback paths (Phase 3 Task 4 — DP optimizer)
/// can swap implementations without touching layouter code.</para>
/// </summary>
internal interface IBreakResolver
{
    /// <summary>Ask the resolver what to do at the current candidate
    /// break point. The layouter passes the per-page context (so the
    /// resolver sees <see cref="FragmentainerContext.RemainingHeight"/>)
    /// + a <see cref="BreakOpportunity"/> describing the candidate.</summary>
    BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx);

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
