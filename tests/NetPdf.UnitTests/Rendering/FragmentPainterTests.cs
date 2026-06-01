// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Unit tests for the <see cref="FragmentPainter"/> pure helpers — the CSS-px →
/// PDF-pt transform (scale + y-flip), color-slot resolution (incl.
/// <c>currentcolor</c>), and the 0xAARRGGBB channel split. The full
/// style-reading + emission path is covered end-to-end by the
/// <see cref="HtmlPdfConvertTests"/> integration tests.
/// </summary>
public sealed class FragmentPainterTests
{
    [Fact]
    public void ToPdfRect_scales_by_0_75_and_flips_y_for_a_top_left_box()
    {
        // 96 px = 72 pt. A box flush to the page top-left on an A4 page (842 pt tall)
        // lands flush to the PDF page's top — its lower edge sits 842 - 72 = 770 pt up.
        FragmentPainter.ToPdfRect(
            leftPx: 0, topPx: 0, widthPx: 96, heightPx: 96, pageHeightPt: 842,
            out var x, out var y, out var w, out var h);

        Assert.Equal(0.0, x, 5);
        Assert.Equal(72.0, w, 5);
        Assert.Equal(72.0, h, 5);
        Assert.Equal(770.0, y, 5);
    }

    [Fact]
    public void ToPdfRect_offsets_and_flips_an_interior_box()
    {
        // left=50px→37.5pt; w=96px→72pt; h=48px→36pt; y = 842 - (100px→75pt) - 36 = 731.
        FragmentPainter.ToPdfRect(
            leftPx: 50, topPx: 100, widthPx: 96, heightPx: 48, pageHeightPt: 842,
            out var x, out var y, out var w, out var h);

        Assert.Equal(37.5, x, 5);
        Assert.Equal(72.0, w, 5);
        Assert.Equal(36.0, h, 5);
        Assert.Equal(731.0, y, 5);
    }

    [Fact]
    public void TryResolveColor_returns_the_packed_value_for_a_color_slot()
    {
        var ok = FragmentPainter.TryResolveColor(
            ComputedSlot.FromColor(0xFF112233), currentColorArgb: 0xFFAABBCC, out var argb);

        Assert.True(ok);
        Assert.Equal(0xFF112233u, argb);
    }

    [Fact]
    public void TryResolveColor_substitutes_currentColor_for_the_currentcolor_sentinel()
    {
        var ok = FragmentPainter.TryResolveColor(
            ComputedSlot.CurrentColor, currentColorArgb: 0xFFAABBCC, out var argb);

        Assert.True(ok);
        Assert.Equal(0xFFAABBCCu, argb);
    }

    [Theory]
    [InlineData(false)] // a length slot is not a color
    [InlineData(true)]  // the unset sentinel is not a color
    public void TryResolveColor_returns_false_for_a_non_color_slot(bool useUnset)
    {
        var slot = useUnset ? ComputedSlot.Unset : ComputedSlot.FromLengthPx(5f);

        var ok = FragmentPainter.TryResolveColor(slot, currentColorArgb: 0xFF000000, out var argb);

        Assert.False(ok);
        Assert.Equal(0u, argb);
    }

    [Fact]
    public void ColorChannels_splits_argb_into_unit_rgb()
    {
        FragmentPainter.ColorChannels(0xFF80C0FF, out var r, out var g, out var b);

        Assert.Equal(0x80 / 255.0, r, 5);
        Assert.Equal(0xC0 / 255.0, g, 5);
        Assert.Equal(1.0, b, 5);
    }

    [Theory]
    [InlineData(0x00000000u, 0)]
    [InlineData(0xFF000000u, 255)]
    [InlineData(0x80123456u, 128)]
    public void Alpha_reads_the_high_byte(uint argb, int expected)
    {
        Assert.Equal(expected, FragmentPainter.Alpha(argb));
    }
}
