// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Behavioral tests for <see cref="SelectorMatcher"/>. Each test parses a small HTML fragment
/// via AngleSharp, picks a target element, and asserts whether the matcher reports a match.
/// Compiles the selector with <see cref="SelectorCompiler"/> at the start of every case so
/// the matcher's contract is exercised end-to-end (compile → match) rather than against
/// hand-built bytecode.
/// </summary>
public sealed class SelectorMatcherTests
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

    // ---------- type / class / id / universal ----------

    [Fact]
    public async Task Universal_matches_every_element()
    {
        var doc = await ParseHtml("<html><body><p>x</p></body></html>");
        var p = Q(doc, "p");
        Assert.True(Matches("*", p));
    }

    [Fact]
    public async Task Type_selector_matches_lowercased_tag()
    {
        var doc = await ParseHtml("<p>x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches("p", p));
        Assert.False(Matches("div", p));
    }

    [Fact]
    public async Task Class_selector_matches_when_class_present()
    {
        var doc = await ParseHtml("<p class=\"foo bar\">x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches(".foo", p));
        Assert.True(Matches(".bar", p));
        Assert.False(Matches(".baz", p));
    }

    [Fact]
    public async Task Id_selector_matches_when_id_equal()
    {
        var doc = await ParseHtml("<p id=\"x1\">x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches("#x1", p));
        Assert.False(Matches("#x2", p));
    }

    [Fact]
    public async Task Compound_selector_requires_all_parts()
    {
        var doc = await ParseHtml("<p class=\"foo\" id=\"x\">x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches("p.foo#x", p));
        Assert.False(Matches("p.bar#x", p));
        Assert.False(Matches("div.foo", p));
    }

    // ---------- attribute selectors ----------

    [Fact]
    public async Task Attribute_exists_matches()
    {
        var doc = await ParseHtml("<input disabled type=\"text\">");
        var input = Q(doc, "input");
        Assert.True(Matches("[disabled]", input));
        Assert.False(Matches("[readonly]", input));
    }

    [Fact]
    public async Task Attribute_equals_matches_exact()
    {
        var doc = await ParseHtml("<input type=\"text\">");
        var input = Q(doc, "input");
        Assert.True(Matches("[type=\"text\"]", input));
        Assert.False(Matches("[type=\"button\"]", input));
    }

    [Fact]
    public async Task Attribute_includes_word_match()
    {
        var doc = await ParseHtml("<p class=\"foo bar baz\">x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches("[class~=\"bar\"]", p));
        Assert.False(Matches("[class~=\"qux\"]", p));
        Assert.False(Matches("[class~=\"oo\"]", p)); // partial; not a whole word
    }

    [Fact]
    public async Task Attribute_dash_match()
    {
        var doc = await ParseHtml("<p lang=\"en-US\">x</p>");
        var p = Q(doc, "p");
        Assert.True(Matches("[lang|=\"en\"]", p));
        Assert.True(Matches("[lang|=\"en-US\"]", p));
        Assert.False(Matches("[lang|=\"fr\"]", p));
    }

    [Fact]
    public async Task Attribute_prefix_suffix_substring()
    {
        var doc = await ParseHtml("<a href=\"https://example.com/path/file.pdf\">x</a>");
        var a = Q(doc, "a");
        Assert.True(Matches("[href^=\"https://\"]", a));
        Assert.True(Matches("[href$=\".pdf\"]", a));
        Assert.True(Matches("[href*=\"example\"]", a));
        Assert.False(Matches("[href^=\"http://\"]", a));
        Assert.False(Matches("[href$=\".html\"]", a));
        Assert.False(Matches("[href*=\"missing\"]", a));
    }

    // ---------- combinators ----------

    [Fact]
    public async Task Descendant_walks_arbitrary_ancestors()
    {
        var doc = await ParseHtml("<div class=\"root\"><section><p>x</p></section></div>");
        var p = Q(doc, "p");
        Assert.True(Matches(".root p", p));
        Assert.True(Matches("div p", p));
        // Negative — no .other ancestor.
        Assert.False(Matches(".other p", p));
    }

    [Fact]
    public async Task Child_only_matches_immediate_parent()
    {
        var doc = await ParseHtml("<div class=\"root\"><section><p>x</p></section></div>");
        var p = Q(doc, "p");
        Assert.True(Matches("section > p", p));
        // .root is grandparent — child combinator must NOT cross it.
        Assert.False(Matches(".root > p", p));
    }

    [Fact]
    public async Task Adjacent_sibling_matches_only_immediate_predecessor()
    {
        var doc = await ParseHtml("<div><h1>t</h1><p>a</p><p>b</p></div>");
        var first = doc.QuerySelectorAll("p")[0]; // the one right after h1
        var second = doc.QuerySelectorAll("p")[1];
        Assert.True(Matches("h1 + p", first));
        // h1 is NOT adjacent to the second p (a p sits between them).
        Assert.False(Matches("h1 + p", second));
    }

    [Fact]
    public async Task General_sibling_matches_any_earlier_sibling()
    {
        var doc = await ParseHtml("<div><h1>t</h1><p>a</p><p>b</p></div>");
        var second = doc.QuerySelectorAll("p")[1];
        Assert.True(Matches("h1 ~ p", second));
        var h1 = Q(doc, "h1");
        // h1 has no preceding h1 sibling.
        Assert.False(Matches("h1 ~ h1", h1));
    }

    // ---------- structural pseudo-classes ----------

    [Fact]
    public async Task FirstChild_LastChild_OnlyChild()
    {
        var doc = await ParseHtml("<ul><li>a</li><li>b</li><li>c</li></ul>");
        var lis = doc.QuerySelectorAll("li");
        Assert.True(Matches(":first-child", lis[0]));
        Assert.False(Matches(":first-child", lis[1]));
        Assert.True(Matches(":last-child", lis[2]));
        Assert.False(Matches(":last-child", lis[1]));
        // None are only children.
        Assert.False(Matches(":only-child", lis[0]));

        var doc2 = await ParseHtml("<div><p>x</p></div>");
        Assert.True(Matches(":only-child", Q(doc2, "p")));
    }

    [Fact]
    public async Task FirstOfType_LastOfType_OnlyOfType()
    {
        var doc = await ParseHtml("<div><h1>t</h1><p>a</p><p>b</p><span>x</span></div>");
        var h1 = Q(doc, "h1");
        var ps = doc.QuerySelectorAll("p");
        var span = Q(doc, "span");

        Assert.True(Matches(":first-of-type", h1));
        Assert.True(Matches(":only-of-type", h1)); // only h1
        Assert.True(Matches(":first-of-type", ps[0]));
        Assert.False(Matches(":first-of-type", ps[1]));
        Assert.True(Matches(":last-of-type", ps[1]));
        Assert.True(Matches(":only-of-type", span));
    }

    [Fact]
    public async Task Empty_matches_no_children_or_whitespace_only()
    {
        var doc = await ParseHtml(
            "<div id=\"a\"></div>" +
            "<div id=\"b\">   </div>" +
            "<div id=\"c\">x</div>" +
            "<div id=\"d\"><p></p></div>");

        Assert.True(Matches(":empty", Q(doc, "#a")));
        Assert.True(Matches(":empty", Q(doc, "#b"))); // whitespace-only text doesn't disqualify
        Assert.False(Matches(":empty", Q(doc, "#c"))); // non-whitespace text disqualifies
        Assert.False(Matches(":empty", Q(doc, "#d"))); // child element disqualifies
    }

    [Fact]
    public async Task Root_matches_document_element()
    {
        var doc = await ParseHtml("<html><body><p>x</p></body></html>");
        var html = doc.DocumentElement!;
        Assert.True(Matches(":root", html));
        Assert.False(Matches(":root", Q(doc, "p")));
    }

    // ---------- :nth-* ----------

    [Theory]
    [InlineData(1, "odd", true)]
    [InlineData(2, "odd", false)]
    [InlineData(2, "even", true)]
    [InlineData(1, "even", false)]
    [InlineData(3, "2n+1", true)]
    [InlineData(4, "2n+1", false)]
    [InlineData(5, "n+3", true)]   // n+3 matches every index >= 3
    [InlineData(2, "n+3", false)]
    [InlineData(2, "-n+3", true)]  // -n+3 matches indices 3, 2, 1
    [InlineData(4, "-n+3", false)]
    [InlineData(3, "3", true)]     // plain integer = exactly that index
    [InlineData(2, "3", false)]
    public async Task Nth_child_matches_per_formula(int oneBasedIndex, string formula, bool expected)
    {
        // Build a list with 6 li elements; pick the one at oneBasedIndex.
        var doc = await ParseHtml("<ul><li>1</li><li>2</li><li>3</li><li>4</li><li>5</li><li>6</li></ul>");
        var li = doc.QuerySelectorAll("li")[oneBasedIndex - 1];
        Assert.Equal(expected, Matches($":nth-child({formula})", li));
    }

    [Fact]
    public async Task NthLastChild_counts_from_end()
    {
        var doc = await ParseHtml("<ul><li>1</li><li>2</li><li>3</li></ul>");
        var lis = doc.QuerySelectorAll("li");
        // last element is index 1 from the end.
        Assert.True(Matches(":nth-last-child(1)", lis[2]));
        Assert.True(Matches(":nth-last-child(3)", lis[0]));
    }

    [Fact]
    public async Task NthOfType_skips_other_tags()
    {
        // Sequence: h1, p, span, p, p — among only <p>, p[1] is index 1, p[3] index 2, p[4] index 3.
        var doc = await ParseHtml("<div><h1>t</h1><p>1</p><span>x</span><p>2</p><p>3</p></div>");
        var ps = doc.QuerySelectorAll("p");
        Assert.True(Matches(":nth-of-type(1)", ps[0]));
        Assert.True(Matches(":nth-of-type(2)", ps[1]));
        Assert.True(Matches(":nth-of-type(3)", ps[2]));
        Assert.False(Matches(":nth-of-type(2)", ps[0]));
    }

    // ---------- functional pseudo-classes ----------

    [Fact]
    public async Task Not_negates_match()
    {
        var doc = await ParseHtml("<div><p class=\"foo\">a</p><p>b</p></div>");
        var ps = doc.QuerySelectorAll("p");
        Assert.False(Matches("p:not(.foo)", ps[0])); // p[0] HAS .foo; :not should reject.
        Assert.True(Matches("p:not(.foo)", ps[1]));  // p[1] doesn't have .foo.
    }

    [Fact]
    public async Task Is_matches_any_alternative()
    {
        var doc = await ParseHtml("<div><h1>t</h1><h2>u</h2><p>v</p></div>");
        var h1 = Q(doc, "h1");
        var p = Q(doc, "p");
        Assert.True(Matches(":is(h1, h2)", h1));
        Assert.False(Matches(":is(h1, h2)", p));
    }

    [Fact]
    public async Task Where_matches_any_alternative_with_zero_specificity()
    {
        var doc = await ParseHtml("<h1>t</h1>");
        var h1 = Q(doc, "h1");
        Assert.True(Matches(":where(h1, h2)", h1));
    }

    [Fact]
    public async Task Has_always_returns_false_in_v1()
    {
        // v1 contract per phase-2 doc: :has() parses but never matches; cascade emits
        // CSS-HAS-RENDERING-NOT-IMPLEMENTED-001.
        var doc = await ParseHtml("<article><h1>t</h1></article>");
        var article = Q(doc, "article");
        Assert.False(Matches("article:has(h1)", article));
    }

    // ---------- dynamic-state pseudo-classes ----------

    [Fact]
    public async Task Hover_focus_active_visited_always_false()
    {
        var doc = await ParseHtml("<a href=\"x\">x</a>");
        var a = Q(doc, "a");
        Assert.False(Matches("a:hover", a));
        Assert.False(Matches("a:focus", a));
        Assert.False(Matches("a:active", a));
        Assert.False(Matches("a:visited", a));
    }

    [Fact]
    public async Task Link_AnyLink_matches_anchor_with_href()
    {
        var doc = await ParseHtml(
            "<a id=\"with\" href=\"x\">x</a>" +
            "<a id=\"without\">y</a>");
        Assert.True(Matches(":link", Q(doc, "#with")));
        Assert.False(Matches(":link", Q(doc, "#without")));
        Assert.True(Matches(":any-link", Q(doc, "#with")));
    }

    // ---------- selector lists / specificity reporting ----------

    [Fact]
    public async Task MatchList_returns_max_specificity_among_matched_alternatives()
    {
        var doc = await ParseHtml("<p id=\"x\" class=\"foo\">x</p>");
        var p = Q(doc, "p");
        var list = SelectorCompiler.Compile(".foo, #x");
        Assert.True(SelectorMatcher.MatchList(list, p, out var spec));
        Assert.Equal(new Specificity(1, 0, 0), spec); // #x dominates .foo
    }

    [Fact]
    public async Task MatchList_returns_false_when_no_alternative_matches()
    {
        var doc = await ParseHtml("<p>x</p>");
        var p = Q(doc, "p");
        var list = SelectorCompiler.Compile("div, span");
        Assert.False(SelectorMatcher.MatchList(list, p, out _));
    }

    [Fact]
    public async Task Match_throws_on_null_arguments()
    {
        var doc = await ParseHtml("<p>x</p>");
        var p = Q(doc, "p");
        var bytecode = SelectorCompiler.Compile("p").Alternatives[0];
        Assert.Throws<System.ArgumentNullException>(() => SelectorMatcher.Match(null!, p));
        Assert.Throws<System.ArgumentNullException>(() => SelectorMatcher.Match(bytecode, null!));
    }
}
