// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 5 — bounded retry coordinator tests. Drives a
/// <see cref="MockLayouter"/> through every retry-loop branch:
/// trivial PageComplete (0 retries), single-retry success (1 retry),
/// double-retry success (2 retries), LastResort with diagnostic
/// emission (2 retries + LastResort), AllDone path, and
/// LastResort-still-NeedsRewind contract violation defense.
/// </summary>
public sealed class LayoutRetryCoordinatorTests
{
    // --- AttemptToStrategy mapping -----------------------------------

    [Theory]
    [InlineData(0, (int)LayoutAttemptStrategy.Strict)]
    [InlineData(1, (int)LayoutAttemptStrategy.DropAvoidInside)]
    [InlineData(2, (int)LayoutAttemptStrategy.LastResort)]
    [InlineData(3, (int)LayoutAttemptStrategy.LastResort)] // saturate beyond MaxRetries
    [InlineData(99, (int)LayoutAttemptStrategy.LastResort)]
    public void AttemptToStrategy_progressive_relaxation(
        int attempt, int expectedStrategyAsInt)
    {
        // Param typed as int (not LayoutAttemptStrategy) because the
        // enum is internal — xUnit's [Theory] discovery requires
        // public-accessible parameter types.
        var expected = (LayoutAttemptStrategy)expectedStrategyAsInt;
        Assert.Equal(expected, LayoutRetryCoordinator.AttemptToStrategy(attempt));
    }

    // --- Trivial PageComplete (no retries) ---------------------------

    [Fact]
    public void Run_returns_layouter_result_when_no_rewind_needed()
    {
        var layouter = new MockLayouter();
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 100);

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        var result = coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(100, result.Cost);
        Assert.Single(layouter.AttemptLog);
        Assert.Equal(LayoutAttemptStrategy.Strict, layouter.AttemptLog[0]);
    }

    [Fact]
    public void Run_returns_AllDone_without_emitting_diagnostic()
    {
        var layouter = new MockLayouter();
        layouter.Plan(LayoutAttemptOutcome.AllDone, cost: 0);
        var sink = new RecordingSink();

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(sink);

        var result = coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Empty(sink.Diagnostics);
    }

    // --- Retry path ---------------------------------------------------

    [Fact]
    public void Run_retries_once_when_first_attempt_returns_NeedsRewind()
    {
        var layouter = new MockLayouter();
        // Attempt 0 (Strict) → NeedsRewind. Attempt 1 (DropAvoidInside)
        // → PageComplete. Coordinator retries once + returns the
        // second result.
        layouter.PlanRewind(cost: 1000);
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 200);

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        var result = coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(200, result.Cost);
        Assert.Equal(2, layouter.AttemptLog.Count);
        Assert.Equal(LayoutAttemptStrategy.Strict, layouter.AttemptLog[0]);
        Assert.Equal(LayoutAttemptStrategy.DropAvoidInside, layouter.AttemptLog[1]);
    }

    [Fact]
    public void Run_retries_twice_when_first_two_attempts_return_NeedsRewind()
    {
        var layouter = new MockLayouter();
        // Attempts 0, 1 → NeedsRewind. Attempt 2 (LastResort) → PageComplete.
        layouter.PlanRewind(cost: 1000);
        layouter.PlanRewind(cost: 800);
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 500);

        var sink = new RecordingSink();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(sink);

        var result = coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(500, result.Cost);
        Assert.Equal(3, layouter.AttemptLog.Count);
        Assert.Equal(LayoutAttemptStrategy.Strict, layouter.AttemptLog[0]);
        Assert.Equal(LayoutAttemptStrategy.DropAvoidInside, layouter.AttemptLog[1]);
        Assert.Equal(LayoutAttemptStrategy.LastResort, layouter.AttemptLog[2]);

        // Diagnostic emitted before LastResort attempt.
        Assert.Single(sink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            sink.Diagnostics[0].Code);
        Assert.Equal(PaginateDiagnosticSeverity.Warning, sink.Diagnostics[0].Severity);
    }

    // --- LastResort + still-NeedsRewind contract violation ----------

    [Fact]
    public void Run_handles_layouter_contract_violation_at_LastResort()
    {
        // Defensive: if the layouter erroneously returns NeedsRewind on
        // the LastResort attempt (contract violation), the coordinator
        // must still terminate + return a best-effort result rather
        // than infinite-looping.
        var layouter = new MockLayouter();
        layouter.PlanRewind(cost: 1000);  // attempt 0
        layouter.PlanRewind(cost: 1000);  // attempt 1
        layouter.PlanRewind(cost: 1000);  // attempt 2 — contract violation

        var sink = new RecordingSink();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(sink);

        var result = coordinator.Run(layouter, ctx, ref layout, resolver);

        // The coordinator should NOT return NeedsRewind to its caller —
        // by the time we exhaust retries, the result must be terminal.
        Assert.NotEqual(LayoutAttemptOutcome.NeedsRewind, result.Outcome);
        Assert.Equal(3, layouter.AttemptLog.Count);

        // Diagnostic still emitted — it's emitted BEFORE the LastResort
        // attempt regardless of what the attempt returns.
        Assert.Single(sink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationForcedOverflow001,
            sink.Diagnostics[0].Code);
    }

    // --- Diagnostic sink threading ---------------------------------

    [Fact]
    public void Run_no_diagnostic_when_sink_is_null()
    {
        // Coordinator with null sink must not throw when LastResort
        // is reached — it just doesn't emit.
        var layouter = new MockLayouter();
        layouter.PlanRewind(cost: 1000);
        layouter.PlanRewind(cost: 1000);
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 500);

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(diagnostics: null);

        // Should not throw.
        var result = coordinator.Run(layouter, ctx, ref layout, resolver);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
    }

    [Fact]
    public void Run_diagnostic_message_includes_page_index()
    {
        var layouter = new MockLayouter();
        layouter.PlanRewind(cost: 1000);
        layouter.PlanRewind(cost: 1000);
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 500);

        var sink = new RecordingSink();
        var ctx = new FragmentainerContext(600, 800) { PageIndex = 42 };
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(sink);

        coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Single(sink.Diagnostics);
        Assert.Contains("42", sink.Diagnostics[0].Message);
    }

    // --- RewindTo checkpoint restoration -------------------------

    [Fact]
    public void Run_restores_state_from_RewindTo_checkpoint()
    {
        // The coordinator calls cp.RestoreInto on the named checkpoint
        // when the layouter returns NeedsRewind. Verify that mutations
        // made on the failing attempt are rolled back.
        var ctx = new FragmentainerContext(600, 800)
        {
            PageIndex = 0,
            UsedBlockSize = 100,
        };
        ctx.NamedStrings["chapter"] = "Original";

        var layout = new LayoutContext(ctx);

        // Capture a checkpoint at the "good" state.
        var lease = LayoutCheckpointPool.Rent();
        var cp = lease.Checkpoint!;
        cp.Capture(ctx, layout, fragmentOutputCursor: 0,
            lastEmittedChildIndex: -1, incomingContinuation: null,
            pageCounterValue: 0);

        // Mock layouter that mutates ctx state in attempt 0 + returns
        // NeedsRewind, then in attempt 1 verifies that state was
        // restored.
        var layouter = new StateMutatingMockLayouter(cp);

        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();
        var result = coordinator.Run(layouter, ctx, ref layout, resolver);

        // After coordinator finishes: state was restored before attempt 1
        // (StateMutatingMockLayouter records the observed state at the
        // start of each attempt).
        Assert.Equal(2, layouter.ObservedAtStart.Count);
        // Attempt 1 must see the original state (UsedBlockSize=100,
        // chapter="Original") — proves the checkpoint was applied.
        Assert.Equal(100, layouter.ObservedAtStart[1].UsedBlockSize);
        Assert.Equal("Original", layouter.ObservedAtStart[1].Chapter);

        LayoutCheckpointPool.Return(lease);
    }

    // --- Null-arg validation ---------------------------------------

    [Fact]
    public void Run_throws_on_null_layouter()
    {
        // Note: try/catch instead of Assert.Throws<> because the
        // coordinator's `ref LayoutContext` parameter cannot be
        // captured by a lambda (CS8175).
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        var threw = false;
        try
        {
            coordinator.Run(null!, ctx, ref layout, resolver);
        }
        catch (System.ArgumentNullException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected ArgumentNullException for null layouter");
    }

    [Fact]
    public void Run_throws_on_null_fragmentainer()
    {
        var layouter = new MockLayouter();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        var threw = false;
        try
        {
            coordinator.Run(layouter, null!, ref layout, resolver);
        }
        catch (System.ArgumentNullException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected ArgumentNullException for null fragmentainer");
    }

    [Fact]
    public void Run_throws_on_null_resolver()
    {
        var layouter = new MockLayouter();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var coordinator = new LayoutRetryCoordinator();

        var threw = false;
        try
        {
            coordinator.Run(layouter, ctx, ref layout, null!);
        }
        catch (System.ArgumentNullException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected ArgumentNullException for null resolver");
    }

    // --- LayoutAttemptResult helpers -------------------------------

    [Fact]
    public void LayoutAttemptResult_PageComplete_helper_sets_correct_fields()
    {
        var continuation = new BlockContinuation(3, 100);
        var r = LayoutAttemptResult.PageComplete(continuation, 250);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, r.Outcome);
        Assert.Same(continuation, r.Continuation);
        Assert.Null(r.RewindTo);
        Assert.Equal(250, r.Cost);
    }

    [Fact]
    public void LayoutAttemptResult_AllDone_helper_clears_continuation()
    {
        var r = LayoutAttemptResult.AllDone(0);
        Assert.Equal(LayoutAttemptOutcome.AllDone, r.Outcome);
        Assert.Null(r.Continuation);
        Assert.Null(r.RewindTo);
        Assert.Equal(0, r.Cost);
    }

    [Fact]
    public void LayoutAttemptResult_NeedsRewind_helper_carries_checkpoint()
    {
        var lease = LayoutCheckpointPool.Rent();
        var cp = lease.Checkpoint!;
        var r = LayoutAttemptResult.NeedsRewind(cp, 1000);
        Assert.Equal(LayoutAttemptOutcome.NeedsRewind, r.Outcome);
        Assert.Null(r.Continuation);
        Assert.Same(cp, r.RewindTo);
        Assert.Equal(1000, r.Cost);
        LayoutCheckpointPool.Return(lease);
    }

    // --- Diagnostic types --------------------------------------

    [Fact]
    public void PaginateDiagnosticCodes_pagination_forced_overflow_literal()
    {
        // Pin the constant value to docs/diagnostics-codes.md row.
        Assert.Equal("PAGINATION-FORCED-OVERFLOW-001",
            PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public void PaginateDiagnosticCodes_pagination_optimizer_fallback_literal()
    {
        Assert.Equal("PAGINATION-OPTIMIZER-FALLBACK-001",
            PaginateDiagnosticCodes.PaginationOptimizerFallback001);
    }

    [Fact]
    public void PaginateDiagnosticCodes_match_facade_codes()
    {
        // Parity check — Paginate-side codes must equal facade-side
        // strings. Diverging would let one assembly emit a code the
        // other doesn't recognize.
        Assert.Equal(NetPdf.DiagnosticCodes.PaginationForcedOverflow001,
            PaginateDiagnosticCodes.PaginationForcedOverflow001);
        Assert.Equal(NetPdf.DiagnosticCodes.PaginationOptimizerFallback001,
            PaginateDiagnosticCodes.PaginationOptimizerFallback001);
    }

    // ====================================================================
    //  Test doubles
    // ====================================================================

    /// <summary>Mock layouter that emits a pre-planned sequence of
    /// <see cref="LayoutAttemptResult"/>s + records each invocation's
    /// strategy.</summary>
    private sealed class MockLayouter : ILayouter
    {
        private readonly Queue<LayoutAttemptResult> _planned = new();
        public List<LayoutAttemptStrategy> AttemptLog { get; } = new();

        public void Plan(LayoutAttemptOutcome outcome, double cost)
        {
            _planned.Enqueue(new LayoutAttemptResult(outcome, null, null, cost));
        }

        public void PlanRewind(double cost)
        {
            // The coordinator only inspects the RewindTo when it's
            // non-null; a null RewindTo here means the coordinator's
            // RestoreInto call is a no-op. Sufficient for testing the
            // retry sequencing.
            _planned.Enqueue(new LayoutAttemptResult(
                LayoutAttemptOutcome.NeedsRewind, null, null, cost));
        }

        public LayoutAttemptResult AttemptLayout(
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver,
            LayoutAttemptStrategy strategy)
        {
            AttemptLog.Add(strategy);
            return _planned.Count > 0
                ? _planned.Dequeue()
                : LayoutAttemptResult.PageComplete(null, 0);
        }
    }

    /// <summary>Mock layouter that mutates state on attempt 0, returns
    /// NeedsRewind with a real checkpoint, then records the observed
    /// state at the start of attempt 1 to verify restoration.</summary>
    private sealed class StateMutatingMockLayouter : ILayouter
    {
        private readonly LayoutCheckpoint _checkpoint;
        public List<(double UsedBlockSize, string? Chapter)> ObservedAtStart { get; } = new();

        public StateMutatingMockLayouter(LayoutCheckpoint checkpoint)
        {
            _checkpoint = checkpoint;
        }

        public LayoutAttemptResult AttemptLayout(
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver,
            LayoutAttemptStrategy strategy)
        {
            ObservedAtStart.Add((
                fragmentainer.UsedBlockSize,
                fragmentainer.NamedStrings.TryGetValue("chapter", out var c) ? c : null));

            if (strategy == LayoutAttemptStrategy.Strict)
            {
                // Mutate state then ask for rewind.
                fragmentainer.UsedBlockSize = 999;
                fragmentainer.NamedStrings["chapter"] = "Mutated";
                return LayoutAttemptResult.NeedsRewind(_checkpoint, cost: 1000);
            }

            // Subsequent attempt — observe the restored state, then complete.
            return LayoutAttemptResult.PageComplete(null, cost: 100);
        }
    }

    /// <summary>Recording sink that captures all emitted diagnostics
    /// for assertion.</summary>
    private sealed class RecordingSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }
}
