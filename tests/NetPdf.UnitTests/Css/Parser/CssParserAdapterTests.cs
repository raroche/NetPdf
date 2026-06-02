// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser;

/// <summary>
/// Per-rule unit tests for the CSS adapter (Phase 2 Task 2 + review cycle 1). Each test
/// fixes a single AngleSharp.Css CSSOM input and asserts the shape of the emitted NetPdf AST.
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
    // Stylesheet metadata (cascade-relevant)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_default_overload_uses_author_origin_and_unknown_owner()
    {
        var sheet = await ParseSheet(".a { color: red }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        Assert.Null(stylesheet.Href);
        Assert.Equal(CssStylesheetOrigin.Author, stylesheet.Origin);
        Assert.Equal(CssStylesheetOwnerKind.Unknown, stylesheet.OwnerKind);
        Assert.Null(stylesheet.MediaQuery);
        Assert.False(stylesheet.IsDisabled);
        Assert.Equal(0, stylesheet.Order);
    }

    [Fact]
    public async Task Adapt_metadata_overload_carries_every_field()
    {
        // The cascade resolver depends on every metadata field; this test pins them all in
        // one place so a future regression that drops a field on the way through is loud.
        var sheet = await ParseSheet(".a { color: red }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet,
            href: "https://example.com/print.css",
            origin: CssStylesheetOrigin.User,
            ownerKind: CssStylesheetOwnerKind.LinkElement,
            mediaQuery: "print",
            isDisabled: true,
            order: 7);

        Assert.Equal("https://example.com/print.css", stylesheet.Href);
        Assert.Equal(CssStylesheetOrigin.User, stylesheet.Origin);
        Assert.Equal(CssStylesheetOwnerKind.LinkElement, stylesheet.OwnerKind);
        Assert.Equal("print", stylesheet.MediaQuery);
        Assert.True(stylesheet.IsDisabled);
        Assert.Equal(7, stylesheet.Order);
    }

    // ------------------------------------------------------------
    // Inline style adaptation (cascade input from style="...")
    // ------------------------------------------------------------

    [Fact]
    public void AdaptInlineStyle_null_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => CssParserAdapter.AdaptInlineStyle(null!));
    }

    [Fact]
    public async Task AdaptInlineStyle_extracts_declarations_from_style_attribute()
    {
        var element = await ParseElementWithStyle("color: red; font-weight: bold");

        var declarations = CssParserAdapter.AdaptInlineStyle(element.GetStyle()!);

        var byProperty = declarations.ToDictionary(d => d.Property);
        Assert.Equal("rgba(255, 0, 0, 1)", byProperty["color"].Value.RawText);
        Assert.Equal("bold", byProperty["font-weight"].Value.RawText);
        Assert.False(byProperty["color"].IsImportant);
    }

    [Fact]
    public async Task AdaptInlineStyle_preserves_important_flag()
    {
        var element = await ParseElementWithStyle("color: red !important");

        var declarations = CssParserAdapter.AdaptInlineStyle(element.GetStyle()!);

        var declaration = Assert.Single(declarations);
        Assert.True(declaration.IsImportant);
    }

    [Fact]
    public async Task AdaptInlineStyle_returns_empty_array_for_empty_attribute()
    {
        var element = await ParseElementWithStyle(string.Empty);

        var declarations = CssParserAdapter.AdaptInlineStyle(element.GetStyle()!);

        Assert.True(declarations.IsEmpty);
    }

    [Fact]
    public async Task AdaptInlineStyle_expands_shorthands_to_longhands_like_stylesheet()
    {
        // Same shorthand-expansion behavior the stylesheet adapter sees — the cascade then
        // doesn't have to deal with both shorthand and longhand inputs.
        var element = await ParseElementWithStyle("background: red");

        var declarations = CssParserAdapter.AdaptInlineStyle(element.GetStyle()!);

        var properties = declarations.Select(d => d.Property).ToHashSet();
        Assert.Contains("background-color", properties);
        Assert.DoesNotContain("background", properties);
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
        Assert.Equal("rgba(255, 0, 0, 1)", declaration.Value.RawText);
        Assert.False(declaration.IsImportant);
        // Source-location scaffolding stays at Unknown until Task 3 wires real positions.
        Assert.Equal(CssSourceLocation.Unknown, rule.Location);
        Assert.Equal(CssSourceLocation.Unknown, declaration.Location);
    }

    [Fact]
    public async Task Adapt_compound_selectors_preserve_their_text()
    {
        var sheet = await ParseSheet(".a, .b > .c { color: red }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        // AngleSharp.Css canonicalizes whitespace around combinators (".a, .b > .c" → ".a, .b>.c").
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
        var sheet = await ParseSheet(".foo { background: url('x.png') no-repeat }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var properties = rule.Declarations.Select(d => d.Property).ToHashSet();
        Assert.Contains("background-image", properties);
        Assert.Contains("background-repeat-x", properties);
        Assert.Contains("background-repeat-y", properties);
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
        Assert.Equal(2, atRule.ChildRules.Length);
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
    // @page (declarations + AngleSharp gaps)
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
    public async Task Adapt_page_rule_recovers_the_dropped_size_descriptor()
    {
        // AngleSharp.Css drops the `size` descriptor (it's a page descriptor, not a property);
        // the pre-pass recovers it + the adapter re-attaches it as a synthetic declaration so
        // downstream @page resolution can read it (Phase 3 Task 21 cycle 2).
        const string css = "@page { size: A4 landscape; margin: 1in }";
        var sheet = await ParseSheet(css);
        var stylesheet = CssParserAdapter.Adapt(sheet, CssPreprocessor.Process(css), href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.Unknown,
            mediaQuery: null, isDisabled: false, order: 0);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var size = atRule.Declarations.SingleOrDefault(d => d.Property == "size");
        Assert.NotNull(size);
        Assert.Equal("A4 landscape", size!.Value.RawText);
    }

    [Fact]
    public async Task Adapt_page_rule_size_descriptor_preserves_importance()
    {
        // The pre-pass strips `!important` from the recovered `size` value text + records the
        // importance; the adapter stamps it onto the synthetic declaration so the @page size
        // cascade honors it (Phase 3 Task 21 cycle 2, review P2). Pre-fix the value text kept
        // "!important" (so the size resolver rejected it) and IsImportant was always false.
        const string css = "@page { size: A5 !important }";
        var sheet = await ParseSheet(css);
        var stylesheet = CssParserAdapter.Adapt(sheet, CssPreprocessor.Process(css), href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.Unknown,
            mediaQuery: null, isDisabled: false, order: 0);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var size = atRule.Declarations.SingleOrDefault(d => d.Property == "size");
        Assert.NotNull(size);
        Assert.Equal("A5", size!.Value.RawText);   // "!important" stripped from the value text
        Assert.True(size.IsImportant);
    }

    [Fact]
    public async Task Adapt_page_rule_pseudo_selector_loss_is_a_known_task_3_blocker()
    {
        // Tracks an AngleSharp.Css 1.0.0-beta.144 limitation: `:first` / `:left` / `:right`
        // and named-page selectors are dropped before the CSSOM (SelectorText is empty,
        // CssText is "@page { ... }"). Phase 2 Task 3's pre-pass tokenizer is the planned
        // fix — it tokenizes the raw stylesheet text and re-attaches the page selector
        // before AngleSharp sees the rule. This test pins the current loss so a future
        // change that silently regresses Task 3's recovery surfaces here.
        var sheet = await ParseSheet("@page :first { margin-top: 0 }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var atRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("page", atRule.Name);
        Assert.Empty(atRule.Prelude);          // Loss — Task 3 must recover ":first" here.
        Assert.NotEmpty(atRule.Declarations);
    }

    [Fact]
    public async Task Adapt_page_rule_margin_box_loss_is_a_known_task_3_blocker()
    {
        // Tracks a second AngleSharp.Css 1.0.0-beta.144 limitation: margin-box at-rules
        // (@top-center, @bottom-right-corner, …) inside @page are silently dropped — they
        // reach neither the parent ICssPageRule nor the top of the stylesheet. Phase 2
        // Task 3's pre-pass tokenizer must recover them and re-parent them under their
        // owning @page rule. The adapter's AdaptMarginRule case is currently dead in the
        // dispatch but kept for forward compat.
        var sheet = await ParseSheet(
            "@page { margin: 1in; @top-center { content: \"Header\"; } @bottom-right { content: counter(page); } }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var pageRule = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("page", pageRule.Name);
        // Task 3 must populate ChildRules with two CssAtRule entries (top-center, bottom-right).
        Assert.Empty(pageRule.ChildRules);
        // Top-level rule list contains only @page — the margin-boxes vanished entirely.
        Assert.Single(stylesheet.Rules);
    }

    // ------------------------------------------------------------
    // @import — typed CssImportRule subtype
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_import_rule_emits_typed_subtype_with_url()
    {
        var sheet = await ParseSheet("@import url(\"foo.css\");");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var importRule = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("foo.css", importRule.Url);
        Assert.Empty(importRule.MediaQuery);
        Assert.Null(importRule.LayerName);
        Assert.Null(importRule.SupportsCondition);
        Assert.True(importRule.ImportedRules.IsEmpty);
    }

    [Fact]
    public async Task Adapt_import_rule_with_media_carries_media_query()
    {
        var sheet = await ParseSheet("@import url(\"print.css\") screen and (min-width: 800px);");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var importRule = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("print.css", importRule.Url);
        Assert.Contains("min-width", importRule.MediaQuery);
        Assert.Null(importRule.LayerName);
        Assert.Null(importRule.SupportsCondition);
    }

    [Fact]
    public async Task Adapt_import_rule_layer_clause_is_lost_until_task_3()
    {
        // AngleSharp.Css 1.0.0-beta.144 mangles `@import url(...) layer(name)` into a
        // malformed media query "not all", losing the layer name entirely. The adapter
        // exposes the LayerName slot as null until Phase 2 Task 3's pre-pass tokenizer
        // recovers the layer clause from the raw stylesheet text. This test pins the
        // current loss + slot existence.
        var sheet = await ParseSheet("@import url(\"theme.css\") layer(framework);");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var importRule = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("theme.css", importRule.Url);
        Assert.Null(importRule.LayerName); // Task 3 must populate to "framework".
        // Media query carries AngleSharp's "not all" garbage — also a Task 3 fix.
        Assert.Equal("not all", importRule.MediaQuery);
    }

    [Fact]
    public async Task Adapt_import_rule_supports_clause_is_lost_until_task_3()
    {
        var sheet = await ParseSheet("@import url(\"grid.css\") supports(display: grid);");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        var importRule = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Null(importRule.SupportsCondition); // Task 3 must populate.
    }

    // ------------------------------------------------------------
    // Statement-form at-rules: @charset, @namespace
    // ------------------------------------------------------------

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
    // Robustness: clean-room contract, source order, deep equality
    // ------------------------------------------------------------

    [Fact]
    public void Adapt_emitted_types_declare_no_anglesharp_property_types()
    {
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssStylesheet));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssStyleRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssAtRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssImportRule));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssDeclaration));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssSelector));
        AssertAllPropertyTypesAreNonAngleSharp(typeof(CssValue));
    }

    [Fact]
    public async Task Adapt_is_deterministic_across_repeated_calls()
    {
        // Note on equality: ImmutableArray<T>.Equals checks reference-equality of the
        // underlying array, not contents — so the records' auto-generated Equals still
        // returns false for two adapt calls. Compare structurally via SequenceEqual.
        var sheet = await ParseSheet(".a { color: red } @media print { p { color: black } } @page { margin: 1in }");
        var first = CssParserAdapter.Adapt(sheet);
        var second = CssParserAdapter.Adapt(sheet);

        Assert.Equal(first.Rules.Length, second.Rules.Length);
        for (var i = 0; i < first.Rules.Length; i++)
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
                Assert.Equal(aa.ChildRules.Length, ab.ChildRules.Length);
                for (var i = 0; i < aa.ChildRules.Length; i++)
                    AssertRulesStructurallyEqual(aa.ChildRules[i], ab.ChildRules[i]);
                break;
            case CssImportRule ia when b is CssImportRule ib:
                Assert.Equal(ia.Url, ib.Url);
                Assert.Equal(ia.MediaQuery, ib.MediaQuery);
                Assert.Equal(ia.LayerName, ib.LayerName);
                Assert.Equal(ia.SupportsCondition, ib.SupportsCondition);
                break;
            default:
                Assert.Fail($"unrecognized rule pair: {a.GetType().Name} vs {b.GetType().Name}");
                break;
        }
    }

    private static void AssertDeclarationsEqual(ImmutableArray<CssDeclaration> a, ImmutableArray<CssDeclaration> b)
    {
        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
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

        Assert.Equal(4, stylesheet.Rules.Length);
        Assert.IsType<CssImportRule>(stylesheet.Rules[0]);
        Assert.IsType<CssStyleRule>(stylesheet.Rules[1]);
        Assert.Equal("media", Assert.IsType<CssAtRule>(stylesheet.Rules[2]).Name);
        Assert.Equal("page", Assert.IsType<CssAtRule>(stylesheet.Rules[3]).Name);
    }

    [Fact]
    public async Task Adapt_emits_immutable_array_collections()
    {
        // Pin the immutability contract: the rule list and inner declaration / child-rule
        // lists are ImmutableArray<T>, not mutable List<T> behind an IReadOnlyList<T>
        // interface. This makes the cascade's per-element caching safe without defensive copies.
        var sheet = await ParseSheet(".a { color: red } @media print { p { color: black } }");
        var stylesheet = CssParserAdapter.Adapt(sheet);

        // Top-level rules — typed as ImmutableArray<CssRule> on the record.
        AssertIsImmutableArrayOf<CssRule>(stylesheet.Rules);

        var styleRule = Assert.IsType<CssStyleRule>(stylesheet.Rules[0]);
        AssertIsImmutableArrayOf<CssDeclaration>(styleRule.Declarations);

        var mediaRule = Assert.IsType<CssAtRule>(stylesheet.Rules[1]);
        AssertIsImmutableArrayOf<CssRule>(mediaRule.ChildRules);
        AssertIsImmutableArrayOf<CssDeclaration>(mediaRule.Declarations);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    private static async Task<ICssStyleSheet> ParseSheet(string css)
    {
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

    private static async Task<IElement> ParseElementWithStyle(string styleAttribute)
    {
        // Build a doc whose <p> carries the style attribute under test, then return the <p>.
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, IsKeepingSourceReferences = true });
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(parser);
        var ctx = BrowsingContext.New(config);

        var html = $"<html><body><p id=\"target\" style=\"{styleAttribute}\">x</p></body></html>";
        var document = await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
        return document.QuerySelector("#target")!;
    }

    private static void AssertIsImmutableArrayOf<T>(ImmutableArray<T> _)
    {
        // The fact that the call site type-checked this method invocation with the property
        // proves the property's declared type is ImmutableArray<T>. No runtime work needed.
        Assert.True(true);
    }

    private static void AssertAllPropertyTypesAreNonAngleSharp(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        Type type)
    {
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
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
