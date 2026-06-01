// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 16 cycle 4d — direct unit tests for the
/// <see cref="FlexGeometryHelper"/> shared helper. Pre-cycle-4d
/// the content-box geometry derivation lived as 3 ~25-line
/// duplicates inside <see cref="BlockLayouter"/>'s flex dispatch
/// sites (outer, recursive, forced-overflow re-route); cycle 4d
/// consolidated them. These tests pin the math contract directly
/// so any future change surfaces here without depending on the
/// dispatch sites.
/// </summary>
public sealed class FlexGeometryHelperTests
{
    [Fact]
    public void Zero_chrome_returns_unchanged_content_box()
    {
        // No borders, no padding → content box equals border box.
        var box = BuildFlexContainer();
        var geom = FlexGeometryHelper.ComputeContentGeometry(
            flexBox: box,
            borderBoxInlineSize: 600,
            borderBoxBlockSize: 400,
            borderBoxInlineOffset: 10,
            borderBoxBlockOffset: 20);

        Assert.Equal(600.0, geom.ContentInlineSize, precision: 3);
        Assert.Equal(400.0, geom.ContentBlockSize, precision: 3);
        Assert.Equal(10.0, geom.ContentInlineOffset, precision: 3);
        Assert.Equal(20.0, geom.ContentBlockOffset, precision: 3);
    }

    [Fact]
    public void Borders_and_padding_subtract_from_size_and_add_to_offset()
    {
        // borders 10/10/10/10 + padding 5/5/5/5 on a 600×400
        // border box → content = 570×370 at offset +15/+15.
        var box = BuildFlexContainer();
        SolidBorders(box.Style);
        SetLengthPx(box.Style, PropertyId.BorderTopWidth, 10);
        SetLengthPx(box.Style, PropertyId.BorderBottomWidth, 10);
        SetLengthPx(box.Style, PropertyId.BorderLeftWidth, 10);
        SetLengthPx(box.Style, PropertyId.BorderRightWidth, 10);
        SetLengthPx(box.Style, PropertyId.PaddingTop, 5);
        SetLengthPx(box.Style, PropertyId.PaddingBottom, 5);
        SetLengthPx(box.Style, PropertyId.PaddingLeft, 5);
        SetLengthPx(box.Style, PropertyId.PaddingRight, 5);

        var geom = FlexGeometryHelper.ComputeContentGeometry(
            flexBox: box,
            borderBoxInlineSize: 600,
            borderBoxBlockSize: 400,
            borderBoxInlineOffset: 0,
            borderBoxBlockOffset: 0);

        // ContentInlineSize = 600 - (10+5+5+10) = 570
        Assert.Equal(570.0, geom.ContentInlineSize, precision: 3);
        // ContentBlockSize = 400 - (10+5+5+10) = 370
        Assert.Equal(370.0, geom.ContentBlockSize, precision: 3);
        // ContentInlineOffset = 0 + 10 (border-left) + 5 (padding-left) = 15
        Assert.Equal(15.0, geom.ContentInlineOffset, precision: 3);
        // ContentBlockOffset = 0 + 10 (border-top) + 5 (padding-top) = 15
        Assert.Equal(15.0, geom.ContentBlockOffset, precision: 3);
    }

    [Fact]
    public void Chrome_exceeding_border_box_clamps_content_size_to_1_pixel_floor()
    {
        // Pathological case: borders/padding sum to MORE than the
        // border box size. The helper's 1-px floor defends against
        // FlexLayouter.ConfigureEmission's non-positive-size
        // ArgumentOutOfRangeException. The content offset still
        // honors the chrome (offset = start_border + start_padding).
        var box = BuildFlexContainer();
        SolidBorders(box.Style);
        SetLengthPx(box.Style, PropertyId.BorderLeftWidth, 50);
        SetLengthPx(box.Style, PropertyId.BorderRightWidth, 50);

        var geom = FlexGeometryHelper.ComputeContentGeometry(
            flexBox: box,
            borderBoxInlineSize: 80,  // < 50+50 = 100 chrome
            borderBoxBlockSize: 400,
            borderBoxInlineOffset: 0,
            borderBoxBlockOffset: 0);

        // 80 - 100 = -20 → clamped to 1.
        Assert.Equal(1.0, geom.ContentInlineSize, precision: 3);
        // Block has no chrome → 400.
        Assert.Equal(400.0, geom.ContentBlockSize, precision: 3);
        // Offset honors border-left even though content is clamped.
        Assert.Equal(50.0, geom.ContentInlineOffset, precision: 3);
    }

    [Fact]
    public void Asymmetric_borders_and_padding_compute_correctly()
    {
        // Different border/padding on each side → exact math
        // verification. border-top: 5, border-bottom: 10,
        // border-left: 3, border-right: 7. padding-top: 2,
        // padding-bottom: 4, padding-left: 1, padding-right: 6.
        var box = BuildFlexContainer();
        SolidBorders(box.Style);
        SetLengthPx(box.Style, PropertyId.BorderTopWidth, 5);
        SetLengthPx(box.Style, PropertyId.BorderBottomWidth, 10);
        SetLengthPx(box.Style, PropertyId.BorderLeftWidth, 3);
        SetLengthPx(box.Style, PropertyId.BorderRightWidth, 7);
        SetLengthPx(box.Style, PropertyId.PaddingTop, 2);
        SetLengthPx(box.Style, PropertyId.PaddingBottom, 4);
        SetLengthPx(box.Style, PropertyId.PaddingLeft, 1);
        SetLengthPx(box.Style, PropertyId.PaddingRight, 6);

        var geom = FlexGeometryHelper.ComputeContentGeometry(
            flexBox: box,
            borderBoxInlineSize: 100,
            borderBoxBlockSize: 100,
            borderBoxInlineOffset: 100,
            borderBoxBlockOffset: 200);

        // ContentInlineSize = 100 - 3 - 1 - 6 - 7 = 83
        Assert.Equal(83.0, geom.ContentInlineSize, precision: 3);
        // ContentBlockSize = 100 - 5 - 2 - 4 - 10 = 79
        Assert.Equal(79.0, geom.ContentBlockSize, precision: 3);
        // ContentInlineOffset = 100 + 3 (border-left) + 1 (padding-left) = 104
        Assert.Equal(104.0, geom.ContentInlineOffset, precision: 3);
        // ContentBlockOffset = 200 + 5 (border-top) + 2 (padding-top) = 207
        Assert.Equal(207.0, geom.ContentBlockOffset, precision: 3);
    }

    // ====================================================================
    //  Test helpers
    // ====================================================================

    private static Box BuildFlexContainer()
    {
        var style = MakeStyle();
        return Box.ForElement(BoxKind.FlexContainer, style, MakeElement());
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, PropertyId id, double px) =>
        style.Set(id, ComputedSlot.FromLengthPx(px));

    // Per CSS Backgrounds & Borders 3 §4.3 the used border-width is 0 unless the
    // matching border-style is a visible value, so box-model tests that set a
    // synthetic border-*-width must also declare a style for it to contribute.
    private static void SolidBorders(ComputedStyle style)
    {
        style.Set(PropertyId.BorderTopStyle, ComputedSlot.FromKeyword(4));    // 4 = solid
        style.Set(PropertyId.BorderRightStyle, ComputedSlot.FromKeyword(4));
        style.Set(PropertyId.BorderBottomStyle, ComputedSlot.FromKeyword(4));
        style.Set(PropertyId.BorderLeftStyle, ComputedSlot.FromKeyword(4));
    }

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
