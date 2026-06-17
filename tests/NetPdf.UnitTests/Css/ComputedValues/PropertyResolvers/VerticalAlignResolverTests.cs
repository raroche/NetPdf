// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// vertical-align cycle — unit tests for <see cref="VerticalAlignResolver"/> (CSS 2.2 §10.8.1
/// <c>vertical-align</c>: <c>baseline | sub | super | text-top | text-bottom | middle | top | bottom |
/// &lt;percentage&gt; | &lt;length&gt;</c>). The keyword indices are the shared contract with the
/// inline-atomic placement consumer (<c>BlockLayouter.ComputeInlineAtomicLayout</c>).
/// </summary>
public sealed class VerticalAlignResolverTests
{
    [Theory]
    [InlineData("baseline", 0)]
    [InlineData("sub", 1)]
    [InlineData("super", 2)]
    [InlineData("text-top", 3)]
    [InlineData("text-bottom", 4)]
    [InlineData("middle", 5)]
    [InlineData("top", 6)]
    [InlineData("bottom", 7)]
    [InlineData("BASELINE", 0)]   // keywords are case-insensitive
    [InlineData("Middle", 5)]
    public void Keywords_resolve_to_their_index(string value, int expectedIndex)
    {
        var result = VerticalAlignResolver.Resolve(value, PropertyId.VerticalAlign, "vertical-align", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.Equal(expectedIndex, result.Slot.AsKeyword());
    }

    [Theory]
    [InlineData("5px", 5.0)]
    [InlineData("-3px", -3.0)]   // vertical-align lengths MAY be negative (not a non-negative property)
    [InlineData("0", 0.0)]
    public void Lengths_resolve_to_px(string value, double expectedPx)
    {
        var result = VerticalAlignResolver.Resolve(value, PropertyId.VerticalAlign, "vertical-align", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, result.Slot.Tag);
        Assert.Equal(expectedPx, result.Slot.AsLengthPx(), 3);
    }

    [Fact]
    public void Percentage_resolves_to_a_percentage_slot()
    {
        // A vertical-align percentage is relative to the line-height — kept as a Percentage slot.
        var result = VerticalAlignResolver.Resolve("50%", PropertyId.VerticalAlign, "vertical-align", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Percentage, result.Slot.Tag);
        Assert.Equal(50.0, result.Slot.AsPercentage(), 3);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("centre")]   // a near-miss of a real keyword is still invalid
    public void Invalid_values_do_not_resolve(string value)
    {
        var result = VerticalAlignResolver.Resolve(value, PropertyId.VerticalAlign, "vertical-align", null, default);
        Assert.True(result.IsInvalid);
    }

    [Fact]
    public void Dispatch_routes_vertical_align_through_the_resolver()
    {
        // The property is no longer UnsupportedUnvalidated — the dispatch routes its VerticalAlign type
        // to the resolver, so a keyword lands as a Keyword slot (was a silent raw passthrough).
        var result = PropertyResolverDispatch.Resolve(PropertyId.VerticalAlign, "middle");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.Equal(5, result.Slot.AsKeyword());
    }
}
