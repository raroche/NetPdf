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
        bool rightPercent = false)
    {
        var style = MakeStyle();
        // position: absolute = keyword id 2.
        style.Set(PropertyId.Position, ComputedSlot.FromKeyword(2));
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

    [Fact]
    public void Auto_top_defers()
    {
        // top unset (= auto default) → unresolved.
        var box = BuildAbsoluteBox(left: 20, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("top", p.DeferReason);
    }

    [Fact]
    public void Auto_left_defers()
    {
        var box = BuildAbsoluteBox(top: 10, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("left", p.DeferReason);
    }

    [Fact]
    public void Auto_width_defers()
    {
        var box = BuildAbsoluteBox(top: 10, left: 20, height: 30, widthAuto: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("width", p.DeferReason);
    }

    [Fact]
    public void Auto_height_defers()
    {
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("height", p.DeferReason);
    }

    [Fact]
    public void Percentage_top_defers()
    {
        var box = BuildAbsoluteBox(top: 50, left: 20, width: 50, height: 30, topPercent: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("top", p.DeferReason);
    }

    [Fact]
    public void Negative_width_defers()
    {
        var box = BuildAbsoluteBox(top: 10, left: 20, width: -50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("negative", p.DeferReason);
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

    // ============================================================
    // Post-PR-#112 review C1 — explicit right/bottom defers (cycle 1
    // anchors via top/left only; right/bottom anchoring + over-
    // constrained resolution is cycle 2). The box must NOT silently
    // resolve via top/left while ignoring right/bottom.
    // ============================================================

    [Fact]
    public void Explicit_right_defers_even_with_full_top_left_width_height()
    {
        // Over-specified (top+left+width+height ALL set) + an explicit
        // right. Cycle 1 defers rather than silently ignoring `right`.
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30, right: 0);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("right", p.DeferReason);
    }

    [Fact]
    public void Explicit_bottom_defers()
    {
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30, bottom: 0);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("bottom", p.DeferReason);
    }

    [Fact]
    public void Percentage_right_defers()
    {
        var box = BuildAbsoluteBox(
            top: 10, left: 20, width: 50, height: 30, right: 25, rightPercent: true);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.False(p.IsResolved);
        Assert.Contains("right", p.DeferReason);
    }

    [Fact]
    public void No_right_bottom_still_resolves()
    {
        // Sanity: the C1 fix doesn't break the happy path — a box with
        // NO right/bottom (the default auto) resolves normally.
        var box = BuildAbsoluteBox(top: 10, left: 20, width: 50, height: 30);
        var p = AbsoluteLayouter.ResolvePlacement(box, OriginCb);
        Assert.True(p.IsResolved);
    }
}
