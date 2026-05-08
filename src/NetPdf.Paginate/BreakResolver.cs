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
    /// across attempts. Pre-fix the stub appended into a <c>List</c>
    /// without ever returning to the pool — once layouters started
    /// registering checkpoints (Phase 3 Task 4+) memory would grow
    /// without bound + defeat the pool entirely.
    ///
    /// <para>The DP optimizer (Task 4) replaces this single-slot
    /// strategy with a frontier-aware version that returns
    /// checkpoints to the pool as the rewind frontier passes them;
    /// until then, single-slot is correct for the stub's greedy
    /// behavior (rewind isn't emitted, so prior checkpoints are
    /// always reachable-only-by-the-most-recent registration).</para>
    /// </summary>
    private LayoutCheckpoint? _lastCheckpoint;

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
    /// design, not falling back from anything.</remarks>
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

            if (opp.ForceBreak || wouldOverflow)
            {
                var cost = CostModel.Score(
                    opp, ctx.BlockSize, OrphansRequired, WidowsRequired,
                    lineCountAfterBreak: WidowsRequired);
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
    public void RegisterCheckpoint(LayoutCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        // Per PR #19 review #1 — return the prior checkpoint to the
        // pool before overwriting. The IBreakResolver contract says
        // the resolver returns checkpoints to LayoutCheckpointPool
        // once the rewind frontier passes; for the stub, "passes" =
        // "a newer checkpoint is registered" (greedy never rewinds,
        // so prior checkpoints are unreachable once superseded).
        if (_lastCheckpoint is not null && !ReferenceEquals(_lastCheckpoint, checkpoint))
        {
            LayoutCheckpointPool.Return(_lastCheckpoint);
        }
        _lastCheckpoint = checkpoint;
    }

    /// <inheritdoc />
    public LayoutCheckpoint? GetLastCheckpoint() => _lastCheckpoint;
}
