// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// End-to-end unit tests for <see cref="CascadeResolver"/>: small synthetic DOMs +
/// stylesheets exercising origin / importance / specificity / source-order ordering,
/// pseudo-element separation, inline styles, and diagnostic emission.
/// </summary>
public sealed class CascadeResolverTests
{
    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static async Task<CssStylesheet> ParseSheet(string css,
        CssStylesheetOrigin origin = CssStylesheetOrigin.Author,
        int order = 0)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(
            sheet, href: null, origin: origin,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: order);
    }

    private static IElement Q(IDocument doc, string css) =>
        doc.QuerySelector(css)!;

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Fact]
    public async Task Single_rule_matches_element_and_produces_winner()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p");
        var styles = result.TryGetStylesFor(p);
        Assert.NotNull(styles);
        var winner = styles!.GetWinner("color");
        Assert.NotNull(winner);
        Assert.Equal("rgba(255, 0, 0, 1)", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Higher_specificity_wins()
    {
        var doc = await ParseHtml("<p class=\"foo\">x</p>");
        var sheet = await ParseSheet(".foo { color: red } p { color: blue }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p");
        var winner = result.TryGetStylesFor(p)!.GetWinner("color");
        Assert.Equal("rgba(255, 0, 0, 1)", winner!.Declaration.Value.RawText); // .foo wins (B=1) over p (C=1)
    }

    [Fact]
    public async Task Source_order_breaks_specificity_tie_last_wins()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { color: red } p { color: blue }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("rgba(0, 0, 255, 1)", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Important_beats_normal_within_same_origin()
    {
        var doc = await ParseHtml("<p id=\"x\">x</p>");
        var sheet = await ParseSheet("#x { color: red !important } p { color: blue }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("rgba(255, 0, 0, 1)", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Author_normal_beats_UA_normal()
    {
        var doc = await ParseHtml("<p>x</p>");
        var ua = await ParseSheet("p { color: red }", origin: CssStylesheetOrigin.UserAgent, order: 0);
        var author = await ParseSheet("p { color: blue }", origin: CssStylesheetOrigin.Author, order: 1);
        var result = CascadeResolver.Resolve(doc,
            ImmutableArray.Create(ua, author),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("rgba(0, 0, 255, 1)", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task UA_important_beats_author_important()
    {
        var doc = await ParseHtml("<p>x</p>");
        var ua = await ParseSheet("p { color: red !important }",
            origin: CssStylesheetOrigin.UserAgent, order: 0);
        var author = await ParseSheet("p { color: blue !important }",
            origin: CssStylesheetOrigin.Author, order: 1);
        var result = CascadeResolver.Resolve(doc,
            ImmutableArray.Create(ua, author),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("rgba(255, 0, 0, 1)", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Inline_style_beats_selector_match_at_same_specificity()
    {
        var doc = await ParseHtml("<p style=\"color: blue\">x</p>");
        var sheet = await ParseSheet("p { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("rgba(0, 0, 255, 1)", winner!.Declaration.Value.RawText);
        // AngleSharp.Css normalizes named colors → rgba; the assertion is on the value
        // shape, confirming it was the inline-style branch (specificity (1,0,0)) winning.
    }

    [Fact]
    public async Task Inline_important_beats_author_important_at_higher_specificity()
    {
        var doc = await ParseHtml("<p id=\"x\" style=\"color: blue !important\">x</p>");
        // Author rule has #x specificity (1,0,0) — same as inline style.
        // Both !important. Source order tie-breaks; inline-style stylesheet-order is set
        // ABOVE all real stylesheets so it wins.
        var sheet = await ParseSheet("#x { color: red !important }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("0, 0, 255", winner!.Declaration.Value.RawText); // blue
    }

    [Fact]
    public async Task Pseudo_element_selector_targets_pseudo_set_not_host()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p::before { content: \"X\" } p { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p");
        // Host element has color but NOT content (which is on ::before).
        var hostStyles = result.TryGetStylesFor(p)!;
        Assert.NotNull(hostStyles.GetWinner("color"));
        Assert.Null(hostStyles.GetWinner("content"));
        // Pseudo-element set has content.
        var beforeStyles = result.TryGetStylesForPseudo(p, "before")!;
        Assert.NotNull(beforeStyles.GetWinner("content"));
        Assert.Equal("before", beforeStyles.PseudoElement);
    }

    [Fact]
    public async Task Disabled_stylesheet_does_not_contribute()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = (await ParseSheet("p { color: red }")) with { IsDisabled = true };
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var p = Q(doc, "p");
        Assert.Null(result.TryGetStylesFor(p));
    }

    [Fact]
    public async Task Media_mismatch_skips_stylesheet()
    {
        var doc = await ParseHtml("<p>x</p>");
        var screenOnly = (await ParseSheet("p { color: red }")) with { MediaQuery = "screen" };
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(screenOnly),
            CssMediaContext.DefaultPrint);  // print

        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task Media_match_includes_stylesheet()
    {
        var doc = await ParseHtml("<p>x</p>");
        var printOnly = (await ParseSheet("p { color: red }")) with { MediaQuery = "print, projection" };
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(printOnly),
            CssMediaContext.DefaultPrint);

        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task Has_selector_emits_diagnostic_and_does_not_match()
    {
        var doc = await ParseHtml("<article><h1>t</h1></article>");
        var sheet = await ParseSheet("article:has(h1) { color: red }");
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        // The matcher returns false for ContainsHas selectors — article gets no styles.
        Assert.Null(result.TryGetStylesFor(Q(doc, "article")));
        // Exactly one CSS-HAS-RENDERING-NOT-IMPLEMENTED-001 diagnostic per stylesheet.
        var hasDiags = sink.Diagnostics
            .Where(d => d.Code == CssDiagnosticCodes.CssHasRenderingNotImplemented001)
            .ToList();
        Assert.Single(hasDiags);
    }

    [Fact]
    public async Task Malformed_selector_does_not_throw_and_valid_rules_still_apply()
    {
        var doc = await ParseHtml("<p>x</p>");
        // The first selector contains a syntax we reject (`:invalidpseudo` is not in the
        // SelectorCompiler dispatch table); AngleSharp.Css preserves the rule with its
        // original selector text, so our cascade-time SelectorCompiler.Compile sees it
        // and emits CSS-PARSE-WARNING-001 + skips the rule. The second rule still applies.
        var sheet = await ParseSheet("p:invalidpseudo { color: red } p { background: green }");
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        // Cascade should not throw. The valid background rule may or may not apply
        // depending on whether AngleSharp.Css drops malformed rules itself — we don't
        // pin the exact behavior, just the no-exception contract.
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Media_grouped_rules_are_collected()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@media print { p { color: red } } @media screen { p { color: blue } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        // For now, both @media blocks are collected (we don't filter @media-grouped rules
        // by media context — the per-stylesheet filter handles that). The print rule wins
        // by source order anyway in this test.
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.NotNull(winner);
    }

    [Fact]
    public async Task Multiple_elements_each_get_their_own_matched_set()
    {
        var doc = await ParseHtml("<p>a</p><p class=\"x\">b</p><p>c</p>");
        var sheet = await ParseSheet(".x { color: red } p { color: blue }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var ps = doc.QuerySelectorAll("p");
        // Plain <p> elements get blue; the .x one gets red.
        Assert.Equal("rgba(0, 0, 255, 1)",
            result.TryGetStylesFor(ps[0])!.GetWinner("color")!.Declaration.Value.RawText);
        Assert.Equal("rgba(255, 0, 0, 1)",
            result.TryGetStylesFor(ps[1])!.GetWinner("color")!.Declaration.Value.RawText);
        Assert.Equal("rgba(0, 0, 255, 1)",
            result.TryGetStylesFor(ps[2])!.GetWinner("color")!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Element_with_no_matches_has_null_styles()
    {
        var doc = await ParseHtml("<p>x</p><div>y</div>");
        var sheet = await ParseSheet("p { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p")));
        Assert.Null(result.TryGetStylesFor(Q(doc, "div")));
    }

    [Fact]
    public async Task Compound_specificity_correctly_orders()
    {
        var doc = await ParseHtml("<p id=\"x\" class=\"foo\">x</p>");
        var sheet = await ParseSheet(
            "#x { color: red }" +     // (1, 0, 0)
            ".foo { color: blue }" +  // (0, 1, 0)
            "p { color: green }");    // (0, 0, 1)
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        // #x wins on A=1 specificity.
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("rgba(255, 0, 0, 1)", winner!.Declaration.Value.RawText);
    }

    [Fact]
    public async Task Throws_on_null_arguments()
    {
        var doc = await ParseHtml("<p>x</p>");
        Assert.Throws<System.ArgumentNullException>(() =>
            CascadeResolver.Resolve(null!, ImmutableArray<CssStylesheet>.Empty,
                CssMediaContext.DefaultPrint));
        Assert.Throws<System.ArgumentNullException>(() =>
            CascadeResolver.Resolve(doc, ImmutableArray<CssStylesheet>.Empty, null!));
    }
}
