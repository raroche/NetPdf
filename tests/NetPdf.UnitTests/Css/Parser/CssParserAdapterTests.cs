// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser;

/// <summary>
/// Per-rule unit tests for the CSS adapter (Phase 2 Task 2). Each test fixes a single
/// AngleSharp.Css CSSOM input and asserts the shape of the emitted NetPdf AST.
/// </summary>
public sealed class CssParserAdapterTests
{
    [Fact]
    public void Adapt_null_sheet_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => CssParserAdapter.Adapt(null!));
    }

    [Fact]
    public async Task Adapt_empty_sheet_returns_empty_rules()
    {
        var sheet = await ParseSheet("");
        var stylesheet = CssParserAdapter.Adapt(sheet);
        Assert.Empty(stylesheet.Rules);
    }

    // ------------------------------------------------------------
    // Style rule shape
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_simple_style_rule_yields_selector_and_one_declaration()
    {
        var sheet = await ParseSheet(".foo { color: red }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal(".foo", rule.Selector.RawText);
        var declaration = Assert.Single(rule.Declarations);
        Assert.Equal("color", declaration.Property);
        // AngleSharp.Css normalizes named colors to rgba(...).
        Assert.Equal("rgba(255, 0, 0, 1)", declaration.Value.RawText);
        Assert.False(declaration.IsImportant);
    }

    [Fact]
    public async Task Adapt_compound_selectors_preserve_their_text()
    {
        var sheet = await ParseSheet(".a, .b > .c { color: red }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        // AngleSharp.Css canonicalizes whitespace around combinators (".a, .b > .c" → ".a, .b>.c").
        // Assert what AngleSharp gave us, not the source text.
        Assert.Equal(".a, .b>.c", rule.Selector.RawText);
    }

    [Fact]
    public async Task Adapt_important_declaration_sets_flag()
    {
        var sheet = await ParseSheet(".foo { color: red !important }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var declaration = Assert.Single(rule.Declarations);
        Assert.True(declaration.IsImportant);
    }

    [Fact]
    public async Task Adapt_shorthand_is_expanded_to_longhands()
    {
        // Documents (and locks down) AngleSharp.Css's shorthand-expansion behavior. The cascade
        // resolver in Task 7 depends on it: longhands are what the cascade operates on. A
        // future AngleSharp.Css change that disables shorthand expansion would fail this test
        // and prompt us to recover the expansion ourselves.
        var sheet = await ParseSheet(".foo { background: url('x.png') no-repeat }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var properties = rule.Declarations.Select(d => d.Property).ToHashSet();
        Assert.Contains("background-image", properties);
        Assert.Contains("background-repeat-x", properties);
        Assert.Contains("background-repeat-y", properties);
        // Original "background" shorthand is NOT in the AST — that's the documented fidelity loss.
        Assert.DoesNotContain("background", properties);
    }

    // ------------------------------------------------------------
    // Block at-rules: @media, @supports
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_media_rule_carries_prelude_and_child_rules()
    {
        var sheet = await ParseSheet("@media print { .a { color: red } }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("media", atRule.Name);
        Assert.Equal("print", atRule.Prelude);
        Assert.Empty(atRule.Declarations);
        var child = Assert.IsType<CssStyleRule>(Assert.Single(atRule.ChildRules));
        Assert.Equal(".a", child.Selector.RawText);
    }

    [Fact]
    public async Task Adapt_media_rule_with_complex_query_preserves_text()
    {
        var sheet = await ParseSheet("@media (min-width: 800px) and (orientation: landscape) { p { color: blue } }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("media", atRule.Name);
        Assert.Contains("min-width", atRule.Prelude);
        Assert.Contains("orientation", atRule.Prelude);
        Assert.Single(atRule.ChildRules);
    }

    [Fact]
    public async Task Adapt_supports_rule_carries_condition_in_prelude()
    {
        var sheet = await ParseSheet("@supports (display: grid) { .g { display: grid } }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("supports", atRule.Name);
        Assert.Contains("display", atRule.Prelude);
        Assert.Contains("grid", atRule.Prelude);
        Assert.Single(atRule.ChildRules);
    }

    // ------------------------------------------------------------
    // @keyframes — grouping rule whose children are keyframe entries
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_keyframes_rule_carries_name_and_keyframe_children()
    {
        var sheet = await ParseSheet("@keyframes pop { 0% { opacity: 0 } 100% { opacity: 1 } }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("keyframes", atRule.Name);
        Assert.Equal("pop", atRule.Prelude);
        Assert.Empty(atRule.Declarations);
        Assert.Equal(2, atRule.ChildRules.Count);
        var firstFrame = Assert.IsType<CssStyleRule>(atRule.ChildRules[0]);
        Assert.Equal("0%", firstFrame.Selector.RawText);
        Assert.Equal("opacity", Assert.Single(firstFrame.Declarations).Property);
    }

    // ------------------------------------------------------------
    // @font-face — declarations only, no children
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_font_face_rule_carries_declarations_no_prelude()
    {
        var sheet = await ParseSheet("@font-face { font-family: 'X'; src: url('x.woff2') format('woff2') }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("font-face", atRule.Name);
        Assert.Empty(atRule.Prelude);
        Assert.NotEmpty(atRule.Declarations);
        var familyDecl = atRule.Declarations.SingleOrDefault(d => d.Property == "font-family");
        Assert.NotNull(familyDecl);
        Assert.Contains("X", familyDecl!.Value.RawText);
        Assert.Empty(atRule.ChildRules);
    }

    // ------------------------------------------------------------
    // @page (declarations) and @top-center (margin-box) at-rules
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_page_rule_carries_declarations()
    {
        var sheet = await ParseSheet("@page { margin: 1in }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("page", atRule.Name);
        Assert.Empty(atRule.ChildRules);
        Assert.NotEmpty(atRule.Declarations);
    }

    [Fact]
    public async Task Adapt_named_page_rule_documents_anglesharp_selector_loss()
    {
        // Documents an AngleSharp.Css 1.0.0-beta.144 limitation: the `:first` pseudo-class on
        // an @page rule is dropped by the parser before reaching the CSSOM (SelectorText is
        // empty, CssText is "@page { ... }"). The adapter cannot recover the selector from
        // outside the CSSOM. When AngleSharp.Css fixes this — or when Phase 2 Task 3's pre-pass
        // tokenizer extracts the selector before AngleSharp parses the rule — flip the
        // assertion to require ":first" in the prelude.
        var sheet = await ParseSheet("@page :first { margin-top: 0 }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("page", atRule.Name);
        Assert.Empty(atRule.Prelude);
        Assert.NotEmpty(atRule.Declarations);
    }

    // ------------------------------------------------------------
    // Statement-form at-rules: @import, @charset, @namespace
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_import_rule_carries_url_in_prelude()
    {
        var sheet = await ParseSheet("@import url(\"foo.css\");");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("import", atRule.Name);
        Assert.Contains("foo.css", atRule.Prelude);
        Assert.Empty(atRule.Declarations);
        Assert.Empty(atRule.ChildRules);
    }

    [Fact]
    public async Task Adapt_import_rule_with_media_includes_media_in_prelude()
    {
        var sheet = await ParseSheet("@import url(\"print.css\") print;");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("import", atRule.Name);
        Assert.Contains("print.css", atRule.Prelude);
        Assert.Contains("print", atRule.Prelude);
    }

    [Fact]
    public async Task Adapt_charset_rule_carries_quoted_charset_in_prelude()
    {
        var sheet = await ParseSheet("@charset \"UTF-8\";");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("charset", atRule.Name);
        Assert.Contains("UTF-8", atRule.Prelude);
    }

    [Fact]
    public async Task Adapt_namespace_rule_with_prefix_carries_both_in_prelude()
    {
        var sheet = await ParseSheet("@namespace svg url(\"http://www.w3.org/2000/svg\");");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("namespace", atRule.Name);
        Assert.Contains("svg", atRule.Prelude);
        Assert.Contains("http://www.w3.org/2000/svg", atRule.Prelude);
    }

    [Fact]
    public async Task Adapt_namespace_rule_without_prefix_carries_url_only()
    {
        var sheet = await ParseSheet("@namespace url(\"http://example.com/ns\");");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("namespace", atRule.Name);
        Assert.Contains("http://example.com/ns", atRule.Prelude);
        Assert.DoesNotContain("svg", atRule.Prelude);
    }

    // ------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------

    [Fact]
    public void Adapt_emitted_types_declare_no_anglesharp_property_types()
    {
        // Compile-time contract: every property on the emitted AST records returns a NetPdf
        // type (or a primitive). If a future change accidentally exposes an AngleSharp type
        // through one of these records, the analyzer finds it here without recursive reflection.
        // We hand-list the types because the contract is small and the list itself is
        // documentation of "this is what downstream stages may depend on."
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssStylesheet));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssStyleRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssAtRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssDeclaration));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssSelector));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssValue));
    }

    [Fact]
    public async Task Adapt_is_deterministic_across_repeated_calls()
    {
        // Records' auto-generated Equals compares list properties by reference (List<T>
        // doesn't override Equals), so two adapt calls produce non-equal records by default.
        // Compare structurally via the AST helper instead.
        var sheet = await ParseSheet(".a { color: red } @media print { p { color: black } } @page { margin: 1in }");
        var first = CssParserAdapter.Adapt(sheet);
        var second = CssParserAdapter.Adapt(sheet);

        Assert.Equal(first.Rules.Count, second.Rules.Count);
        for (var i = 0; i < first.Rules.Count; i++)
        {
            AssertRulesStructurallyEqual(first.Rules[i], second.Rules[i]);
        }
    }

    private static void AssertRulesStructurallyEqual(CssRule a, CssRule b)
    {
        Assert.Equal(a.GetType(), b.GetType());
        switch (a)
        {
            case CssStyleRule sa when b is CssStyleRule sb:
                Assert.Equal(sa.Selector, sb.Selector);
                AssertDeclarationsEqual(sa.Declarations, sb.Declarations);
                break;
            case CssAtRule aa when b is CssAtRule ab:
                Assert.Equal(aa.Name, ab.Name);
                Assert.Equal(aa.Prelude, ab.Prelude);
                AssertDeclarationsEqual(aa.Declarations, ab.Declarations);
                Assert.Equal(aa.ChildRules.Count, ab.ChildRules.Count);
                for (var i = 0; i < aa.ChildRules.Count; i++)
                    AssertRulesStructurallyEqual(aa.ChildRules[i], ab.ChildRules[i]);
                break;
            default:
                Assert.Fail($"unrecognized rule pair: {a.GetType().Name} vs {b.GetType().Name}");
                break;
        }
    }

    private static void AssertDeclarationsEqual(IReadOnlyList<CssDeclaration> a, IReadOnlyList<CssDeclaration> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Property, b[i].Property);
            Assert.Equal(a[i].Value.RawText, b[i].Value.RawText);
            Assert.Equal(a[i].IsImportant, b[i].IsImportant);
        }
    }

    [Fact]
    public async Task Adapt_multiple_rules_preserve_source_order()
    {
        var sheet = await ParseSheet("""
            @import url("a.css");
            .a { color: red }
            @media print { .b { color: black } }
            @page { margin: 1in }
            """);
        var stylesheet = CssParserAdapter.Adapt(sheet);

        Assert.Equal(4, stylesheet.Rules.Count);
        Assert.Equal("import", Assert.IsType<CssAtRule>(stylesheet.Rules[0]).Name);
        Assert.IsType<CssStyleRule>(stylesheet.Rules[1]);
        Assert.Equal("media", Assert.IsType<CssAtRule>(stylesheet.Rules[2]).Name);
        Assert.Equal("page", Assert.IsType<CssAtRule>(stylesheet.Rules[3]).Name);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    private static async Task<ICssStyleSheet> ParseSheet(string css)
    {
        // Mirror HtmlParsingHost's configuration so the adapter sees rules in exactly the
        // shape a real document would deliver. The test embeds the CSS in a <style> element
        // so document.StyleSheets[0] is the resulting parsed sheet.
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, IsKeepingSourceReferences = true });
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(parser);
        var ctx = BrowsingContext.New(config);

        var html = $"<html><head><style>{css}</style></head><body></body></html>";
        var document = await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
        return document.StyleSheets.OfType<ICssStyleSheet>().Single();
    }

    private static void AssertAllPropertyTypesAreNonAngleSharp(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        Type type)
    {
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            // Drill into generic args so IReadOnlyList<CssRule> is asserted against CssRule.
            foreach (var t in EnumerateUsedTypes(prop.PropertyType))
            {
                var asm = t.Assembly.GetName().Name;
                Assert.False(
                    asm is not null && asm.StartsWith("AngleSharp", StringComparison.Ordinal),
                    $"{type.FullName}.{prop.Name} declares an AngleSharp type ({t.FullName}) — adapter is leaking.");
            }
        }
    }

    private static IEnumerable<Type> EnumerateUsedTypes(Type t)
    {
        yield return t;
        if (!t.IsGenericType) yield break;
        foreach (var arg in t.GetGenericArguments())
            foreach (var sub in EnumerateUsedTypes(arg))
                yield return sub;
    }
}
