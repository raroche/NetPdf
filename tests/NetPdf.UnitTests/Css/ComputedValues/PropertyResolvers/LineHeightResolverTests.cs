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
/// line-height cycle — <see cref="LineHeightResolver"/> resolves the full
/// <c>normal | &lt;number&gt; | &lt;length&gt; | &lt;percentage&gt;</c> grammar. Pre-fix the dispatch
/// left <c>line-height</c> unwired, so every value fell through to UnsupportedUnvalidated and a declared
/// <c>line-height: 24px</c> never reached the computed style.
/// </summary>
public sealed class LineHeightResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static ResolverResult Resolve(string value, ICssDiagnosticsSink? sink = null)
        => LineHeightResolver.Resolve(value, PropertyId.LineHeight, "line-height", sink, default);

    [Fact]
    public void Normal_resolves_to_keyword_zero()
    {
        var r = Resolve("normal");
        Assert.True(r.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, r.Slot.Tag);
        Assert.Equal(0, r.Slot.AsKeyword());
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("2", 2.0)]
    [InlineData("0", 0.0)]       // line-height: 0 is valid
    [InlineData("1.25", 1.25)]
    public void Unitless_number_resolves_to_number_slot(string value, double expected)
    {
        var r = Resolve(value);
        Assert.True(r.IsResolved);
        Assert.Equal(ComputedSlotTag.Number, r.Slot.Tag);
        Assert.Equal(expected, r.Slot.AsNumber(), precision: 4);
    }

    [Theory]
    [InlineData("24px", 24.0)]
    [InlineData("18pt", 24.0)]   // 18pt = 24px (×4/3)
    public void Absolute_length_resolves_to_lengthpx(string value, double expectedPx)
    {
        var r = Resolve(value);
        Assert.True(r.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, r.Slot.Tag);
        Assert.Equal(expectedPx, r.Slot.AsLengthPx(), precision: 2);
    }

    [Fact]
    public void Percentage_resolves_to_percentage_slot()
    {
        var r = Resolve("150%");
        Assert.True(r.IsResolved);
        Assert.Equal(ComputedSlotTag.Percentage, r.Slot.Tag);
        Assert.Equal(150.0, r.Slot.AsPercentage(), precision: 3);
    }

    [Fact]
    public void Em_length_defers_for_the_box_builder()
    {
        // A font-relative length can't fold at cascade time (font-size context is the box-builder's) —
        // it stays Deferred raw for DeferredLengthResolver, which already lists PropertyId.LineHeight.
        var r = Resolve("2em");
        Assert.True(r.IsDeferred, $"expected 2em to defer; got {r.State}");
        Assert.Equal("2em", r.RawText);
    }

    [Fact]
    public void Negative_number_is_invalid_with_diagnostic()
    {
        var sink = new CapturingSink();
        var r = Resolve("-1.5", sink);
        Assert.True(r.IsInvalid);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    [Fact]
    public void Negative_length_is_invalid()
    {
        // line-height is a non-negative property (NonNegativeProperties) — LengthResolver rejects it.
        var r = Resolve("-10px", new CapturingSink());
        Assert.True(r.IsInvalid);
    }
}
