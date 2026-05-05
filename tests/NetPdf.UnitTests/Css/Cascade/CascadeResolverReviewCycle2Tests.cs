// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Review-cycle 2 tests for Phase 2 Task 7. Covers the deeper recommendations:
/// real layer plumbing through the resolver, <c>@import supports()</c> paren-tolerance,
/// real media-query feature evaluation, and the end-to-end preprocessor path used by
/// integration tests. Each rec is a section.
/// </summary>
public sealed class CascadeResolverReviewCycle2Tests
{
    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static IElement Q(IDocument doc, string css) => doc.QuerySelector(css)!;

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    /// <summary>Build a synthetic stylesheet from a flat list of rules so layer / @import
    /// scenarios that AngleSharp.Css drops can still be tested end-to-end.</summary>
    private static CssStylesheet Sheet(params CssRule[] rules) => new(
        Rules: ImmutableArray.Create(rules),
        Href: null, Origin: CssStylesheetOrigin.Author,
        OwnerKind: CssStylesheetOwnerKind.StyleElement,
        MediaQuery: null, IsDisabled: false, Order: 0,
        Location: CssSourceLocation.Unknown);

    private static CssStyleRule StyleRule(string selector, string property, string value, bool important = false) =>
        new(new CssSelector(selector),
            ImmutableArray.Create(new CssDeclaration(property, new CssValue(value), important, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);

    private static CssAtRule LayerRule(string prelude, params CssRule[] children) =>
        new(Name: "layer", Prelude: prelude,
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create(children),
            Location: CssSourceLocation.Unknown);

    // ============================================================
    // Rec 1 — Layer plumbing through the resolver
    // ============================================================

    [Fact]
    public async Task Rec1_Layered_normal_loses_to_unlayered_normal()
    {
        // Per CSS Cascade L4 §6.4.4: unlayered NORMAL beats any named layer.
        // Layer "foo" → p { color: red }; unlayered → p { color: blue }.
        // Unlayered should win.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(
            LayerRule("foo", StyleRule("p", "color", "red")),
            StyleRule("p", "color", "blue"));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("blue", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Layered_important_beats_unlayered_important()
    {
        // For !important the layer order REVERSES — unlayered loses to any named layer.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(
            LayerRule("foo", StyleRule("p", "color", "red", important: true)),
            StyleRule("p", "color", "blue", important: true));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("red", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Among_named_layers_normal_later_declared_wins()
    {
        // Two named layers in declaration order: foo (1), bar (2). Both target the same
        // property at same specificity. For NORMAL declarations, the LATER-declared layer
        // (bar) wins.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(
            LayerRule("foo", StyleRule("p", "color", "red")),
            LayerRule("bar", StyleRule("p", "color", "blue")));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("blue", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Among_named_layers_important_earlier_declared_wins()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(
            LayerRule("foo", StyleRule("p", "color", "red", important: true)),
            LayerRule("bar", StyleRule("p", "color", "blue", important: true)));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        // Earlier-declared layer "foo" wins for important.
        Assert.Equal("red", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Statement_form_atLayer_registers_order()
    {
        // @layer foo, bar; declares the order BEFORE any block-form. Then later,
        // @layer foo { ... } and @layer bar { ... } pick up indices 1 and 2 respectively.
        // Because we have the ordering registered first, the foo-block should win for
        // !important even though it appears after the bar-block in source order.
        var doc = await ParseHtml("<p>x</p>");
        // Statement-form @layer foo, bar; — no children.
        var statementForm = new CssAtRule(
            Name: "layer", Prelude: "foo, bar",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(
            statementForm,
            LayerRule("bar", StyleRule("p", "color", "blue", important: true)),
            LayerRule("foo", StyleRule("p", "color", "red", important: true)));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        // foo is registered first (statement-form) → lower LayerOrder → wins for !important.
        Assert.Equal("red", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Anonymous_layer_gets_synthetic_unique_name()
    {
        // Two anonymous block-form layers — each gets its own synthetic index. For NORMAL
        // declarations, the later one wins.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(
            LayerRule("", StyleRule("p", "color", "red")),
            LayerRule("", StyleRule("p", "color", "blue")));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("blue", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Repeated_layer_name_keeps_original_index()
    {
        // @layer foo { p { color: red } }
        // @layer bar { p { color: green } }
        // @layer foo { p { color: blue } }
        // Per spec, the second @layer foo merges into the original foo, NOT a new layer.
        // For normal, bar (later-declared) beats foo. So winner = green.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(
            LayerRule("foo", StyleRule("p", "color", "red")),
            LayerRule("bar", StyleRule("p", "color", "green")),
            LayerRule("foo", StyleRule("p", "color", "blue")));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        // For NORMAL the later-declared LAYER wins → bar (index 2) > foo (index 1).
        // Within foo, source order tie-breaks: blue (later) > red. But bar still wins overall.
        Assert.Equal("green", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Rec1_Import_layer_routes_imported_rules_to_named_layer()
    {
        var doc = await ParseHtml("<p>x</p>");
        var importedRule = StyleRule("p", "color", "red");
        var import = new CssImportRule(
            Url: "x.css", MediaQuery: "", LayerName: "foo",
            SupportsCondition: null,
            ImportedRules: ImmutableArray.Create<CssRule>(importedRule),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(import, StyleRule("p", "color", "blue"));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        // foo is layered (index 1); blue is unlayered. Unlayered NORMAL beats named layer
        // → blue wins.
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("blue", winner!.Declaration.Value.RawText);
    }

    // ============================================================
    // Rec 2 — @import supports() paren-tolerance
    // ============================================================

    [Fact]
    public async Task Rec2_Import_supports_bare_declaration_evaluates_correctly()
    {
        // The Task 3 preprocessor strips outer parens from supports() — `(display: grid)`
        // arrives as `display: grid`. The cascade must wrap before evaluation.
        var doc = await ParseHtml("<p>x</p>");
        var importedRule = StyleRule("p", "color", "red");
        var import = new CssImportRule(
            Url: "x.css", MediaQuery: "",
            LayerName: null,
            SupportsCondition: "display: grid",  // bare, no leading paren
            ImportedRules: ImmutableArray.Create<CssRule>(importedRule),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(import);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        // `display` is a registered property → supports() resolves true → import applied.
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    [Fact]
    public async Task Rec2_Import_supports_unsupported_property_skips_import()
    {
        var doc = await ParseHtml("<p>x</p>");
        var importedRule = StyleRule("p", "color", "red");
        var import = new CssImportRule(
            Url: "x.css", MediaQuery: "",
            LayerName: null,
            SupportsCondition: "totally-fake-property: foo",
            ImportedRules: ImmutableArray.Create<CssRule>(importedRule),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(import);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    // ============================================================
    // Rec 3 — Media query feature evaluator
    // ============================================================

    [Fact]
    public void Rec3_Not_print_inverts_for_print_context()
    {
        var ctx = CssMediaContext.DefaultPrint;
        Assert.False(ctx.Matches("not print"));
        Assert.True(ctx.Matches("not screen"));
    }

    [Fact]
    public void Rec3_MinWidth_evaluated_against_viewport()
    {
        // Default print viewport is 816px (8.5in × 96).
        var ctx = CssMediaContext.DefaultPrint;
        Assert.True(ctx.Matches("(min-width: 800px)"));
        Assert.True(ctx.Matches("(min-width: 816px)"));
        Assert.False(ctx.Matches("(min-width: 817px)"));
    }

    [Fact]
    public void Rec3_MaxWidth_evaluated_against_viewport()
    {
        var ctx = CssMediaContext.DefaultPrint;
        Assert.True(ctx.Matches("(max-width: 816px)"));
        Assert.False(ctx.Matches("(max-width: 815px)"));
    }

    [Fact]
    public void Rec3_TypeAndFeature_combines_both()
    {
        var ctx = CssMediaContext.DefaultPrint;
        Assert.True(ctx.Matches("print and (min-width: 800px)"));
        Assert.False(ctx.Matches("print and (min-width: 9999px)"));
        Assert.False(ctx.Matches("screen and (min-width: 800px)"));
    }

    [Fact]
    public void Rec3_Orientation_landscape_for_wider_than_tall()
    {
        // Default print: 816 × 1056 → portrait.
        var portrait = CssMediaContext.DefaultPrint;
        Assert.True(portrait.Matches("(orientation: portrait)"));
        Assert.False(portrait.Matches("(orientation: landscape)"));

        var landscape = portrait with { ViewportWidthPx = 1056, ViewportHeightPx = 816 };
        Assert.True(landscape.Matches("(orientation: landscape)"));
        Assert.False(landscape.Matches("(orientation: portrait)"));
    }

    [Fact]
    public void Rec3_PrefersColorScheme_against_context()
    {
        var light = CssMediaContext.DefaultPrint; // PreferredColorScheme = "light"
        Assert.True(light.Matches("(prefers-color-scheme: light)"));
        Assert.False(light.Matches("(prefers-color-scheme: dark)"));
        var dark = light with { PreferredColorScheme = "dark" };
        Assert.True(dark.Matches("(prefers-color-scheme: dark)"));
    }

    [Fact]
    public void Rec3_Unknown_feature_returns_false()
    {
        var ctx = CssMediaContext.DefaultPrint;
        // Unknown features evaluate to false (conservative — don't apply rules guarded by
        // features we can't validate).
        Assert.False(ctx.Matches("(some-unknown-feature)"));
        Assert.False(ctx.Matches("(some-unknown-feature: 42)"));
        Assert.False(ctx.Matches("print and (some-unknown-feature: 42)"));
    }

    [Fact]
    public void Rec3_TypeAndUnknownFeature_results_in_no_match()
    {
        var ctx = CssMediaContext.DefaultPrint;
        // print and (unknown) — type matches but feature doesn't → no match.
        Assert.False(ctx.Matches("print and (hover: hover)"));
    }

    [Fact]
    public void Rec3_CommaSeparated_alternatives_match_if_any_branch_matches()
    {
        var ctx = CssMediaContext.DefaultPrint;
        // First branch fails (screen), second matches (print and OK width).
        Assert.True(ctx.Matches("screen, print and (min-width: 100px)"));
    }

    [Fact]
    public void Rec3_FeatureOnly_no_type_implicit_all()
    {
        var ctx = CssMediaContext.DefaultPrint;
        Assert.True(ctx.Matches("(min-width: 100px)"));
        Assert.False(ctx.Matches("(min-width: 99999px)"));
    }

    [Fact]
    public void Rec3_Resolution_dppx()
    {
        var ctx = CssMediaContext.DefaultPrint with { DevicePixelRatio = 2.0 };
        Assert.True(ctx.Matches("(min-resolution: 1.5dppx)"));
        Assert.True(ctx.Matches("(min-resolution: 2dppx)"));
        Assert.False(ctx.Matches("(min-resolution: 3dppx)"));
    }

    // ============================================================
    // Rec 1 + Rec 3 + Rec 4 — Backwards compat sanity
    // ============================================================

    [Fact]
    public async Task Cycle2_smoke_basic_cascade_still_produces_winner()
    {
        // Quick sanity that the resolver still produces simple-case winners after the
        // layer / media / supports rewrites.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = Sheet(StyleRule("p", "color", "red"));
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.Equal("red",
            result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color")!.Declaration.Value.RawText);
    }
}
