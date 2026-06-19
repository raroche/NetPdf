// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// line-height cycle — <c>ComputedStyle.ReadLineHeightPx</c> decodes the computed grammar that
/// <c>LineHeightResolver</c> produces into the USED px the layouters + painter consume: a length →
/// that px, a number → number × the element's font-size, a percentage → % of font-size, and
/// <c>normal</c> / unset → 0 (the font-size × 1.2 fallback sentinel).
/// </summary>
public sealed class LineHeightReaderTests
{
    private static ComputedStyle Style() => ComputedStyle.RentForExclusiveTesting();

    [Fact]
    public void Unset_returns_zero_sentinel()
    {
        // 0 = the `normal` sentinel — the caller falls back to font-size × 1.2.
        Assert.Equal(0.0, Style().ReadLineHeightPx(16), precision: 4);
    }

    [Fact]
    public void Normal_keyword_returns_zero_sentinel()
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromKeyword(0));   // normal
        Assert.Equal(0.0, s.ReadLineHeightPx(16), precision: 4);
    }

    [Fact]
    public void Length_slot_returns_the_px_independent_of_font_size()
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(24));
        Assert.Equal(24.0, s.ReadLineHeightPx(16), precision: 4);
        Assert.Equal(24.0, s.ReadLineHeightPx(40), precision: 4);   // a length doesn't scale with font-size
    }

    [Theory]
    [InlineData(1.5, 16, 24.0)]
    [InlineData(2.0, 20, 40.0)]
    [InlineData(1.0, 32, 32.0)]
    public void Number_slot_multiplies_the_font_size(double number, double fontSizePx, double expected)
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromNumber(number));
        Assert.Equal(expected, s.ReadLineHeightPx(fontSizePx), precision: 4);
    }

    [Fact]
    public void Percentage_slot_is_a_fraction_of_font_size()
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromPercentage(150));
        Assert.Equal(24.0, s.ReadLineHeightPx(16), precision: 4);   // 150% × 16
    }
}
