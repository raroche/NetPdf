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
/// End-to-end tests for <see cref="VarResolver"/>: small synthetic DOMs + stylesheets
/// exercising custom-property cascade, inheritance from parent, var() substitution in
/// non-custom declarations, fallback handling, and circular-reference detection.
/// </summary>
public sealed class VarResolverTests
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

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Fact]
    public async Task Var_resolves_against_own_element_custom_property()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { --color: red; color: var(--color) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var styles = resolved.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(styles);
        var color = styles!.GetWinner("color");
        Assert.NotNull(color);
        // Custom-property values are preserved as raw text per CSS Custom Properties L1
        // §3 (token streams) — AngleSharp doesn't normalize the inner color name.
        Assert.Equal("red", color!.ResolvedValue);
    }

    [Fact]
    public async Task Var_inherits_from_parent_element()
    {
        var doc = await ParseHtml(
            "<html><body><div class=\"root\"><p>x</p></div></body></html>");
        // Custom property declared on .root (parent of p), referenced on p.
        var sheet = await ParseSheet(
            ".root { --primary: red } p { color: var(--primary) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.NotNull(color);
        Assert.Equal("red", color!.ResolvedValue);
    }

    [Fact]
    public async Task Child_overrides_inherited_custom_property()
    {
        var doc = await ParseHtml(
            "<html><body><div class=\"root\"><p class=\"child\">x</p></div></body></html>");
        var sheet = await ParseSheet(
            ".root { --primary: red } .child { --primary: blue } p { color: var(--primary) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        // p inherits --primary from .child (which overrides .root) → blue.
        Assert.Equal("blue", color!.ResolvedValue);
    }

    [Fact]
    public async Task Missing_custom_property_uses_fallback()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { color: var(--missing, green) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        // The full value is "var(--missing, green)" which AngleSharp preserves verbatim
        // for var()-bearing declarations. After substitution: just "green" remains.
        Assert.Equal("green", color!.ResolvedValue);
    }

    [Fact]
    public async Task Missing_custom_property_no_fallback_yields_unset_sentinel()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { color: var(--missing) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal(VarSubstitution.UnsetSentinel, color!.ResolvedValue);
    }

    [Fact]
    public async Task Custom_property_value_containing_var_resolves_recursively()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --primary: var(--brand); --brand: red; color: var(--primary) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Equal("red", color!.ResolvedValue);
    }

    [Fact]
    public async Task Circular_var_reference_emits_diagnostic_and_uses_fallback()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --a: var(--b); --b: var(--a); color: var(--a, green) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var sink = new CapturingSink();
        var resolved = VarResolver.Resolve(cascade, doc, sink);

        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public async Task Sibling_does_not_inherit_custom_property()
    {
        // Sibling-isolation: --a defined on .left should NOT visible on .right.
        var doc = await ParseHtml(
            "<html><body><div class=\"left\"></div><div class=\"right\"><p>x</p></div></body></html>");
        var sheet = await ParseSheet(
            ".left { --primary: red } p { color: var(--primary, green) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        // p is inside .right; --primary not in scope → fallback green.
        Assert.Equal("green", color!.ResolvedValue);
    }

    [Fact]
    public async Task Multiple_vars_in_single_value_all_resolve()
    {
        // Test against a genuine LONGHAND (transform) so AngleSharp preserves the var()-bearing value as
        // a single pending-substitution value with MULTIPLE var() refs — a var()-in-shorthand (e.g.
        // padding) is expanded to EMPTY longhands AngleSharp can't resolve, and those empty declarations
        // are (correctly, RC-13) dropped before the cascade rather than falsely "resolving" to nothing.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --a: 5px; --b: 10px; transform: translate(var(--a), var(--b)) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var styles = resolved.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(styles);
        var transform = styles!.GetWinner("transform");
        Assert.NotNull(transform);
        // BOTH var()s substituted: neither var( nor the custom-property names remain.
        Assert.DoesNotContain("var(", transform!.ResolvedValue);
        Assert.Contains("5px", transform.ResolvedValue);
        Assert.Contains("10px", transform.ResolvedValue);
    }

    [Fact]
    public async Task Resolved_set_carries_original_declaration_and_cascade_key()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { --c: red; color: var(--c) }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var color = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.NotNull(color!.OriginalDeclaration);
        Assert.Equal("color", color.OriginalDeclaration.Property);
        Assert.Contains("var(--c)", color.OriginalDeclaration.Value.RawText);
        Assert.Equal(CssStylesheetOrigin.Author, color.Key.Origin);
    }

    [Fact]
    public async Task Element_with_no_matched_styles_has_no_resolved_set()
    {
        var doc = await ParseHtml("<p>x</p><div>y</div>");
        var sheet = await ParseSheet("p { color: red }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        Assert.NotNull(resolved.TryGetStylesFor(Q(doc, "p")));
        Assert.Null(resolved.TryGetStylesFor(Q(doc, "div")));
    }

    [Fact]
    public async Task Pseudo_element_uses_host_custom_property_table()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "p { --label: \"X\" } p::before { content: var(--label, \"fallback\") }");
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var beforeStyles = resolved.TryGetStylesForPseudo(Q(doc, "p"), "before");
        Assert.NotNull(beforeStyles);
        var content = beforeStyles!.GetWinner("content");
        Assert.NotNull(content);
        Assert.Contains("X", content!.ResolvedValue);
    }

    [Fact]
    public void Throws_on_null_arguments()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => VarResolver.Resolve(null!, null!));
    }
}
