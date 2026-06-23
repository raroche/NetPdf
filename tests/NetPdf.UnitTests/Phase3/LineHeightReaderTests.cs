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
/// <c>normal</c> / unset → <see langword="null"/> (the caller's font-size × 1.2 fallback). An EXPLICIT
/// <c>0</c> / <c>0px</c> / <c>0%</c> returns <c>0.0</c> — distinct from <c>normal</c> — so a collapsed
/// line box is honored (post-PR-#197 review P2).
/// </summary>
public sealed class LineHeightReaderTests
{
    private static ComputedStyle Style() => ComputedStyle.RentForExclusiveTesting();

    [Fact]
    public void Unset_returns_null_for_the_normal_fallback()
    {
        Assert.Null(Style().ReadLineHeightPx(16));
    }

    [Fact]
    public void Normal_keyword_returns_null_for_the_normal_fallback()
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromKeyword(0));   // normal
        Assert.Null(s.ReadLineHeightPx(16));
    }

    [Fact]
    public void Length_slot_returns_the_px_independent_of_font_size()
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(24));
        Assert.Equal(24.0, s.ReadLineHeightPx(16)!.Value, precision: 4);
        Assert.Equal(24.0, s.ReadLineHeightPx(40)!.Value, precision: 4);   // a length doesn't scale with font-size
    }

    [Theory]
    [InlineData(1.5, 16, 24.0)]
    [InlineData(2.0, 20, 40.0)]
    [InlineData(1.0, 32, 32.0)]
    public void Number_slot_multiplies_the_font_size(double number, double fontSizePx, double expected)
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromNumber(number));
        Assert.Equal(expected, s.ReadLineHeightPx(fontSizePx)!.Value, precision: 4);
    }

    [Fact]
    public void Percentage_slot_is_a_fraction_of_font_size()
    {
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromPercentage(150));
        Assert.Equal(24.0, s.ReadLineHeightPx(16)!.Value, precision: 4);   // 150% × 16
    }

    [Fact]
    public void Percentage_slot_scales_with_the_reading_font_size()
    {
        // The reader resolves a Percentage line-height slot against the font-size passed to it (% × font),
        // so the SAME 150% slot yields a different px per font-size. This is correct whenever a Percentage
        // slot is actually present — e.g. a defensive read or a path that hasn't converted it.
        // NOTE: a DECLARED `%` line-height no longer inherits as a Percentage across a font-size change in
        // the body OR margin-box paths — it is converted to a LENGTH at the DECLARING element's font-size
        // (BoxBuilder.ResolveDeclaredPercentLineHeightInPlace / MarginBoxStyle, CSS Inline 3 §4.2,
        // `line-height-percentage-inheritance` closed) before it inherits, so the inherited value is a
        // LengthPx, not a Percentage. This test exercises the reader's Percentage arithmetic itself.
        var s = Style();
        s.Set(PropertyId.LineHeight, ComputedSlot.FromPercentage(150));
        Assert.Equal(24.0, s.ReadLineHeightPx(16)!.Value, precision: 4);   // 150% × 16
        Assert.Equal(60.0, s.ReadLineHeightPx(40)!.Value, precision: 4);   // 150% × 40
    }

    [Fact]
    public void Explicit_zero_is_distinct_from_normal_returning_0_not_null()
    {
        // Post-PR-#197 review P2 — a valid `line-height: 0` / `0px` / `0%` must collapse the line box,
        // NOT silently fall back to font-size × 1.2. The reader returns 0.0 (non-null) for all three forms.
        var asLength = Style(); asLength.Set(PropertyId.LineHeight, ComputedSlot.FromLengthPx(0));
        var asNumber = Style(); asNumber.Set(PropertyId.LineHeight, ComputedSlot.FromNumber(0));
        var asPercent = Style(); asPercent.Set(PropertyId.LineHeight, ComputedSlot.FromPercentage(0));

        Assert.Equal(0.0, asLength.ReadLineHeightPx(16)!.Value, precision: 4);
        Assert.Equal(0.0, asNumber.ReadLineHeightPx(16)!.Value, precision: 4);
        Assert.Equal(0.0, asPercent.ReadLineHeightPx(16)!.Value, precision: 4);
        // ... and all three are NON-null (a numeric sentinel couldn't tell explicit 0 from the default).
        Assert.NotNull(asLength.ReadLineHeightPx(16));
        Assert.NotNull(asNumber.ReadLineHeightPx(16));
        Assert.NotNull(asPercent.ReadLineHeightPx(16));
    }
}
