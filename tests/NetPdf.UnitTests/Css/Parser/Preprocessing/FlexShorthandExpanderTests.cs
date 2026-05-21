// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Per Phase 3 Task 15 L13 — unit tests for
/// <see cref="FlexShorthandExpander"/>. Covers every grammar branch
/// of CSS Flexbox L1 §7.4 plus rejection of malformed input.
/// </summary>
public sealed class FlexShorthandExpanderTests
{
    [Fact]
    public void None_expands_to_zero_zero_auto()
    {
        // Per CSS Flexbox L1 §7.4: `flex: none` → `0 0 auto`. Items
        // neither grow nor shrink; basis = declared main-size.
        Assert.True(FlexShorthandExpander.TryExpand("none", out var g, out var s, out var b));
        Assert.Equal("0", g);
        Assert.Equal("0", s);
        Assert.Equal("auto", b);
    }

    [Fact]
    public void Auto_expands_to_one_one_auto()
    {
        // Per §7.4: `flex: auto` → `1 1 auto`. Items grow + shrink
        // from their declared main-size.
        Assert.True(FlexShorthandExpander.TryExpand("auto", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("auto", b);
    }

    [Fact]
    public void Single_number_expands_to_grow_with_default_shrink_and_zero_basis()
    {
        // Per §7.4: `flex: <number>` → `<num> 1 0%`. The bare number
        // ALWAYS sets `flex-basis: 0` (not the declared width) — the
        // most common shorthand for proportional flex layout.
        Assert.True(FlexShorthandExpander.TryExpand("1", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("0", b);
    }

    [Fact]
    public void Single_fractional_number_preserves_value()
    {
        // Per CSS Values L4 §6.1 — numbers can be fractional.
        Assert.True(FlexShorthandExpander.TryExpand("2.5", out var g, out var s, out var b));
        Assert.Equal("2.5", g);
        Assert.Equal("1", s);
        Assert.Equal("0", b);
    }

    [Fact]
    public void Single_length_basis_expands_to_default_grow_shrink_with_basis()
    {
        // Per §7.4: `flex: <basis>` → `1 1 <basis>`. The bare length
        // sets the basis; grow + shrink default to 1.
        Assert.True(FlexShorthandExpander.TryExpand("100px", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("100px", b);
    }

    [Fact]
    public void Single_percentage_basis_expands_to_default_grow_shrink_with_basis()
    {
        // Per §7.4 + §7.2.1: percentage is a valid <basis>.
        Assert.True(FlexShorthandExpander.TryExpand("25%", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("25%", b);
    }

    [Fact]
    public void Two_numbers_expand_to_grow_shrink_with_zero_basis()
    {
        // Per §7.4: `<grow> <shrink>` → basis 0.
        Assert.True(FlexShorthandExpander.TryExpand("2 3", out var g, out var s, out var b));
        Assert.Equal("2", g);
        Assert.Equal("3", s);
        Assert.Equal("0", b);
    }

    [Fact]
    public void Number_plus_length_basis_expands_to_grow_default_shrink_basis()
    {
        // Per §7.4: `<grow> <basis>` → shrink 1.
        Assert.True(FlexShorthandExpander.TryExpand("2 100px", out var g, out var s, out var b));
        Assert.Equal("2", g);
        Assert.Equal("1", s);
        Assert.Equal("100px", b);
    }

    [Fact]
    public void Number_plus_auto_basis_expands_to_grow_default_shrink_auto()
    {
        // Per §7.4: `<grow> auto` (= one of the keyword bases).
        Assert.True(FlexShorthandExpander.TryExpand("1 auto", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("auto", b);
    }

    [Fact]
    public void Three_values_expand_to_grow_shrink_basis()
    {
        // Per §7.4: full three-value form preserves all three.
        Assert.True(FlexShorthandExpander.TryExpand("2 0 100px", out var g, out var s, out var b));
        Assert.Equal("2", g);
        Assert.Equal("0", s);
        Assert.Equal("100px", b);
    }

    [Fact]
    public void Three_values_with_percentage_basis_preserves_unit()
    {
        Assert.True(FlexShorthandExpander.TryExpand("1 1 50%", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("50%", b);
    }

    [Fact]
    public void Bare_zero_in_third_position_is_valid_length_basis()
    {
        // Per CSS Values §4.3.2 — `0` is a valid length without a
        // unit. `flex: 1 1 0` is the canonical shorthand for
        // proportional layout with a zero basis (= identical to
        // `flex: 1`).
        Assert.True(FlexShorthandExpander.TryExpand("1 1 0", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("0", b);
    }

    [Fact]
    public void Css_wide_keywords_pass_through_to_all_three_longhands()
    {
        // Per CSS Cascade §7 — wide keywords are property-agnostic
        // and apply uniformly to each longhand.
        Assert.True(FlexShorthandExpander.TryExpand("inherit", out var g, out var s, out var b));
        Assert.Equal("inherit", g);
        Assert.Equal("inherit", s);
        Assert.Equal("inherit", b);

        Assert.True(FlexShorthandExpander.TryExpand("initial", out g, out s, out b));
        Assert.Equal("initial", g);
        Assert.Equal("initial", s);
        Assert.Equal("initial", b);

        Assert.True(FlexShorthandExpander.TryExpand("unset", out g, out s, out b));
        Assert.Equal("unset", g);
        Assert.Equal("unset", s);
        Assert.Equal("unset", b);
    }

    [Fact]
    public void Empty_input_returns_false()
    {
        Assert.False(FlexShorthandExpander.TryExpand("", out _, out _, out _));
        Assert.False(FlexShorthandExpander.TryExpand("   ", out _, out _, out _));
    }

    [Fact]
    public void More_than_three_tokens_returns_false()
    {
        // Per §7.4: max 3 tokens.
        Assert.False(FlexShorthandExpander.TryExpand("1 1 100px 50%", out _, out _, out _));
    }

    [Fact]
    public void Two_basis_tokens_returns_false()
    {
        // Per §7.4: only one <basis> allowed.
        Assert.False(FlexShorthandExpander.TryExpand("100px 50%", out _, out _, out _));
    }

    [Fact]
    public void Three_numbers_returns_false()
    {
        // Per §7.4: third position must be <basis>, not <number>.
        // Note: `1 1 0` is accepted because 0 is a valid bare-zero
        // length per CSS Values §4.3.2. But `1 1 2` is NOT valid.
        Assert.False(FlexShorthandExpander.TryExpand("1 1 2", out _, out _, out _));
    }

    [Fact]
    public void Garbage_input_returns_false()
    {
        Assert.False(FlexShorthandExpander.TryExpand("xyz", out _, out _, out _));
        Assert.False(FlexShorthandExpander.TryExpand("100xyz", out _, out _, out _));
    }

    [Fact]
    public void Em_basis_unit_recognized()
    {
        // Per CSS Values L4 — em is a valid relative length unit.
        Assert.True(FlexShorthandExpander.TryExpand("1.5em", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("1.5em", b);
    }

    [Fact]
    public void Content_keyword_basis_recognized()
    {
        // Per CSS Flexbox L1 §7.2.1 — `content` is a valid basis.
        Assert.True(FlexShorthandExpander.TryExpand("1 1 content", out var g, out var s, out var b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("content", b);
    }

    [Fact]
    public void Case_insensitive_keyword_matching()
    {
        // CSS keyword names are case-insensitive per CSS Values L4 §3.1.
        Assert.True(FlexShorthandExpander.TryExpand("NONE", out var g, out var s, out var b));
        Assert.Equal("0", g);
        Assert.Equal("0", s);
        Assert.Equal("auto", b);

        Assert.True(FlexShorthandExpander.TryExpand("Auto", out g, out s, out b));
        Assert.Equal("1", g);
        Assert.Equal("1", s);
        Assert.Equal("auto", b);
    }
}
