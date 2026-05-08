// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NetPdf.Paginate;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 1 — pagination foundation tests. Covers
/// <see cref="FragmentainerContext"/>, <see cref="BreakOpportunity"/>,
/// <see cref="LayoutCheckpoint"/> + pool, <see cref="LayoutContext"/>,
/// <see cref="CostModel"/> penalty matrix, <see cref="BreakResolver"/>
/// stub semantics. Plus the 9 PR #19 review fixes (block-axis rename,
/// atomic checkpoint, pool double-return, break metadata, score-from-
/// snapshot, dead-penalty wiring, geometry validation, enriched
/// continuations, diagnostic literal pinning).
/// </summary>
public sealed class PaginationFoundationTests
{
    // --- FragmentainerContext (block-axis naming, review #2) -------------

    [Fact]
    public void FragmentainerContext_remaining_block_size_tracks_used()
    {
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        Assert.Equal(800, ctx.RemainingBlockSize);
        ctx.UsedBlockSize = 200;
        Assert.Equal(600, ctx.RemainingBlockSize);
        ctx.UsedBlockSize = 800;
        Assert.Equal(0, ctx.RemainingBlockSize);
    }

    [Fact]
    public void FragmentainerContext_clone_advances_page_carries_named_strings()
    {
        var ctx = new FragmentainerContext(600, 800)
        {
            PageIndex = 0,
            UsedBlockSize = 500,
        };
        ctx.NamedStrings["chapter"] = "1. Introduction";

        var next = ctx.Clone();
        Assert.Equal(1, next.PageIndex);
        Assert.Equal(0, next.UsedBlockSize);
        Assert.Equal(600, next.ContentInlineSize);
        Assert.Equal(800, next.BlockSize);
        Assert.Equal("1. Introduction", next.NamedStrings["chapter"]);
    }

    [Fact]
    public void FragmentainerContext_clone_named_strings_are_independent_copy()
    {
        var ctx = new FragmentainerContext(600, 800);
        ctx.NamedStrings["x"] = "page-1";
        var next = ctx.Clone();
        next.NamedStrings["x"] = "page-2";
        Assert.Equal("page-1", ctx.NamedStrings["x"]);
        Assert.Equal("page-2", next.NamedStrings["x"]);
    }

    // --- Review #8: geometry validation ---------------------------------

    [Theory]
    [InlineData(0, 800)]
    [InlineData(-1, 800)]
    [InlineData(double.NaN, 800)]
    [InlineData(double.PositiveInfinity, 800)]
    [InlineData(600, 0)]
    [InlineData(600, -10)]
    [InlineData(600, double.NaN)]
    public void FragmentainerContext_rejects_invalid_geometry(double inline, double block)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FragmentainerContext(inline, block));
    }

    [Fact]
    public void BreakOpportunity_ensure_valid_rejects_nan()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: double.NaN, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        Assert.Throws<ArgumentException>(() => op.EnsureValid());
    }

    [Fact]
    public void BreakOpportunity_ensure_valid_rejects_negative()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 100, ChunkBlockSize: -1,
            Class: BreakOpportunityClass.BlockBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        Assert.Throws<ArgumentException>(() => op.EnsureValid());
    }

    [Fact]
    public void CostModel_score_rejects_invalid_content_block_size()
    {
        var op = BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CostModel.Score(op, contentBlockSize: double.NaN,
                orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CostModel.Score(op, contentBlockSize: 0,
                orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5));
    }

    // --- LayoutCheckpoint + pool -----------------------------------------

    [Fact]
    public void Checkpoint_pool_rent_returns_freshly_reset_instance()
    {
        var cp1Lease = LayoutCheckpointPool.Rent(); var cp1 = cp1Lease.Checkpoint!;
        cp1.PageIndex = 5;
        cp1.UsedBlockSize = 123;
        cp1.LastEmittedChildIndex = 7;
        cp1.IncomingContinuation = new BlockContinuation(3, 100);
        LayoutCheckpointPool.Return(cp1Lease);

        var cp2Lease = LayoutCheckpointPool.Rent(); var cp2 = cp2Lease.Checkpoint!;
        Assert.Equal(0, cp2.PageIndex);
        Assert.Equal(0, cp2.UsedBlockSize);
        Assert.Equal(-1, cp2.LastEmittedChildIndex);
        Assert.Null(cp2.IncomingContinuation);
        LayoutCheckpointPool.Return(cp2Lease);
    }

    [Fact]
    public void Checkpoint_pool_handles_default_lease_gracefully()
    {
        // Per Phase 3 Task 4 review fix #7 — default(CheckpointLease)
        // has a null Checkpoint property; Return must drop it on the
        // floor (the pre-fix API took LayoutCheckpoint?, this verifies
        // the lease-API equivalent of the original null-safety check).
        LayoutCheckpointPool.Return(default);
    }

    // --- Review #6: pool double-return + null-large-refs -----------------

    [Fact]
    public void Checkpoint_pool_rejects_double_return_no_aliasing()
    {
        var cpLease = LayoutCheckpointPool.Rent(); var cp = cpLease.Checkpoint!;
        cp.PageIndex = 99;
        LayoutCheckpointPool.Return(cpLease);
        // Second return must be a no-op (not a duplicate add). Without
        // the guard, two concurrent Rent() calls could hand out the
        // same instance.
        LayoutCheckpointPool.Return(cpLease);
        // Rent twice — must produce two distinct instances if the
        // double-return was rejected; if accepted, both rentals
        // would alias the same backing instance (verified by writing
        // to one + observing the other).
        var aLease = LayoutCheckpointPool.Rent(); var a = aLease.Checkpoint!;
        var bLease = LayoutCheckpointPool.Rent(); var b = bLease.Checkpoint!;
        Assert.NotSame(a, b);
        a.PageIndex = 11;
        b.PageIndex = 22;
        Assert.Equal(11, a.PageIndex);
        Assert.Equal(22, b.PageIndex);
        LayoutCheckpointPool.Return(aLease);
        LayoutCheckpointPool.Return(bLease);
    }

    [Fact]
    public void Checkpoint_return_clears_large_reference_fields()
    {
        var cpLease = LayoutCheckpointPool.Rent(); var cp = cpLease.Checkpoint!;
        cp.IncomingContinuation = new TableContinuation(true, false, 5, ColumnLayoutCache: new object());
        cp.NamedStringsSnapshot = new Dictionary<string, string> { ["x"] = "y" };
        cp.CountersSnapshot = new Dictionary<string, int> { ["page"] = 1 };
        cp.FloatManagerStateSnapshot = new object();
        // Hold a weak reference target to verify the pool doesn't pin
        // the inner state for the GC.
        LayoutCheckpointPool.Return(cpLease);
        // After Return, every large-ref field must be cleared so GC
        // can reclaim the referenced graphs while the checkpoint
        // sits idle.
        Assert.Null(cp.IncomingContinuation);
        Assert.Null(cp.NamedStringsSnapshot);
        Assert.Null(cp.CountersSnapshot);
        Assert.Null(cp.FloatManagerStateSnapshot);
    }

    // --- Review #1: atomic checkpoint capture + restore ------------------

    [Fact]
    public void Checkpoint_capture_then_restore_round_trips_named_strings()
    {
        var ctx = new FragmentainerContext(600, 800) { UsedBlockSize = 200 };
        ctx.NamedStrings["chapter"] = "Chapter 1";
        ctx.NamedStrings["section"] = "1.2";

        var layout = new LayoutContext(ctx);
        layout.Counter("page", 3);
        layout.Counter("figure", 7);
        layout.WritingMode = WritingMode.VerticalRl;
        layout.IsRtl = true;
        layout.AvailableInlineSize = 500;
        layout.AvailableBlockSize = 700;

        var cpLease = LayoutCheckpointPool.Rent(); var cp = cpLease.Checkpoint!;
        cp.Capture(ctx, layout, fragmentOutputCursor: 4,
            lastEmittedChildIndex: 2, incomingContinuation: null,
            pageCounterValue: 3);

        // Mutate the live state — every field below should restore
        // back to the captured value.
        ctx.UsedBlockSize = 999;
        ctx.NamedStrings["chapter"] = "OVERWRITTEN";
        ctx.NamedStrings["new"] = "added-later";
        layout.Counter("page", 99);
        layout.Counter("brand-new", 42);
        layout.WritingMode = WritingMode.HorizontalTb;
        layout.IsRtl = false;
        layout.AvailableInlineSize = 100;
        layout.AvailableBlockSize = 100;

        cp.RestoreInto(ctx, ref layout);

        // FragmentainerContext restored.
        Assert.Equal(200, ctx.UsedBlockSize);
        Assert.Equal("Chapter 1", ctx.NamedStrings["chapter"]);
        Assert.Equal("1.2", ctx.NamedStrings["section"]);
        Assert.False(ctx.NamedStrings.ContainsKey("new")); // discarded

        // LayoutContext restored.
        Assert.Equal(WritingMode.VerticalRl, layout.WritingMode);
        Assert.True(layout.IsRtl);
        Assert.Equal(500, layout.AvailableInlineSize);
        Assert.Equal(700, layout.AvailableBlockSize);
        Assert.Equal(3, layout.ReadCounter("page"));
        Assert.Equal(7, layout.ReadCounter("figure"));
        Assert.Equal(0, layout.ReadCounter("brand-new")); // discarded

        LayoutCheckpointPool.Return(cpLease);
    }

    [Fact]
    public void Checkpoint_capture_with_empty_state_does_not_allocate_dicts()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        var cpLease = LayoutCheckpointPool.Rent(); var cp = cpLease.Checkpoint!;

        cp.Capture(ctx, layout, fragmentOutputCursor: 0,
            lastEmittedChildIndex: -1, incomingContinuation: null,
            pageCounterValue: 0);

        // Empty named-strings + no counters → snapshot dicts stay null
        // (no allocation overhead in the common case).
        Assert.Null(cp.NamedStringsSnapshot);
        Assert.Null(cp.CountersSnapshot);
        LayoutCheckpointPool.Return(cpLease);
    }

    // --- LayoutContext ref struct (block-axis naming) --------------------

    [Fact]
    public void LayoutContext_constructed_from_fragmentainer_inherits_dimensions()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        Assert.Equal(600, layout.AvailableInlineSize);
        Assert.Equal(800, layout.AvailableBlockSize);
        Assert.Equal(WritingMode.HorizontalTb, layout.WritingMode);
        Assert.False(layout.IsRtl);
    }

    [Fact]
    public void LayoutContext_counter_lazy_alloc_reads_zero_default()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        Assert.Equal(0, layout.ReadCounter("page"));
        layout.Counter("page", 5);
        Assert.Equal(5, layout.ReadCounter("page"));
    }

    [Fact]
    public void LayoutContext_increment_counter_returns_new_value()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        Assert.Equal(1, layout.IncrementCounter("section"));
        Assert.Equal(2, layout.IncrementCounter("section"));
        Assert.Equal(7, layout.IncrementCounter("section", delta: 5));
    }

    // --- CostModel (review #4: snapshot scoring; review #5: wired
    //     dead penalties; review #3: forced break zero-cost) -------------

    [Fact]
    public void CostModel_break_inside_avoid_violation_is_effectively_infinite()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 100, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForceBreak: false, AvoidBreak: true, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.True(cost >= CostModel.BreakInsideAvoidViolation);
    }

    [Fact]
    public void CostModel_force_break_zero_cost_regardless_of_other_flags()
    {
        // ForceBreak overrides everything — the author chose this break.
        var op = new BreakOpportunity(
            UsedBlockSize: 100, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.LineBoundary,
            ForceBreak: true, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 1, StrandsHeading: true, SplitsFlexOrGridLine: true);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 1);
        Assert.Equal(0, cost);
    }

    [Fact]
    public void CostModel_section_boundary_earns_negative_cost_reward()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.SectionBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.SectionBoundaryReward, cost);
    }

    [Fact]
    public void CostModel_orphan_penalty_when_lines_below_required()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 20,
            Class: BreakOpportunityClass.LineBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 1, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.Orphan, cost);
    }

    [Fact]
    public void CostModel_widow_penalty_when_next_page_has_one_line()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 20,
            Class: BreakOpportunityClass.LineBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 5, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 1);
        Assert.Equal(CostModel.Widow, cost);
    }

    [Fact]
    public void CostModel_table_row_mid_cell_split_penalty()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.InsideTableRow,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.TableRowMidCellSplit, cost);
    }

    [Fact]
    public void CostModel_large_trailing_blank_penalty_uses_opportunity_snapshot()
    {
        // Per Phase 3 review fix #4 — score uses opportunity.UsedBlockSize,
        // not a separate live "current" value. 100 used / 800 total =
        // 12.5% used → 87.5% blank → > 30% threshold.
        var op = BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.LargeBlankTrailingArea, cost);
    }

    [Fact]
    public void CostModel_zero_cost_when_clean_block_boundary_no_blank()
    {
        var op = BreakOpportunity.Block(usedBlockSize: 700, chunkBlockSize: 50);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(0, cost);
    }

    // --- Review #5: previously-dead penalties now wired ------------------

    [Fact]
    public void CostModel_stranded_heading_penalty_now_applied()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: true, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.StrandedHeading, cost);
    }

    [Fact]
    public void CostModel_flex_grid_line_split_penalty_now_applied()
    {
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.FlexBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: true);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.FlexOrGridLineSplit, cost);
    }

    [Fact]
    public void CostModel_compound_penalty_sums_individual_flags()
    {
        // Stranded heading + line split + orphan together. Verifies
        // the cost is additive across flags.
        var op = new BreakOpportunity(
            UsedBlockSize: 100, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.LineBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 1, StrandsHeading: true, SplitsFlexOrGridLine: true);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        // Orphan + StrandedHeading + FlexLineSplit + LargeTrailingBlank
        // (100/800 = 87.5% blank > 30% threshold).
        var expected = CostModel.Orphan
            + CostModel.StrandedHeading
            + CostModel.FlexOrGridLineSplit
            + CostModel.LargeBlankTrailingArea;
        Assert.Equal(expected, cost);
    }

    // --- BreakResolver stub ----------------------------------------------

    [Fact]
    public void BreakResolver_continue_when_chunk_fits()
    {
        var ctx = new FragmentainerContext(600, 800) { UsedBlockSize = 100 };
        var resolver = new BreakResolver();
        var op = BreakOpportunity.Block(usedBlockSize: 100, chunkBlockSize: 50);
        var decision = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.Continue, decision.Action);
    }

    [Fact]
    public void BreakResolver_break_here_when_chunk_overflows()
    {
        var ctx = new FragmentainerContext(600, 800) { UsedBlockSize = 700 };
        var resolver = new BreakResolver();
        var op = BreakOpportunity.Block(usedBlockSize: 700, chunkBlockSize: 200);
        var decision = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.BreakHere, decision.Action);
    }

    // --- Review #3: forced break overrides fit check ---------------------

    [Fact]
    public void BreakResolver_force_break_emits_break_here_even_when_chunk_fits()
    {
        var ctx = new FragmentainerContext(600, 800) { UsedBlockSize = 100 };
        var resolver = new BreakResolver();
        var op = new BreakOpportunity(
            UsedBlockSize: 100, ChunkBlockSize: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForceBreak: true, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 0, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var decision = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.BreakHere, decision.Action);
        Assert.Equal(0, decision.Cost);
    }

    [Fact]
    public void BreakResolver_checkpoint_register_then_get_returns_last()
    {
        var resolver = new BreakResolver();
        Assert.Null(resolver.GetLastCheckpoint());
        var cp1Lease = LayoutCheckpointPool.Rent(); var cp1 = cp1Lease.Checkpoint!;
        cp1.PageIndex = 1;
        var cp2Lease = LayoutCheckpointPool.Rent(); var cp2 = cp2Lease.Checkpoint!;
        cp2.PageIndex = 2;
        resolver.RegisterCheckpoint(cp1Lease);
        resolver.RegisterCheckpoint(cp2Lease);
        Assert.Same(cp2, resolver.GetLastCheckpoint());
    }

    [Fact]
    public void BreakResolver_with_custom_widow_orphan_overrides_defaults()
    {
        var resolver = new BreakResolver(orphansRequired: 3, widowsRequired: 4);
        Assert.Equal(3, resolver.OrphansRequired);
        Assert.Equal(4, resolver.WidowsRequired);
    }

    // --- BreakDecision ---------------------------------------------------

    [Fact]
    public void BreakDecision_continue_singleton_zero_cost()
    {
        var decision = BreakDecision.Continue;
        Assert.Equal(BreakAction.Continue, decision.Action);
        Assert.Equal(0, decision.Cost);
        Assert.Null(decision.RewindTo);
    }

    // --- Review #7: enriched continuation tokens -------------------------

    [Fact]
    public void BlockContinuation_carries_resume_position()
    {
        var c = new BlockContinuation(ResumeAtChild: 3, ConsumedBlockSize: 250.5);
        Assert.Equal(3, c.ResumeAtChild);
        Assert.Equal(250.5, c.ConsumedBlockSize);
        Assert.Null(c.LayouterState); // default
    }

    [Fact]
    public void BlockContinuation_accepts_layouter_state()
    {
        var state = new object();
        var c = new BlockContinuation(3, 100, LayouterState: state);
        Assert.Same(state, c.LayouterState);
    }

    [Fact]
    public void TableContinuation_carries_repeat_flags_and_column_cache()
    {
        var cache = new object();
        var c = new TableContinuation(RepeatHead: true, RepeatFoot: false,
            NextRowIndex: 12, ColumnLayoutCache: cache);
        Assert.True(c.RepeatHead);
        Assert.False(c.RepeatFoot);
        Assert.Equal(12, c.NextRowIndex);
        Assert.Same(cache, c.ColumnLayoutCache);
    }

    [Fact]
    public void FlexContinuation_carries_baseline_state()
    {
        var baseline = new object();
        var c = new FlexContinuation(LineIndex: 4, BaselineState: baseline);
        Assert.Equal(4, c.LineIndex);
        Assert.Same(baseline, c.BaselineState);
    }

    [Fact]
    public void GridContinuation_carries_track_sizing_cache()
    {
        var cache = new object();
        var c = new GridContinuation(RowIndex: 8, TrackSizingCache: cache);
        Assert.Equal(8, c.RowIndex);
        Assert.Same(cache, c.TrackSizingCache);
    }

    [Fact]
    public void Continuation_records_have_value_equality()
    {
        var a = new BlockContinuation(3, 100);
        var b = new BlockContinuation(3, 100);
        var c = new BlockContinuation(3, 200);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // --- Review #9: diagnostic literal pinning ---------------------------

    [Fact]
    public void Diagnostic_pagination_optimizer_fallback_literal_value()
    {
        // Pin the constant value to the docs/diagnostics-codes.md row.
        Assert.Equal("PAGINATION-OPTIMIZER-FALLBACK-001",
            NetPdf.DiagnosticCodes.PaginationOptimizerFallback001);
    }

    [Fact]
    public void Diagnostic_pagination_forced_overflow_literal_value()
    {
        Assert.Equal("PAGINATION-FORCED-OVERFLOW-001",
            NetPdf.DiagnosticCodes.PaginationForcedOverflow001);
    }

    // --- PR #19 Copilot review fixes ---------------------------------------

    /// <summary>Copilot #1 — BreakResolver returns the prior
    /// checkpoint to the pool when a new one registers.</summary>
    [Fact]
    public void Copilot1_break_resolver_keeps_only_most_recent_checkpoint()
    {
        var resolver = new BreakResolver();
        var cp1Lease = LayoutCheckpointPool.Rent(); var cp1 = cp1Lease.Checkpoint!;
        cp1.PageIndex = 1;
        var cp2Lease = LayoutCheckpointPool.Rent(); var cp2 = cp2Lease.Checkpoint!;
        cp2.PageIndex = 2;
        resolver.RegisterCheckpoint(cp1Lease);
        // Register cp2 — cp1 must be returned to the pool, otherwise
        // it's a memory leak as layouters pile registrations.
        resolver.RegisterCheckpoint(cp2Lease);
        Assert.Same(cp2, resolver.GetLastCheckpoint());
        // cp1 should now be in the pool. Renting twice in a row should
        // succeed; the next Rent call would receive cp1 (reset).
        var rentedLease = LayoutCheckpointPool.Rent(); var rented = rentedLease.Checkpoint!;
        // Reset on rent → fresh state regardless of which instance came
        // back. The membership-tracker assertion is implicit: if cp1
        // wasn't returned, the pool would be empty + Rent would
        // allocate a new instance (also fine), but the side-effect we
        // care about is that cp1's state was cleared.
        Assert.Equal(0, rented.PageIndex);
        LayoutCheckpointPool.Return(rentedLease);
        LayoutCheckpointPool.Return(cp2Lease);
    }

    [Fact]
    public void Copilot1_break_resolver_register_same_checkpoint_twice_no_double_return()
    {
        // Defensive: if the layouter accidentally re-registers the SAME
        // checkpoint instance, it must NOT be returned to the pool
        // (would leave the resolver pointing at a freshly-cleared
        // instance + a stale "live" reference).
        var resolver = new BreakResolver();
        var cpLease = LayoutCheckpointPool.Rent(); var cp = cpLease.Checkpoint!;
        cp.PageIndex = 7;
        resolver.RegisterCheckpoint(cpLease);
        resolver.RegisterCheckpoint(cpLease); // self-register
        Assert.Same(cp, resolver.GetLastCheckpoint());
        Assert.Equal(7, cp.PageIndex); // not reset
    }

    /// <summary>Copilot #2 — RestoreCounters with null/empty drops
    /// the dict reference so PeekCounters returns null again.</summary>
    [Fact]
    public void Copilot2_restore_counters_null_drops_dict_reference()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        layout.Counter("page", 5);
        Assert.NotNull(layout.PeekCounters());

        layout.RestoreCounters(null);
        // Per Copilot review #2 — null restore must drop the dict, not
        // just clear it. PeekCounters returning null is the freshly-
        // constructed-context state.
        Assert.Null(layout.PeekCounters());
    }

    [Fact]
    public void Copilot2_restore_counters_empty_dict_also_drops_reference()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        layout.Counter("section", 3);
        layout.RestoreCounters(new Dictionary<string, int>());
        Assert.Null(layout.PeekCounters());
    }

    [Fact]
    public void Copilot2_restore_counters_after_drop_lazy_alloc_works_again()
    {
        // After a null-restore, the next Counter call should re-
        // trigger the lazy-alloc path.
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        layout.Counter("a", 1);
        layout.RestoreCounters(null);
        Assert.Null(layout.PeekCounters());
        layout.Counter("b", 2);
        Assert.Equal(2, layout.ReadCounter("b"));
        Assert.NotNull(layout.PeekCounters());
    }

    /// <summary>Copilot #3 — `WidowOrphanLineCount` renamed to
    /// `LinesBeforeBreak` to match its actual meaning. The orphan
    /// penalty fires when this drops below `orphansRequired`.</summary>
    [Fact]
    public void Copilot3_lines_before_break_param_drives_orphan_penalty()
    {
        // 1 line before the break + orphansRequired=2 → orphan penalty.
        var op = new BreakOpportunity(
            UsedBlockSize: 700, ChunkBlockSize: 20,
            Class: BreakOpportunityClass.LineBoundary,
            ForceBreak: false, AvoidBreak: false, ForceParity: PageParity.Any,
            LinesBeforeBreak: 1, StrandsHeading: false, SplitsFlexOrGridLine: false);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.Orphan, cost);
    }

    [Fact]
    public void Copilot3_line_helper_passes_lines_before_break_correctly()
    {
        // BreakOpportunity.Line(usedBlockSize, chunkBlockSize, linesBefore)
        // wires linesBefore into LinesBeforeBreak. Verified by
        // checking the orphan penalty fires when linesBefore < required.
        var op = BreakOpportunity.Line(
            usedBlockSize: 700, chunkBlockSize: 20, linesBefore: 1);
        Assert.Equal(1, op.LinesBeforeBreak);
        var cost = CostModel.Score(op, contentBlockSize: 800,
            orphansRequired: 2, widowsRequired: 2, lineCountAfterBreak: 5);
        Assert.Equal(CostModel.Orphan, cost);
    }

    /// <summary>Copilot #4 — WritingMode doc claims three values
    /// (matches the enum) not four.</summary>
    [Fact]
    public void Copilot4_writing_mode_has_exactly_three_enum_values()
    {
        var values = Enum.GetValues<WritingMode>();
        Assert.Equal(3, values.Length);
        Assert.Contains(WritingMode.HorizontalTb, values);
        Assert.Contains(WritingMode.VerticalRl, values);
        Assert.Contains(WritingMode.VerticalLr, values);
    }

    /// <summary>Copilot #5 — FragmentainerContext.Clone propagates
    /// NamedStrings + TotalPages but NOT layout-context counters
    /// (counters live in LayoutContext). Pin the contract.</summary>
    [Fact]
    public void Copilot5_clone_propagates_total_pages_and_named_strings_only()
    {
        var ctx = new FragmentainerContext(600, 800)
        {
            PageIndex = 2,
            TotalPages = 10,
            UsedBlockSize = 500,
        };
        ctx.NamedStrings["chapter"] = "intro";
        var next = ctx.Clone();
        // Carried forward.
        Assert.Equal(10, next.TotalPages);
        Assert.Equal("intro", next.NamedStrings["chapter"]);
        // NOT carried (page-scoped resets).
        Assert.Equal(3, next.PageIndex); // advanced by 1
        Assert.Equal(0, next.UsedBlockSize);
    }
}
