// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
/// Round-2 Copilot review regression tests for PR #5 (findings #7-#11, #13).
/// JSON-escape regression test (#12) is in the
/// <c>CssPropertyGeneratorJsonEscapeTests</c> file. Each test pins a previously broken
/// behavior so it can't quietly regress.
/// </summary>
public sealed class CascadeCopilotReview2Tests
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

    // ============================================================
    // Copilot #7 — @keyframes / @font-face / @charset etc. silent
    // ============================================================

    [Theory]
    [InlineData("keyframes")]
    [InlineData("font-feature-values")]
    [InlineData("font-palette-values")]
    [InlineData("scroll-timeline")]
    [InlineData("view-transition")]
    [InlineData("charset")]
    [InlineData("namespace")]
    public async Task Copilot7_Known_cascade_silent_atrule_emits_no_diagnostic(string name)
    {
        var doc = await ParseHtml("<p>x</p>");
        // Synthetic at-rule with non-empty ChildRules so we hit the "is grouping rule"
        // branch in the resolver.
        var inner = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var atRule = new CssAtRule(
            Name: name, Prelude: "test",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(inner),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(atRule);
        var sink = new CapturingSink();
        _ = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }

    [Fact]
    public async Task Copilot7_Truly_unknown_atrule_with_children_still_emits_diagnostic()
    {
        // Sanity — a truly unknown grouping at-rule still emits the diagnostic.
        var doc = await ParseHtml("<p>x</p>");
        var inner = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var atRule = new CssAtRule(
            Name: "scope", Prelude: "",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(inner),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(atRule);
        var sink = new CapturingSink();
        _ = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }

    // ============================================================
    // Copilot #8 — @supports mixed and/or rejected
    // ============================================================

    [Fact]
    public async Task Copilot8_Mixed_and_or_at_same_level_skips_block_with_diagnostic()
    {
        // `(A) and (B) or (C)` — mixed connectors at the same level. Per Conditional L3
        // §4.1.1, malformed; v1 evaluator throws which the cascade catches as
        // CSS-AT-RULE-UNKNOWN-001 (un-evaluable @supports condition).
        var doc = await ParseHtml("<p>x</p>");
        var inner = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var supports = new CssAtRule(
            Name: "supports",
            Prelude: "(color: red) and (display: block) or (margin: 0)",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(inner),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(supports);
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        // Block skipped — no color on p.
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
        // Diagnostic emitted explaining the un-evaluable condition.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }

    [Fact]
    public async Task Copilot8_Parenthesized_mix_is_accepted()
    {
        // `((A) and (B)) or (C)` — explicit parens disambiguate. Should evaluate fine.
        var doc = await ParseHtml("<p>x</p>");
        var inner = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var supports = new CssAtRule(
            Name: "supports",
            Prelude: "((color: red) and (display: block)) or (margin: 0)",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(inner),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(supports);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        // Inner (color: red) and (display: block) evaluates true → outer or-true → block applied.
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    [Theory]
    [InlineData("(color: red) and (display: block) and (margin-top: 0)")]
    [InlineData("(color: red) or (display: block) or (margin-top: 0)")]
    public async Task Copilot8_All_same_connector_chains_evaluate_normally(string condition)
    {
        var doc = await ParseHtml("<p>x</p>");
        var inner = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var supports = new CssAtRule(
            Name: "supports", Prelude: condition,
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(inner),
            Location: CssSourceLocation.Unknown);
        var sheet = Sheet(supports);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    // ============================================================
    // Copilot #10 — Incremental bloom propagation (no behavioral regression)
    // ============================================================

    [Fact]
    public async Task Copilot10_Deep_DOM_cascade_still_produces_correct_winners()
    {
        // 10-level nested DOM — the bloom is now built incrementally per frame. Verify
        // the cascade still produces correct matches even with the new propagation.
        var doc = await ParseHtml("""
            <html><body>
              <section class="outer">
                <article>
                  <header><h1>title</h1></header>
                  <main>
                    <div class="container">
                      <div class="row">
                        <p class="lead">hi</p>
                      </div>
                    </div>
                  </main>
                </article>
              </section>
            </body></html>
            """);
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var rawSheet = parser.ParseStyleSheet(
            ".outer .lead { color: red } .row > p { color: blue }");
        var sheet = CssParserAdapter.Adapt(rawSheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);

        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p.lead");
        // Both rules match (.outer ancestor, .row > p direct parent). The cascade dedups
        // via cycle-3 fix (#1), so one declaration at max specificity. Both have B=2
        // specificity — last in source order wins → blue.
        var winner = result.TryGetStylesFor(p)!.GetWinner("color");
        Assert.NotNull(winner);
    }

    [Fact]
    public async Task Copilot10_Sibling_descendants_each_get_correct_bloom()
    {
        // Two sibling subtrees with different ancestor tokens — the bloom propagation
        // must NOT leak tokens between siblings.
        var doc = await ParseHtml("""
            <html><body>
              <div class="left"><p>L</p></div>
              <div class="right"><p>R</p></div>
            </body></html>
            """);
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var rawSheet = parser.ParseStyleSheet(".left p { color: red } .right p { color: blue }");
        var sheet = CssParserAdapter.Adapt(rawSheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);

        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var ps = doc.QuerySelectorAll("p");
        // L's p inherits .left via descendant; R's p inherits .right.
        Assert.Contains("255, 0, 0", result.TryGetStylesFor(ps[0])!.GetWinner("color")!.Declaration.Value.RawText);
        Assert.Contains("0, 0, 255", result.TryGetStylesFor(ps[1])!.GetWinner("color")!.Declaration.Value.RawText);
    }
}
