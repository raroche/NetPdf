// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="BorderBoxShorthandExpander"/> — Phase 3 Task 21, expanding the
/// <c>border-width</c> / <c>border-style</c> / <c>border-color</c> 1–4-value box shorthands for
/// <c>@page</c> margin-box bodies into their per-edge longhands.
/// </summary>
public sealed class BorderBoxShorthandExpanderTests
{
    private static Dictionary<string, string> Expand(string property, string value)
    {
        Assert.True(BorderBoxShorthandExpander.TryExpand(property, value, out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        return map;
    }

    [Fact]
    public void Border_width_one_value_applies_to_all_four_edges()
    {
        var d = Expand("border-width", "2px");
        Assert.Equal(4, d.Count);
        foreach (var edge in new[] { "top", "right", "bottom", "left" })
            Assert.Equal("2px", d[$"border-{edge}-width"]);
    }

    [Fact]
    public void Border_style_four_values_are_top_right_bottom_left()
    {
        var d = Expand("border-style", "solid dashed dotted double");
        Assert.Equal("solid", d["border-top-style"]);
        Assert.Equal("dashed", d["border-right-style"]);
        Assert.Equal("dotted", d["border-bottom-style"]);
        Assert.Equal("double", d["border-left-style"]);
    }

    [Fact]
    public void Border_width_two_values_are_vertical_then_horizontal()
    {
        var d = Expand("border-width", "1px 3px");
        Assert.Equal("1px", d["border-top-width"]);
        Assert.Equal("3px", d["border-right-width"]);
        Assert.Equal("1px", d["border-bottom-width"]);
        Assert.Equal("3px", d["border-left-width"]);
    }

    [Fact]
    public void Border_color_three_values_are_top_horizontal_bottom()
    {
        var d = Expand("border-color", "red green blue");
        Assert.Equal("red", d["border-top-color"]);
        Assert.Equal("green", d["border-right-color"]);
        Assert.Equal("blue", d["border-bottom-color"]);
        Assert.Equal("green", d["border-left-color"]);   // 3-value: left mirrors right
    }

    [Fact]
    public void Border_color_keeps_a_functional_color_token_intact()
    {
        // Paren-aware tokenization: rgb(0, 128, 0) stays one token in a multi-value box list.
        var d = Expand("border-color", "red rgb(0, 128, 0)");
        Assert.Equal("red", d["border-top-color"]);
        Assert.Equal("rgb(0, 128, 0)", d["border-right-color"]);
        Assert.Equal("red", d["border-bottom-color"]);
        Assert.Equal("rgb(0, 128, 0)", d["border-left-color"]);
    }

    [Fact]
    public void Border_style_strips_block_comments()
    {
        var d = Expand("border-style", "solid /* c */ dashed");
        Assert.Equal("solid", d["border-top-style"]);
        Assert.Equal("dashed", d["border-right-style"]);
    }

    [Theory]
    [InlineData("border-width", "width", "inherit")]
    [InlineData("border-width", "width", "unset")]
    [InlineData("border-style", "style", "initial")]
    [InlineData("border-style", "style", "revert")]
    [InlineData("border-color", "color", "inherit")]
    [InlineData("border-color", "color", "unset")]
    public void Border_box_css_wide_keyword_applies_to_every_longhand(string property, string suffix, string keyword)
    {
        // A whole-value CSS-wide keyword maps to all four per-edge longhands of the property — across
        // all three box shorthands (width / style / color).
        var d = Expand(property, keyword);
        Assert.Equal(4, d.Count);
        foreach (var edge in new[] { "top", "right", "bottom", "left" })
            Assert.Equal(keyword, d[$"border-{edge}-{suffix}"]);
    }

    [Theory]
    [InlineData("border-width", "1bananas")]            // a bad width unit
    [InlineData("border-width", "1px 2px 3px 4px 5px")]  // five values
    [InlineData("border-style", "solid wavy")]           // `wavy` isn't a <line-style>
    [InlineData("border-color", "red notacolor")]
    [InlineData("border-width", "")]
    public void Rejects_malformed(string property, string value)
    {
        Assert.False(BorderBoxShorthandExpander.TryExpand(property, value, out _));
    }

    [Fact]
    public void IsBorderBoxShorthand_recognizes_only_the_three_box_shorthands()
    {
        Assert.True(BorderBoxShorthandExpander.IsBorderBoxShorthand("border-width"));
        Assert.True(BorderBoxShorthandExpander.IsBorderBoxShorthand("border-style"));
        Assert.True(BorderBoxShorthandExpander.IsBorderBoxShorthand("BORDER-COLOR"));  // case-insensitive
        Assert.False(BorderBoxShorthandExpander.IsBorderBoxShorthand("border"));         // the other expander
        Assert.False(BorderBoxShorthandExpander.IsBorderBoxShorthand("border-top"));     // a per-side shorthand
        Assert.False(BorderBoxShorthandExpander.IsBorderBoxShorthand("border-top-width")); // a longhand
    }
}
