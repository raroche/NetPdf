// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Paginate.Diagnostics;

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
/// <para><b>Diagnostic emission (Phase 3 Task 5 PR #21 review fix #5).</b>
/// The optimizer signals fallback via
/// <see cref="OptimizerResult.FellBackToGreedy"/>. When this resolver
/// is constructed with a non-<see langword="null"/>
/// <see cref="IPaginateDiagnosticsSink"/>, it emits
/// <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> directly on each batched
/// fallback — closing the gap where future layouters could silently
/// drop the diagnostic by forgetting to read
/// <see cref="OptimizerResult.FellBackToGreedy"/>. The integrating
/// composition root translates the Paginate-side diagnostic to the
/// public <c>NetPdf.IDiagnosticsSink</c> at the assembly boundary.</para>
/// </summary>
internal sealed class OptimizingBreakResolver : IBreakResolver
{
    /// <summary>Author's <c>orphans</c> property — defaults to 2 per
    /// CSS Fragmentation L3 §4.2.</summary>
    public int OrphansRequired { get; }

    /// <summary>Author's <c>widows</c> property — defaults to 2 per
    /// CSS Fragmentation L3 §4.2.</summary>
    public int WidowsRequired { get; }

    /// <summary>Per Phase 3 Task 5 PR #21 review fix #5 — optional
    /// diagnostic sink. When non-<see langword="null"/>, the resolver
    /// emits <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> on every batched
    /// fallback. <see langword="null"/> sink keeps the legacy
    /// "FallbackCount only" behavior for unit tests.</summary>
    public IPaginateDiagnosticsSink? Diagnostics { get; }

    /// <summary>Per PR #19 review #1 + Phase 3 Task 4 review fix #7 —
    /// stores the full <see cref="CheckpointLease"/> (not just the
    /// bare checkpoint) so the resolver-internal Return correctly
    /// presents the lease's token, letting the pool reject
    /// stale-after-rerent races. Same single-slot strategy as the
    /// greedy stub for now; Task 5's bounded retry loop will replace
    /// with a frontier-aware version that retains multiple leases.</summary>
    private CheckpointLease _lastLease;

    /// <summary>Per Phase 3 Task 4 — count of times <see cref="ResolveBreaks"/>
    /// fell through to greedy on this resolver instance. Test-only
    /// observation point + a redundancy check for the diagnostic
    /// emission path.</summary>
    internal int FallbackCount { get; private set; }

    public OptimizingBreakResolver()
        : this(orphansRequired: 2, widowsRequired: 2, diagnostics: null) { }

    public OptimizingBreakResolver(int orphansRequired, int widowsRequired)
        : this(orphansRequired, widowsRequired, diagnostics: null) { }

    public OptimizingBreakResolver(
        int orphansRequired,
        int widowsRequired,
        IPaginateDiagnosticsSink? diagnostics)
    {
        if (orphansRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(orphansRequired));
        if (widowsRequired < 0)
            throw new ArgumentOutOfRangeException(nameof(widowsRequired));
        OrphansRequired = orphansRequired;
        WidowsRequired = widowsRequired;
        Diagnostics = diagnostics;
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
    /// reports <see cref="OptimizerResult.FellBackToGreedy"/>:
    /// <list type="bullet">
    ///   <item><see cref="FallbackCount"/> increments (test
    ///   observation hook).</item>
    ///   <item>Per Phase 3 Task 5 PR #21 review fix #5 — emits
    ///   <c>PAGINATION-OPTIMIZER-FALLBACK-001</c> on
    ///   <see cref="Diagnostics"/> when supplied. The fallback reason
    ///   from the optimizer is included in the message so the consumer
    ///   can distinguish budget-exhaustion vs monotonicity-violation
    ///   vs no-feasible-pair root causes.</item>
    /// </list></remarks>
    public OptimizerResult ResolveBreaks(
        IReadOnlyList<BreakOpportunity> opportunities,
        FragmentainerContext ctx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunities);
        ArgumentNullException.ThrowIfNull(ctx);

        var result = Optimizer.Optimize(
            opportunities,
            contentBlockSize: ctx.BlockSize,
            orphansRequired: OrphansRequired,
            widowsRequired: WidowsRequired,
            cancellationToken: cancellationToken);

        if (result.FellBackToGreedy)
        {
            FallbackCount++;
            // Per PR #21 review fix #5 + PR #24 review pass — emit the
            // diagnostic guarded against sink throws. The
            // IPaginateDiagnosticsSink contract says Emit MUST NOT
            // throw, but a misbehaving sink shouldn't be able to take
            // down the layout pipeline. Catch + drop on the floor;
            // the FallbackCount is still incremented so observability
            // tools that check the resolver instance can detect the
            // condition.
            SafeEmit(Diagnostics, new PaginateDiagnostic(
                PaginateDiagnosticCodes.PaginationOptimizerFallback001,
                $"Optimizer fell back to greedy: {result.FallbackReason ?? "<no reason given>"}",
                PaginateDiagnosticSeverity.Info));
        }

        return result;
    }

    /// <inheritdoc />
    public void RegisterCheckpoint(CheckpointLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease.Checkpoint);
        // Per Phase 3 Task 4 review fix #7 — return the prior lease to
        // the pool. The pool's CAS rejects stale-after-rerent so the
        // Return here is safe even if the prior checkpoint was
        // already cycled through a different path.
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

    /// <summary>Per PR #24 review pass — guarded diagnostic emission.
    /// The <see cref="IPaginateDiagnosticsSink"/> contract says
    /// implementations MUST NOT throw, but a misbehaving sink (e.g.,
    /// a hostile host adapter) shouldn't be able to take down the
    /// layout pipeline. Wraps <see cref="IPaginateDiagnosticsSink.Emit"/>
    /// in try/catch + drops the exception on the floor — the
    /// fallback count is still incremented at the call site so
    /// observability tools can detect the condition without parsing
    /// diagnostics.
    ///
    /// <para>Catches all exceptions deliberately. The alternative —
    /// letting a sink throw bubble up — would corrupt the layouter's
    /// retry state (Capture / Register lifecycle) without any
    /// recovery path.</para></summary>
    internal static void SafeEmit(IPaginateDiagnosticsSink? sink, PaginateDiagnostic diagnostic)
    {
        if (sink is null) return;
        try { sink.Emit(diagnostic); }
        catch { /* contract violation by sink — drop on floor */ }
    }
}
