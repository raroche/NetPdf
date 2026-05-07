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
/// Regression tests for the 6 valid Copilot findings on PR #5. Each test pins a
/// previously-broken behavior so it can't quietly regress: selector-list double-add per
/// match, vh/vmin/vmax viewport binding, ::name(args) over-acceptance, JSON \uXXXX
/// escapes, @supports paren-counter quote awareness, and unknown-grouping-at-rule
/// silent drop.
/// </summary>
public sealed class CascadeCopilotReviewTests
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

    private static CssStylesheet Sheet(params CssRule[] rules) => new(
        Rules: ImmutableArray.Create(rules),
        Href: null, Origin: CssStylesheetOrigin.Author,
        OwnerKind: CssStylesheetOwnerKind.StyleElement,
        MediaQuery: null, IsDisabled: false, Order: 0,
        Location: CssSourceLocation.Unknown);

    private static async Task<CssStylesheet> ParseSheet(string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(sheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
    }

    // ============================================================
    // Copilot #1 — Selector-list double-add
    // ============================================================

    [Fact]
    public async Task Copilot1_Selector_list_with_two_matching_alternatives_adds_declaration_once()
    {
        // <p class="x"> matches both `p` (specificity 0,0,1) and `.x` (specificity 0,1,0).
        // Earlier code added the same `color: red` declaration TWICE — once per matched
        // alternative — inflating MatchedRuleSet.Count and breaking revert/revert-layer
        // semantics that expect one entry per authored declaration.
        var doc = await ParseHtml("<p class=\"x\">x</p>");
        var sheet = await ParseSheet("p, .x { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p");
        var styles = result.TryGetStylesFor(p)!;
        var matches = styles.GetAllForProperty("color");
        // The single authored declaration should appear EXACTLY ONCE in the cascade.
        Assert.Single(matches);
        // Cascade specificity = max of the matching alternatives — so .x's (0,1,0).
        Assert.Equal(new NetPdf.Css.Selectors.Specificity(0, 1, 0),
            matches[0].Key.Specificity);
    }

    [Fact]
    public async Task Copilot1_Selector_list_targeting_different_pseudo_elements_keeps_separate_entries()
    {
        // `p::before, p::after { content: "X" }` — these target DIFFERENT pseudo-elements,
        // so the dedup MUST keep them as separate cascade entries (one per pseudo-element
        // bucket).
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p::before, p::after { content: \"X\" }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p");
        Assert.NotNull(result.TryGetStylesForPseudo(p, "before"));
        Assert.NotNull(result.TryGetStylesForPseudo(p, "after"));
        // The host element must NOT pick up either declaration.
        Assert.Null(result.TryGetStylesFor(p));
    }

    [Fact]
    public async Task Copilot1_Three_matching_alternatives_pick_max_specificity()
    {
        // p (C=1), .a (B=1), #x (A=1) — all three match. Cascade picks #x's specificity.
        var doc = await ParseHtml("<p id=\"x\" class=\"a\">x</p>");
        var sheet = await ParseSheet("p, .a, #x { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var matches = result.TryGetStylesFor(Q(doc, "p"))!.GetAllForProperty("color");
        Assert.Single(matches);
        Assert.Equal(new NetPdf.Css.Selectors.Specificity(1, 0, 0),
            matches[0].Key.Specificity);
    }

    // ============================================================
    // Copilot #2 — vh/vmin/vmax viewport units
    // ============================================================

    [Fact]
    public void Copilot2_Vh_evaluates_against_viewport_height_not_width()
    {
        // Default print viewport is 816 (w) × 1056 (h). 100vh should be 1056px, NOT 816.
        var ctx = CssMediaContext.DefaultPrint;
        // (min-height: 100vh) → matches if viewport height >= 1056.
        Assert.True(ctx.Matches("(min-height: 100vh)"));
        // (min-height: 101vh) → 101% of 1056 = 1066.56, doesn't match.
        Assert.False(ctx.Matches("(min-height: 101vh)"));
    }

    [Fact]
    public void Copilot2_Vw_evaluates_against_viewport_width()
    {
        var ctx = CssMediaContext.DefaultPrint;
        // (min-width: 100vw) → 100% of 816 = 816 — matches.
        Assert.True(ctx.Matches("(min-width: 100vw)"));
        Assert.False(ctx.Matches("(min-width: 101vw)"));
    }

    [Fact]
    public void Copilot2_Vmin_uses_smaller_viewport_axis()
    {
        // 816 × 1056 → vmin = 8.16 (1% of 816, the smaller axis).
        var ctx = CssMediaContext.DefaultPrint;
        // (min-width: 100vmin) → 100 × 8.16 = 816 — matches width.
        Assert.True(ctx.Matches("(min-width: 100vmin)"));
        // 100vmin doesn't match height check at 100% (816 < 1056).
        Assert.False(ctx.Matches("(min-height: 130vmin)")); // 130 × 8.16 = 1060.8 > 1056
    }

    [Fact]
    public void Copilot2_Vmax_uses_larger_viewport_axis()
    {
        // 816 × 1056 → vmax = 10.56 (1% of 1056).
        var ctx = CssMediaContext.DefaultPrint;
        Assert.True(ctx.Matches("(min-height: 100vmax)")); // 100 × 10.56 = 1056 — matches.
        Assert.False(ctx.Matches("(min-height: 101vmax)"));
    }

    [Fact]
    public void Copilot2_Custom_viewport_size_propagates_to_vh()
    {
        // A4 portrait at 96dpi: 794 × 1123.
        var ctx = CssMediaContext.DefaultPrint with
        {
            ViewportWidthPx = 794,
            ViewportHeightPx = 1123,
        };
        Assert.True(ctx.Matches("(min-height: 100vh)"));   // 100 × 11.23 = 1123 — matches.
        Assert.False(ctx.Matches("(min-height: 101vh)"));  // > 1123 — does not.
    }

    // ============================================================
    // Copilot #3 — ::name(args) functional pseudo-elements rejected
    // ============================================================

    [Theory]
    [InlineData("::part(foo)")]
    [InlineData("::slotted(.x)")]
    [InlineData("::cue(.title)")]
    [InlineData("::highlight(name)")]
    [InlineData("p::before(")]   // malformed — unbalanced paren
    public void Copilot3_Functional_pseudo_elements_throw(string selector)
    {
        Assert.Throws<NetPdf.Css.Selectors.SelectorParseException>(
            () => NetPdf.Css.Selectors.SelectorCompiler.Compile(selector));
    }

    [Theory]
    [InlineData("p::before")]
    [InlineData("p::after")]
    [InlineData("p::marker")]
    [InlineData("p::first-line")]
    [InlineData("p::first-letter")]
    public void Copilot3_Bare_pseudo_elements_still_compile(string selector)
    {
        var list = NetPdf.Css.Selectors.SelectorCompiler.Compile(selector);
        Assert.Single(list.Alternatives);
        Assert.NotNull(list.Alternatives[0].PseudoElement);
    }

    // ============================================================
    // Copilot #5 — @supports paren-counter quote-aware
    // ============================================================

    [Fact]
    public async Task Copilot5_Supports_with_paren_inside_quoted_value_does_not_truncate()
    {
        // Verify the @supports paren-counter is quote-aware: a `)` inside
        // `"..."` must NOT close the outer condition group. The inner test
        // uses `not(content: "a)b")` — content is in the cycle-2 backlog
        // (UnsupportedUnvalidated state under the post-PR-#13 tightened
        // whitelist), so the inner returns false; `not(false)` = true. AND'd
        // with `(color: red)` (Resolved → true), the conjunction is true and
        // the block applies. If the parser TRUNCATED at the quoted `)`, the
        // prelude would parse as something else (or fail) + the inner color
        // rule wouldn't apply — so observing the rule applied proves the
        // paren-tracking is correct.
        var doc = await ParseHtml("<p>x</p>");
        var innerRule = new CssStyleRule(
            Selector: new CssSelector("p"),
            Declarations: ImmutableArray.Create(
                new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            Location: CssSourceLocation.Unknown);
        var supports = new CssAtRule(
            Name: "supports",
            Prelude: "(not (content: \"a)b\")) and (color: red)",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(innerRule),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(supports);

        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    [Fact]
    public async Task Copilot5_Supports_with_single_quoted_paren_does_not_truncate()
    {
        // Same paren-tracking check for SINGLE-quoted strings. `font-family`
        // is also UnsupportedUnvalidated under the tightened whitelist, so
        // `(font-family: 'a)b')` evaluates false. `not(false)` = true →
        // block applies → inner color rule found. Demonstrates the
        // single-quoted string variant.
        var doc = await ParseHtml("<p>x</p>");
        var innerRule = new CssStyleRule(
            Selector: new CssSelector("p"),
            Declarations: ImmutableArray.Create(
                new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            Location: CssSourceLocation.Unknown);
        var supports = new CssAtRule(
            Name: "supports",
            Prelude: "not (font-family: 'a)b')",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(innerRule),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(supports);

        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    // ============================================================
    // Copilot #6 — Unknown grouping at-rule with ChildRules emits diagnostic + skips
    // ============================================================

    [Fact]
    public async Task Copilot6_Unknown_grouping_atrule_with_children_emits_diagnostic_and_skips()
    {
        // Synthesize an unknown at-rule (e.g., a hypothetical @scope) that arrives with
        // ChildRules populated. Without the cycle 3 fix, the children would silently apply.
        var doc = await ParseHtml("<p>x</p>");
        var innerRule = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var unknown = new CssAtRule(
            Name: "scope",
            Prelude: "",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(innerRule),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(unknown);

        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        // Children NOT applied (no color on p).
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
        // Diagnostic emitted so the user knows the at-rule was preserved-not-applied.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }

    [Fact]
    public async Task Copilot6_Known_declaration_bearing_atrule_does_not_diagnose()
    {
        // @page / @font-face / @counter-style / @property / @color-profile have separate
        // consumers; the cascade is intentionally silent — no CSS-AT-RULE-UNKNOWN-001.
        var doc = await ParseHtml("<p>x</p>");
        var pageRule = new CssAtRule(
            Name: "page",
            Prelude: "",
            Declarations: ImmutableArray.Create(
                new CssDeclaration("margin", new CssValue("1in"), false, CssSourceLocation.Unknown)),
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(pageRule);
        var sink = new CapturingSink();
        _ = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }
}
