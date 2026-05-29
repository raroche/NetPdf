// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 19 cycle 1 — unit tests for the pure placement math in
/// <see cref="AbsoluteLayouter.ResolvePlacement"/>. Covers the explicit-
/// only cycle-1 contract (top/left/width/height as pixels), the
/// containing-block origin offset, and the cycle-1 defer cases
/// (auto / percentage / negative).
/// </summary>
public sealed class AbsoluteLayouterTests
{
    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }

    private static Box BuildAbsoluteBox(
        double? top = null, double? left = null,
        double? width = null, double? height = null,
        bool topPercent = false, bool widthAuto = false,
        double? right = null, double? bottom = null,
        bool rightPercent = false, bool inlineMarginsAuto = false)
    {
        var style = MakeStyle();
        // position: absolute = keyword id 2.
        style.Set(PropertyId.Position, ComputedSlot.FromKeyword(2));
        if (inlineMarginsAuto)
        {
            // explicit `margin-left/right: auto` (Keyword slot).
            style.Set(PropertyId.MarginLeft, ComputedSlot.FromKeyword(0));
            style.Set(PropertyId.MarginRight, ComputedSlot.FromKeyword(0));
        }
        if (topPercent)
        {
            style.Set(PropertyId.Top, ComputedSlot.FromPercentage(top ?? 0));
        }
        else if (top is { } t)
        {
            style.Set(PropertyId.Top, ComputedSlot.FromLengthPx(t));
        }
        if (left is { } l) style.Set(PropertyId.Left, ComputedSlot.FromLengthPx(l));
        if (!widthAuto && width is { } w)
        {
            style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(w));
        }
        if (height is { } h) style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(h));
        if (rightPercent)
        {
            style.Set(PropertyId.Right, ComputedSlot.FromPercentage(right ?? 0));
        }
        else if (right is { } r)
        {
            style.Set(PropertyId.Right, ComputedSlot.FromLengthPx(r));
        }
        if (bottom is { } b) style.Set(PropertyId.Bottom, ComputedSlot.FromLengthPx(b));
        return Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
    }

    private static readonly AbsoluteContainingBlock OriginCb =
        new(InlineOrigin: 0, BlockOrigin: 0, InlineSize: 600, BlockSize: 800);

    [Fact]
    public void Explicit_top_left_width_height_resolves_at_cb_origin()
    {
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);

        Assert.True(p.IsResolved);
        Assert.Equal(20.0, p.InlineOffset, precision: 3);
        Assert.Equal(10.0, p.BlockOffset, precision: 3);
        Assert.Equal(50.0, p.InlineSize, precision: 3);
        Assert.Equal(30.0, p.BlockSize, precision: 3);
    }

    [Fact]
    public void Containing_block_origin_offsets_the_placement()
    {
        // A non-zero CB origin (= a positioned ancestor's content-box
        // origin) shifts the resolved offsets by the CB origin.
        var cb = new AbsoluteContainingBlock(
            InlineOrigin: 100, BlockOrigin: 200, InlineSize: 400, BlockSize: 500);
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, cb);

        Assert.True(p.IsResolved);
        Assert.Equal(120.0, p.InlineOffset, precision: 3); // 100 + 20
        Assert.Equal(210.0, p.BlockOffset, precision: 3);  // 200 + 10
        Assert.Equal(50.0, p.InlineSize, precision: 3);
        Assert.Equal(30.0, p.BlockSize, precision: 3);
    }

    // ============================================================
    // Cycle 2b — the full §10.3.7 / §10.6.4 constraint solver.
    // CB = OriginCb (origin 0, inline 600, block 800; margins 0,
    // chrome 0 unless noted).
    // ============================================================

    [Fact]
    public void Auto_top_uses_static_position()
    {
        // top auto + bottom auto + height given → top = static position
        // (CB origin = 0).
        var box = BuildAbsoluteBox(left: 20, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(20.0, p.InlineOffset, precision: 3);
        Assert.Equal(0.0, p.BlockOffset, precision: 3);
        Assert.Equal(30.0, p.BlockSize, precision: 3);
    }

    [Fact]
    public void Auto_left_uses_static_position()
    {
        var box = BuildAbsoluteBox(top: 10, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(0.0, p.InlineOffset, precision: 3);   // static position
        Assert.Equal(10.0, p.BlockOffset, precision: 3);
    }

    [Fact]
    public void Auto_width_with_left_fills_available()
    {
        // left given, right auto, width auto → width = available from
        // left to CB inline-end = 600 - 20 = 580 (shrink-to-fit approx).
        var box = BuildAbsoluteBox(top: 10, left: 20, height: 30, widthAuto: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(20.0, p.InlineOffset, precision: 3);
        Assert.Equal(580.0, p.InlineSize, precision: 3);
    }

    [Fact]
    public void Auto_height_with_top_fills_available()
    {
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(10.0, p.BlockOffset, precision: 3);
        Assert.Equal(790.0, p.BlockSize, precision: 3);  // 800 - 10
    }

    [Fact]
    public void Percentage_top_resolves_against_cb_block_extent()
    {
        // top: 50% of CB block (800) = 400; height 30 given; bottom auto.
        var box = BuildAbsoluteBox(top: 50, left: 20, width: 50, height: 30, topPercent: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(400.0, p.BlockOffset, precision: 3);
        Assert.Equal(30.0, p.BlockSize, precision: 3);
    }

    [Fact]
    public void Right_anchored_resolves_left_from_remainder()
    {
        // left auto, right=100, width=50 → left = 600 - 100 - 50 = 450.
        var box = BuildAbsoluteBox(top: 10, width: 50, height: 30, right: 100);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(450.0, p.InlineOffset, precision: 3);
        Assert.Equal(50.0, p.InlineSize, precision: 3);
    }

    [Fact]
    public void Bottom_anchored_resolves_top_from_remainder()
    {
        // top auto, bottom=200, height=30 → top = 800 - 200 - 30 = 570.
        var box = BuildAbsoluteBox(left: 10, width: 50, height: 30, bottom: 200);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(570.0, p.BlockOffset, precision: 3);
        Assert.Equal(30.0, p.BlockSize, precision: 3);
    }

    [Fact]
    public void Both_insets_with_auto_width_fills()
    {
        // left=20, right=30, width auto → width = 600 - 20 - 30 = 550.
        var box = BuildAbsoluteBox(top: 10, left: 20, height: 30, right: 30, widthAuto: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(20.0, p.InlineOffset, precision: 3);
        Assert.Equal(550.0, p.InlineSize, precision: 3);
    }

    [Fact]
    public void Over_constrained_ignores_right_inset_ltr()
    {
        // left=20, width=50, right=0 ALL given, margins 0 (initial) →
        // over-constrained: ignore `right` (LTR), anchor via left.
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30, right: 0);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(20.0, p.InlineOffset, precision: 3);
        Assert.Equal(50.0, p.InlineSize, precision: 3);
    }

    [Fact]
    public void Over_constrained_percentage_right_ignored()
    {
        // left=20, width=50, right=25% (=150), margins 0 → over-
        // constrained, ignore right, anchor via left.
        var box = BuildAbsoluteBox(
            top: 10, left: 20, width: 50, height: 30, right: 25, rightPercent: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(20.0, p.InlineOffset, precision: 3);
        Assert.Equal(50.0, p.InlineSize, precision: 3);
    }

    [Fact]
    public void Over_constrained_with_auto_margins_centers()
    {
        // left=20, width=50, right=0 all given + BOTH margins auto →
        // §10.3.7 centering: margins split the slack
        // (600 - 20 - 0 - 50 = 530) → each 265. Border-box left =
        // left(20) + marginLeft(265) = 285.
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30, right: 0,
            inlineMarginsAuto: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(285.0, p.InlineOffset, precision: 3);
        Assert.Equal(50.0, p.InlineSize, precision: 3);
    }

    [Fact]
    public void Negative_resolved_size_clamps_to_zero()
    {
        // left=400, right=400, width auto → fill = 600-400-400 = -200;
        // CSS clamps used width to >= 0, so the box resolves with
        // width 0 (not dropped).
        var box = BuildAbsoluteBox(top: 10, left: 400, height: 30, right: 400, widthAuto: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(0.0, p.InlineSize, precision: 3);
        Assert.Equal(400.0, p.InlineOffset, precision: 3);
    }

    [Fact]
    public void Zero_offsets_resolve_at_cb_origin()
    {
        var box = BuildAbsoluteBox(top: 0, left: 0, width: 100, height: 100);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
        Assert.Equal(0.0, p.InlineOffset, precision: 3);
        Assert.Equal(0.0, p.BlockOffset, precision: 3);
    }
}
