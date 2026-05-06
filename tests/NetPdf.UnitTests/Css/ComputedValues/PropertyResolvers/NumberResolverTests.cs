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
    [InlineData("-2.5", -2.5)]
    [InlineData("0.001", 0.001)]
    [InlineData("100", 100.0)]
    public void Number_round_trips(string input, double expected)
    {
        var slot = NumberResolver.ResolveNumber(input, PropertyId.FlexGrow, "flex-grow", null, default);
        Assert.Equal(ComputedSlotTag.Number, slot.Tag);
        Assert.Equal(expected, slot.AsNumber(), 5);
    }

    [Theory]
    [InlineData("16px")]
    [InlineData("1em")]
    [InlineData("foo")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Number_with_unit_or_garbage_emits_diagnostic(string input)
    {
        var sink = new CapturingSink();
        var slot = NumberResolver.ResolveNumber(input, PropertyId.FlexGrow, "flex-grow", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        if (input.Length > 0)
            Assert.Single(sink.Diagnostics);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("-5", -5)]
    [InlineData("+10", 10)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void Integer_round_trips(string input, int expected)
    {
        var slot = NumberResolver.ResolveInteger(input, PropertyId.FlexGrow, "z-index", null, default);
        Assert.Equal(ComputedSlotTag.Integer, slot.Tag);
        Assert.Equal(expected, slot.AsInteger());
    }

    [Theory]
    [InlineData("1.5")]    // decimal — not an integer
    [InlineData("1e3")]    // exponent — not an integer
    [InlineData("16px")]
    [InlineData("foo")]
    public void Integer_with_decimal_or_unit_emits_diagnostic(string input)
    {
        var sink = new CapturingSink();
        var slot = NumberResolver.ResolveInteger(input, PropertyId.FlexGrow, "z-index", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Single(sink.Diagnostics);
    }
}
