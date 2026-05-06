// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Unit tests for <see cref="LengthResolver"/> — covers the full dimension family
/// (Length / LengthPercentage / LengthPercentageAuto / Percentage / TextSpacing).
/// </summary>
public sealed class LengthResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // ============================================================
    // Absolute lengths fold to px per CSS Values L4 §6.1
    // ============================================================

    [Theory]
    [InlineData("16px", 16.0)]
    [InlineData("0", 0.0)]                  // bare zero is a valid length
    [InlineData("1in", 96.0)]
    [InlineData("2.54cm", 96.0)]            // 2.54cm = 1in = 96px
    [InlineData("25.4mm", 96.0)]
    [InlineData("72pt", 96.0)]              // 72pt = 1in
    [InlineData("1pc", 16.0)]               // 1pc = 12pt = 16px
    [InlineData("-32px", -32.0)]
    [InlineData("0.5px", 0.5)]
    public void Absolute_lengths_reduce_to_px(string input, double expectedPx)
    {
        var slot = LengthResolver.Resolve(input, PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", null, default);
        Assert.Equal(ComputedSlotTag.LengthPx, slot.Tag);
        Assert.Equal(expectedPx, slot.AsLengthPx(), 3);
    }

    [Fact]
    public void Q_unit_is_one_quarter_millimeter()
    {
        // 1Q = 0.25mm = (96 / 25.4 / 4) px ≈ 0.9449
        var slot = LengthResolver.Resolve("1q", PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", null, default);
        Assert.Equal(96.0 / 25.4 / 4.0, slot.AsLengthPx(), 5);
    }

    [Fact]
    public void Bare_nonzero_number_emits_diagnostic()
    {
        // CSS Values L4 §6.2: only `0` is allowed without a unit.
        var sink = new CapturingSink();
        var slot = LengthResolver.Resolve("16", PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    // ============================================================
    // Percentages
    // ============================================================

    [Theory]
    [InlineData("50%", 50.0)]
    [InlineData("0%", 0.0)]
    [InlineData("100%", 100.0)]
    [InlineData("-25%", -25.0)]
    [InlineData("33.33%", 33.33)]
    public void Percentage_on_LengthPercentage_reduces(string input, double expectedPct)
    {
        var slot = LengthResolver.Resolve(input, PropertyType.LengthPercentage, PropertyId.PaddingTop,
            "padding-top", null, default);
        Assert.Equal(ComputedSlotTag.Percentage, slot.Tag);
        Assert.Equal(expectedPct, slot.AsPercentage(), 2);
    }

    [Fact]
    public void Percentage_on_Length_emits_diagnostic()
    {
        var sink = new CapturingSink();
        var slot = LengthResolver.Resolve("50%", PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Percentage_on_LengthPercentageAuto_reduces()
    {
        var slot = LengthResolver.Resolve("75%", PropertyType.LengthPercentageAuto, PropertyId.Width,
            "width", null, default);
        Assert.Equal(ComputedSlotTag.Percentage, slot.Tag);
        Assert.Equal(75.0, slot.AsPercentage(), 2);
    }

    // ============================================================
    // Auto / normal keywords (LengthPercentageAuto / TextSpacing)
    // ============================================================

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void Auto_on_LengthPercentageAuto_reduces_to_keyword(string input)
    {
        var slot = LengthResolver.Resolve(input, PropertyType.LengthPercentageAuto, PropertyId.Width,
            "width", null, default);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
        Assert.Equal(LengthResolver.KeywordIdAuto, slot.AsKeyword());
    }

    [Fact]
    public void Auto_on_LengthPercentage_emits_diagnostic()
    {
        // padding-top doesn't accept `auto` (it's LengthPercentage, not LengthPercentageAuto).
        var sink = new CapturingSink();
        var slot = LengthResolver.Resolve("auto", PropertyType.LengthPercentage, PropertyId.PaddingTop,
            "padding-top", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("Normal")]
    public void Normal_on_TextSpacing_reduces_to_keyword(string input)
    {
        var slot = LengthResolver.Resolve(input, PropertyType.TextSpacing, PropertyId.LetterSpacing,
            "letter-spacing", null, default);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
    }

    // ============================================================
    // Font-relative / viewport / container units defer (no diagnostic)
    // ============================================================

    [Theory]
    [InlineData("2em")]
    [InlineData("1.5rem")]
    [InlineData("2ch")]
    [InlineData("1ex")]
    [InlineData("2lh")]
    [InlineData("1rlh")]
    [InlineData("2cap")]
    [InlineData("1ic")]
    [InlineData("50vw")]
    [InlineData("100vh")]
    [InlineData("10svw")]
    [InlineData("10lvw")]
    [InlineData("10dvw")]
    [InlineData("10vmin")]
    [InlineData("10vmax")]
    [InlineData("10cqw")]
    [InlineData("10cqh")]
    [InlineData("10cqi")]
    [InlineData("10cqb")]
    [InlineData("10cqmin")]
    [InlineData("10cqmax")]
    public void Context_relative_units_defer_with_no_diagnostic(string input)
    {
        var sink = new CapturingSink();
        var slot = LengthResolver.Resolve(input, PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Empty(sink.Diagnostics);
    }

    // ============================================================
    // Garbage rejected with diagnostic
    // ============================================================

    [Theory]
    [InlineData("nonsense")]
    [InlineData("16xpx")]
    [InlineData("px")]
    [InlineData("16px16px")]      // two values
    [InlineData("16fooz")]
    public void Garbage_emits_diagnostic_and_returns_unset(string input)
    {
        var sink = new CapturingSink();
        var slot = LengthResolver.Resolve(input, PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Single(sink.Diagnostics);
    }
}
