// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="PaddingShorthandExpander"/> — Phase 3 Task 21, expanding the
/// <c>padding</c> 1–4-value box shorthand for <c>@page</c> margin-box bodies into the
/// <c>padding-top</c> / <c>-right</c> / <c>-bottom</c> / <c>-left</c> longhands NetPdf consumes.
/// </summary>
public sealed class PaddingShorthandExpanderTests
{
    private static Dictionary<string, string> Expand(string value)
    {
        Assert.True(PaddingShorthandExpander.TryExpand("padding", value, out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        return map;
    }

    [Fact]
    public void One_value_applies_to_all_four_sides()
    {
        var d = Expand("5px");
        Assert.Equal(4, d.Count);
        foreach (var side in new[] { "top", "right", "bottom", "left" })
            Assert.Equal("5px", d[$"padding-{side}"]);
    }

    [Fact]
    public void Two_values_are_vertical_then_horizontal()
    {
        var d = Expand("1px 2px");
        Assert.Equal("1px", d["padding-top"]);
        Assert.Equal("2px", d["padding-right"]);
        Assert.Equal("1px", d["padding-bottom"]);
        Assert.Equal("2px", d["padding-left"]);
    }

    [Fact]
    public void Three_values_are_top_horizontal_bottom()
    {
        var d = Expand("1px 2px 3px");
        Assert.Equal("1px", d["padding-top"]);
        Assert.Equal("2px", d["padding-right"]);
        Assert.Equal("3px", d["padding-bottom"]);
        Assert.Equal("2px", d["padding-left"]);
    }

    [Fact]
    public void Four_values_are_top_right_bottom_left()
    {
        var d = Expand("1px 2px 3px 4px");
        Assert.Equal("1px", d["padding-top"]);
        Assert.Equal("2px", d["padding-right"]);
        Assert.Equal("3px", d["padding-bottom"]);
        Assert.Equal("4px", d["padding-left"]);
    }

    [Fact]
    public void Strips_block_comments_before_tokenizing()
    {
        // CSS comments are whitespace — an interleaved /* … */ must NOT make the value reject.
        var d = Expand("1px /* t */ 2px");
        Assert.Equal("1px", d["padding-top"]);
        Assert.Equal("2px", d["padding-right"]);
    }

    [Fact]
    public void Expands_an_absolute_calc_part_and_rejects_an_unsupported_function_atomically()
    {
        // The paren-aware tokenizer keeps calc(1px + 2px) as ONE token (not split on its inner
        // spaces). Since the body-calc cycle, an ABSOLUTE-term calc part passes atomic resolver
        // validation (LengthResolver folds it to 3px), so the shorthand EXPANDS; a still-unsupported
        // function (url()) keeps the clean atomic rejection.
        var d = Expand("calc(1px + 2px) 5px");
        Assert.Equal("calc(1px + 2px)", d["padding-top"]);
        Assert.Equal("5px", d["padding-right"]);
        Assert.False(PaddingShorthandExpander.TryExpand("padding", "url(x.png) 5px", out _));
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("initial")]
    [InlineData("unset")]
    public void Css_wide_keyword_applies_to_every_longhand(string keyword)
    {
        var d = Expand(keyword);
        Assert.Equal(4, d.Count);
        foreach (var side in new[] { "top", "right", "bottom", "left" })
            Assert.Equal(keyword, d[$"padding-{side}"]);
    }

    [Theory]
    [InlineData("10xyz")]                 // a bad unit → atomic reject via dispatch
    [InlineData("1px 2px 3px 4px 5px")]   // five values
    [InlineData("")]
    [InlineData("1px inherit")]           // CSS-wide keyword is only valid as the sole value
    public void Rejects_malformed(string value)
    {
        Assert.False(PaddingShorthandExpander.TryExpand("padding", value, out _));
    }

    [Fact]
    public void IsPaddingShorthand_recognizes_only_the_box_shorthand()
    {
        Assert.True(PaddingShorthandExpander.IsPaddingShorthand("padding"));
        Assert.True(PaddingShorthandExpander.IsPaddingShorthand("PADDING"));     // case-insensitive
        Assert.False(PaddingShorthandExpander.IsPaddingShorthand("padding-top")); // a longhand
        Assert.False(PaddingShorthandExpander.IsPaddingShorthand("margin"));
    }
}
