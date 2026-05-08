// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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

    /// <summary>Stack of registered checkpoints. The most recently
    /// registered is at the top; <see cref="GetLastCheckpoint"/>
    /// returns the top. <c>List</c> rather than <c>Stack</c> so
    /// tests can inspect prior registrations.</summary>
    private readonly List<LayoutCheckpoint> _checkpoints = new();

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
    public void RegisterCheckpoint(LayoutCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _checkpoints.Add(checkpoint);
    }

    /// <inheritdoc />
    public LayoutCheckpoint? GetLastCheckpoint()
    {
        if (_checkpoints.Count == 0) return null;
        return _checkpoints[^1];
    }
}
