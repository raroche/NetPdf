// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
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
    public void Run_throws_InvalidOperationException_when_LastResort_returns_NeedsRewind()
    {
        // Per PR #24 review pass — a layouter that returns NeedsRewind
        // on the LastResort attempt is a HARD contract violation. The
        // coordinator throws InvalidOperationException rather than
        // synthesizing a PageComplete (which the cycle 1 + 2 behavior
        // did, masking real layouter bugs that could drop content).
        // Fail-fast surfaces the violation immediately.
        var layouter = new MockLayouter();
        layouter.PlanRewind(cost: 1000);  // attempt 0
        layouter.PlanRewind(cost: 1000);  // attempt 1
        layouter.PlanRewind(cost: 1000);  // attempt 2 — contract violation

        var sink = new RecordingSink();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(sink);

        var threw = false;
        try
        {
            coordinator.Run(layouter, ctx, ref layout, resolver);
        }
        catch (System.InvalidOperationException ex)
        {
            threw = true;
            Assert.Contains("ILayouter contract violation", ex.Message);
            Assert.Contains("LastResort", ex.Message);
        }
        Assert.True(threw, "Coordinator must throw on LastResort+NeedsRewind contract violation");

        // All 3 attempts ran (the throw fires at the end of the
        // 3rd attempt's NeedsRewind branch).
        Assert.Equal(3, layouter.AttemptLog.Count);

        // Diagnostic still emitted before the LastResort attempt —
        // emission order is BEFORE attempt runs.
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
    //  PR #21 follow-up review fixes — regression tests
    // ====================================================================

    // --- Rec #1: NeedsRewind with null RewindTo is a contract violation -

    [Fact]
    public void LayoutAttemptResult_validate_throws_on_NeedsRewind_with_null_checkpoint()
    {
        // Direct construction (bypassing the factory) with NeedsRewind
        // + null RewindTo violates the invariant. ValidateOrThrow
        // catches it.
        var bad = new LayoutAttemptResult(
            LayoutAttemptOutcome.NeedsRewind,
            Continuation: null,
            RewindTo: null,
            Cost: 1000);

        Assert.Throws<InvalidOperationException>(() => bad.ValidateOrThrow());
    }

    [Fact]
    public void LayoutAttemptResult_validate_throws_on_AllDone_with_continuation()
    {
        var bad = new LayoutAttemptResult(
            LayoutAttemptOutcome.AllDone,
            Continuation: new BlockContinuation(0, 0),
            RewindTo: null,
            Cost: 0);

        Assert.Throws<InvalidOperationException>(() => bad.ValidateOrThrow());
    }

    [Fact]
    public void LayoutAttemptResult_NeedsRewind_factory_throws_on_null_checkpoint()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LayoutAttemptResult.NeedsRewind(null!, cost: 0));
    }

    [Fact]
    public void Coordinator_throws_when_layouter_returns_NeedsRewind_with_null_checkpoint()
    {
        // A layouter that constructs NeedsRewind directly with null
        // RewindTo (bypassing the factory) MUST cause the coordinator
        // to throw — not silently retry from dirty state.
        var layouter = new InvariantViolatingLayouter();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        var threw = false;
        try
        {
            coordinator.Run(layouter, ctx, ref layout, resolver);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert.True(threw, "Coordinator must throw on invariant violation, not retry from dirty state.");
    }

    // --- Rec #2: fragmentainer sync after restore ----------------------

    [Fact]
    public void Coordinator_syncs_fragmentainer_to_captured_one_after_restore()
    {
        // Per PR #21 review fix #2 — after RestoreInto reseats
        // layout.Fragmentainer to the captured (original) one, the
        // coordinator must pass that SAME instance (not the original
        // parameter) into the next AttemptLayout call.
        var original = new FragmentainerContext(600, 800)
        {
            PageIndex = 0,
            UsedBlockSize = 100,
        };
        var layout = new LayoutContext(original);

        // Capture a checkpoint while layout.Fragmentainer = original.
        var lease = LayoutCheckpointPool.Rent();
        var cp = lease.Checkpoint!;
        cp.Capture(original, layout, fragmentOutputCursor: 0,
            lastEmittedChildIndex: -1, incomingContinuation: null,
            pageCounterValue: 0);

        // Layouter that swaps to a clone on attempt 0 + asks for
        // rewind, then on attempt 1 records which fragmentainer it
        // received.
        var layouter = new SwapAndRewindLayouter(cp, original);

        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();
        var result = coordinator.Run(layouter, original, ref layout, resolver);

        // Per PR #24 review pass — SwapAndRewindLayouter's
        // attempt-1 path returns AllDone (no further pages needed).
        // The test's primary assertion is ReceivedFragmentainers
        // reference identity, not the outcome shape.
        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Equal(2, layouter.ReceivedFragmentainers.Count);
        // After restore, the coordinator MUST pass the captured
        // (original) fragmentainer to attempt 1, NOT the speculative
        // clone that the layouter swapped to during attempt 0.
        Assert.Same(original, layouter.ReceivedFragmentainers[1]);

        LayoutCheckpointPool.Return(lease);
    }

    // --- Rec #3: IFragmentSink.RollbackTo is called on rewind --------

    [Fact]
    public void Coordinator_calls_RollbackTo_with_captured_cursor_on_rewind()
    {
        var sink = new RecordingFragmentSink();
        var layouter = new MockLayouter();
        // Capture a real checkpoint with a non-zero FragmentOutputCursor
        // so we can verify the rollback gets the right value.
        layouter.PlanRewindWithCursor(cost: 1000, fragmentOutputCursor: 7);
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 100);

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(diagnostics: null, fragmentSink: sink);

        coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Single(sink.RollbackCallLog);
        Assert.Equal(7, sink.RollbackCallLog[0]);

        layouter.Dispose();
    }

    [Fact]
    public void Coordinator_rolls_back_twice_for_two_rewinds()
    {
        var sink = new RecordingFragmentSink();
        var layouter = new MockLayouter();
        layouter.PlanRewindWithCursor(cost: 1000, fragmentOutputCursor: 5);  // attempt 0 fails
        layouter.PlanRewindWithCursor(cost: 1000, fragmentOutputCursor: 5);  // attempt 1 fails
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 200);          // attempt 2 (LastResort)

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        // Provide the diagnostics sink so the LastResort emits properly.
        var diagSink = new RecordingSink();
        var coordinator = new LayoutRetryCoordinator(diagSink, sink);

        coordinator.Run(layouter, ctx, ref layout, resolver);

        Assert.Equal(2, sink.RollbackCallLog.Count);

        layouter.Dispose();
    }

    [Fact]
    public void Coordinator_no_rollback_when_no_fragment_sink_supplied()
    {
        // Coordinator with null fragmentSink must not throw on rewind —
        // layouters that emit only on PageComplete don't need a sink.
        var layouter = new MockLayouter();
        layouter.PlanRewindWithCursor(cost: 1000, fragmentOutputCursor: 7);
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 100);

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(diagnostics: null, fragmentSink: null);

        // Should not throw despite fragmentSink being null.
        var result = coordinator.Run(layouter, ctx, ref layout, resolver);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);

        layouter.Dispose();
    }

    // --- Rec #4: IBreakResolver.Dispose returns held lease -----------

    [Fact]
    public void BreakResolver_dispose_returns_held_lease_to_pool()
    {
        var resolver = new BreakResolver();
        var lease = LayoutCheckpointPool.Rent();
        var cp = lease.Checkpoint!;
        cp.PageIndex = 7;
        resolver.RegisterCheckpoint(lease);

        Assert.Same(cp, resolver.GetLastCheckpoint());

        resolver.Dispose();

        // After dispose, the lease should be returned to the pool —
        // the next Rent might receive cp (now reset).
        var nextLease = LayoutCheckpointPool.Rent();
        // Reset on rent → fresh state.
        Assert.Equal(0, nextLease.Checkpoint!.PageIndex);
        LayoutCheckpointPool.Return(nextLease);
    }

    [Fact]
    public void BreakResolver_dispose_is_idempotent()
    {
        var resolver = new BreakResolver();
        var lease = LayoutCheckpointPool.Rent();
        resolver.RegisterCheckpoint(lease);
        resolver.Dispose();
        // Second dispose must be safe — no-op (default-struct lease
        // has null Checkpoint after first dispose).
        resolver.Dispose();
    }

    [Fact]
    public void OptimizingResolver_dispose_returns_held_lease()
    {
        var resolver = new OptimizingBreakResolver();
        var lease = LayoutCheckpointPool.Rent();
        var cp = lease.Checkpoint!;
        cp.PageIndex = 11;
        resolver.RegisterCheckpoint(lease);

        Assert.Same(cp, resolver.GetLastCheckpoint());

        resolver.Dispose();

        var nextLease = LayoutCheckpointPool.Rent();
        Assert.Equal(0, nextLease.Checkpoint!.PageIndex);
        LayoutCheckpointPool.Return(nextLease);
    }

    [Fact]
    public void Resolver_using_block_releases_lease_on_scope_exit()
    {
        // Idiomatic usage pattern — resolvers are IDisposable so
        // `using var resolver = ...` releases the held lease on
        // scope exit.
        LayoutCheckpoint? captured;
        using (var resolver = new BreakResolver())
        {
            var lease = LayoutCheckpointPool.Rent();
            captured = lease.Checkpoint;
            captured!.PageIndex = 99;
            resolver.RegisterCheckpoint(lease);
        }
        // After using-block: the lease was returned via resolver.Dispose.
        // Renting again should yield a Reset checkpoint.
        var nextLease = LayoutCheckpointPool.Rent();
        Assert.Equal(0, nextLease.Checkpoint!.PageIndex);
        LayoutCheckpointPool.Return(nextLease);
    }

    // --- Rec #5: PAGINATION-OPTIMIZER-FALLBACK-001 emission ---------

    [Fact]
    public void OptimizingResolver_emits_fallback_diagnostic_when_optimizer_falls_back()
    {
        var sink = new RecordingSink();
        using var resolver = new OptimizingBreakResolver(
            orphansRequired: 2, widowsRequired: 2, diagnostics: sink);

        // Trigger fallback via non-monotonic input.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 50, chunkBlockSize: 50),  // out of order
        };
        var ctx = new FragmentainerContext(600, 800);

        var result = resolver.ResolveBreaks(ops, ctx);

        Assert.True(result.FellBackToGreedy);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(PaginateDiagnosticCodes.PaginationOptimizerFallback001,
            sink.Diagnostics[0].Code);
        Assert.Equal(PaginateDiagnosticSeverity.Info, sink.Diagnostics[0].Severity);
        // Message should include the FallbackReason for diagnostic clarity.
        Assert.Contains("monotonically", sink.Diagnostics[0].Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptimizingResolver_does_not_emit_when_no_fallback()
    {
        var sink = new RecordingSink();
        using var resolver = new OptimizingBreakResolver(
            orphansRequired: 2, widowsRequired: 2, diagnostics: sink);

        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 50),
        };
        var ctx = new FragmentainerContext(600, 800);

        var result = resolver.ResolveBreaks(ops, ctx);

        Assert.False(result.FellBackToGreedy);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void OptimizingResolver_null_sink_does_not_throw_on_fallback()
    {
        // Construction with null sink keeps the test-friendly behavior
        // (FallbackCount only). No NullReferenceException on emit path.
        using var resolver = new OptimizingBreakResolver(
            orphansRequired: 2, widowsRequired: 2, diagnostics: null);

        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 50, chunkBlockSize: 50),  // non-monotonic
        };
        var ctx = new FragmentainerContext(600, 800);

        // Should not throw.
        var result = resolver.ResolveBreaks(ops, ctx);
        Assert.True(result.FellBackToGreedy);
        Assert.Equal(1, resolver.FallbackCount);
    }

    // --- Rec #6: CancellationToken --------------------------------

    [Fact]
    public void Coordinator_throws_OperationCanceled_before_first_attempt()
    {
        var layouter = new MockLayouter();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var threw = false;
        try
        {
            coordinator.Run(layouter, ctx, ref layout, resolver, cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw);
        Assert.Empty(layouter.AttemptLog);  // never reached AttemptLayout
    }

    [Fact]
    public void Coordinator_throws_OperationCanceled_between_retries()
    {
        // Cancel after the first attempt's NeedsRewind — the second
        // iteration's pre-attempt check fires the exception.
        var layouter = new CancelAfterFirstAttemptLayouter();
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator();

        var threw = false;
        try
        {
            coordinator.Run(layouter, ctx, ref layout, resolver,
                layouter.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw);
        // Exactly one attempt ran before cancellation fired.
        Assert.Single(layouter.AttemptLog);

        layouter.Dispose();
    }

    // --- Copilot #2: PageComplete XML doc nit ---------------------

    [Fact]
    public void LayoutAttemptResult_PageComplete_helper_accepts_non_zero_cost()
    {
        // Per Copilot #2 — the docstring no longer implies zero cost.
        var continuation = new BlockContinuation(3, 100);
        var r = LayoutAttemptResult.PageComplete(continuation, cost: 250);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, r.Outcome);
        Assert.Same(continuation, r.Continuation);
        Assert.Null(r.RewindTo);
        Assert.Equal(250, r.Cost);
    }

    // ====================================================================
    //  PR #24 review pass — regression tests for tightened contracts
    // ====================================================================

    [Fact]
    public void PageComplete_factory_throws_on_null_continuation()
    {
        // Per PR #24 review pass — PageComplete requires a non-null
        // Continuation. The factory enforces at construction.
        Assert.Throws<System.ArgumentNullException>(() =>
            LayoutAttemptResult.PageComplete(null!, cost: 100));
    }

    [Fact]
    public void Validate_throws_on_PageComplete_with_null_continuation()
    {
        // Per PR #24 review pass — ValidateOrThrow catches direct-record
        // construction that bypasses the factory.
        var bad = new LayoutAttemptResult(
            LayoutAttemptOutcome.PageComplete,
            Continuation: null,
            RewindTo: null,
            Cost: 0);

        var threw = false;
        try { bad.ValidateOrThrow(); }
        catch (System.InvalidOperationException ex)
        {
            threw = true;
            Assert.Contains("PageComplete requires non-null Continuation", ex.Message);
        }
        Assert.True(threw);
    }

    [Fact]
    public void OptimizingResolver_diagnostic_sink_throw_is_swallowed()
    {
        // Per PR #24 review pass — IPaginateDiagnosticsSink contract
        // says Emit MUST NOT throw, but a misbehaving sink shouldn't
        // be able to take down the layout pipeline. The resolver wraps
        // Emit in try/catch + drops the exception. Tests with a
        // throwing sink verify the pipeline survives.
        var throwingSink = new ThrowingSink();
        using var resolver = new OptimizingBreakResolver(2, 2, throwingSink);

        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 50, chunkBlockSize: 50),  // non-monotonic
        };
        var ctx = new FragmentainerContext(600, 800);

        // Should NOT throw despite the sink throwing.
        var result = resolver.ResolveBreaks(ops, ctx);
        Assert.True(result.FellBackToGreedy);
        // FallbackCount still increments — observability preserved.
        Assert.Equal(1, resolver.FallbackCount);
    }

    [Fact]
    public void Coordinator_diagnostic_sink_throw_is_swallowed()
    {
        // Same guard at the coordinator level. A throwing sink during
        // the LastResort emission must not corrupt the retry state.
        var throwingSink = new ThrowingSink();
        var layouter = new MockLayouter();
        layouter.PlanRewind(cost: 1000);  // attempt 0
        layouter.PlanRewind(cost: 1000);  // attempt 1
        layouter.Plan(LayoutAttemptOutcome.PageComplete, cost: 100);  // attempt 2 — succeeds

        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var resolver = new BreakResolver();
        var coordinator = new LayoutRetryCoordinator(throwingSink);

        // Should NOT throw despite the sink throwing during LastResort
        // emission.
        var result = coordinator.Run(layouter, ctx, ref layout, resolver);
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.Equal(3, layouter.AttemptLog.Count);
    }

    [Fact]
    public void OptimizingResolver_resolve_breaks_honors_cancellation_token()
    {
        // Per PR #24 review pass — ResolveBreaks accepts a
        // CancellationToken so batched windows can be cancelled.
        using var resolver = new OptimizingBreakResolver();
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
        };
        var ctx = new FragmentainerContext(600, 800);

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        var threw = false;
        try
        {
            resolver.ResolveBreaks(ops, ctx, cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw);
    }

    [Fact]
    public void Optimizer_optimize_honors_cancellation_token()
    {
        // Direct test of Optimizer.Optimize CT.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
        };

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        var threw = false;
        try
        {
            Optimizer.Optimize(ops, contentBlockSize: 800,
                orphansRequired: 2, widowsRequired: 2,
                cancellationToken: cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw);
    }

    /// <summary>Sink that throws on every Emit call. Used to verify
    /// the layout pipeline's defensive try/catch guards.</summary>
    private sealed class ThrowingSink : IPaginateDiagnosticsSink
    {
        public void Emit(PaginateDiagnostic diagnostic)
            => throw new System.InvalidOperationException("Hostile sink");
    }

    // ====================================================================
    //  Test doubles
    // ====================================================================

    /// <summary>Mock layouter that emits a pre-planned sequence of
    /// <see cref="LayoutAttemptResult"/>s + records each invocation's
    /// strategy. Per Phase 3 Task 5 PR #21 review fix #1 — rewind
    /// results carry a real checkpoint (rented from the pool) so they
    /// satisfy the new <see cref="LayoutAttemptResult.ValidateOrThrow"/>
    /// invariant. The mock returns its rented leases on
    /// <see cref="IDisposable.Dispose"/>.</summary>
    private sealed class MockLayouter : ILayouter, IDisposable
    {
        private readonly Queue<LayoutAttemptResult> _planned = new();
        private readonly List<CheckpointLease> _rentedLeases = new();
        public List<LayoutAttemptStrategy> AttemptLog { get; } = new();

        public void Plan(LayoutAttemptOutcome outcome, double cost)
        {
            // Validate the planned outcome — the test contract is
            // that PlanRewind must be used for NeedsRewind so the
            // checkpoint is non-null.
            if (outcome == LayoutAttemptOutcome.NeedsRewind)
            {
                throw new InvalidOperationException(
                    "Use PlanRewind() for NeedsRewind to attach a real checkpoint.");
            }
            // Per PR #24 review pass — PageComplete requires non-null
            // Continuation. The mock supplies a placeholder
            // BlockContinuation; tests that need to inspect the
            // continuation specifically should use the lower-level
            // _planned.Enqueue + a custom result, or this is the
            // sentinel.
            LayoutContinuation? continuation = outcome == LayoutAttemptOutcome.PageComplete
                ? new BlockContinuation(ResumeAtChild: 0, ConsumedBlockSize: 0)
                : null;
            _planned.Enqueue(new LayoutAttemptResult(outcome, continuation, null, cost));
        }

        public void PlanRewind(double cost)
        {
            // Per PR #21 review fix #1 — NeedsRewind now requires a
            // non-null RewindTo. Capture a real checkpoint from the
            // pool (the coordinator's RestoreInto call is then a real
            // no-op-on-fresh-state, since we never call Capture on it).
            var lease = LayoutCheckpointPool.Rent();
            _rentedLeases.Add(lease);
            _planned.Enqueue(LayoutAttemptResult.NeedsRewind(lease.Checkpoint!, cost));
        }

        public void PlanRewindWithCursor(double cost, int fragmentOutputCursor)
        {
            // Variant for fragment-rollback testing. Sets the captured
            // checkpoint's FragmentOutputCursor so the coordinator's
            // sink.RollbackTo gets a verifiable value.
            var lease = LayoutCheckpointPool.Rent();
            _rentedLeases.Add(lease);
            lease.Checkpoint!.FragmentOutputCursor = fragmentOutputCursor;
            _planned.Enqueue(LayoutAttemptResult.NeedsRewind(lease.Checkpoint, cost));
        }

        public LayoutAttemptResult AttemptLayout(
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver,
            LayoutAttemptStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            // Per PR #21 review fix #6 — layouters should observe the
            // cancellation token. The mock checks once per attempt;
            // real layouters check inside their inner loops too.
            cancellationToken.ThrowIfCancellationRequested();
            AttemptLog.Add(strategy);
            return _planned.Count > 0
                ? _planned.Dequeue()
                : LayoutAttemptResult.AllDone(0);
        }

        public void Dispose()
        {
            foreach (var lease in _rentedLeases)
            {
                LayoutCheckpointPool.Return(lease);
            }
            _rentedLeases.Clear();
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
            LayoutAttemptStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return LayoutAttemptResult.AllDone(cost: 100);
        }
    }

    /// <summary>Recording sink that captures all emitted diagnostics
    /// for assertion.</summary>
    private sealed class RecordingSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    /// <summary>Recording fragment sink that records each
    /// <see cref="IFragmentSink.RollbackTo"/> call's cursor for
    /// assertion. Mock for the Phase 3 Task 5 PR #21 review fix #3
    /// fragment-rollback contract.</summary>
    private sealed class RecordingFragmentSink : IFragmentSink
    {
        public List<int> RollbackCallLog { get; } = new();
        public void RollbackTo(int cursor) => RollbackCallLog.Add(cursor);
    }

    /// <summary>Layouter that constructs <see cref="LayoutAttemptResult"/>
    /// with NeedsRewind + null RewindTo (bypassing the factory's
    /// non-null guard) to verify the coordinator's
    /// <see cref="LayoutAttemptResult.ValidateOrThrow"/> catches the
    /// invariant violation.</summary>
    private sealed class InvariantViolatingLayouter : ILayouter
    {
        public LayoutAttemptResult AttemptLayout(
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver,
            LayoutAttemptStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            // Direct construction violates the invariant — null RewindTo
            // with NeedsRewind outcome.
            return new LayoutAttemptResult(
                LayoutAttemptOutcome.NeedsRewind,
                Continuation: null,
                RewindTo: null,
                Cost: 1000);
        }
    }

    /// <summary>Layouter that swaps <c>layout.Fragmentainer</c> to a
    /// clone on attempt 0, asks for rewind to a checkpoint that
    /// captured the original ref, then on attempt 1 records which
    /// fragmentainer instance the coordinator passed in. Verifies
    /// PR #21 review fix #2 (fragmentainer sync after restore).</summary>
    private sealed class SwapAndRewindLayouter : ILayouter
    {
        private readonly LayoutCheckpoint _checkpoint;
        private readonly FragmentainerContext _original;
        public List<FragmentainerContext> ReceivedFragmentainers { get; } = new();
        private bool _rewindAlreadyAsked;

        public SwapAndRewindLayouter(LayoutCheckpoint checkpoint, FragmentainerContext original)
        {
            _checkpoint = checkpoint;
            _original = original;
        }

        public LayoutAttemptResult AttemptLayout(
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver,
            LayoutAttemptStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            ReceivedFragmentainers.Add(fragmentainer);

            if (!_rewindAlreadyAsked)
            {
                _rewindAlreadyAsked = true;
                // Speculative swap — clone the fragmentainer + repoint
                // layout. After rewind, layout.Fragmentainer should be
                // back to _original (per Task 4 review fix #6).
                var speculative = _original.Clone();
                layout.Fragmentainer = speculative;
                return LayoutAttemptResult.NeedsRewind(_checkpoint, cost: 1000);
            }

            return LayoutAttemptResult.AllDone(cost: 100);
        }
    }

    /// <summary>Layouter that triggers cancellation between attempts
    /// 0 and 1 by canceling its own token after attempt 0.</summary>
    private sealed class CancelAfterFirstAttemptLayouter : ILayouter, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<CheckpointLease> _rentedLeases = new();
        public List<LayoutAttemptStrategy> AttemptLog { get; } = new();
        public CancellationToken CancellationToken => _cts.Token;

        public LayoutAttemptResult AttemptLayout(
            FragmentainerContext fragmentainer,
            ref LayoutContext layout,
            IBreakResolver resolver,
            LayoutAttemptStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptLog.Add(strategy);

            // After attempt 0, cancel our own CTS so the coordinator's
            // pre-attempt-1 check fires.
            _cts.Cancel();

            var lease = LayoutCheckpointPool.Rent();
            _rentedLeases.Add(lease);
            return LayoutAttemptResult.NeedsRewind(lease.Checkpoint!, cost: 1000);
        }

        public void Dispose()
        {
            _cts.Dispose();
            foreach (var lease in _rentedLeases)
            {
                LayoutCheckpointPool.Return(lease);
            }
            _rentedLeases.Clear();
        }
    }
}
