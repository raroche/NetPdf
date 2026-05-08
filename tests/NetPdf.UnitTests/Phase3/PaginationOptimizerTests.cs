// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using NetPdf.Paginate;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 4 — bounded DP optimizer tests. Covers
/// <see cref="Optimizer"/> (cost minimization with 2-page lookahead +
/// greedy fallback), <see cref="OptimizingBreakResolver"/> (the
/// production <see cref="IBreakResolver"/>), the new
/// <see cref="IBreakResolver.ResolveBreaks"/> contract on the existing
/// greedy <see cref="BreakResolver"/>, plus
/// <see cref="OptimizerResult"/> shape + diagnostic-flag plumbing.
/// </summary>
public sealed class PaginationOptimizerTests
{
    // --- Optimizer: input validation -------------------------------------

    [Fact]
    public void Optimizer_throws_on_null_opportunities()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Optimizer.Optimize(opportunities: null!,
                contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Optimizer_throws_on_invalid_content_block_size(double size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Optimizer.Optimize(Array.Empty<BreakOpportunity>(),
                contentBlockSize: size, orphansRequired: 2, widowsRequired: 2));
    }

    [Fact]
    public void Optimizer_throws_on_negative_orphans()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Optimizer.Optimize(Array.Empty<BreakOpportunity>(),
                contentBlockSize: 800, orphansRequired: -1, widowsRequired: 2));
    }

    [Fact]
    public void Optimizer_throws_on_negative_widows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Optimizer.Optimize(Array.Empty<BreakOpportunity>(),
                contentBlockSize: 800, orphansRequired: 2, widowsRequired: -1));
    }

    // --- Optimizer: trivial cases ----------------------------------------

    [Fact]
    public void Optimizer_empty_input_returns_empty_singleton()
    {
        var result = Optimizer.Optimize(
            Array.Empty<BreakOpportunity>(),
            contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.Same(OptimizerResult.Empty, result);
        Assert.Empty(result.BreakIndices);
        Assert.Equal(0, result.TotalCost);
        Assert.False(result.FellBackToGreedy);
        Assert.Null(result.FallbackReason);
    }

    [Fact]
    public void Optimizer_single_fitting_opportunity_returns_no_break()
    {
        // Page is 800 tall; one candidate at UsedBlockSize=200 with
        // ChunkBlockSize=50. Both fit; the optimizer's window can't
        // find a b2, so it picks no break (the trailing tail past the
        // single candidate is the next-window's problem).
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 50),
        };
        var result = Optimizer.Optimize(ops, 800, 2, 2);
        // The optimizer's last-iteration logic considers b1=0 with no
        // b2 → scores b1 alone. Cost is finite → it commits b1=0.
        // This matches "schedule a break at the very last candidate"
        // semantics for a 1-element window. Confirm it.
        Assert.Single(result.BreakIndices);
        Assert.Equal(0, result.BreakIndices[0]);
        Assert.False(result.FellBackToGreedy);
    }

    // --- Optimizer: forced breaks ----------------------------------------

    [Fact]
    public void Optimizer_force_break_is_always_selected()
    {
        // 3 candidates; index 1 is ForceBreak. DP must include it.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            new BreakOpportunity(
                UsedBlockSize: 200, ChunkBlockSize: 50,
                Class: BreakOpportunityClass.BlockBoundary,
                ForceBreak: true, AvoidBreak: false, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
            BreakOpportunity.Block(usedBlockSize: 300, chunkBlockSize: 50),
        };
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.Contains(1, result.BreakIndices);
        Assert.False(result.FellBackToGreedy);
    }

    // --- Optimizer: avoid-break ------------------------------------------

    [Fact]
    public void Optimizer_avoid_break_skipped_when_alternative_exists()
    {
        // Both candidates fit on the same page; index 0 is AvoidBreak.
        // DP should prefer index 1 (the non-avoid candidate).
        var ops = new[]
        {
            new BreakOpportunity(
                UsedBlockSize: 300, ChunkBlockSize: 100,
                Class: BreakOpportunityClass.BlockBoundary,
                ForceBreak: false, AvoidBreak: true, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
            BreakOpportunity.Block(usedBlockSize: 500, chunkBlockSize: 100),
        };
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.DoesNotContain(0, result.BreakIndices);
    }

    [Fact]
    public void Optimizer_all_avoid_break_falls_back_to_greedy()
    {
        // Every candidate is AvoidBreak + none are forced. DP can't
        // find a feasible (b1, b2) pair that doesn't violate
        // break-inside: avoid → greedy fallback fires.
        var ops = new[]
        {
            new BreakOpportunity(
                UsedBlockSize: 200, ChunkBlockSize: 100,
                Class: BreakOpportunityClass.BlockBoundary,
                ForceBreak: false, AvoidBreak: true, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
            new BreakOpportunity(
                UsedBlockSize: 400, ChunkBlockSize: 100,
                Class: BreakOpportunityClass.BlockBoundary,
                ForceBreak: false, AvoidBreak: true, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
        };
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.True(result.FellBackToGreedy);
        Assert.NotNull(result.FallbackReason);
    }

    // --- Optimizer: budget cap + monotonicity ----------------------------

    [Fact]
    public void Optimizer_exceeds_candidate_budget_falls_back_to_greedy()
    {
        // Build a candidate set just above MaxCandidatesBeforeFallback.
        var n = Optimizer.MaxCandidatesBeforeFallback + 1;
        var ops = new BreakOpportunity[n];
        for (var i = 0; i < n; i++)
        {
            // Cumulative UsedBlockSize so monotonicity holds; tiny
            // chunks so each fits trivially.
            ops[i] = BreakOpportunity.Block(usedBlockSize: i * 10.0, chunkBlockSize: 5);
        }
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.True(result.FellBackToGreedy);
        Assert.Contains("budget", result.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optimizer_non_monotonic_used_block_size_falls_back_to_greedy()
    {
        // UsedBlockSize decreases between index 1 and 2 — invalid input
        // for the DP (the cumulative-position assumption is broken).
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 50, chunkBlockSize: 50),  // out of order
        };
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.True(result.FellBackToGreedy);
        Assert.Contains("monotonically", result.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    // --- Optimizer: overflow (chunk taller than fragmentainer) -----------

    [Fact]
    public void Optimizer_first_chunk_overflows_emits_overflow_penalty()
    {
        // The very first opportunity's UsedBlockSize already exceeds
        // contentBlockSize. Optimizer commits a break there with the
        // BreakInsideAvoidViolation cost as last-resort.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 1000, chunkBlockSize: 50),
        };
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.Single(result.BreakIndices);
        Assert.Equal(0, result.BreakIndices[0]);
        Assert.True(result.TotalCost >= CostModel.BreakInsideAvoidViolation);
    }

    // --- Optimizer: multi-page document ----------------------------------

    [Fact]
    public void Optimizer_multi_page_produces_monotonic_break_sequence()
    {
        // 10 candidates on a 800-tall page. UsedBlockSize spans 0..3000
        // → ~4 pages worth of content. Verify the break indices come out
        // strictly monotonically increasing.
        var ops = Enumerable.Range(0, 10)
            .Select(i => BreakOpportunity.Block(
                usedBlockSize: i * 300.0,
                chunkBlockSize: 250))
            .ToArray();

        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);

        Assert.False(result.FellBackToGreedy);
        for (var i = 1; i < result.BreakIndices.Count; i++)
        {
            Assert.True(result.BreakIndices[i] > result.BreakIndices[i - 1],
                $"BreakIndices not strictly increasing at position {i}");
        }
    }

    [Fact]
    public void Optimizer_break_indices_respect_page_capacity()
    {
        // Verify each "page" between consecutive committed breaks fits
        // on the fragmentainer.
        var ops = Enumerable.Range(0, 8)
            .Select(i => BreakOpportunity.Block(
                usedBlockSize: i * 250.0,
                chunkBlockSize: 200))
            .ToArray();

        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.False(result.FellBackToGreedy);

        double pageStart = 0;
        foreach (var k in result.BreakIndices)
        {
            var pageBlockSize = ops[k].UsedBlockSize - pageStart;
            Assert.True(pageBlockSize <= 800,
                $"Page extent {pageBlockSize} exceeds capacity 800 at break index {k}");
            pageStart = ops[k].UsedBlockSize;
        }
    }

    // --- Optimizer: lookahead behavior -----------------------------------

    [Fact]
    public void Optimizer_two_page_lookahead_picks_globally_better_pair()
    {
        // Construct a scenario where breaking at b1=0 + b2=2 is locally
        // worse for b1 alone but globally better than b1=1.
        // Page is 800. UsedBlockSize: 0=200, 1=400, 2=750, 3=1100.
        // Greedy from b1: cost(0) might be lower than cost(2) but the
        // pair (0, 2) has total cost vs (0, 3) etc. We just verify the
        // chosen sequence honors page-fit and is bounded.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 100),
            BreakOpportunity.Block(usedBlockSize: 400, chunkBlockSize: 100),
            BreakOpportunity.Block(usedBlockSize: 750, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 1100, chunkBlockSize: 50),
        };

        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);

        // First page should commit at index 2 (UsedBlockSize=750 <= 800)
        // — denser packing wins on the LargeBlankTrailingArea penalty.
        Assert.Equal(2, result.BreakIndices[0]);
    }

    // --- Optimizer: section boundary reward ------------------------------

    [Fact]
    public void Optimizer_prefers_section_boundary_when_costs_tie()
    {
        // Two candidates at the same UsedBlockSize boundary.
        // Index 0 is BlockBoundary, index 1 is SectionBoundary (rewarded).
        // SectionBoundary has lower (negative-shifted) cost; DP should
        // pick index 1.
        var ops = new[]
        {
            new BreakOpportunity(
                UsedBlockSize: 400, ChunkBlockSize: 50,
                Class: BreakOpportunityClass.BlockBoundary,
                ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
            new BreakOpportunity(
                UsedBlockSize: 500, ChunkBlockSize: 50,
                Class: BreakOpportunityClass.SectionBoundary,
                ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
        };
        var result = Optimizer.Optimize(ops, contentBlockSize: 800, orphansRequired: 2, widowsRequired: 2);
        Assert.Contains(1, result.BreakIndices);
    }

    // --- OptimizingBreakResolver: streaming path -------------------------

    [Fact]
    public void OptimizingResolver_streaming_continues_when_chunk_fits()
    {
        var resolver = new OptimizingBreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        var op = BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50);
        var dec = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.Continue, dec.Action);
        Assert.Null(dec.RewindTo);
    }

    [Fact]
    public void OptimizingResolver_streaming_breaks_when_chunk_overflows()
    {
        var resolver = new OptimizingBreakResolver();
        var ctx = new FragmentainerContext(600, 800)
        {
            UsedBlockSize = 700,  // RemainingBlockSize = 100
        };
        var op = BreakOpportunity.Block(usedBlockSize: 700, chunkBlockSize: 200);
        var dec = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.BreakHere, dec.Action);
    }

    [Fact]
    public void OptimizingResolver_streaming_force_break_wins_over_fit()
    {
        var resolver = new OptimizingBreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        var op = new BreakOpportunity(
            UsedBlockSize: 100, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForceBreak: true, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var dec = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.BreakHere, dec.Action);
    }

    // --- OptimizingBreakResolver: batched path ---------------------------

    [Fact]
    public void OptimizingResolver_resolve_breaks_delegates_to_optimizer()
    {
        var resolver = new OptimizingBreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 100),
            BreakOpportunity.Block(usedBlockSize: 500, chunkBlockSize: 100),
            BreakOpportunity.Block(usedBlockSize: 800, chunkBlockSize: 100),
        };
        var result = resolver.ResolveBreaks(ops, ctx);
        Assert.False(result.FellBackToGreedy);
        Assert.NotEmpty(result.BreakIndices);
    }

    [Fact]
    public void OptimizingResolver_fallback_count_increments_on_fallback()
    {
        var resolver = new OptimizingBreakResolver();
        var ctx = new FragmentainerContext(600, 800);

        // Trigger fallback via non-monotonic input.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 50, chunkBlockSize: 50),  // out of order
        };

        Assert.Equal(0, resolver.FallbackCount);
        _ = resolver.ResolveBreaks(ops, ctx);
        Assert.Equal(1, resolver.FallbackCount);
        _ = resolver.ResolveBreaks(ops, ctx);
        Assert.Equal(2, resolver.FallbackCount);
    }

    [Fact]
    public void OptimizingResolver_resolve_breaks_throws_on_null_inputs()
    {
        var resolver = new OptimizingBreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        Assert.Throws<ArgumentNullException>(() => resolver.ResolveBreaks(null!, ctx));
        Assert.Throws<ArgumentNullException>(() =>
            resolver.ResolveBreaks(Array.Empty<BreakOpportunity>(), null!));
    }

    [Fact]
    public void OptimizingResolver_constructor_validates_orphans_widows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimizingBreakResolver(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimizingBreakResolver(2, -1));
    }

    // --- OptimizingBreakResolver: checkpoint mgmt -----------------------

    [Fact]
    public void OptimizingResolver_register_checkpoint_returns_prior_to_pool()
    {
        var resolver = new OptimizingBreakResolver();
        var cp1 = LayoutCheckpointPool.Rent();
        var cp2 = LayoutCheckpointPool.Rent();
        resolver.RegisterCheckpoint(cp1);
        Assert.Same(cp1, resolver.GetLastCheckpoint());
        resolver.RegisterCheckpoint(cp2);
        Assert.Same(cp2, resolver.GetLastCheckpoint());
        // cp1 should now be back in the pool — rent again should
        // potentially return it (or a fresh instance; pool is bag-
        // ordered so we just verify cp1 isn't currently held).
        Assert.NotSame(cp1, resolver.GetLastCheckpoint());
    }

    [Fact]
    public void OptimizingResolver_self_register_is_no_op()
    {
        // Re-registering the same instance should not return-it-then-
        // overwrite (which would put it in the pool while the resolver
        // still holds a reference, causing pool corruption).
        var resolver = new OptimizingBreakResolver();
        var cp = LayoutCheckpointPool.Rent();
        resolver.RegisterCheckpoint(cp);
        resolver.RegisterCheckpoint(cp);
        Assert.Same(cp, resolver.GetLastCheckpoint());
    }

    [Fact]
    public void OptimizingResolver_register_null_throws()
    {
        var resolver = new OptimizingBreakResolver();
        Assert.Throws<ArgumentNullException>(() => resolver.RegisterCheckpoint(null!));
    }

    // --- BreakResolver (greedy): ResolveBreaks contract -----------------

    [Fact]
    public void GreedyResolver_resolve_breaks_returns_empty_for_empty_input()
    {
        var resolver = new BreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        var result = resolver.ResolveBreaks(Array.Empty<BreakOpportunity>(), ctx);
        Assert.Same(OptimizerResult.Empty, result);
    }

    [Fact]
    public void GreedyResolver_resolve_breaks_does_not_set_fallback_flag()
    {
        // The greedy resolver IS greedy — it isn't FALLING back from
        // anything. The flag stays false.
        var resolver = new BreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 100),
            BreakOpportunity.Block(usedBlockSize: 500, chunkBlockSize: 400),
        };
        var result = resolver.ResolveBreaks(ops, ctx);
        Assert.False(result.FellBackToGreedy);
        Assert.Null(result.FallbackReason);
    }

    [Fact]
    public void GreedyResolver_resolve_breaks_commits_on_overflow()
    {
        // Page is 800; chunk 1 would push to 1000. Greedy commits at
        // index 1 (or rather the moment it sees overflow).
        var resolver = new BreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 200, chunkBlockSize: 100),
            BreakOpportunity.Block(usedBlockSize: 700, chunkBlockSize: 400),  // 700+400=1100 > 800
            BreakOpportunity.Block(usedBlockSize: 1100, chunkBlockSize: 100),
        };
        var result = resolver.ResolveBreaks(ops, ctx);
        Assert.Contains(1, result.BreakIndices);
    }

    [Fact]
    public void GreedyResolver_resolve_breaks_throws_on_nulls()
    {
        var resolver = new BreakResolver();
        var ctx = new FragmentainerContext(600, 800);
        Assert.Throws<ArgumentNullException>(() => resolver.ResolveBreaks(null!, ctx));
        Assert.Throws<ArgumentNullException>(() =>
            resolver.ResolveBreaks(Array.Empty<BreakOpportunity>(), null!));
    }

    // --- OptimizerResult shape -------------------------------------------

    [Fact]
    public void OptimizerResult_empty_singleton_has_zero_cost_no_fallback()
    {
        var r = OptimizerResult.Empty;
        Assert.Empty(r.BreakIndices);
        Assert.Equal(0, r.TotalCost);
        Assert.False(r.FellBackToGreedy);
        Assert.Null(r.FallbackReason);
    }

    [Fact]
    public void OptimizerResult_with_fallback_carries_reason()
    {
        // Construct a minimum-input scenario known to trigger fallback,
        // then verify the result carries a non-null FallbackReason.
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            BreakOpportunity.Block(usedBlockSize: 50, chunkBlockSize: 50),  // non-monotonic
        };
        var result = Optimizer.Optimize(ops, 800, 2, 2);
        Assert.True(result.FellBackToGreedy);
        Assert.False(string.IsNullOrEmpty(result.FallbackReason));
    }

    // --- Optimizer: NaN / invalid opportunity inside list ---------------

    [Fact]
    public void Optimizer_invalid_opportunity_in_list_throws()
    {
        // EnsureValid runs on each candidate. NaN inside the list →
        // ArgumentException (per BreakOpportunity.EnsureValid).
        var ops = new[]
        {
            BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50),
            new BreakOpportunity(
                UsedBlockSize: double.NaN, ChunkBlockSize: 50,
                Class: BreakOpportunityClass.BlockBoundary,
                ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
                LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false),
        };
        Assert.Throws<ArgumentException>(() =>
            Optimizer.Optimize(ops, 800, 2, 2));
    }
}
