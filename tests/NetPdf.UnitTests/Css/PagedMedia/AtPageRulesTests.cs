// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.PagedMedia;
using NetPdf.Css.Parser;
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

    // ---- Named pages (cycle 7) ----

    [Fact]
    public void MatchTier_named_page_matches_an_assigned_page_at_tier_3()
    {
        Assert.Equal(3, AtPageRules.MatchTier("chapter",
            new AtPageRules.PageSelectorContext(2, AssignedPageName: "chapter")));
        Assert.Equal(-1, AtPageRules.MatchTier("chapter",
            new AtPageRules.PageSelectorContext(2, AssignedPageName: "index")));   // different name
        Assert.Equal(-1, AtPageRules.MatchTier("chapter",
            new AtPageRules.PageSelectorContext(2)));                              // unnamed page
    }

    [Fact]
    public void MatchTier_named_page_outranks_first_on_a_named_first_page()
    {
        // CSS Page 3 §3.1 specificity: a named selector (tier 3) outranks :first (tier 2).
        var ctx = new AtPageRules.PageSelectorContext(0, AssignedPageName: "cover");
        Assert.Equal(3, AtPageRules.MatchTier("cover", ctx));
        Assert.Equal(2, AtPageRules.MatchTier(":first", ctx));
    }

    [Fact]
    public void MatchTier_named_page_is_case_sensitive()
    {
        // CSS custom-idents are case-sensitive.
        Assert.Equal(-1, AtPageRules.MatchTier("Chapter",
            new AtPageRules.PageSelectorContext(0, AssignedPageName: "chapter")));
    }

    [Theory]
    [InlineData("auto")]           // the initial page value, not a name
    [InlineData("inherit")]        // a CSS-wide keyword, not a name
    [InlineData("initial")]
    [InlineData("unset")]
    [InlineData("revert")]
    [InlineData("default")]        // reserved (CSS Page 3)
    [InlineData("chapter:first")]  // a compound — deferred
    [InlineData("123")]            // leading digit — not an ident
    [InlineData("-1")]             // a leading '-' before a digit is not a valid ident (review P1)
    [InlineData("--name")]         // custom-property syntax — not a page name
    [InlineData(":nth(2)")]        // an unknown pseudo
    public void MatchTier_rejects_non_name_or_compound_selectors(string prelude)
    {
        Assert.Equal(-1, AtPageRules.MatchTier(prelude,
            new AtPageRules.PageSelectorContext(0, AssignedPageName: prelude)));   // even self-named, invalid → no match
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("initial")]
    [InlineData("unset")]
    [InlineData("revert")]
    public async Task ResolveUsedPageName_treats_css_wide_keywords_as_no_name(string keyword)
    {
        // Review P1: a CSS-wide `page` value must NOT become a literal page name — it resolves to the
        // parent's used value (the walk continues). With no ancestor naming a page, the result is "".
        var (doc, cascade) = await ResolveAsync(
            "<html><body><div id='d'>x</div></body></html>", $"#d {{ page: {keyword} }}");
        Assert.Equal("", AtPageRules.ResolveUsedPageName(doc.QuerySelector("#d"), cascade));
    }

    [Theory]
    [InlineData("-1")]      // a leading '-' before a digit
    [InlineData("123")]     // leading digit
    [InlineData("a b")]     // whitespace — not a single ident
    public async Task ResolveUsedPageName_rejects_invalid_raw_values(string raw)
    {
        // Review P1: an INVALID raw `page` value is treated as `auto` (error recovery), not a literal name.
        var (doc, cascade) = await ResolveAsync(
            "<html><body><div id='d'>x</div></body></html>", $"#d {{ page: {raw} }}");
        Assert.Equal("", AtPageRules.ResolveUsedPageName(doc.QuerySelector("#d"), cascade));
    }

    [Fact]
    public async Task ResolveUsedPageName_css_wide_inherit_resolves_to_a_named_ancestor()
    {
        // `page: inherit` on a child under a named ancestor resolves to the ancestor's name (the walk
        // continues past `inherit` to the parent that names a page) — not the literal "inherit".
        var (doc, cascade) = await ResolveAsync(
            "<html><body><div class='ch'><div id='d'>x</div></div></body></html>",
            ".ch { page: chapter } #d { page: inherit }");
        Assert.Equal("chapter", AtPageRules.ResolveUsedPageName(doc.QuerySelector("#d"), cascade));
    }

    [Fact]
    public async Task ResolveUsedPageName_reads_the_nearest_non_auto_ancestor()
    {
        // CSS Page 3 §3.4 used value: a `page: auto` (the default) resolves to the parent's used value, so
        // a <p> inside a `.chapter { page: chapter }` inherits "chapter". AngleSharp drops `page`, so this
        // also exercises the CssPreprocessor recovery.
        var (doc, cascade) = await ResolveAsync(
            "<html><body><div class='chapter'><p id='p'>x</p></div><p id='q'>y</p></body></html>",
            ".chapter { page: chapter }");
        Assert.Equal("chapter", AtPageRules.ResolveUsedPageName(doc.QuerySelector("#p"), cascade));
        Assert.Equal("", AtPageRules.ResolveUsedPageName(doc.QuerySelector("#q"), cascade));  // no page in chain
        Assert.Equal("", AtPageRules.ResolveUsedPageName(null, cascade));                     // a blank page
    }

    private static async Task<(IDocument Doc, ResolvedCascadeResult Cascade)> ResolveAsync(
        string html, string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var doc = await ctx.OpenAsync(req => req.Content(html));
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = CssParserAdapter.Adapt(parser.ParseStyleSheet(css),
            NetPdf.Css.Parser.Preprocessing.CssPreprocessor.Process(css), href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet), CssMediaContext.DefaultPrint);
        return (doc, VarResolver.Resolve(cascade, doc));
    }
}
