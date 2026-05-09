// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 8 cycle 1 — unit tests for the
/// <see cref="FloatManager"/> public API. Integration with
/// <see cref="BlockLayouter"/> is exercised by the floats-related
/// tests in <c>BlockLayouterTests</c>.</summary>
public class FloatManagerTests
{
    // --- PlaceFloat: basic side alignment -------------------------

    [Fact]
    public void PlaceFloat_left_aligns_to_containing_inline_start()
    {
        var fm = new FloatManager();
        var (inline, block) = fm.PlaceFloat(
            side: FloatSide.Left,
            inlineSize: 100,
            blockSize: 50,
            containingInlineStart: 0,
            containingInlineEnd: 600,
            currentBlockY: 0);
        Assert.Equal(0, inline);
        Assert.Equal(0, block);
    }

    [Fact]
    public void PlaceFloat_right_aligns_to_containing_inline_end_minus_size()
    {
        var fm = new FloatManager();
        var (inline, block) = fm.PlaceFloat(
            side: FloatSide.Right,
            inlineSize: 100,
            blockSize: 50,
            containingInlineStart: 0,
            containingInlineEnd: 600,
            currentBlockY: 0);
        Assert.Equal(500, inline);  // 600 - 100
        Assert.Equal(0, block);
    }

    // --- PlaceFloat: same-side stacking ---------------------------

    [Fact]
    public void PlaceFloat_two_left_floats_stack_vertically()
    {
        var fm = new FloatManager();
        // First float at y=0, height=80.
        var (_, block1) = fm.PlaceFloat(
            FloatSide.Left, 100, 80, 0, 600, currentBlockY: 0);
        Assert.Equal(0, block1);

        // Second left float at currentBlockY=10 → stacked below first
        // (y=80, since first's bottom = 0+80 = 80 > 10).
        var (_, block2) = fm.PlaceFloat(
            FloatSide.Left, 100, 50, 0, 600, currentBlockY: 10);
        Assert.Equal(80, block2);
    }

    [Fact]
    public void PlaceFloat_left_does_not_constrain_right_in_cycle_1()
    {
        // Cycle 1 MVP: same-side floats stack; opposite-side floats
        // don't constrain each other (no inline-axis packing).
        var fm = new FloatManager();
        var (_, leftBlock) = fm.PlaceFloat(
            FloatSide.Left, 100, 200, 0, 600, currentBlockY: 0);
        Assert.Equal(0, leftBlock);

        // Right float at currentBlockY=0 → placed at y=0 (left float's
        // 200-tall presence doesn't push it down in cycle 1).
        var (_, rightBlock) = fm.PlaceFloat(
            FloatSide.Right, 100, 50, 0, 600, currentBlockY: 0);
        Assert.Equal(0, rightBlock);
    }

    [Fact]
    public void PlaceFloat_advances_to_currentBlockY_when_no_prior_float_constrains()
    {
        var fm = new FloatManager();
        var (_, block) = fm.PlaceFloat(
            FloatSide.Left, 100, 80, 0, 600, currentBlockY: 200);
        Assert.Equal(200, block);
    }

    // --- PlaceFloat: arg validation -------------------------------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void PlaceFloat_rejects_invalid_inline_size(double bad)
    {
        var fm = new FloatManager();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.PlaceFloat(FloatSide.Left, bad, 50, 0, 600, 0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void PlaceFloat_rejects_invalid_block_size(double bad)
    {
        var fm = new FloatManager();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.PlaceFloat(FloatSide.Left, 100, bad, 0, 600, 0));
    }

    [Fact]
    public void PlaceFloat_rejects_NaN_currentBlockY()
    {
        var fm = new FloatManager();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.PlaceFloat(FloatSide.Left, 100, 50, 0, 600, double.NaN));
    }

    // --- ActiveFloats ---------------------------------------------

    [Fact]
    public void ActiveFloats_starts_empty()
    {
        var fm = new FloatManager();
        Assert.Empty(fm.ActiveFloats);
    }

    [Fact]
    public void ActiveFloats_grows_with_each_placement()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 50, 0, 600, 0);
        fm.PlaceFloat(FloatSide.Right, 80, 40, 0, 600, 0);
        Assert.Equal(2, fm.ActiveFloats.Count);
    }

    // --- GetClearedBlockY -----------------------------------------

    [Fact]
    public void GetClearedBlockY_none_returns_input_unchanged()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 200, 0, 600, 0);
        Assert.Equal(50, fm.GetClearedBlockY(50, ClearKind.None));
    }

    [Fact]
    public void GetClearedBlockY_left_advances_past_left_float_bottom()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 200, 0, 600, 0);
        // Left float: y=[0, 200). clear:left at y=50 → advance to 200.
        Assert.Equal(200, fm.GetClearedBlockY(50, ClearKind.Left));
    }

    [Fact]
    public void GetClearedBlockY_right_advances_past_right_float_bottom()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Right, 100, 150, 0, 600, 0);
        Assert.Equal(150, fm.GetClearedBlockY(0, ClearKind.Right));
    }

    [Fact]
    public void GetClearedBlockY_left_ignores_right_floats()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Right, 100, 500, 0, 600, 0);
        // clear:left only inspects left floats — none here. Returns input.
        Assert.Equal(50, fm.GetClearedBlockY(50, ClearKind.Left));
    }

    [Fact]
    public void GetClearedBlockY_both_advances_past_max_of_left_and_right()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 200, 0, 600, 0);
        fm.PlaceFloat(FloatSide.Right, 100, 300, 0, 600, 0);
        Assert.Equal(300, fm.GetClearedBlockY(0, ClearKind.Both));
    }

    [Fact]
    public void GetClearedBlockY_does_not_pull_value_below_input()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 80, 0, 600, 0);
        // Float bottom = 80; input = 200 (already past). Returns 200,
        // not 80.
        Assert.Equal(200, fm.GetClearedBlockY(200, ClearKind.Left));
    }

    [Fact]
    public void GetClearedBlockY_inline_start_resolves_as_left_in_cycle_1()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 80, 0, 600, 0);
        Assert.Equal(80, fm.GetClearedBlockY(0, ClearKind.InlineStart));
    }

    [Fact]
    public void GetClearedBlockY_inline_end_resolves_as_right_in_cycle_1()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Right, 100, 60, 0, 600, 0);
        Assert.Equal(60, fm.GetClearedBlockY(0, ClearKind.InlineEnd));
    }

    [Fact]
    public void GetClearedBlockY_with_no_floats_returns_input_for_any_clear()
    {
        var fm = new FloatManager();
        Assert.Equal(150, fm.GetClearedBlockY(150, ClearKind.Both));
        Assert.Equal(150, fm.GetClearedBlockY(150, ClearKind.Left));
        Assert.Equal(150, fm.GetClearedBlockY(150, ClearKind.Right));
    }

    [Fact]
    public void GetClearedBlockY_rejects_NaN_input()
    {
        var fm = new FloatManager();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.GetClearedBlockY(double.NaN, ClearKind.Both));
    }

    // --- Snapshot / RestoreFrom -----------------------------------

    [Fact]
    public void Snapshot_captures_active_floats()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 80, 0, 600, 0);
        fm.PlaceFloat(FloatSide.Right, 80, 50, 0, 600, 0);
        var snapshot = fm.Snapshot();
        Assert.NotNull(snapshot);
    }

    [Fact]
    public void RestoreFrom_null_snapshot_resets_to_no_floats()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 80, 0, 600, 0);
        Assert.Single(fm.ActiveFloats);

        fm.RestoreFrom(null);
        Assert.Empty(fm.ActiveFloats);
    }

    [Fact]
    public void RestoreFrom_replays_snapshot_floats()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 80, 0, 600, 0);
        fm.PlaceFloat(FloatSide.Right, 80, 50, 0, 600, 0);
        var snapshot = fm.Snapshot();

        // Mutate live state.
        fm.PlaceFloat(FloatSide.Left, 50, 30, 0, 600, 200);
        Assert.Equal(3, fm.ActiveFloats.Count);

        // Restore — back to 2 floats.
        fm.RestoreFrom(snapshot);
        Assert.Equal(2, fm.ActiveFloats.Count);
    }

    [Fact]
    public void Snapshot_does_not_alias_live_state()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 80, 0, 600, 0);
        var snapshot = fm.Snapshot();

        // Add a float AFTER snapshot — snapshot must be unaffected.
        fm.PlaceFloat(FloatSide.Right, 50, 30, 0, 600, 0);

        var fm2 = new FloatManager();
        fm2.RestoreFrom(snapshot);
        // fm2 has only the original 1 float, not the post-snapshot 2.
        Assert.Single(fm2.ActiveFloats);
    }

    [Fact]
    public void RestoreFrom_rejects_unrecognized_snapshot_type()
    {
        var fm = new FloatManager();
        Assert.Throws<InvalidOperationException>(() =>
            fm.RestoreFrom("not a snapshot"));
    }

    // ====================================================================
    //  Phase 3 Task 8 cycle 2 — GetAvailableInlineRange
    // ====================================================================

    [Fact]
    public void GetAvailableInlineRange_no_floats_returns_full_range()
    {
        var fm = new FloatManager();
        var range = fm.GetAvailableInlineRange(blockY: 100,
            containingStart: 0, containingEnd: 600);
        Assert.Equal(0, range.InlineStart);
        Assert.Equal(600, range.InlineEnd);
    }

    [Fact]
    public void GetAvailableInlineRange_left_float_pushes_start_past_its_right_edge()
    {
        var fm = new FloatManager();
        // Left float: inline=[0, 100), block=[0, 200).
        fm.PlaceFloat(FloatSide.Left, 100, 200, 0, 600, 0);
        // Query at y=50 (inside float's vertical extent) — left edge
        // should be 100 (past float's right edge).
        var range = fm.GetAvailableInlineRange(50, 0, 600);
        Assert.Equal(100, range.InlineStart);
        Assert.Equal(600, range.InlineEnd);
    }

    [Fact]
    public void GetAvailableInlineRange_right_float_pulls_end_before_its_left_edge()
    {
        var fm = new FloatManager();
        // Right float: inline=[500, 600), block=[0, 150).
        fm.PlaceFloat(FloatSide.Right, 100, 150, 0, 600, 0);
        var range = fm.GetAvailableInlineRange(50, 0, 600);
        Assert.Equal(0, range.InlineStart);
        Assert.Equal(500, range.InlineEnd);
    }

    [Fact]
    public void GetAvailableInlineRange_left_and_right_floats_both_constrain()
    {
        var fm = new FloatManager();
        fm.PlaceFloat(FloatSide.Left, 100, 200, 0, 600, 0);
        fm.PlaceFloat(FloatSide.Right, 80, 200, 0, 600, 0);
        var range = fm.GetAvailableInlineRange(50, 0, 600);
        Assert.Equal(100, range.InlineStart);
        Assert.Equal(520, range.InlineEnd);  // 600 - 80
    }

    [Fact]
    public void GetAvailableInlineRange_y_above_float_top_is_unconstrained()
    {
        var fm = new FloatManager();
        // Float starts at y=100.
        fm.PlaceFloat(FloatSide.Left, 100, 50, 0, 600, currentBlockY: 100);
        // Query at y=50 (BEFORE float's top) — full range available.
        var range = fm.GetAvailableInlineRange(50, 0, 600);
        Assert.Equal(0, range.InlineStart);
        Assert.Equal(600, range.InlineEnd);
    }

    [Fact]
    public void GetAvailableInlineRange_y_at_or_past_float_bottom_is_unconstrained()
    {
        var fm = new FloatManager();
        // Float at y=[0, 100). Query at y=100 (= float bottom, exclusive).
        fm.PlaceFloat(FloatSide.Left, 100, 100, 0, 600, 0);
        var range = fm.GetAvailableInlineRange(100, 0, 600);
        Assert.Equal(0, range.InlineStart);
        Assert.Equal(600, range.InlineEnd);
    }

    [Fact]
    public void GetAvailableInlineRange_two_left_floats_stacked_only_active_constrains()
    {
        var fm = new FloatManager();
        // Float 1: y=[0, 50).
        fm.PlaceFloat(FloatSide.Left, 100, 50, 0, 600, 0);
        // Float 2 stacks below: y=[50, 130).
        fm.PlaceFloat(FloatSide.Left, 80, 80, 0, 600, currentBlockY: 50);
        // Query at y=80 — only float 2 is active (y in [50, 130)).
        var range = fm.GetAvailableInlineRange(80, 0, 600);
        Assert.Equal(80, range.InlineStart);  // float 2's right edge
    }

    [Fact]
    public void GetAvailableInlineRange_oversized_left_float_can_make_end_below_start()
    {
        var fm = new FloatManager();
        // Oversized left float: inline=[0, 700) on a 600-wide CB.
        fm.PlaceFloat(FloatSide.Left, 700, 50, 0, 600, 0);
        var range = fm.GetAvailableInlineRange(20, 0, 600);
        // Left edge pushed to 700; right edge stays at 600. Caller
        // sees end < start (degenerate); cycle 2 callers clamp width
        // to 0.
        Assert.Equal(700, range.InlineStart);
        Assert.Equal(600, range.InlineEnd);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GetAvailableInlineRange_rejects_non_finite_inputs(double bad)
    {
        var fm = new FloatManager();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.GetAvailableInlineRange(bad, 0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.GetAvailableInlineRange(0, bad, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fm.GetAvailableInlineRange(0, 0, bad));
    }
}
