// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Per Phase 3 Task 15 L14 — unit tests for <see cref="CrossAxisFlow"/>.
/// Covers the wrap (= identity) + wrap-reverse (= swapped) physical-offset
/// formulas at multiple cursor positions.
/// </summary>
public sealed class CrossAxisFlowTests
{
    [Fact]
    public void Wrap_physical_offset_is_content_offset_plus_cursor_at_origin()
    {
        // Per CSS Flexbox L1 §6.3 — for `flex-wrap: wrap` the physical
        // axis IS the swapped axis: the line's physical top edge is
        // contentCrossOffset + the cursor position.
        var flow = new CrossAxisFlow(
            IsReversed: false,
            ContentCrossOffset: 10.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(10.0, flow.PhysicalLineOffset(swappedCursor: 0.0, lineCrossSize: 50.0));
    }

    [Fact]
    public void Wrap_physical_offset_grows_linearly_with_cursor()
    {
        // Wrap mode: physical = contentCrossOffset + swappedCursor.
        // lineCrossSize doesn't enter the formula because the line's
        // TOP edge sits exactly where the cursor is.
        var flow = new CrossAxisFlow(
            IsReversed: false,
            ContentCrossOffset: 0.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(50.0, flow.PhysicalLineOffset(swappedCursor: 50.0, lineCrossSize: 30.0));
        Assert.Equal(100.0, flow.PhysicalLineOffset(swappedCursor: 100.0, lineCrossSize: 30.0));
    }

    [Fact]
    public void Wrap_reverse_first_line_anchors_at_physical_end_minus_line_size()
    {
        // Per CSS Flexbox L1 §6.3 — for `flex-wrap: wrap-reverse` the
        // line stack starts at the swapped cross-start (= physical
        // cross-END for row + horizontal-tb LTR). With cursor = 0 +
        // a 50-tall line in a 200-tall container, the line's PHYSICAL-
        // TOP edge sits at 200 - 0 - 50 = 150 (= 50 below the
        // container's physical-end edge).
        var flow = new CrossAxisFlow(
            IsReversed: true,
            ContentCrossOffset: 0.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(150.0, flow.PhysicalLineOffset(swappedCursor: 0.0, lineCrossSize: 50.0));
    }

    [Fact]
    public void Wrap_reverse_second_line_anchors_below_first_in_physical_terms()
    {
        // Wrap-reverse second line: cursor advanced past line 0 +
        // line 0's cross-extent → cursor = 50. Line 1's PHYSICAL-TOP
        // = 200 - 50 - lineCrossSize(50) = 100 (= just below line 0).
        // This proves the line stack walks downward in physical
        // coordinates while the cursor walks upward in swapped
        // coordinates.
        var flow = new CrossAxisFlow(
            IsReversed: true,
            ContentCrossOffset: 0.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(100.0, flow.PhysicalLineOffset(swappedCursor: 50.0, lineCrossSize: 50.0));
    }

    [Fact]
    public void Wrap_reverse_respects_content_cross_offset_padding()
    {
        // With a non-zero contentCrossOffset (= wrapper has top
        // padding / border), the wrap-reverse formula still anchors
        // the line stack at the wrapper's content-box END, which is
        // contentCrossOffset + containerCrossSize. Line 0 with cursor
        // 0 + 30-tall line in a 200-tall content box at offset 20
        // sits at 20 + 200 - 0 - 30 = 190.
        var flow = new CrossAxisFlow(
            IsReversed: true,
            ContentCrossOffset: 20.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(190.0, flow.PhysicalLineOffset(swappedCursor: 0.0, lineCrossSize: 30.0));
    }

    [Fact]
    public void Wrap_respects_content_cross_offset_padding()
    {
        // For wrap mode the offset just shifts the entire line stack
        // by the wrapper's padding/border.
        var flow = new CrossAxisFlow(
            IsReversed: false,
            ContentCrossOffset: 20.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(70.0, flow.PhysicalLineOffset(swappedCursor: 50.0, lineCrossSize: 30.0));
    }

    [Fact]
    public void Wrap_reverse_with_unequal_line_sizes_anchors_each_correctly()
    {
        // Verify the formula handles unequal line cross-extents:
        // line 0 = 30 tall; line 1 = 50 tall; cursor walks 0 → 30.
        // Line 0 physical: 200 - 0 - 30 = 170.
        // Line 1 physical: 200 - 30 - 50 = 120 (= 50 below line 0).
        var flow = new CrossAxisFlow(
            IsReversed: true,
            ContentCrossOffset: 0.0,
            ContainerCrossSize: 200.0);
        Assert.Equal(170.0, flow.PhysicalLineOffset(swappedCursor: 0.0, lineCrossSize: 30.0));
        Assert.Equal(120.0, flow.PhysicalLineOffset(swappedCursor: 30.0, lineCrossSize: 50.0));
    }
}
