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

    // =====================================================================
    //  PR-#91 review F1 — atomic validation (CSS Cascade L4 §4.2)
    // =====================================================================

    [Fact]
    public void Invalid_end_component_drops_the_whole_shorthand()
    {
        // Per F1 — `grid-row: 2 / 0` has an invalid end component (= 0
        // is rejected per §8.3 line-number 0 rule). The whole shorthand
        // must drop atomically; the start MUST NOT survive at 2.
        Assert.False(GridLineShorthandExpander.TryExpand("2 / 0", out _, out _));
    }

    [Fact]
    public void Invalid_start_component_drops_the_whole_shorthand()
    {
        Assert.False(GridLineShorthandExpander.TryExpand("0 / 2", out _, out _));
    }

    [Fact]
    public void Span_alone_drops_the_whole_shorthand()
    {
        // `span` alone is invalid per §8.3 — must have integer or ident.
        Assert.False(GridLineShorthandExpander.TryExpand("span / 3", out _, out _));
    }

    [Fact]
    public void Malformed_first_component_drops_the_whole_shorthand()
    {
        // `@` is unparseable.
        Assert.False(GridLineShorthandExpander.TryExpand("@ / 2", out _, out _));
    }

    [Fact]
    public void CSS_wide_keyword_in_compound_drops_the_whole_shorthand()
    {
        // `initial` as a per-component value isn't a valid <grid-line>
        // (= CSS-wide keywords are only valid as the SOLE declaration
        // value; the GridLineResolver's F3 defense rejects them).
        Assert.False(GridLineShorthandExpander.TryExpand("2 / initial", out _, out _));
    }

    // =====================================================================
    //  PR-#91 review F2 — var() interaction
    // =====================================================================

    [Fact]
    public void Value_containing_var_function_is_not_expanded()
    {
        // Per F2 — the preprocessor can't know the post-substitution
        // structure of `2 / var(--end)`, so the expander returns false
        // (= shorthand silently drops at the cascade). Post-substitution
        // re-expansion is a separate cycle's scope.
        Assert.False(GridLineShorthandExpander.TryExpand(
            "2 / var(--end)", out _, out _));
    }

    [Fact]
    public void Value_that_is_entirely_var_function_is_not_expanded()
    {
        Assert.False(GridLineShorthandExpander.TryExpand(
            "var(--placement)", out _, out _));
    }

    // =====================================================================
    //  PR-#91 review F5 — `none` is a valid named line
    // =====================================================================

    [Fact]
    public void Single_none_duplicates_to_end_as_named_line()
    {
        // Per F5 — CSS Grid §8.3 excludes ONLY `auto` and `span` from
        // <custom-ident>. `none` IS a valid named line; the omitted-pair
        // rule duplicates it.
        Assert.True(GridLineShorthandExpander.TryExpand("none", out var s, out var e));
        Assert.Equal("none", s);
        Assert.Equal("none", e);
    }
}
