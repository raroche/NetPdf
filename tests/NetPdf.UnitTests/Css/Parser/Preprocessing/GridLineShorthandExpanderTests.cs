// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 17 cycle 0c — unit tests for
/// <see cref="GridLineShorthandExpander"/>. Covers every grammar branch
/// of CSS Grid L1 §8.4 for the <c>grid-row</c> / <c>grid-column</c>
/// shorthand pair plus the omitted-pair rule + CSS-wide-keyword
/// passthrough + malformed-input rejection.
/// </summary>
public sealed class GridLineShorthandExpanderTests
{
    // =====================================================================
    //  Single-value forms (= omitted second value)
    // =====================================================================

    [Fact]
    public void Single_auto_pairs_to_auto()
    {
        Assert.True(GridLineShorthandExpander.TryExpand("auto", out var s, out var e));
        Assert.Equal("auto", s);
        Assert.Equal("auto", e);
    }

    [Fact]
    public void Single_integer_pairs_to_auto()
    {
        // Per §8.4 — integer is NOT a <custom-ident>, so the omitted
        // end value falls back to auto.
        Assert.True(GridLineShorthandExpander.TryExpand("2", out var s, out var e));
        Assert.Equal("2", s);
        Assert.Equal("auto", e);
    }

    [Fact]
    public void Single_negative_integer_pairs_to_auto()
    {
        Assert.True(GridLineShorthandExpander.TryExpand("-1", out var s, out var e));
        Assert.Equal("-1", s);
        Assert.Equal("auto", e);
    }

    [Fact]
    public void Single_custom_ident_duplicates_to_end()
    {
        // Per §8.4 — a bare <custom-ident> duplicates to the omitted
        // end longhand. The downstream GridLineResolver will resolve
        // each longhand to a named-line GridLineValue.
        Assert.True(GridLineShorthandExpander.TryExpand("foo", out var s, out var e));
        Assert.Equal("foo", s);
        Assert.Equal("foo", e);
    }

    [Fact]
    public void Single_span_compound_pairs_to_auto()
    {
        // `span 2` contains the reserved 'span' keyword — NOT a
        // <custom-ident>, so the omitted end falls back to auto.
        Assert.True(GridLineShorthandExpander.TryExpand("span 2", out var s, out var e));
        Assert.Equal("span 2", s);
        Assert.Equal("auto", e);
    }

    [Fact]
    public void Single_ident_with_integer_compound_pairs_to_auto()
    {
        // `foo 2` is a compound form (custom-ident + occurrence), NOT a
        // bare <custom-ident> — so the omitted end falls back to auto.
        Assert.True(GridLineShorthandExpander.TryExpand("foo 2", out var s, out var e));
        Assert.Equal("foo 2", s);
        Assert.Equal("auto", e);
    }

    // =====================================================================
    //  Two-value form (= explicit pair)
    // =====================================================================

    [Fact]
    public void Two_integer_values_separate_to_start_and_end()
    {
        Assert.True(GridLineShorthandExpander.TryExpand("2 / 4", out var s, out var e));
        Assert.Equal("2", s);
        Assert.Equal("4", e);
    }

    [Fact]
    public void Integer_and_span_combine_correctly()
    {
        Assert.True(GridLineShorthandExpander.TryExpand("2 / span 3", out var s, out var e));
        Assert.Equal("2", s);
        Assert.Equal("span 3", e);
    }

    [Fact]
    public void Auto_and_ident_combine()
    {
        Assert.True(GridLineShorthandExpander.TryExpand("auto / foo", out var s, out var e));
        Assert.Equal("auto", s);
        Assert.Equal("foo", e);
    }

    [Fact]
    public void Whitespace_around_slash_is_tolerated()
    {
        // Authors may write the slash with no surrounding whitespace.
        Assert.True(GridLineShorthandExpander.TryExpand("2/4", out var s, out var e));
        Assert.Equal("2", s);
        Assert.Equal("4", e);
    }

    // =====================================================================
    //  CSS-wide keywords
    // =====================================================================

    [Theory]
    [InlineData("initial")]
    [InlineData("inherit")]
    [InlineData("unset")]
    [InlineData("revert")]
    [InlineData("revert-layer")]
    public void CSS_wide_keywords_pass_through_to_both_longhands(string keyword)
    {
        Assert.True(GridLineShorthandExpander.TryExpand(keyword, out var s, out var e));
        Assert.Equal(keyword, s);
        Assert.Equal(keyword, e);
    }

    // =====================================================================
    //  Rejection paths
    // =====================================================================

    [Fact]
    public void Empty_value_is_rejected()
    {
        Assert.False(GridLineShorthandExpander.TryExpand("", out _, out _));
        Assert.False(GridLineShorthandExpander.TryExpand("   ", out _, out _));
    }

    [Fact]
    public void Three_slash_separated_values_are_rejected()
    {
        // grid-row / grid-column accept at most 2 values per §8.4.
        Assert.False(GridLineShorthandExpander.TryExpand("1 / 2 / 3", out _, out _));
    }

    [Fact]
    public void Trailing_slash_with_empty_second_is_rejected()
    {
        Assert.False(GridLineShorthandExpander.TryExpand("2 /", out _, out _));
        Assert.False(GridLineShorthandExpander.TryExpand("/ 2", out _, out _));
    }

    [Fact]
    public void Comments_in_value_are_stripped()
    {
        // Mirrors FlexShorthandExpander behavior — CSS block comments
        // are stripped before tokenizing.
        Assert.True(GridLineShorthandExpander.TryExpand(
            "/* before */ 2 /* mid */ / /* end */ 4", out var s, out var e));
        Assert.Equal("2", s);
        Assert.Equal("4", e);
    }
}
