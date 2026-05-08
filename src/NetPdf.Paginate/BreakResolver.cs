// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan §"Build the paginator first" — Task 1 stub
/// implementation of <see cref="IBreakResolver"/>. Greedy: always
/// returns <see cref="BreakAction.Continue"/> when the next chunk
/// fits, <see cref="BreakAction.BreakHere"/> when it doesn't, plus
/// honors author-forced breaks (<see cref="BreakOpportunity.ForceBreak"/>)
/// even when the chunk would fit.
///
/// <para>The bounded DP optimizer (Phase 3 Task 4) replaces this
/// implementation with one that minimizes total cost across a
/// candidate window — until then, this stub lets the
/// layouter scaffolding compile + run end-to-end with deterministic
/// (if unsophisticated) page breaks.</para>
///
/// <para>Layouters are written against
/// <see cref="IBreakResolver"/>, NOT against this concrete class —
/// when the optimizer ships, swapping in is a registration change
/// not a layouter rewrite.</para>
///
/// <para><b>Cost model integration.</b> Even the stub consults
/// <see cref="CostModel.Score"/> to populate
/// <see cref="BreakDecision.Cost"/>. Per Phase 3 review fix #4 the
/// score is computed against the opportunity's
/// <see cref="BreakOpportunity.UsedBlockSize"/> snapshot (not the
/// live fragmentainer state), so deferred candidate evaluation by
/// the eventual DP optimizer (Task 4) doesn't re-introduce a
/// live-state dependency.</para>
/// </summary>
internal sealed class BreakResolver : IBreakResolver
{
    /// <summary>Author's <c>orphans</c> property — defaults to 2 per
    /// CSS Fragmentation L3 §4.2 + the CSS 2.1 initial value table.
    /// Tests override via <see cref="BreakResolver(int, int)"/>.</summary>
    public int OrphansRequired { get; }

    /// <summary>Author's <c>widows</c> property — defaults to 2 per
    /// CSS Fragmentation L3 §4.2.</summary>
    public int WidowsRequired { get; }

    /// <summary>Per PR #19 review #1 — Task-1 stub keeps only the most
    /// recent registered checkpoint. When a new checkpoint registers,
    /// the prior one is returned to <see cref="LayoutCheckpointPool"/>
    /// so the bounded retry loop's checkpoint allocation amortizes
    /// across attempts.
    ///
    /// <para>Per Phase 3 Task 4 review fix #7 — stores the full
    /// <see cref="CheckpointLease"/> (not just the bare checkpoint)
    /// so the resolver-internal Return correctly presents the
    /// lease's token, letting the pool reject stale-after-rerent
    /// races on the prior checkpoint.</para></summary>
    private CheckpointLease _lastLease;

    public BreakResolver() : this(orphansRequired: 2, widowsRequired: 2) { }

    public BreakResolver(int orphansRequired, int widowsRequired)
    {
        OrphansRequired = orphansRequired;
        WidowsRequired = widowsRequired;
    }

    /// <inheritdoc />
    public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var cost = CostModel.Score(
            opportunity,
            contentBlockSize: ctx.BlockSize,
            orphansRequired: OrphansRequired,
            widowsRequired: WidowsRequired,
            // Stub doesn't know the next-page line count yet; assume
            // the widows constraint is satisfied. The DP optimizer
            // (Task 4) plumbs lookahead into this argument.
            lineCountAfterBreak: WidowsRequired);

        // Per Phase 3 review fix #3 — author-forced break wins
        // regardless of whether the chunk would fit. The cost model
        // returns 0 for ForceBreak, but the action MUST be BreakHere
        // not Continue.
        if (opportunity.ForceBreak)
        {
            return new BreakDecision(BreakAction.BreakHere, cost, RewindTo: null);
        }

        // Trivial fit: next chunk fits on the current page → continue.
        if (opportunity.ChunkBlockSize <= ctx.RemainingBlockSize)
        {
            return new BreakDecision(BreakAction.Continue, cost, RewindTo: null);
        }

        // Doesn't fit. Per the stub's greedy rule, always break here.
        // The Task 4 optimizer replaces this with cost-minimizing
        // candidate selection.
        return new BreakDecision(BreakAction.BreakHere, cost, RewindTo: null);
    }

    /// <inheritdoc />
    /// <remarks>Per Phase 3 Task 4 — the greedy resolver implements
    /// <see cref="ResolveBreaks"/> by walking the candidate list once
    /// + committing a break when the chunk overflows OR a forced
    /// break demands it. Identical algorithm to
    /// <see cref="Optimizer"/>'s greedy fallback path (which the
    /// optimizing resolver uses on budget-exceeded windows). The
    /// result's <see cref="OptimizerResult.FellBackToGreedy"/> is
    /// <see langword="false"/> here — this resolver IS greedy by
    /// design, not falling back from anything.
    ///
    /// <para>Per Phase 3 Task 4 Copilot review #5 — also commits a
    /// break + applies the overflow penalty when
    /// <see cref="BreakOpportunity.ChunkBlockSize"/> exceeds the
    /// fragmentainer extent (a chunk taller than a page can't fit on
    /// any page; the cost must reflect the overflow even when the
    /// current page hasn't yet exceeded its capacity).</para></remarks>
    public OptimizerResult ResolveBreaks(
        IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx)
    {
        ArgumentNullException.ThrowIfNull(opportunities);
        ArgumentNullException.ThrowIfNull(ctx);

        if (opportunities.Count == 0)
        {
            return OptimizerResult.Empty;
        }

        var breaks = new List<int>(capacity: Math.Min(64, opportunities.Count));
        double totalCost = 0;
        double pageStart = 0;

        for (var i = 0; i < opportunities.Count; i++)
        {
            var opp = opportunities[i];
            opp.EnsureValid();

            var pageSoFar = opp.UsedBlockSize - pageStart;
            var wouldOverflow = (pageSoFar + opp.ChunkBlockSize) > ctx.BlockSize;
            // Per Copilot review #5 — single-chunk-too-tall case.
            var chunkTooTall = opp.ChunkBlockSize > ctx.BlockSize;

            if (opp.ForceBreak || wouldOverflow || chunkTooTall)
            {
                var cost = CostModel.Score(
                    opp, ctx.BlockSize, OrphansRequired, WidowsRequired,
                    lineCountAfterBreak: WidowsRequired,
                    pageStart: pageStart);
                if (pageSoFar > ctx.BlockSize)
                {
                    cost += CostModel.BreakInsideAvoidViolation;
                }
                breaks.Add(i);
                totalCost += cost;
                pageStart = opp.UsedBlockSize;
            }
        }

        return new OptimizerResult(breaks, totalCost,
            FellBackToGreedy: false, FallbackReason: null);
    }

    /// <inheritdoc />
    public void RegisterCheckpoint(CheckpointLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease.Checkpoint);
        // Per PR #19 review #1 + Phase 3 Task 4 review fix #7 —
        // return the prior lease to the pool before overwriting. The
        // pool's lease-token CAS detects + rejects stale Returns, so
        // even if the prior checkpoint was returned earlier through
        // some other path the Return here is safe. Self-register
        // (the same lease registered twice) is a no-op so the still-
        // held lease keeps its rental.
        if (_lastLease.Checkpoint is not null
            && !ReferenceEquals(_lastLease.Checkpoint, lease.Checkpoint))
        {
            LayoutCheckpointPool.Return(_lastLease);
        }
        _lastLease = lease;
    }

    /// <inheritdoc />
    public LayoutCheckpoint? GetLastCheckpoint() => _lastLease.Checkpoint;

    /// <inheritdoc />
    /// <remarks>Per Phase 3 Task 5 PR #21 review fix #4 — releases
    /// the final held lease. Idempotent: a second Dispose call is a
    /// no-op (default-struct lease has null Checkpoint).</remarks>
    public void Dispose()
    {
        if (_lastLease.Checkpoint is not null)
        {
            LayoutCheckpointPool.Return(_lastLease);
            _lastLease = default;
        }
    }
}
