// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Review-cycle 2 tests for Phase 2 Task 6 — covers ten review-driven hardening fixes that
/// closed correctness gaps left by review cycle 1: pseudo-elements inside functional pseudo-
/// classes, pseudo-element tail allowlist, forgiving-list match-nothing semantics, ASCII-only
/// case folding + HTML CI defaults, <c>--</c>-prefix identifiers, <c>:empty</c> NBSP-aware
/// whitespace, <c>:nth-child(An+B of S)</c>, legacy single-colon pseudo-elements, and
/// whitespace-dependent descendant combinator parsing.
/// </summary>
public sealed class SelectorReviewCycle2Tests
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
    // Rec 1 — Pseudo-elements rejected inside functional pseudo-classes
    // ============================================================

    [Fact]
    public async Task Rec1_Is_with_pseudo_element_does_not_match_everything()
    {
        // Before: :is(::before) inner compound emitted no match opcode → bytecode = [End]
        //         → matched every element. Silent miscascade.
        // After: pseudo-element inside :is() is invalid. Forgiving mode drops the bad
        //        alternative; whole sub-list is empty → :is() matches nothing.
        var doc = await ParseHtml("<p>x</p><div>y</div>");
        Assert.False(Matches(":is(::before)", Q(doc, "p")));
        Assert.False(Matches(":is(::before)", Q(doc, "div")));
    }

    [Fact]
    public async Task Rec1_Where_with_pseudo_element_does_not_match_everything()
    {
        var doc = await ParseHtml("<p>x</p>");
        Assert.False(Matches(":where(::before)", Q(doc, "p")));
    }

    [Fact]
    public void Rec1_Not_with_pseudo_element_throws_strict()
    {
        // :not() is non-forgiving — a pseudo-element argument is invalid → parse error.
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile("p:not(::before)"));
    }

    [Fact]
    public void Rec1_Has_with_pseudo_element_throws_strict()
    {
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile("p:has(::before)"));
    }

    [Fact]
    public async Task Rec1_Is_with_mixed_pseudo_element_and_valid_alternative_drops_bad_keeps_good()
    {
        // :is(::before, p) — forgiving: ::before is dropped, p survives.
        var doc = await ParseHtml("<p>x</p><div>y</div>");
        Assert.True(Matches(":is(::before, p)", Q(doc, "p")));
        Assert.False(Matches(":is(::before, p)", Q(doc, "div")));
    }

    [Fact]
    public void Rec1_Legacy_single_colon_pseudo_element_inside_is_also_rejected()
    {
        // :is(:before) — legacy syntax for pseudo-element is also invalid in functional contexts.
        // Forgiving mode drops it, sub-list ends up empty (valid match-nothing).
        var list = SelectorCompiler.Compile(":is(:before)");
        Assert.Single(list.Alternatives);
        Assert.True(list.Alternatives[0].SubGroups[0].SubGroups.IsEmpty);
    }

    // ============================================================
    // Rec 2 — Pseudo-element tail allowlist
    // ============================================================

    [Theory]
    [InlineData("p::before:hover")]      // :hover allowed
    [InlineData("p::before:focus")]
    [InlineData("p::before:active")]
    [InlineData("p::before:focus-visible")]
    [InlineData("p::before:focus-within")]
    [InlineData("p::before:not(.foo)")]  // :not() allowed
    [InlineData("p::before:is(.a, .b)")]
    [InlineData("p::before:where(.a)")]
    public void Rec2_Allowed_tail_pseudo_classes_compile(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        Assert.NotNull(list.Alternatives[0].PseudoElement);
    }

    [Theory]
    [InlineData("p::before:first-child")]   // structural pseudo-class - rejected
    [InlineData("p::before:last-child")]
    [InlineData("p::before:nth-child(1)")]
    [InlineData("p::before:empty")]
    [InlineData("p::before:root")]
    [InlineData("p::before.foo")]            // class - rejected
    [InlineData("p::before#bar")]            // id - rejected
    [InlineData("p::before[disabled]")]      // attribute - rejected
    public void Rec2_Disallowed_tail_after_pseudo_element_throws(string selector)
    {
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(selector));
    }

    // ============================================================
    // Rec 3 — Forgiving list match-nothing semantics
    // ============================================================

    [Fact]
    public async Task Rec3_Is_with_all_invalid_arguments_is_valid_but_matches_nothing()
    {
        var doc = await ParseHtml("<p>x</p>");
        var p = Q(doc, "p");
        // All alternatives are invalid → empty sub-list → matches nothing.
        var list = SelectorCompiler.Compile(":is(:unknown1, :unknown2, ::before)");
        Assert.Single(list.Alternatives);
        Assert.False(SelectorMatcher.MatchList(list, p, out _));
    }

    [Fact]
    public void Rec3_Authored_empty_is_still_throws()
    {
        // Authored-empty `:is()` (nothing between parens, not even invalid alternatives) is
        // a parse error — distinguishes from cycle 2's "all-invalid-dropped" valid case.
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(":is()"));
    }

    [Fact]
    public void Rec3_Authored_whitespace_only_is_still_throws()
    {
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(":is(   )"));
    }

    // ============================================================
    // Rec 4 — ASCII-only case folding + HTML CI defaults
    // ============================================================

    [Fact]
    public async Task Rec4_Type_attribute_default_is_case_insensitive()
    {
        // 'type' is on the HTML CI list, so [type="TEXT"] matches <input type="text"> by default.
        var doc = await ParseHtml("<input type=\"text\">");
        var input = Q(doc, "input");
        Assert.True(Matches("[type=\"TEXT\"]", input));
        Assert.True(Matches("[type=\"text\"]", input));
    }

    [Fact]
    public async Task Rec4_Type_attribute_explicit_s_flag_overrides_default_ci()
    {
        // Explicit `s` flag forces case-sensitive even for HTML CI attributes.
        var doc = await ParseHtml("<input type=\"text\">");
        var input = Q(doc, "input");
        Assert.False(Matches("[type=\"TEXT\" s]", input));
        Assert.True(Matches("[type=\"text\" s]", input));
    }

    [Theory]
    [InlineData("lang", "en", "EN")]
    [InlineData("rel", "stylesheet", "Stylesheet")]
    [InlineData("dir", "ltr", "LTR")]
    [InlineData("disabled", "disabled", "DISABLED")]
    public async Task Rec4_HTML_attributes_are_case_insensitive_by_default(string attr, string actual, string queried)
    {
        var doc = await ParseHtml($"<div {attr}=\"{actual}\">x</div>");
        var div = Q(doc, "div");
        Assert.True(Matches($"[{attr}=\"{queried}\"]", div));
    }

    [Fact]
    public async Task Rec4_Custom_data_attribute_is_case_sensitive_by_default()
    {
        var doc = await ParseHtml("<div data-x=\"text\">x</div>");
        var div = Q(doc, "div");
        Assert.False(Matches("[data-x=\"TEXT\"]", div));
        Assert.True(Matches("[data-x=\"TEXT\" i]", div));  // explicit i flag flips
    }

    [Fact]
    public async Task Rec4_Case_folding_is_ASCII_only_not_Unicode()
    {
        // Per CSS Selectors L4 §6.3.2, case-insensitive matching is ASCII-only — so 'é' /
        // 'É' do NOT fold together (they're outside the A-Z / a-z range). Using
        // OrdinalIgnoreCase would have over-matched here.
        var doc = await ParseHtml("<div data-x=\"é\">x</div>");
        var div = Q(doc, "div");
        Assert.False(Matches("[data-x=\"É\" i]", div));
    }

    [Fact]
    public async Task Rec4_Case_insensitive_word_match_is_ASCII_only()
    {
        var doc = await ParseHtml("<div data-tag=\"foo\">x</div>");
        var div = Q(doc, "div");
        Assert.True(Matches("[data-tag~=\"FOO\" i]", div));
        Assert.False(Matches("[data-tag~=\"FOO\"]", div));
    }

    // ============================================================
    // Rec 5 — -- prefix identifiers
    // ============================================================

    [Theory]
    [InlineData(".--foo")]
    [InlineData("#--my-id")]
    public void Rec5_Identifiers_starting_with_double_hyphen_compile(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        Assert.Single(list.Alternatives);
    }

    [Fact]
    public async Task Rec5_Class_with_double_hyphen_prefix_matches_dom()
    {
        var doc = await ParseHtml("<div class=\"--foo\">x</div>");
        Assert.True(Matches(".--foo", Q(doc, "div")));
    }

    [Fact]
    public async Task Rec5_Id_with_double_hyphen_prefix_matches_dom()
    {
        var doc = await ParseHtml("<div id=\"--my-id\">x</div>");
        Assert.True(Matches("#--my-id", Q(doc, "div")));
    }

    [Fact]
    public void Rec5_Attribute_value_can_start_with_double_hyphen()
    {
        // Unquoted attribute values per Syntax L3 follow the same identifier grammar.
        // [data-token=--my-token] is a valid selector.
        var list = SelectorCompiler.Compile("[data-token=--my-token]");
        Assert.Single(list.Alternatives);
    }

    // ============================================================
    // Rec 6 — :empty only ASCII whitespace
    // ============================================================

    [Fact]
    public async Task Rec6_Empty_with_ASCII_whitespace_only_text_matches()
    {
        var doc = await ParseHtml("<p>   \t\n</p>");
        Assert.True(Matches(":empty", Q(doc, "p")));
    }

    [Fact]
    public async Task Rec6_Empty_with_NBSP_does_not_match()
    {
        // NBSP (U+00A0) is Unicode whitespace but NOT HTML "ASCII whitespace" per HTML §3.2.5.
        // <p>&nbsp;</p> must NOT match :empty. The previous string.IsNullOrWhiteSpace
        // implementation incorrectly considered NBSP whitespace.
        var doc = await ParseHtml("<p> </p>");
        Assert.False(Matches(":empty", Q(doc, "p")));
    }

    [Fact]
    public async Task Rec6_Empty_with_Unicode_whitespace_does_not_match()
    {
        // Other Unicode whitespace characters also disqualify (e.g., U+2028 line separator).
        var doc = await ParseHtml("<p>\u2028</p>");
        Assert.False(Matches(":empty", Q(doc, "p")));
    }

    [Fact]
    public async Task Rec6_Empty_with_truly_empty_element_matches()
    {
        var doc = await ParseHtml("<p></p>");
        Assert.True(Matches(":empty", Q(doc, "p")));
    }

    // ============================================================
    // Rec 7 — :nth-child(An+B of S)
    // ============================================================

    [Fact]
    public async Task Rec7_NthChild_of_S_filters_siblings_by_selector()
    {
        // 5 li's; 3 are .item. :nth-child(2 of .item) should match the 2nd .item (li 3).
        var doc = await ParseHtml(
            "<ul>" +
            "<li>plain1</li>" +
            "<li class=\"item\">A</li>" +
            "<li>plain2</li>" +
            "<li class=\"item\">B</li>" +
            "<li class=\"item\">C</li>" +
            "</ul>");
        var lis = doc.QuerySelectorAll("li");
        Assert.False(Matches(":nth-child(1 of .item)", lis[0]));  // not an .item
        Assert.True(Matches(":nth-child(1 of .item)", lis[1]));   // 1st .item
        Assert.False(Matches(":nth-child(1 of .item)", lis[2]));  // not an .item
        Assert.True(Matches(":nth-child(2 of .item)", lis[3]));   // 2nd .item
        Assert.True(Matches(":nth-child(3 of .item)", lis[4]));   // 3rd .item
    }

    [Fact]
    public async Task Rec7_NthLastChild_of_S_counts_from_end()
    {
        var doc = await ParseHtml(
            "<ul>" +
            "<li class=\"item\">A</li>" +
            "<li class=\"item\">B</li>" +
            "<li class=\"item\">C</li>" +
            "</ul>");
        var lis = doc.QuerySelectorAll("li");
        Assert.True(Matches(":nth-last-child(1 of .item)", lis[2]));
        Assert.True(Matches(":nth-last-child(2 of .item)", lis[1]));
    }

    [Fact]
    public async Task Rec7_NthChild_even_of_filter()
    {
        var doc = await ParseHtml(
            "<ul>" +
            "<li class=\"i\">A</li>" +
            "<li class=\"i\">B</li>" +
            "<li class=\"i\">C</li>" +
            "<li class=\"i\">D</li>" +
            "</ul>");
        var lis = doc.QuerySelectorAll("li");
        // Even items in the .i set: B (index 2), D (index 4).
        Assert.False(Matches(":nth-child(even of .i)", lis[0]));
        Assert.True(Matches(":nth-child(even of .i)", lis[1]));
        Assert.False(Matches(":nth-child(even of .i)", lis[2]));
        Assert.True(Matches(":nth-child(even of .i)", lis[3]));
    }

    [Fact]
    public void Rec7_NthChild_of_S_specificity_includes_filter_max()
    {
        // Per CSS Selectors L4 §17, :nth-child(of S) contributes (0, 1, 0) + max(S).
        var alt = SelectorCompiler.Compile(":nth-child(2 of #foo)").Alternatives[0];
        Assert.Equal(new Specificity(1, 1, 0), alt.Specificity);
    }

    [Fact]
    public async Task Rec7_NthChild_of_S_with_complex_filter_works()
    {
        // :nth-child(even of :not([hidden])) — count visible siblings.
        var doc = await ParseHtml(
            "<ul>" +
            "<li>A</li>" +
            "<li hidden>B</li>" +
            "<li>C</li>" +
            "<li hidden>D</li>" +
            "<li>E</li>" +
            "</ul>");
        var lis = doc.QuerySelectorAll("li");
        // Visible items: A (index 1), C (index 2), E (index 3). Even: C only.
        Assert.False(Matches(":nth-child(even of :not([hidden]))", lis[0])); // A: index 1
        Assert.False(Matches(":nth-child(even of :not([hidden]))", lis[1])); // hidden, not in set
        Assert.True(Matches(":nth-child(even of :not([hidden]))", lis[2]));  // C: index 2
    }

    [Fact]
    public void Rec7_NthOfType_does_not_accept_of_clause()
    {
        // Per spec, only :nth-child / :nth-last-child accept `of S`.
        Assert.Throws<SelectorParseException>(
            () => SelectorCompiler.Compile(":nth-of-type(2 of .item)"));
    }

    // ============================================================
    // Rec 8 — Legacy single-colon pseudo-elements
    // ============================================================

    [Theory]
    [InlineData("p:before", "before")]
    [InlineData("p:after", "after")]
    [InlineData("p:first-line", "first-line")]
    [InlineData("p:first-letter", "first-letter")]
    public void Rec8_Legacy_single_colon_pseudo_elements_compile(string selector, string expected)
    {
        var alt = SelectorCompiler.Compile(selector).Alternatives[0];
        Assert.Equal(expected, alt.PseudoElement);
    }

    [Fact]
    public void Rec8_Single_colon_other_pseudo_classes_unchanged()
    {
        // Sanity — non-legacy single-colon pseudo-classes still parse as pseudo-classes.
        var alt = SelectorCompiler.Compile("p:hover").Alternatives[0];
        Assert.Null(alt.PseudoElement);
    }

    [Fact]
    public void Rec8_Legacy_pseudo_element_specificity_matches_modern()
    {
        // :before should have same specificity as ::before — both contribute (0, 0, 1) for
        // the pseudo-element. p:before = (0, 0, 2), p::before = (0, 0, 2).
        var legacy = SelectorCompiler.Compile("p:before").Alternatives[0];
        var modern = SelectorCompiler.Compile("p::before").Alternatives[0];
        Assert.Equal(modern.Specificity, legacy.Specificity);
    }

    // ============================================================
    // Rec 9 — Whitespace-dependent descendant combinator
    // ============================================================

    [Theory]
    [InlineData("div*")]
    [InlineData(".foo*")]
    [InlineData("h1*")]
    public void Rec9_Adjacent_compound_starts_without_whitespace_throw(string selector)
    {
        // div* is NOT div<descendant>* — the previous parser silently promoted the implicit
        // gap to descendant combinator, which is wrong. Now it throws so the cascade emits
        // CSS-PARSE-WARNING-001 and the rule is dropped.
        Assert.Throws<SelectorParseException>(() => SelectorCompiler.Compile(selector));
    }

    [Theory]
    [InlineData("div *")]
    [InlineData(".foo *")]
    [InlineData("div > *")]
    public void Rec9_Whitespace_or_explicit_combinator_still_works(string selector)
    {
        var list = SelectorCompiler.Compile(selector);
        Assert.Single(list.Alternatives);
    }

    [Fact]
    public void Rec9_Compound_internal_chain_unaffected()
    {
        // Within a compound, .#[: don't need whitespace — div.foo, div#bar, div[disabled],
        // div:hover all parse as a single compound.
        Assert.Single(SelectorCompiler.Compile("div.foo").Alternatives);
        Assert.Single(SelectorCompiler.Compile("div#bar").Alternatives);
        Assert.Single(SelectorCompiler.Compile("div[disabled]").Alternatives);
        Assert.Single(SelectorCompiler.Compile("div:hover").Alternatives);
    }
}
