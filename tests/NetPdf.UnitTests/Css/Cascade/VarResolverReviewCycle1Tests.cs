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
/// VarResolver-level regression tests for the deeper review recommendations: pseudo-
/// element custom-property layering (#3) and custom-only-element exposure (#6) need
/// the full cascade pipeline to verify, so they live here rather than the
/// VarSubstitution-level test file.
/// </summary>
public sealed class VarResolverReviewCycle1Tests
{
    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

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

    private static IElement Q(IDocument doc, string css) => doc.QuerySelector(css)!;

    // ============================================================
    // Rec 3 — Pseudo-element custom-property layering
    // ============================================================

    [Fact]
    public async Task Rec3_PseudoElement_own_custom_property_overrides_host()
    {
        // p declares --primary: red. p::before redeclares --primary: blue.
        // ::before's content uses var(--primary) — should resolve to BLUE (its own
        // layer), NOT RED (the host's). Earlier code reused the host's table so the
        // pseudo's --primary was ignored.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --primary: red } p::before { --primary: blue; content: var(--primary) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var beforeStyles = resolved.TryGetStylesForPseudo(Q(doc, "p"), "before");
        Assert.NotNull(beforeStyles);
        var content = beforeStyles!.GetWinner("content");
        Assert.NotNull(content);
        Assert.Equal("blue", content!.ResolvedValue);
    }

    [Fact]
    public async Task Rec3_PseudoElement_inherits_unmodified_host_custom_property()
    {
        // ::before doesn't redeclare --primary → inherits from host.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --primary: red } p::before { content: var(--primary) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var content = resolved.TryGetStylesForPseudo(Q(doc, "p"), "before")!.GetWinner("content");
        Assert.Equal("red", content!.ResolvedValue);
    }

    // ============================================================
    // Rec 6 — Custom-only-element exposure
    // ============================================================

    [Fact]
    public async Task Rec6_Element_with_only_custom_properties_still_exposed_in_result()
    {
        // div has ONLY --primary, no observable values. The element should still appear
        // in TryGetStylesFor so callers can read its CustomProperties.
        var doc = await ParseHtml("<div></div>");
        var sheet = await ParseSheet("div { --primary: red }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var div = Q(doc, "div");
        var styles = resolved.TryGetStylesFor(div);
        Assert.NotNull(styles);
        // No non-custom winners.
        Assert.Equal(0, styles!.Count);
        // But the resolved table contains --primary.
        Assert.True(styles.CustomProperties.TryGetValue("--primary", out var v));
        Assert.Equal("red", v);
    }

    [Fact]
    public async Task Rec6_Inheritance_through_custom_only_element_still_works()
    {
        // Sanity: a custom-only middle element still propagates its custom properties
        // to children.
        var doc = await ParseHtml(
            "<html><body><div class=\"middle\"><p class=\"child\">x</p></div></body></html>");
        var sheet = await ParseSheet(
            ".middle { --primary: red } .child { color: var(--primary) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, ".child"))!.GetWinner("color");
        Assert.Equal("red", color!.ResolvedValue);
    }

    // ============================================================
    // Rec 2 — Cycle invalidation through the resolver
    // ============================================================

    [Fact]
    public async Task Rec2_Element_with_cyclic_custom_properties_invalidates_at_cascade_level()
    {
        // p { --a: var(--b); --b: var(--a); color: var(--a, green) }
        // Per spec: --a and --b are both invalid. var(--a, green) → "green".
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --a: var(--b); --b: var(--a); color: var(--a, green) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("green", color!.ResolvedValue);
    }
}
