// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 Task 4 — the production <see cref="IBreakResolver"/>:
/// delegates to <see cref="Optimizer.Optimize"/> for batched windows
/// (the cost-minimizing path) + falls back to greedy single-candidate
/// streaming when the layouter calls <see cref="ConsiderBreakAt"/>.
///
/// <para><b>Why two paths.</b> The bounded DP optimizer's 2-page
/// lookahead requires SEEING a window of candidates — a layouter that
/// streams "consider this one, now this one, now this one" can't
/// produce DP-optimal breaks (the lookahead has nothing to look ahead
/// at). Two viable strategies:</para>
/// <list type="bullet">
///   <item><b>Batched (preferred).</b> Layouter pre-computes its
///   candidate set for a logical section + calls
///   <see cref="ResolveBreaks"/>. The DP runs end-to-end + returns
///   globally-best break indices. Used by inline (paragraph lines),
///   table (rows), flex (lines), grid (rows).</item>
///   <item><b>Streaming (fallback).</b> Layouter calls
///   <see cref="ConsiderBreakAt"/> per candidate. Even the optimizing
///   resolver degrades to greedy here — there's no window. Used by
///   block layouters with deep recursion + unknown total height.
///   <see cref="OptimizerResult.FellBackToGreedy"/> would not apply
///   here (no DP was attempted); the layouter SHOULD prefer batched
///   when the section is enumerable.</item>
/// </list>
///
/// <para><b>Replacement of <see cref="BreakResolver"/>.</b> The Phase 3
/// plan + Task 1 stub explicitly leave the optimizer for Task 4. The
/// composition root (Phase 3 Task 7+ — the first real layouter) wires
/// either resolver behind the <see cref="IBreakResolver"/> interface;
/// tests pin both.</para>
///
/// <para><b>Diagnostic emission.</b> The optimizer signals fallback
/// via <see cref="OptimizerResult.FellBackToGreedy"/>. The integrating
/// layouter is responsible for translating that flag into a
/// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> diagnostic on the public
/// <c>NetPdf.IDiagnosticsSink</c>. <see cref="OptimizingBreakResolver"/>
/// itself does NOT emit diagnostics — Paginate's dependency direction
/// (NetPdf → NetPdf.Paginate, not the reverse) prohibits referencing
/// the facade's sink type. See <c>OptimizerResult</c> XML doc for the
/// full rationale.</para>
/// </summary>
internal sealed class OptimizingBreakResolver : IBreakResolver
{
    /// <summary>Author's <c>orphans</c> property — defaults to 2 per
    /// CSS Fragmentation L3 §4.2.</summary>
    public int OrphansRequired { get; }

    /// <summary>Author's <c>widows</c> property — defaults to 2 per
    /// CSS Fragmentation L3 §4.2.</summary>
    public int WidowsRequired { get; }

    /// <summary>Per PR #19 review #1 — same single-slot checkpoint
    /// strategy as the greedy stub. The DP optimizer's bounded retry
    /// loop (Task 5) replaces this with a frontier-aware version that
    /// can retain multiple checkpoints; until then, single-slot is
    /// correct for the windowed batched flow (the resolver's caller
    /// re-registers a fresh checkpoint at the start of each window).</summary>
    private LayoutCheckpoint? _lastCheckpoint;

    /// <summary>Per Phase 3 Task 4 — count of times <see cref="ResolveBreaks"/>
    /// fell through to greedy on this resolver instance. Test-only
    /// observation point; the layouter integration emits the
    /// per-call diagnostic via <see cref="OptimizerResult.FellBackToGreedy"/>.</summary>
    internal int FallbackCount { get; private set; }

    public OptimizingBreakResolver() : this(orphansRequired: 2, widowsRequired: 2) { }

    public OptimizingBreakResolver(int orphansRequired, int widowsRequired)
    {
        if (orphansRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(orphansRequired));
        if (widowsRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(widowsRequired));
        OrphansRequired = orphansRequired;
        WidowsRequired = widowsRequired;
    }

    /// <inheritdoc />
    /// <remarks>Streaming path — degrades to greedy regardless of
    /// resolver kind. The DP needs a window; one candidate isn't a
    /// window. Layouters that can pre-enumerate their candidates
    /// SHOULD call <see cref="ResolveBreaks"/> instead.</remarks>
    public BreakDecision ConsiderBreakAt(BreakOpportunity opportunity, FragmentainerContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var cost = CostModel.Score(
            opportunity,
            contentBlockSize: ctx.BlockSize,
            orphansRequired: OrphansRequired,
            widowsRequired: WidowsRequired,
            // Streaming path doesn't know the next-page line count —
            // the batched path is preferred for accurate widow scoring.
            lineCountAfterBreak: WidowsRequired);

        if (opportunity.ForceBreak)
        {
            return new BreakDecision(BreakAction.BreakHere, cost, RewindTo: null);
        }

        if (opportunity.ChunkBlockSize <= ctx.RemainingBlockSize)
        {
            return new BreakDecision(BreakAction.Continue, cost, RewindTo: null);
        }

        return new BreakDecision(BreakAction.BreakHere, cost, RewindTo: null);
    }

    /// <inheritdoc />
    /// <remarks>Per Phase 3 Task 4 — runs the bounded DP optimizer
    /// over <paramref name="opportunities"/>. When the optimizer
    /// reports <see cref="OptimizerResult.FellBackToGreedy"/>,
    /// <see cref="FallbackCount"/> increments so tests can observe
    /// the fallback rate without parsing diagnostics.</remarks>
    public OptimizerResult ResolveBreaks(
        IReadOnlyList<BreakOpportunity> opportunities, FragmentainerContext ctx)
    {
        ArgumentNullException.ThrowIfNull(opportunities);
        ArgumentNullException.ThrowIfNull(ctx);

        var result = Optimizer.Optimize(
            opportunities,
            contentBlockSize: ctx.BlockSize,
            orphansRequired: OrphansRequired,
            widowsRequired: WidowsRequired);

        if (result.FellBackToGreedy)
        {
            FallbackCount++;
        }

        return result;
    }

    /// <inheritdoc />
    public void RegisterCheckpoint(LayoutCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (_lastCheckpoint is not null && !ReferenceEquals(_lastCheckpoint, checkpoint))
        {
            LayoutCheckpointPool.Return(_lastCheckpoint);
        }
        _lastCheckpoint = checkpoint;
    }

    /// <inheritdoc />
    public LayoutCheckpoint? GetLastCheckpoint() => _lastCheckpoint;
}
