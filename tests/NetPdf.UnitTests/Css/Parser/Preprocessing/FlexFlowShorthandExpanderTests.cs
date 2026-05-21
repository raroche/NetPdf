// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L16 — unit tests for
/// <see cref="FlexFlowShorthandExpander"/>. Covers every grammar
/// branch of CSS Flexbox L1 §6.1 plus rejection of malformed input.
/// </summary>
public sealed class FlexFlowShorthandExpanderTests
{
    [Fact]
    public void Single_direction_expands_with_default_wrap()
    {
        // Per §6.1: `flex-flow: row` → direction=row, wrap=nowrap (the
        // CSS initial values for the unspecified longhand).
        Assert.True(FlexFlowShorthandExpander.TryExpand("row", out var d, out var w));
        Assert.Equal("row", d);
        Assert.Equal("nowrap", w);
    }

    [Fact]
    public void Single_row_reverse_direction_expands()
    {
        Assert.True(FlexFlowShorthandExpander.TryExpand("row-reverse", out var d, out var w));
        Assert.Equal("row-reverse", d);
        Assert.Equal("nowrap", w);
    }

    [Fact]
    public void Single_column_direction_expands()
    {
        Assert.True(FlexFlowShorthandExpander.TryExpand("column", out var d, out var w));
        Assert.Equal("column", d);
        Assert.Equal("nowrap", w);
    }

    [Fact]
    public void Single_column_reverse_direction_expands()
    {
        Assert.True(FlexFlowShorthandExpander.TryExpand("column-reverse", out var d, out var w));
        Assert.Equal("column-reverse", d);
        Assert.Equal("nowrap", w);
    }

    [Fact]
    public void Single_wrap_expands_with_default_direction()
    {
        // Per §6.1: `flex-flow: wrap` → direction=row (default),
        // wrap=wrap.
        Assert.True(FlexFlowShorthandExpander.TryExpand("wrap", out var d, out var w));
        Assert.Equal("row", d);
        Assert.Equal("wrap", w);
    }

    [Fact]
    public void Single_nowrap_expands()
    {
        Assert.True(FlexFlowShorthandExpander.TryExpand("nowrap", out var d, out var w));
        Assert.Equal("row", d);
        Assert.Equal("nowrap", w);
    }

    [Fact]
    public void Single_wrap_reverse_expands()
    {
        Assert.True(FlexFlowShorthandExpander.TryExpand("wrap-reverse", out var d, out var w));
        Assert.Equal("row", d);
        Assert.Equal("wrap-reverse", w);
    }

    [Fact]
    public void Two_value_direction_wrap_expands()
    {
        // Per §6.1: `flex-flow: row wrap` (direction-first order).
        Assert.True(FlexFlowShorthandExpander.TryExpand("row wrap", out var d, out var w));
        Assert.Equal("row", d);
        Assert.Equal("wrap", w);
    }

    [Fact]
    public void Two_value_wrap_direction_expands()
    {
        // Per §6.1: `flex-flow: wrap row` (wrap-first order;
        // same meaning per the `||` combinator).
        Assert.True(FlexFlowShorthandExpander.TryExpand("wrap row", out var d, out var w));
        Assert.Equal("row", d);
        Assert.Equal("wrap", w);
    }

    [Fact]
    public void Two_value_complex_combination_expands()
    {
        // `flex-flow: column-reverse wrap-reverse` — the production-
        // typical recipe for a reverse-stacked wrapped column.
        Assert.True(FlexFlowShorthandExpander.TryExpand(
            "column-reverse wrap-reverse", out var d, out var w));
        Assert.Equal("column-reverse", d);
        Assert.Equal("wrap-reverse", w);
    }

    [Fact]
    public void Two_value_complex_combination_reverse_order_expands()
    {
        // Same values, swapped order.
        Assert.True(FlexFlowShorthandExpander.TryExpand(
            "wrap-reverse column-reverse", out var d, out var w));
        Assert.Equal("column-reverse", d);
        Assert.Equal("wrap-reverse", w);
    }

    [Fact]
    public void Case_insensitive_keyword_matching()
    {
        // CSS keyword names are case-insensitive per CSS Values L4 §3.1.
        Assert.True(FlexFlowShorthandExpander.TryExpand("ROW", out var d, out var w));
        Assert.Equal("ROW", d);
        Assert.Equal("nowrap", w);

        Assert.True(FlexFlowShorthandExpander.TryExpand("Column WRAP", out d, out w));
        Assert.Equal("Column", d);
        Assert.Equal("WRAP", w);
    }

    [Fact]
    public void Css_wide_keywords_pass_through_to_both_longhands()
    {
        // Per CSS Cascade §7 — wide keywords are property-agnostic
        // and apply uniformly to each longhand.
        Assert.True(FlexFlowShorthandExpander.TryExpand("inherit", out var d, out var w));
        Assert.Equal("inherit", d);
        Assert.Equal("inherit", w);

        Assert.True(FlexFlowShorthandExpander.TryExpand("initial", out d, out w));
        Assert.Equal("initial", d);
        Assert.Equal("initial", w);

        Assert.True(FlexFlowShorthandExpander.TryExpand("unset", out d, out w));
        Assert.Equal("unset", d);
        Assert.Equal("unset", w);

        Assert.True(FlexFlowShorthandExpander.TryExpand("revert", out d, out w));
        Assert.Equal("revert", d);
        Assert.Equal("revert", w);
    }

    [Fact]
    public void Empty_input_returns_false()
    {
        Assert.False(FlexFlowShorthandExpander.TryExpand("", out _, out _));
        Assert.False(FlexFlowShorthandExpander.TryExpand("   ", out _, out _));
    }

    [Fact]
    public void More_than_two_tokens_returns_false()
    {
        // Per §6.1: max 2 tokens.
        Assert.False(FlexFlowShorthandExpander.TryExpand(
            "row wrap nowrap", out _, out _));
    }

    [Fact]
    public void Two_direction_tokens_returns_false()
    {
        // Per §6.1: one of each; two directions is invalid.
        Assert.False(FlexFlowShorthandExpander.TryExpand("row column", out _, out _));
    }

    [Fact]
    public void Two_wrap_tokens_returns_false()
    {
        // Per §6.1: one of each; two wraps is invalid.
        Assert.False(FlexFlowShorthandExpander.TryExpand("wrap nowrap", out _, out _));
    }

    [Fact]
    public void Unknown_keyword_returns_false()
    {
        Assert.False(FlexFlowShorthandExpander.TryExpand("xyz", out _, out _));
        Assert.False(FlexFlowShorthandExpander.TryExpand("row xyz", out _, out _));
    }
}
