// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Phase 5 layout→PDF cycle 4 — unit tests for <see cref="FontWeightResolver"/>
/// (CSS Fonts 4 §2.2). Resolves to an integer slot; bolder/lighter defer + resolve
/// against the parent weight.
/// </summary>
public sealed class FontWeightResolverTests
{
    private static ResolverResult Resolve(string value) =>
        FontWeightResolver.Resolve(value, PropertyId.FontWeight, "font-weight", null, default);

    [Theory]
    [InlineData("normal", 400)]
    [InlineData("bold", 700)]
    [InlineData("100", 100)]
    [InlineData("350", 350)]
    [InlineData("1000", 1000)]
    [InlineData("BOLD", 700)]   // case-insensitive
    public void Resolves_to_integer(string value, int expected)
    {
        var result = Resolve(value);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Integer, result.Slot.Tag);
        Assert.Equal(expected, result.Slot.AsInteger());
    }

    [Theory]
    [InlineData("bolder")]
    [InlineData("lighter")]
    public void Relative_keywords_defer(string value)
    {
        var result = Resolve(value);
        Assert.True(result.IsDeferred);
        Assert.Equal(value, result.RawText);
    }

    [Theory]
    [InlineData("0")]       // below the [1,1000] range
    [InlineData("1001")]    // above
    [InlineData("heavy")]   // not a keyword
    public void Out_of_range_or_unknown_is_invalid(string value)
    {
        Assert.True(Resolve(value).IsInvalid);
    }

    [Theory]
    [InlineData("bolder", 100, 400)]
    [InlineData("bolder", 400, 700)]
    [InlineData("bolder", 600, 900)]
    [InlineData("lighter", 400, 100)]
    [InlineData("lighter", 600, 400)]
    [InlineData("lighter", 800, 700)]
    [InlineData("lighter", 900, 700)]
    public void TryResolveRelativeToParent_uses_the_range_table(string raw, int parent, int expected)
    {
        Assert.True(FontWeightResolver.TryResolveRelativeToParent(raw, parent, out var w));
        Assert.Equal(expected, w);
    }

    [Fact]
    public void Dispatch_routes_font_weight()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.FontWeight, "bold");
        Assert.True(result.IsResolved);
        Assert.Equal(700, result.Slot.AsInteger());
    }
}
