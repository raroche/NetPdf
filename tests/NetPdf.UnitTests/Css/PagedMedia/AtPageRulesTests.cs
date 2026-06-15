// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.PagedMedia;
using Xunit;

namespace NetPdf.UnitTests.Css.PagedMedia;

/// <summary>
/// Unit tests for the multi-page driver cycle 6 page-selector matching in <see cref="AtPageRules"/>:
/// <see cref="AtPageRules.PageSelectorContext"/> (first-page + LTR parity) and
/// <see cref="AtPageRules.MatchTier"/> (which <c>@page</c> selectors apply to a given page, and at what
/// CSS Page 3 §3.1 specificity tier: bare 0 &lt; <c>:left</c>/<c>:right</c> 1 &lt; <c>:first</c>/<c>:blank</c> 2).
/// </summary>
public sealed class AtPageRulesTests
{
    [Fact]
    public void PageSelectorContext_first_page_is_first_and_right()
    {
        var ctx = new AtPageRules.PageSelectorContext(0);
        Assert.True(ctx.IsFirstPage);
        Assert.True(ctx.IsRightPage);   // LTR: page 0 is recto/right
    }

    [Theory]
    [InlineData(0, true)]    // right (recto)
    [InlineData(1, false)]   // left (verso)
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void PageSelectorContext_parity_alternates_by_index(int pageIndex, bool isRight)
    {
        var ctx = new AtPageRules.PageSelectorContext(pageIndex);
        Assert.Equal(isRight, ctx.IsRightPage);
        Assert.Equal(pageIndex == 0, ctx.IsFirstPage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MatchTier_bare_applies_to_every_page_at_tier_0(string? prelude)
    {
        Assert.Equal(0, AtPageRules.MatchTier(prelude, new AtPageRules.PageSelectorContext(0)));
        Assert.Equal(0, AtPageRules.MatchTier(prelude, new AtPageRules.PageSelectorContext(5)));
    }

    [Fact]
    public void MatchTier_first_matches_only_the_first_page_at_tier_2()
    {
        Assert.Equal(2, AtPageRules.MatchTier(":first", new AtPageRules.PageSelectorContext(0)));
        Assert.Equal(-1, AtPageRules.MatchTier(":first", new AtPageRules.PageSelectorContext(1)));
    }

    [Fact]
    public void MatchTier_blank_matches_only_a_blank_page_at_tier_2()
    {
        Assert.Equal(2, AtPageRules.MatchTier(":blank", new AtPageRules.PageSelectorContext(3, IsBlank: true)));
        Assert.Equal(-1, AtPageRules.MatchTier(":blank", new AtPageRules.PageSelectorContext(3, IsBlank: false)));
    }

    [Fact]
    public void MatchTier_left_and_right_match_by_parity_at_tier_1()
    {
        // Page 0 = right, page 1 = left.
        Assert.Equal(1, AtPageRules.MatchTier(":right", new AtPageRules.PageSelectorContext(0)));
        Assert.Equal(-1, AtPageRules.MatchTier(":right", new AtPageRules.PageSelectorContext(1)));
        Assert.Equal(1, AtPageRules.MatchTier(":left", new AtPageRules.PageSelectorContext(1)));
        Assert.Equal(-1, AtPageRules.MatchTier(":left", new AtPageRules.PageSelectorContext(0)));
    }

    [Fact]
    public void MatchTier_list_returns_the_highest_matching_selectors_tier()
    {
        // `:first, :left` — page 0 (first AND right): only :first matches → tier 2; page 1 (left): only
        // :left matches → tier 1; page 2 (right, non-first): neither matches → -1.
        Assert.Equal(2, AtPageRules.MatchTier(":first, :left", new AtPageRules.PageSelectorContext(0)));
        Assert.Equal(1, AtPageRules.MatchTier(":first, :left", new AtPageRules.PageSelectorContext(1)));
        Assert.Equal(-1, AtPageRules.MatchTier(":first, :left", new AtPageRules.PageSelectorContext(2)));
    }

    [Theory]
    [InlineData("chapter")]      // named page — cycle 7
    [InlineData(":first:left")]  // a compound — deferred
    [InlineData(":nth(2)")]      // an unknown pseudo
    public void MatchTier_named_or_compound_selectors_never_match_first_cut(string prelude)
    {
        Assert.Equal(-1, AtPageRules.MatchTier(prelude, new AtPageRules.PageSelectorContext(0)));
        Assert.Equal(-1, AtPageRules.MatchTier(prelude, new AtPageRules.PageSelectorContext(1, IsBlank: true)));
    }
}
