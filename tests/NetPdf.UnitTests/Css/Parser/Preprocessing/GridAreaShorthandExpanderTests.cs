// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 17 cycle 0c — unit tests for
/// <see cref="GridAreaShorthandExpander"/>. Covers the four-value
/// grid-area shorthand grammar per CSS Grid L1 §8.4 + the
/// omitted-pair mapping rules + CSS-wide-keyword passthrough +
/// malformed-input rejection.
///
/// <para><b>Mapping per §8.4</b> (= row-start / column-start /
/// row-end / column-end):</para>
/// <list type="bullet">
///   <item>1 value: A → A / &lt;A&gt; / &lt;A&gt; / &lt;A&gt;</item>
///   <item>2 values: A B → A / B / &lt;A&gt; / &lt;B&gt;</item>
///   <item>3 values: A B C → A / B / C / &lt;B&gt;</item>
///   <item>4 values: A B C D → A / B / C / D</item>
/// </list>
/// <para>Where &lt;X&gt; = X if X is a bare custom-ident, else auto.</para>
/// </summary>
public sealed class GridAreaShorthandExpanderTests
{
    // =====================================================================
    //  1-value form
    // =====================================================================

    [Fact]
    public void Single_custom_ident_duplicates_to_all_four_longhands()
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "foo", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("foo", rs);
        Assert.Equal("foo", cs);
        Assert.Equal("foo", re);
        Assert.Equal("foo", ce);
    }

    [Fact]
    public void Single_integer_uses_auto_for_omitted_three()
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "2", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("2", rs);
        Assert.Equal("auto", cs);
        Assert.Equal("auto", re);
        Assert.Equal("auto", ce);
    }

    [Fact]
    public void Single_auto_pairs_to_all_auto()
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "auto", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("auto", rs);
        Assert.Equal("auto", cs);
        Assert.Equal("auto", re);
        Assert.Equal("auto", ce);
    }

    // =====================================================================
    //  2-value form
    // =====================================================================

    [Fact]
    public void Two_idents_pair_with_self_duplication()
    {
        // foo / bar → row-start: foo; column-start: bar; row-end: <foo>;
        // column-end: <bar>. Both A and B are custom-idents → duplicated.
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "foo / bar", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("foo", rs);
        Assert.Equal("bar", cs);
        Assert.Equal("foo", re);
        Assert.Equal("bar", ce);
    }

    [Fact]
    public void Two_integers_pair_to_auto_for_omitted_ends()
    {
        // 2 / 3 → integers, so omitted ends are auto.
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "2 / 3", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("2", rs);
        Assert.Equal("3", cs);
        Assert.Equal("auto", re);
        Assert.Equal("auto", ce);
    }

    [Fact]
    public void Two_value_mixed_ident_and_integer()
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "foo / 2", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("foo", rs);
        Assert.Equal("2", cs);
        // <foo> is custom-ident → duplicates; <2> is integer → auto.
        Assert.Equal("foo", re);
        Assert.Equal("auto", ce);
    }

    // =====================================================================
    //  3-value form
    // =====================================================================

    [Fact]
    public void Three_value_omits_column_end_per_spec()
    {
        // 2 / 3 / 4 → row-start: 2; column-start: 3; row-end: 4;
        // column-end: <3> = auto (= 3 is integer).
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "2 / 3 / 4", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("2", rs);
        Assert.Equal("3", cs);
        Assert.Equal("4", re);
        Assert.Equal("auto", ce);
    }

    [Fact]
    public void Three_value_with_ident_in_column_start_duplicates_to_column_end()
    {
        // 2 / foo / span 3 → column-end: <foo> = foo (= ident → duplicates).
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "2 / foo / span 3", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("2", rs);
        Assert.Equal("foo", cs);
        Assert.Equal("span 3", re);
        Assert.Equal("foo", ce);
    }

    // =====================================================================
    //  4-value form
    // =====================================================================

    [Fact]
    public void Four_values_map_directly_with_no_omission()
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "1 / 2 / 3 / 4", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("1", rs);
        Assert.Equal("2", cs);
        Assert.Equal("3", re);
        Assert.Equal("4", ce);
    }

    [Fact]
    public void Four_value_with_span_in_each_position()
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "2 / 3 / span 2 / span 4",
            out var rs, out var cs, out var re, out var ce));
        Assert.Equal("2", rs);
        Assert.Equal("3", cs);
        Assert.Equal("span 2", re);
        Assert.Equal("span 4", ce);
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
    public void CSS_wide_keywords_pass_through_to_all_four_longhands(string keyword)
    {
        Assert.True(GridAreaShorthandExpander.TryExpand(
            keyword, out var rs, out var cs, out var re, out var ce));
        Assert.Equal(keyword, rs);
        Assert.Equal(keyword, cs);
        Assert.Equal(keyword, re);
        Assert.Equal(keyword, ce);
    }

    // =====================================================================
    //  Rejection paths
    // =====================================================================

    [Fact]
    public void Five_slash_separated_values_are_rejected()
    {
        // Max 4 values per §8.4.
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "1 / 2 / 3 / 4 / 5", out _, out _, out _, out _));
    }

    [Fact]
    public void Empty_value_is_rejected()
    {
        Assert.False(GridAreaShorthandExpander.TryExpand("", out _, out _, out _, out _));
        Assert.False(GridAreaShorthandExpander.TryExpand("   ", out _, out _, out _, out _));
    }

    [Fact]
    public void Empty_slot_in_middle_is_rejected()
    {
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "1 / / 3", out _, out _, out _, out _));
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "1 //3", out _, out _, out _, out _));
    }

    [Fact]
    public void Trailing_or_leading_slash_is_rejected()
    {
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "1 /", out _, out _, out _, out _));
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "/ 1", out _, out _, out _, out _));
    }

    // =====================================================================
    //  PR-#91 review F1 — atomic validation
    // =====================================================================

    [Fact]
    public void Invalid_third_component_drops_the_whole_shorthand()
    {
        // grid-area: 2 / 3 / 0 / 5 — the row-end value 0 is invalid per
        // §8.3. Per CSS Cascade §4.2 the whole shorthand drops, NOT
        // partially applies row-start=2 + column-start=3 + column-end=5
        // and only drops row-end.
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "2 / 3 / 0 / 5", out _, out _, out _, out _));
    }

    [Fact]
    public void Invalid_first_component_drops_the_whole_shorthand()
    {
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "0 / 2 / 3 / 4", out _, out _, out _, out _));
    }

    [Fact]
    public void Span_alone_anywhere_drops_the_whole_shorthand()
    {
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "span / 2 / 3 / 4", out _, out _, out _, out _));
    }

    // =====================================================================
    //  PR-#91 review F2 — var() interaction
    // =====================================================================

    [Fact]
    public void Value_containing_var_function_is_not_expanded()
    {
        // Per F2 — the expander can't know the post-substitution shape.
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "var(--area)", out _, out _, out _, out _));
        Assert.False(GridAreaShorthandExpander.TryExpand(
            "2 / var(--cs) / 4 / 5", out _, out _, out _, out _));
    }

    // =====================================================================
    //  PR-#91 review F5 — `none` is a valid named line
    // =====================================================================

    [Fact]
    public void Single_none_duplicates_to_all_four_per_omitted_pair_rule()
    {
        // Per F5 — `none` is a valid <custom-ident>, so the omitted-pair
        // rule duplicates it to all four longhands.
        Assert.True(GridAreaShorthandExpander.TryExpand(
            "none", out var rs, out var cs, out var re, out var ce));
        Assert.Equal("none", rs);
        Assert.Equal("none", cs);
        Assert.Equal("none", re);
        Assert.Equal("none", ce);
    }
}
