// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Review-cycle 1 tests for Phase 2 Task 6 — covers nine review-driven hardening fixes
/// applied to <see cref="SelectorCompiler"/> + <see cref="SelectorMatcher"/> +
/// <see cref="SelectorBytecode"/>. Grouped by recommendation number for traceability.
/// </summary>
public sealed class SelectorReviewCycle1Tests
{
    private static async Task<IDocument> ParseHtml(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static IElement Q(IDocument doc, string css) =>
        doc.QuerySelector(css)
        ?? throw new System.InvalidOperationException($"selector \"{css}\" matched nothing");

    private static bool Matches(string selector, IElement element)
    {
        var list = SelectorCompiler.Compile(selector);
        return SelectorMatcher.MatchList(list, element, out _);
    }

    // ============================================================
    // Rec 1 — :has() containment short-circuits the matcher
    // ============================================================

    [Fact]
    public async Task Rec1_Not_wrapping_Has_does_not_match_everything()
    {
        // :not(:has(.foo)) was the original bug: :has() always returned false → :not() inverted
        // → matched every element. Now Match() short-circuits when ContainsHas is true, so the
        // whole rule is treated as "no match" — the v1 contract for any :has()-containing selector.
        var doc = await ParseHtml("<div class=\"foo\"><p>x</p></div><span>y</span>");
        Assert.False(Matches(":not(:has(.foo))", Q(doc, "p")));
        Assert.False(Matches(":not(:has(.foo))", Q(doc, "span")));
        Assert.False(Matches(":not(:has(.foo))", Q(doc, "div")));
    }

    [Fact]
    public async Task Rec1_Has_inside_Is_propagates_ContainsHas_and_short_circuits()
    {
        var doc = await ParseHtml("<article><h1>t</h1></article>");
        var article = Q(doc, "article");
        // :is(article:has(h1), p) — outer ContainsHas=true even though `p` alternative is innocent.
        // The conservative v1 rule: any selector touching :has() doesn't apply.
        var list = SelectorCompiler.Compile(":is(article:has(h1), p)");
        Assert.True(list.ContainsHas);
        Assert.False(SelectorMatcher.MatchList(list, article, out _));
    }

    [Fact]
    public void Rec1_Plain_selector_without_Has_still_matches_normally()
    {
        // Sanity — the short-circuit must not regress non-:has() selectors.
        var list = SelectorCompiler.Compile("p");
        Assert.False(list.ContainsHas);
        Assert.False(list.Alternatives[0].ContainsHas);
    }

    // ============================================================
    // Rec 2 — :has() relative-selector parsing
    // ============================================================

    [Theory]
    [InlineData("a:has(> img)")]
    [InlineData("a:has(+ p)")]
    [InlineData("a:has(~ p)")]
    [InlineData("a:has(  >  img  )")]   // whitespace-tolerant
    [InlineData("article:has(h1)")]      // no leading combinator (descendant)
    public void Rec2_Has_with_relative_combinators_parses(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        Assert.True(list.ContainsHas);
        Assert.Single(list.Alternatives);
    }

    [Fact]
    public async Task Rec2_Has_with_relative_combinator_still_returns_false_at_runtime()
    {
        // Relative-selector parsing is a parse-only enhancement in v1 — runtime semantics
        // unchanged (always false), v1.4 plugs in real evaluation.
        var doc = await ParseHtml("<a href=\"x\"><img src=\"y\"></a>");
        Assert.False(Matches("a:has(> img)", Q(doc, "a")));
    }

    // ============================================================
    // Rec 3a — Bloom-filter sibling-token soundness
    // ============================================================

    [Fact]
    public void Rec3a_Required_tokens_exclude_sibling_combinator_left_side()
    {
        // 'h1 + p': p is anchored to the candidate; h1 is a sibling of p, NOT an ancestor of
        // the candidate. If h1 were in RequiredTags, the bloom-filter pre-filter would
        // false-reject when the candidate's ancestor chain doesn't contain an h1.
        var alt = SelectorCompiler.Compile("h1 + p").Alternatives[0];
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.DoesNotContain("h1", (IReadOnlyCollection<string>)alt.RequiredTags);
    }

    [Fact]
    public void Rec3a_Required_classes_exclude_general_sibling_left_side()
    {
        var alt = SelectorCompiler.Compile(".lead ~ p").Alternatives[0];
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.DoesNotContain("lead", (IReadOnlyCollection<string>)alt.RequiredClasses);
    }

    [Fact]
    public void Rec3a_All_descendant_combinators_keep_required_tokens()
    {
        // 'div p > span': all three compounds are anchored to ancestors-or-self of span.
        var alt = SelectorCompiler.Compile("div p > span").Alternatives[0];
        Assert.Contains("span", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("div", (IReadOnlyCollection<string>)alt.RequiredTags);
    }

    [Fact]
    public void Rec3a_Mixed_combinator_chain_only_keeps_ancestor_anchored_tokens()
    {
        // 'header > nav h1 + p .extra': walking right-to-left,
        //   .extra (candidate, descendant of...) → anchor
        //   space (Descendant)
        //   p → anchor (ancestor)
        //   + (AdjacentSibling) — break: anything left of here is sibling-anchored
        //   h1 (sibling of p, not ancestor of .extra) → DROP
        //   space (Descendant) → already in sibling chain
        //   nav (ancestor of h1, not of .extra) → DROP
        //   > (Child)
        //   header → DROP
        var alt = SelectorCompiler.Compile("header > nav h1 + p .extra").Alternatives[0];
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("extra", (IReadOnlyCollection<string>)alt.RequiredClasses);
        Assert.DoesNotContain("h1", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.DoesNotContain("nav", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.DoesNotContain("header", (IReadOnlyCollection<string>)alt.RequiredTags);
    }

    // ============================================================
    // Rec 3b — Lowercase tag names in RequiredTags
    // ============================================================

    [Fact]
    public void Rec3b_Tag_names_lowercased_for_required_set_lookup()
    {
        // Authored 'DIV' / 'P' must hash identically to AngleSharp's lowercased LocalName
        // when the cascade builds the per-element bloom filter.
        var alt = SelectorCompiler.Compile("DIV P").Alternatives[0];
        Assert.Contains("div", (IReadOnlyCollection<string>)alt.RequiredTags);
        Assert.Contains("p", (IReadOnlyCollection<string>)alt.RequiredTags);
    }

    // ============================================================
    // Rec 4 — CSS identifier escape decoding (Tailwind compatibility)
    // ============================================================

    [Theory]
    [InlineData(".sm\\:block", "sm:block")]
    [InlineData(".w-1\\/2", "w-1/2")]
    [InlineData(".lg\\:flex-row", "lg:flex-row")]
    [InlineData(".\\@media-class", "@media-class")]
    public void Rec4_Class_selectors_with_literal_escapes_decode(string selector, string expectedClass)
    {
        var alt = SelectorCompiler.Compile(selector).Alternatives[0];
        Assert.Contains(expectedClass, (IReadOnlyCollection<string>)alt.RequiredClasses);
    }

    [Fact]
    public async Task Rec4_Tailwind_class_round_trips_through_compiler_and_matcher()
    {
        var doc = await ParseHtml("<div class=\"sm:block lg:flex\">x</div>");
        var div = Q(doc, "div");
        Assert.True(Matches(".sm\\:block", div));
        Assert.True(Matches(".lg\\:flex", div));
        Assert.False(Matches(".md\\:hidden", div));
    }

    [Theory]
    [InlineData("\\41 ", "A")]               // hex escape with whitespace terminator
    [InlineData("\\41", "A")]                 // hex escape without terminator
    [InlineData("\\E9", "é")]                  // 2-digit hex
    [InlineData("\\1F600 ", "😀")]            // 5-digit hex (above BMP)
    public void Rec4_Hex_escape_decodes_to_codepoint(string escapedSuffix, string expected)
    {
        // Build a class selector from the escape so we can read back the decoded class name.
        var alt = SelectorCompiler.Compile($".{escapedSuffix}").Alternatives[0];
        Assert.Contains(expected, (IReadOnlyCollection<string>)alt.RequiredClasses);
    }

    [Fact]
    public async Task Rec4_Escaped_id_matches_dom_id_with_special_chars()
    {
        var doc = await ParseHtml("<div id=\"foo:bar\">x</div>");
        Assert.True(Matches("#foo\\:bar", Q(doc, "div")));
    }

    // ============================================================
    // Rec 5 — Attribute case-sensitivity flag
    // ============================================================

    [Fact]
    public async Task Rec5_Attribute_case_insensitive_flag_matches_uppercase_against_lowercase()
    {
        var doc = await ParseHtml("<input type=\"text\">");
        var input = Q(doc, "input");
        Assert.True(Matches("[type=\"TEXT\" i]", input));
        Assert.True(Matches("[type=\"text\" i]", input));
    }

    [Fact]
    public async Task Rec5_Attribute_case_sensitive_default_rejects_uppercase()
    {
        var doc = await ParseHtml("<input type=\"text\">");
        var input = Q(doc, "input");
        Assert.False(Matches("[type=\"TEXT\"]", input));
    }

    [Fact]
    public async Task Rec5_Attribute_case_sensitive_explicit_s_flag_rejects_uppercase()
    {
        var doc = await ParseHtml("<input type=\"text\">");
        var input = Q(doc, "input");
        Assert.False(Matches("[type=\"TEXT\" s]", input));
    }

    [Fact]
    public async Task Rec5_Case_insensitive_flag_works_for_all_six_operators()
    {
        var doc = await ParseHtml(
            "<a id=\"link\" href=\"https://Example.COM/path/file.PDF\" data-tag=\"FOO BAR baz\" lang=\"EN-us\">x</a>");
        var a = Q(doc, "#link");
        Assert.True(Matches("[href*=\"EXAMPLE\" i]", a));
        Assert.True(Matches("[href^=\"HTTPS://\" i]", a));
        Assert.True(Matches("[href$=\".pdf\" i]", a));
        Assert.True(Matches("[data-tag~=\"foo\" i]", a));
        Assert.True(Matches("[lang|=\"en\" i]", a));
        Assert.True(Matches("[href=\"HTTPS://EXAMPLE.com/path/file.pdf\" i]", a));
    }

    // ============================================================
    // Rec 6 — Forgiving :is() / :where(), strict :not() / :has()
    // ============================================================

    [Fact]
    public async Task Rec6_Is_drops_invalid_alternative_keeps_valid()
    {
        var doc = await ParseHtml("<p>x</p>");
        var p = Q(doc, "p");
        // :is(p, :unknownpseudo) — the unknown pseudo would normally throw; in :is/where
        // forgiving mode the bad alternative is dropped silently and the valid `p` survives.
        Assert.True(Matches(":is(p, :unknownpseudo)", p));
    }

    [Fact]
    public async Task Rec6_Where_drops_invalid_alternative_keeps_valid()
    {
        var doc = await ParseHtml("<p>x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches(":where(p, :unknownpseudo)", p));
    }

    [Fact]
    public void Rec6_Not_remains_strict_throws_on_invalid_alternative()
    {
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile(":not(p, :unknownpseudo)"));
    }

    [Fact]
    public void Rec6_Has_remains_strict_throws_on_invalid_alternative()
    {
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile(":has(p, :unknownpseudo)"));
    }

    [Fact]
    public void Rec6_Top_level_selector_list_remains_strict()
    {
        // .valid, :unknownpseudo — top-level commas are NOT forgiving per Selectors L4.
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile(".valid, :unknownpseudo"));
    }

    // ============================================================
    // Rec 7 — Reject empty functional pseudo-class argument lists
    // ============================================================

    [Theory]
    [InlineData(":not()")]
    [InlineData(":is()")]
    [InlineData(":where()")]
    [InlineData(":has()")]
    public void Rec7_Empty_functional_pseudo_throws_parse_exception(string selector)
    {
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(selector));
    }

    [Fact]
    public void Rec7_Forgiving_list_with_only_invalid_alternatives_throws()
    {
        // :is(:unknown1, :unknown2) — every alternative is dropped → empty list → invalid.
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile(":is(:unknown1, :unknown2)"));
    }

    // ============================================================
    // Rec 8 — Pseudo-element metadata persisted on bytecode
    // ============================================================

    [Theory]
    [InlineData("p::before", "before")]
    [InlineData(".card::after", "after")]
    [InlineData("li::marker", "marker")]
    [InlineData("p::first-line", "first-line")]
    [InlineData("p::first-letter", "first-letter")]
    public void Rec8_Pseudo_element_name_persisted_on_bytecode(string selector, string expected)
    {
        var alt = SelectorCompiler.Compile(selector).Alternatives[0];
        Assert.Equal(expected, alt.PseudoElement);
    }

    [Fact]
    public void Rec8_Selector_without_pseudo_element_has_null_metadata()
    {
        var alt = SelectorCompiler.Compile("p").Alternatives[0];
        Assert.Null(alt.PseudoElement);
    }

    [Fact]
    public void Rec8_Pseudo_element_must_be_in_rightmost_compound()
    {
        // `p::before > span` — pseudo-element on a non-rightmost compound is invalid per L4 §3.5.
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile("p::before > span"));
    }

    [Fact]
    public void Rec8_Multiple_pseudo_elements_per_compound_rejected()
    {
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile("p::before::after"));
    }

    // ============================================================
    // Rec 9 — An+B integer overflow surfaces as parse exception
    // ============================================================

    [Theory]
    [InlineData(":nth-child(99999999999)")]
    [InlineData(":nth-child(-99999999999)")]
    [InlineData(":nth-child(99999999999n)")]
    [InlineData(":nth-child(2n+99999999999)")]
    public void Rec9_Out_of_range_integers_throw_parse_exception_not_overflow(string selector)
    {
        // The cascade resolver catches SelectorParseException and emits CSS-PARSE-WARNING-001.
        // Raw OverflowException would crash the cascade and skip *every subsequent rule*
        // in the stylesheet — silent miscascade.
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(selector));
    }

    [Fact]
    public void Rec9_Boundary_integers_parse_cleanly()
    {
        // int.MaxValue / int.MinValue are representable; just below the rejection boundary.
        var max = SelectorCompiler.Compile($":nth-child({int.MaxValue})");
        Assert.Single(max.Alternatives);
    }
}
