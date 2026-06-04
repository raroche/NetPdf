// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="BorderShorthandExpander"/> — Phase 3 Task 21, expanding the
/// <c>border</c> / <c>border-&lt;side&gt;</c> shorthands for <c>@page</c> margin-box bodies into the
/// <c>border-*-width</c> / <c>-style</c> / <c>-color</c> longhands NetPdf consumes.
/// </summary>
public sealed class BorderShorthandExpanderTests
{
    private static Dictionary<string, string> Expand(string property, string value)
    {
        Assert.True(BorderShorthandExpander.TryExpand(property, value, out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        return map;
    }

    [Fact]
    public void Border_expands_to_all_four_edges()
    {
        var d = Expand("border", "1px solid red");
        Assert.Equal(12, d.Count);
        foreach (var edge in new[] { "top", "right", "bottom", "left" })
        {
            Assert.Equal("1px", d[$"border-{edge}-width"]);
            Assert.Equal("solid", d[$"border-{edge}-style"]);
            Assert.Equal("red", d[$"border-{edge}-color"]);
        }
    }

    [Theory]
    [InlineData("border-top")]
    [InlineData("border-right")]
    [InlineData("border-bottom")]
    [InlineData("border-left")]
    public void Border_side_expands_to_one_edge(string property)
    {
        var d = Expand(property, "2px dashed blue");
        Assert.Equal(3, d.Count);
        Assert.Equal("2px", d[$"{property}-width"]);
        Assert.Equal("dashed", d[$"{property}-style"]);
        Assert.Equal("blue", d[$"{property}-color"]);
    }

    [Fact]
    public void Border_omitted_components_reset_to_initial()
    {
        // `border: solid` → width = medium, color = currentcolor (the §4.3 initial values).
        var d = Expand("border", "solid");
        Assert.Equal("medium", d["border-top-width"]);
        Assert.Equal("solid", d["border-top-style"]);
        Assert.Equal("currentcolor", d["border-top-color"]);
    }

    [Fact]
    public void Border_components_are_order_independent()
    {
        var d = Expand("border", "red 1px solid");
        Assert.Equal("1px", d["border-top-width"]);
        Assert.Equal("solid", d["border-top-style"]);
        Assert.Equal("red", d["border-top-color"]);
    }

    [Fact]
    public void Border_keeps_a_functional_color_token_intact()
    {
        // Paren-aware tokenization: rgb(0, 128, 0) stays one token despite its spaces/commas.
        var d = Expand("border", "thick double rgb(0, 128, 0)");
        Assert.Equal("thick", d["border-top-width"]);
        Assert.Equal("double", d["border-top-style"]);
        Assert.Equal("rgb(0, 128, 0)", d["border-top-color"]);
    }

    [Theory]
    [InlineData("border", "1px 2px solid red")]    // two widths
    [InlineData("border", "solid red blue")]        // two colors
    [InlineData("border", "solid dashed 1px red")]  // two styles
    [InlineData("border", "1bananas solid red")]    // invalid width unit → atomic reject via dispatch
    [InlineData("border", "")]
    [InlineData("border-top", "1px solid red blue")]
    public void Border_rejects_malformed(string property, string value)
    {
        Assert.False(BorderShorthandExpander.TryExpand(property, value, out _));
    }

    [Fact]
    public void IsBorderShorthand_recognizes_the_handled_forms()
    {
        Assert.True(BorderShorthandExpander.IsBorderShorthand("border"));
        Assert.True(BorderShorthandExpander.IsBorderShorthand("border-top"));
        Assert.True(BorderShorthandExpander.IsBorderShorthand("BORDER-LEFT"));   // case-insensitive
        Assert.False(BorderShorthandExpander.IsBorderShorthand("border-top-width")); // a longhand
        Assert.False(BorderShorthandExpander.IsBorderShorthand("border-width"));      // deferred box shorthand
    }
}
