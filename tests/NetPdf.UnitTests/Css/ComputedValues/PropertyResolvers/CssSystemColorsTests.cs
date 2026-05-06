// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues.PropertyResolvers;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Coverage for <see cref="CssSystemColors"/> (CSS Color L4 §10) — the print-friendly
/// fixed-value mapping used by Rec 2 so property defaults like <c>color: canvastext</c>
/// resolve cleanly through the cascade pipeline.
/// </summary>
public sealed class CssSystemColorsTests
{
    [Theory]
    [InlineData("canvas",         0xFFFFFFFFu)]   // paper
    [InlineData("canvastext",     0xFF000000u)]   // ink
    [InlineData("CanvasText",     0xFF000000u)]   // case-insensitive
    [InlineData("CANVASTEXT",     0xFF000000u)]
    [InlineData("linktext",       0xFF0000EEu)]
    [InlineData("visitedtext",    0xFF551A8Bu)]
    [InlineData("activetext",     0xFFEE0000u)]
    [InlineData("buttonface",     0xFFEFEFEFu)]
    [InlineData("buttontext",     0xFF000000u)]
    [InlineData("buttonborder",   0xFF808080u)]
    [InlineData("field",          0xFFFFFFFFu)]
    [InlineData("fieldtext",      0xFF000000u)]
    [InlineData("highlight",      0xFFB4D5FEu)]
    [InlineData("highlighttext",  0xFF000000u)]
    [InlineData("selecteditem",   0xFFB4D5FEu)]
    [InlineData("mark",           0xFFFFFF00u)]
    [InlineData("marktext",       0xFF000000u)]
    [InlineData("graytext",       0xFF808080u)]
    [InlineData("accentcolor",    0xFF0000EEu)]
    [InlineData("accentcolortext",0xFFFFFFFFu)]
    public void Known_system_colors_resolve(string name, uint expected)
    {
        Assert.True(CssSystemColors.TryGet(name, out var argb));
        Assert.Equal(expected, argb);
    }

    [Theory]
    [InlineData("canvas-extended")]
    [InlineData("notasystemcolor")]
    [InlineData("")]
    [InlineData("red")]   // named color, not system color
    public void Unknown_or_excluded_names_return_false(string name) =>
        Assert.False(CssSystemColors.TryGet(name, out _));

    [Fact]
    public void All_system_colors_are_opaque()
    {
        // Sanity: the print palette is fully opaque. Catches a future copy-paste
        // error that drops alpha to zero.
        var samples = new[] { "canvas", "canvastext", "linktext", "visitedtext",
            "activetext", "buttonface", "highlight", "mark", "graytext", "accentcolor" };
        foreach (var name in samples)
        {
            Assert.True(CssSystemColors.TryGet(name, out var argb));
            Assert.Equal(0xFFu, (argb >> 24) & 0xFFu);
        }
    }
}
