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
/// (Length / LengthPercentage / LengthPercentageAuto / Percentage / TextSpacing)
/// against the cycle-1-review <see cref="ResolverResult"/> contract.
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
    [InlineData("-32px", -32.0)]            // top: -32px (positioning offset)
    [InlineData("0.5px", 0.5)]
    public void Absolute_lengths_reduce_to_px(string input, double expectedPx)
    {
        // Use Top — accepts negatives + LengthPercentageAuto.
        var result = LengthResolver.Resolve(input, PropertyType.LengthPercentageAuto, PropertyId.Top,
            "top", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, result.Slot.Tag);
        Assert.Equal(expectedPx, result.Slot.AsLengthPx(), 3);
    }

    [Fact]
    public void Q_unit_is_one_quarter_millimeter()
    {
        var result = LengthResolver.Resolve("1q", PropertyType.LengthPercentageAuto, PropertyId.Top,
            "top", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(96.0 / 25.4 / 4.0, result.Slot.AsLengthPx(), 5);
    }

    [Fact]
    public void Bare_nonzero_number_is_invalid()
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("16", PropertyType.LengthPercentageAuto, PropertyId.Top,
            "top", sink, default);
        Assert.True(result.IsInvalid);
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
    [InlineData("33.33%", 33.33)]
    public void Percentage_on_LengthPercentage_reduces(string input, double expectedPct)
    {
        var result = LengthResolver.Resolve(input, PropertyType.LengthPercentage, PropertyId.PaddingTop,
            "padding-top", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Percentage, result.Slot.Tag);
        Assert.Equal(expectedPct, result.Slot.AsPercentage(), 2);
    }

    [Fact]
    public void Percentage_on_Length_is_invalid()
    {
        // Synthesize a Length-only property by using the type. (No real Length-only
        // property in cycle 1's properties.json, but the resolver accepts the type.)
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("50%", PropertyType.Length, PropertyId.BorderTopWidth,
            "border-top-width", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Percentage_on_LengthPercentageAuto_reduces()
    {
        var result = LengthResolver.Resolve("75%", PropertyType.LengthPercentageAuto, PropertyId.Width,
            "width", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Percentage, result.Slot.Tag);
        Assert.Equal(75.0, result.Slot.AsPercentage(), 2);
    }

    // ============================================================
    // Auto / normal keywords
    // ============================================================

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void Auto_on_LengthPercentageAuto_reduces_to_keyword(string input)
    {
        var result = LengthResolver.Resolve(input, PropertyType.LengthPercentageAuto, PropertyId.Width,
            "width", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.Equal(LengthResolver.KeywordIdAuto, result.Slot.AsKeyword());
    }

    [Fact]
    public void Auto_on_LengthPercentage_is_invalid()
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("auto", PropertyType.LengthPercentage, PropertyId.PaddingTop,
            "padding-top", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Normal_on_TextSpacing_reduces_to_keyword()
    {
        var result = LengthResolver.Resolve("normal", PropertyType.TextSpacing, PropertyId.LetterSpacing,
            "letter-spacing", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
    }

    // ============================================================
    // Context-relative units defer (no diagnostic) — Rec 1 distinguishes
    // Deferred from Invalid/Resolved
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
    public void Context_relative_units_defer_with_raw_text(string input)
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve(input, PropertyType.LengthPercentageAuto, PropertyId.Width,
            "width", sink, default);
        Assert.True(result.IsDeferred);
        Assert.Equal(input, result.RawText);
        Assert.Empty(sink.Diagnostics);
    }

    // ============================================================
    // Garbage rejected with diagnostic
    // ============================================================

    [Theory]
    [InlineData("nonsense")]
    [InlineData("16xpx")]
    [InlineData("px")]
    [InlineData("16px16px")]
    [InlineData("16fooz")]
    public void Garbage_is_invalid(string input)
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve(input, PropertyType.LengthPercentageAuto, PropertyId.Width,
            "width", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
    }

    // ============================================================
    // Rec 4 — Non-negative properties reject negatives
    // ============================================================

    [Theory]
    [InlineData("padding-top",   "PaddingTop")]
    [InlineData("padding-right", "PaddingRight")]
    [InlineData("padding-bottom","PaddingBottom")]
    [InlineData("padding-left",  "PaddingLeft")]
    [InlineData("width",         "Width")]
    [InlineData("height",        "Height")]
    [InlineData("min-width",     "MinWidth")]
    [InlineData("min-height",    "MinHeight")]
    public void Non_negative_properties_reject_negative_length(string propertyName, string idName)
    {
        var pid = (PropertyId)System.Enum.Parse(typeof(PropertyId), idName);
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("-10px",
            propertyName.StartsWith("padding") ? PropertyType.LengthPercentage : PropertyType.LengthPercentageAuto,
            pid, propertyName, sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("negative", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_negative_properties_reject_negative_percentage()
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("-10%", PropertyType.LengthPercentage,
            PropertyId.PaddingTop, "padding-top", sink, default);
        Assert.True(result.IsInvalid);
    }

    // Per Phase 3 Task 15 L8 post-PR-#68 hardening F#3 — flex-basis
    // joined the non-negative set per CSS Flexbox L1 §7.2 (the
    // `<'width'>` reference brings in CSS Sizing §5's non-negative
    // rule). Pre-fix, `flex-basis: -10px` / `-10%` resolved
    // successfully (because FlexBasis joined the LengthResolver
    // dispatch in L8 without joining NonNegativeProperties), then
    // floored silently in layout. Now they invalidate at parse time
    // with CSS-PROPERTY-VALUE-INVALID-001 + the cascade falls back
    // to the property's initial value (`auto`).
    [Fact]
    public void Flex_basis_rejects_negative_length()
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("-10px", PropertyType.FlexBasis,
            PropertyId.FlexBasis, "flex-basis", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("negative", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_basis_rejects_negative_percentage()
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("-5%", PropertyType.FlexBasis,
            PropertyId.FlexBasis, "flex-basis", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("negative", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_basis_accepts_zero_length()
    {
        // Per CSS Flexbox §7.2 + Sizing §5 — zero is valid for
        // flex-basis (= the canonical `flex: 1 1 0` recipe). Sanity
        // check that the non-negative gate doesn't trip on exactly 0.
        var result = LengthResolver.Resolve("0px", PropertyType.FlexBasis,
            PropertyId.FlexBasis, "flex-basis", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(0.0, result.Slot.AsLengthPx(), 3);
    }

    [Theory]
    [InlineData("margin-top",    "MarginTop")]
    [InlineData("margin-bottom", "MarginBottom")]
    [InlineData("margin-left",   "MarginLeft")]
    [InlineData("margin-right",  "MarginRight")]
    [InlineData("top",           "Top")]
    [InlineData("right",         "Right")]
    [InlineData("bottom",        "Bottom")]
    [InlineData("left",          "Left")]
    public void Negative_allowed_properties_accept_negative_length(string propertyName, string idName)
    {
        var pid = (PropertyId)System.Enum.Parse(typeof(PropertyId), idName);
        var result = LengthResolver.Resolve("-10px", PropertyType.LengthPercentageAuto,
            pid, propertyName, null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(-10.0, result.Slot.AsLengthPx(), 3);
    }

    // ============================================================
    // Rec 6 — letter-spacing rejects %, word-spacing accepts it
    // ============================================================

    [Fact]
    public void Letter_spacing_rejects_percentage()
    {
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("5%", PropertyType.TextSpacing,
            PropertyId.LetterSpacing, "letter-spacing", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("letter-spacing", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Word_spacing_accepts_percentage()
    {
        var result = LengthResolver.Resolve("5%", PropertyType.TextSpacing,
            PropertyId.WordSpacing, "word-spacing", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Percentage, result.Slot.Tag);
        Assert.Equal(5.0, result.Slot.AsPercentage(), 2);
    }

    [Fact]
    public void Letter_spacing_accepts_negative_length()
    {
        // CSS Text 3 §10.1 — letter-spacing accepts negative lengths (tightens text).
        var result = LengthResolver.Resolve("-2px", PropertyType.TextSpacing,
            PropertyId.LetterSpacing, "letter-spacing", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(-2.0, result.Slot.AsLengthPx(), 3);
    }

    // ============================================================
    // Rec 8 — finite/range guards keep slot factories from throwing
    // ============================================================

    [Fact]
    public void Length_overflowing_float_range_is_invalid_not_throw()
    {
        // 1e308 in is past float.MaxValue when converted to px (× 96). The pre-check
        // catches it and emits a diagnostic instead of letting the slot factory throw.
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("1e308in", PropertyType.LengthPercentageAuto,
            PropertyId.Top, "top", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    // ============================================================
    // Phase 3 Task 14 cycle 1 hardening (Finding 3) — column-gap +
    // column-width non-negative (CSS Multi-column L1 §3.1 + §6.1)
    // ============================================================

    [Fact]
    public void Column_gap_negative_is_rejected()
    {
        // CSS Multi-column L1 §6.1 — column-gap admits non-negative
        // <length> | normal. A negative value falls back to the
        // initial keyword (normal) + emits CSS-PROPERTY-VALUE-INVALID-001.
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("-10px", PropertyType.Length,
            PropertyId.ColumnGap, "column-gap", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("negative", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Column_width_negative_is_rejected()
    {
        // CSS Multi-column L1 §3.1 — column-width admits non-negative
        // <length> | auto. A negative value falls back to auto + emits
        // CSS-PROPERTY-VALUE-INVALID-001.
        var sink = new CapturingSink();
        var result = LengthResolver.Resolve("-50px", PropertyType.Length,
            PropertyId.ColumnWidth, "column-width", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("negative", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Column_gap_zero_is_accepted()
    {
        // Zero is non-negative + a valid length per CSS Values L4 §6.2.
        var result = LengthResolver.Resolve("0px", PropertyType.Length,
            PropertyId.ColumnGap, "column-gap", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(0.0, result.Slot.AsLengthPx(), 3);
    }

    [Fact]
    public void Column_gap_positive_is_accepted()
    {
        var result = LengthResolver.Resolve("32px", PropertyType.Length,
            PropertyId.ColumnGap, "column-gap", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(32.0, result.Slot.AsLengthPx(), 3);
    }
}
