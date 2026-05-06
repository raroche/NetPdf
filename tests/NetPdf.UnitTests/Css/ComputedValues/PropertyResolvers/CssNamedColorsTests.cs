// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues.PropertyResolvers;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

public sealed class CssNamedColorsTests
{
    [Theory]
    [InlineData("red",          0xFFFF0000u)]
    [InlineData("Red",          0xFFFF0000u)]
    [InlineData("RED",          0xFFFF0000u)]
    [InlineData("white",        0xFFFFFFFFu)]
    [InlineData("black",        0xFF000000u)]
    [InlineData("blue",         0xFF0000FFu)]
    [InlineData("rebeccapurple",0xFF663399u)]
    [InlineData("aqua",         0xFF00FFFFu)]
    [InlineData("cyan",         0xFF00FFFFu)]   // alias
    [InlineData("fuchsia",      0xFFFF00FFu)]
    [InlineData("magenta",      0xFFFF00FFu)]   // alias
    [InlineData("gray",         0xFF808080u)]
    [InlineData("grey",         0xFF808080u)]   // alias
    [InlineData("darkgray",     0xFFA9A9A9u)]
    [InlineData("darkgrey",     0xFFA9A9A9u)]   // alias
    public void Known_named_colors_resolve(string name, uint expected)
    {
        Assert.True(CssNamedColors.TryGet(name, out var argb));
        Assert.Equal(expected, argb);
    }

    [Theory]
    [InlineData("not-a-color")]
    [InlineData("redish")]
    [InlineData("")]
    [InlineData("currentcolor")]      // CSS-wide, NOT in this table
    [InlineData("transparent")]       // CSS-wide, NOT in this table
    public void Unknown_or_excluded_names_return_false(string name) =>
        Assert.False(CssNamedColors.TryGet(name, out _));

    [Fact]
    public void All_named_colors_are_opaque()
    {
        // Sanity: every named color should have alpha = 0xFF (the spec defines them
        // all as opaque). Catches a future copy-paste error that drops the alpha bits.
        var samples = new[] { "red", "lime", "blue", "olive", "navy", "teal",
            "rebeccapurple", "darkslateblue", "lightcoral" };
        foreach (var name in samples)
        {
            Assert.True(CssNamedColors.TryGet(name, out var argb));
            Assert.Equal(0xFFu, (argb >> 24) & 0xFFu);
        }
    }
}
