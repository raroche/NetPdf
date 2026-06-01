// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Phase 5 layout→PDF cycle 3 — unit tests for <see cref="LineWidthResolver"/>
/// (CSS Backgrounds &amp; Borders 3 §4.2 <c>&lt;line-width&gt;</c>:
/// <c>thin | medium | thick | &lt;length&gt;</c>).
/// </summary>
public sealed class LineWidthResolverTests
{
    [Theory]
    [InlineData("thin", 1.0)]
    [InlineData("medium", 3.0)]
    [InlineData("thick", 5.0)]
    [InlineData("THIN", 1.0)]   // keywords are case-insensitive
    [InlineData("Medium", 3.0)]
    public void Keywords_resolve_to_px(string value, double expectedPx)
    {
        var result = LineWidthResolver.Resolve(value, PropertyId.BorderTopWidth, "border-top-width", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, result.Slot.Tag);
        Assert.Equal(expectedPx, result.Slot.AsLengthPx(), 3);
    }

    [Theory]
    [InlineData("2px", 2.0)]
    [InlineData("0", 0.0)]
    [InlineData("0.5px", 0.5)]
    [InlineData("1pt", 96.0 / 72.0)]
    public void Lengths_resolve_to_px(string value, double expectedPx)
    {
        var result = LineWidthResolver.Resolve(value, PropertyId.BorderTopWidth, "border-top-width", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, result.Slot.Tag);
        Assert.Equal(expectedPx, result.Slot.AsLengthPx(), 3);
    }

    [Theory]
    [InlineData("-1px")]   // <line-width> is non-negative (§4.2)
    [InlineData("50%")]    // percentages are not admitted
    [InlineData("garbage")]
    public void Invalid_values_do_not_resolve(string value)
    {
        var result = LineWidthResolver.Resolve(value, PropertyId.BorderTopWidth, "border-top-width", null, default);
        Assert.True(result.IsInvalid);
    }

    [Fact]
    public void Dispatch_routes_border_width_through_the_resolver()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.BorderTopWidth, "medium");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, result.Slot.Tag);
        Assert.Equal(3.0, result.Slot.AsLengthPx(), 3);
    }
}
