// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 5 layout→PDF cycle 3 — verifies the CSS Backgrounds &amp; Borders 3 §4.3
/// used-width style gate in <see cref="ComputedStyleLayoutExtensions.ReadLengthPxOrZero"/>:
/// a resolved <c>border-*-width</c> contributes to layout only when the matching
/// <c>border-*-style</c> is a visible value. This is what keeps resolving
/// <c>border-*-width</c> (initial <c>medium</c> = 3px) from growing every box.
/// </summary>
public sealed class BorderWidthStyleGateTests
{
    [Fact]
    public void Border_width_reads_zero_when_style_is_unset_default_none()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.BorderTopWidth, ComputedSlot.FromLengthPx(5));
        // border-top-style unset → defaults to `none`.
        Assert.Equal(0.0, style.ReadLengthPxOrZero(PropertyId.BorderTopWidth), 3);
    }

    [Theory]
    [InlineData(0)] // none
    [InlineData(1)] // hidden
    public void Border_width_reads_zero_for_none_or_hidden_style(int styleKeyword)
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.BorderLeftWidth, ComputedSlot.FromLengthPx(8));
        style.Set(PropertyId.BorderLeftStyle, ComputedSlot.FromKeyword(styleKeyword));
        Assert.Equal(0.0, style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth), 3);
    }

    [Theory]
    [InlineData(2)] // dotted
    [InlineData(4)] // solid
    [InlineData(5)] // double
    public void Border_width_contributes_for_a_visible_style(int styleKeyword)
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.BorderBottomWidth, ComputedSlot.FromLengthPx(6));
        style.Set(PropertyId.BorderBottomStyle, ComputedSlot.FromKeyword(styleKeyword));
        Assert.Equal(6.0, style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth), 3);
    }

    [Fact]
    public void Non_border_lengths_are_not_affected_by_the_gate()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.PaddingTop, ComputedSlot.FromLengthPx(7));
        // No border-style anywhere, but padding must still read its value.
        Assert.Equal(7.0, style.ReadLengthPxOrZero(PropertyId.PaddingTop), 3);
    }
}
