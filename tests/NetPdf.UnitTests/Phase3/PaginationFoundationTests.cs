// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Paginate;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 1 — pagination foundation tests. Covers
/// <see cref="FragmentainerContext"/>, <see cref="BreakOpportunity"/>,
/// <see cref="LayoutCheckpoint"/> + pool, <see cref="LayoutContext"/>,
/// <see cref="CostModel"/> penalty matrix, <see cref="BreakResolver"/>
/// stub semantics. Integration tests for the optimizer + actual
/// layouters land in later Phase 3 tasks.
/// </summary>
public sealed class PaginationFoundationTests
{
    // --- FragmentainerContext ----------------------------------------------

    [Fact]
    public void FragmentainerContext_remaining_height_tracks_used()
    {
        var ctx = new FragmentainerContext(contentAreaWidth: 600, contentAreaHeight: 800);
        Assert.Equal(800, ctx.RemainingHeight);
        ctx.UsedHeight = 200;
        Assert.Equal(600, ctx.RemainingHeight);
        ctx.UsedHeight = 800;
        Assert.Equal(0, ctx.RemainingHeight);
    }

    [Fact]
    public void FragmentainerContext_clone_advances_page_carries_named_strings()
    {
        var ctx = new FragmentainerContext(600, 800)
        {
            PageIndex = 0,
            UsedHeight = 500,
        };
        ctx.NamedStrings["chapter"] = "1. Introduction";

        var next = ctx.Clone();
        Assert.Equal(1, next.PageIndex);
        Assert.Equal(0, next.UsedHeight);
        Assert.Equal(600, next.ContentAreaWidth);
        Assert.Equal(800, next.ContentAreaHeight);
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

    // --- LayoutCheckpoint + pool -------------------------------------------

    [Fact]
    public void Checkpoint_pool_rent_returns_freshly_reset_instance()
    {
        var cp1 = LayoutCheckpointPool.Rent();
        cp1.PageIndex = 5;
        cp1.UsedHeight = 123;
        cp1.LastEmittedChildIndex = 7;
        cp1.IncomingContinuation = new BlockContinuation(3, 100);
        LayoutCheckpointPool.Return(cp1);

        var cp2 = LayoutCheckpointPool.Rent();
        // Pool may or may not return the same instance under churn; both
        // ways the rented instance must be reset.
        Assert.Equal(0, cp2.PageIndex);
        Assert.Equal(0, cp2.UsedHeight);
        Assert.Equal(-1, cp2.LastEmittedChildIndex);
        Assert.Null(cp2.IncomingContinuation);
        LayoutCheckpointPool.Return(cp2);
    }

    [Fact]
    public void Checkpoint_pool_handles_null_return_gracefully()
    {
        // Idempotent / null-safe Return (defensive contract).
        LayoutCheckpointPool.Return(null);
        // No exception = pass.
    }

    // --- LayoutContext ref struct ------------------------------------------

    [Fact]
    public void LayoutContext_constructed_from_fragmentainer_inherits_dimensions()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        Assert.Equal(600, layout.AvailableWidth);
        Assert.Equal(800, layout.AvailableHeight);
        Assert.Equal(WritingMode.HorizontalTb, layout.WritingMode);
        Assert.False(layout.IsRtl);
    }

    [Fact]
    public void LayoutContext_counter_lazy_alloc_reads_zero_default()
    {
        var ctx = new FragmentainerContext(600, 800);
        var layout = new LayoutContext(ctx);
        Assert.Equal(0, layout.ReadCounter("page"));
        // Read alone shouldn't trigger allocation.
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

    // --- CostModel ---------------------------------------------------------

    [Fact]
    public void CostModel_break_inside_avoid_violation_is_effectively_infinite()
    {
        var op = new BreakOpportunity(
            UsedHeight: 100,
            ChunkHeight: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForbidsBreak: true,
            WidowOrphanLineCount: 0);
        var cost = CostModel.Score(op,
            usedHeight: 100, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 5);
        Assert.True(cost >= CostModel.BreakInsideAvoidViolation);
    }

    [Fact]
    public void CostModel_section_boundary_earns_negative_cost_reward()
    {
        var op = new BreakOpportunity(
            UsedHeight: 100,
            ChunkHeight: 50,
            Class: BreakOpportunityClass.SectionBoundary,
            ForbidsBreak: false,
            WidowOrphanLineCount: 0);
        // Use UsedHeight close to ContentAreaHeight so the
        // large-trailing-blank penalty doesn't fire.
        var cost = CostModel.Score(op,
            usedHeight: 700, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 5);
        Assert.Equal(CostModel.SectionBoundaryReward, cost);
    }

    [Fact]
    public void CostModel_orphan_penalty_when_lines_below_required()
    {
        var op = new BreakOpportunity(
            UsedHeight: 100,
            ChunkHeight: 20,
            Class: BreakOpportunityClass.LineBoundary,
            ForbidsBreak: false,
            WidowOrphanLineCount: 1); // 1 line at bottom of page; 2 required
        var cost = CostModel.Score(op,
            usedHeight: 700, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 5);
        Assert.Equal(CostModel.Orphan, cost);
    }

    [Fact]
    public void CostModel_widow_penalty_when_next_page_has_one_line()
    {
        var op = new BreakOpportunity(
            UsedHeight: 100,
            ChunkHeight: 20,
            Class: BreakOpportunityClass.LineBoundary,
            ForbidsBreak: false,
            WidowOrphanLineCount: 5); // satisfies orphans
        var cost = CostModel.Score(op,
            usedHeight: 700, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 1); // widow
        Assert.Equal(CostModel.Widow, cost);
    }

    [Fact]
    public void CostModel_table_row_mid_cell_split_penalty()
    {
        var op = new BreakOpportunity(
            UsedHeight: 100,
            ChunkHeight: 50,
            Class: BreakOpportunityClass.InsideTableRow,
            ForbidsBreak: false,
            WidowOrphanLineCount: 0);
        var cost = CostModel.Score(op,
            usedHeight: 700, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 5);
        Assert.Equal(CostModel.TableRowMidCellSplit, cost);
    }

    [Fact]
    public void CostModel_large_trailing_blank_penalty_applied()
    {
        var op = new BreakOpportunity(
            UsedHeight: 100,
            ChunkHeight: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForbidsBreak: false,
            WidowOrphanLineCount: 0);
        // 100 used / 800 total = 12.5% used, 87.5% blank → > 30% threshold.
        var cost = CostModel.Score(op,
            usedHeight: 100, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 5);
        Assert.Equal(CostModel.LargeBlankTrailingArea, cost);
    }

    [Fact]
    public void CostModel_zero_cost_when_clean_block_boundary_no_blank()
    {
        var op = new BreakOpportunity(
            UsedHeight: 700,
            ChunkHeight: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForbidsBreak: false,
            WidowOrphanLineCount: 0);
        // 700 / 800 = 87.5% used, 12.5% blank — under 30%.
        var cost = CostModel.Score(op,
            usedHeight: 700, contentAreaHeight: 800,
            orphansRequired: 2, widowsRequired: 2,
            lineCountAfterBreak: 5);
        Assert.Equal(0, cost);
    }

    // --- BreakResolver stub ------------------------------------------------

    [Fact]
    public void BreakResolver_continue_when_chunk_fits()
    {
        var ctx = new FragmentainerContext(600, 800) { UsedHeight = 100 };
        var resolver = new BreakResolver();
        var op = new BreakOpportunity(
            UsedHeight: 100, ChunkHeight: 50,
            Class: BreakOpportunityClass.BlockBoundary,
            ForbidsBreak: false, WidowOrphanLineCount: 0);
        var decision = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.Continue, decision.Action);
    }

    [Fact]
    public void BreakResolver_break_here_when_chunk_overflows()
    {
        var ctx = new FragmentainerContext(600, 800) { UsedHeight = 700 };
        var resolver = new BreakResolver();
        var op = new BreakOpportunity(
            UsedHeight: 700, ChunkHeight: 200, // 700 + 200 > 800
            Class: BreakOpportunityClass.BlockBoundary,
            ForbidsBreak: false, WidowOrphanLineCount: 0);
        var decision = resolver.ConsiderBreakAt(op, ctx);
        Assert.Equal(BreakAction.BreakHere, decision.Action);
    }

    [Fact]
    public void BreakResolver_checkpoint_register_then_get_returns_last()
    {
        var resolver = new BreakResolver();
        Assert.Null(resolver.GetLastCheckpoint());
        var cp1 = LayoutCheckpointPool.Rent();
        cp1.PageIndex = 1;
        var cp2 = LayoutCheckpointPool.Rent();
        cp2.PageIndex = 2;
        resolver.RegisterCheckpoint(cp1);
        resolver.RegisterCheckpoint(cp2);
        Assert.Same(cp2, resolver.GetLastCheckpoint());
    }

    [Fact]
    public void BreakResolver_with_custom_widow_orphan_overrides_defaults()
    {
        var resolver = new BreakResolver(orphansRequired: 3, widowsRequired: 4);
        Assert.Equal(3, resolver.OrphansRequired);
        Assert.Equal(4, resolver.WidowsRequired);
    }

    // --- BreakDecision -----------------------------------------------------

    [Fact]
    public void BreakDecision_continue_singleton_zero_cost()
    {
        var decision = BreakDecision.Continue;
        Assert.Equal(BreakAction.Continue, decision.Action);
        Assert.Equal(0, decision.Cost);
        Assert.Null(decision.RewindTo);
    }

    // --- Continuation token shapes -----------------------------------------

    [Fact]
    public void BlockContinuation_carries_resume_position()
    {
        var c = new BlockContinuation(ResumeAtChild: 3, ConsumedHeight: 250.5);
        Assert.Equal(3, c.ResumeAtChild);
        Assert.Equal(250.5, c.ConsumedHeight);
    }

    [Fact]
    public void TableContinuation_carries_repeat_flags()
    {
        var c = new TableContinuation(RepeatHead: true, RepeatFoot: false, NextRowIndex: 12);
        Assert.True(c.RepeatHead);
        Assert.False(c.RepeatFoot);
        Assert.Equal(12, c.NextRowIndex);
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
}
