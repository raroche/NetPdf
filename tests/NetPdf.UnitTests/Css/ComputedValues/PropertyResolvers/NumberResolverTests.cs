// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

public sealed class NumberResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData("0.5", 0.5)]
    [InlineData("0.001", 0.001)]
    [InlineData("100", 100.0)]
    public void Number_round_trips(string input, double expected)
    {
        var result = NumberResolver.ResolveNumber(input, PropertyId.FlexGrow, "flex-grow", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Number, result.Slot.Tag);
        Assert.Equal(expected, result.Slot.AsNumber(), 5);
    }

    [Theory]
    [InlineData("16px")]
    [InlineData("1em")]
    [InlineData("foo")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Number_with_unit_or_garbage_is_invalid(string input)
    {
        var sink = new CapturingSink();
        var result = NumberResolver.ResolveNumber(input, PropertyId.FlexGrow, "flex-grow", sink, default);
        Assert.True(result.IsInvalid);
        if (input.Length > 0)
            Assert.Single(sink.Diagnostics);
    }

    // ============================================================
    // Rec 5 — flex-grow / flex-shrink reject negatives per Flexbox 1 §7.1
    // ============================================================

    [Theory]
    [InlineData("FlexGrow",   "flex-grow")]
    [InlineData("FlexShrink", "flex-shrink")]
    public void Flex_grow_and_shrink_reject_negative(string idName, string propName)
    {
        var pid = (PropertyId)System.Enum.Parse(typeof(PropertyId), idName);
        var sink = new CapturingSink();
        var result = NumberResolver.ResolveNumber("-2.5", pid, propName, sink, default);
        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d =>
            d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001 &&
            d.Message.Contains("negative", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flex_grow_accepts_zero()
    {
        var result = NumberResolver.ResolveNumber("0", PropertyId.FlexGrow, "flex-grow", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(0.0, result.Slot.AsNumber(), 5);
    }

    // ============================================================
    // Integer
    // ============================================================

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("-5", -5)]
    [InlineData("+10", 10)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void Integer_round_trips(string input, int expected)
    {
        // Use Top (negatives allowed — positioning offset). FlexGrow / FlexShrink
        // are in NonNegativeProperties so they'd reject negative integers.
        var result = NumberResolver.ResolveInteger(input, PropertyId.Top, "z-index", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Integer, result.Slot.Tag);
        Assert.Equal(expected, result.Slot.AsInteger());
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("16px")]
    [InlineData("foo")]
    public void Integer_with_decimal_or_unit_is_invalid(string input)
    {
        var sink = new CapturingSink();
        var result = NumberResolver.ResolveInteger(input, PropertyId.Top, "z-index", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
    }
}
