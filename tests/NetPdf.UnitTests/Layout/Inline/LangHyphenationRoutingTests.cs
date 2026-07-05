// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Parser;
using NetPdf.Hyphenation;
using NetPdf.Languages.European;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Text.Hyphenation;
using Xunit;

namespace NetPdf.UnitTests.Layout.Inline;

/// <summary>
/// Phase-5 — end-to-end proof that layout routes the <c>hyphens: auto</c> hyphenator by an element's
/// effective HTML <c>lang</c> (the mechanism <see cref="BlockLayouter"/> wires at its inline-layout call
/// site). Each test builds a REAL box tree with <see cref="BoxBuilder"/> from parsed HTML, then exercises
/// the exact two steps the layouter runs: <see cref="BlockLayouter.ResolveEffectiveLanguage"/> on the
/// paragraph box's DOM element, and <see cref="HyphenationRegistry.ResolveOrDefault"/> on the result. The
/// <c>NetPdf.Languages.European</c> pack (referenced by this test assembly) registers de/fr, so a German
/// document hyphenates with German rules. The literal <c>LayoutPerRun</c> pass-through of the resolved
/// hyphenator is covered byte-for-byte by the golden suites (which don't load any pack → English default →
/// unchanged output).
/// </summary>
public sealed class LangHyphenationRoutingTests
{
    public LangHyphenationRoutingTests() => EuropeanHyphenation.Register(); // idempotent; belt-and-braces

    [Fact]
    public async Task Document_lang_de_routes_the_paragraph_to_the_german_hyphenator()
    {
        var pBox = await ParagraphBox("<html lang=\"de\"><body><p>Silbentrennung</p></body></html>");

        var lang = BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement);
        Assert.Equal("de", lang); // inherited from <html lang="de"> up the ancestor chain

        // The resolved hyphenator is German (not the English default) and hyphenates the German exception
        // word at the German break positions — the routing changed which hyphenator layout uses.
        var hyphenator = HyphenationRegistry.ResolveOrDefault(lang);
        Assert.NotSame(EnUsHyphenation.Default, hyphenator);
        Assert.Equal(new[] { 3, 6, 10 }, hyphenator.FindHyphenationPoints("Silbentrennung"));
    }

    [Fact]
    public async Task Element_level_lang_fr_routes_that_paragraph_to_the_french_hyphenator()
    {
        var pBox = await ParagraphBox("<html><body><p lang=\"fr\">ordinateur</p></body></html>");

        var lang = BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement);
        Assert.Equal("fr", lang);

        var hyphenator = HyphenationRegistry.ResolveOrDefault(lang);
        Assert.NotSame(EnUsHyphenation.Default, hyphenator);
        Assert.Equal(new[] { 2, 4, 6 }, hyphenator.FindHyphenationPoints("ordinateur"));
    }

    [Fact]
    public async Task Nearest_lang_wins_over_an_ancestor_lang()
    {
        // <html lang="de"> ancestor, but the <p lang="fr"> override is nearer → French.
        var pBox = await ParagraphBox("<html lang=\"de\"><body><p lang=\"fr\">ordinateur</p></body></html>");
        Assert.Equal("fr", BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
    }

    [Fact]
    public async Task Lang_is_inherited_through_intermediate_ancestors()
    {
        var pBox = await ParagraphBox("<html lang=\"de\"><body><section><div><p>Silbentrennung</p></div></section></body></html>");
        Assert.Equal("de", BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
    }

    [Fact]
    public async Task No_lang_anywhere_falls_back_to_the_bundled_english_hyphenator()
    {
        // This is the byte-identity guarantee: an unrouted document resolves to EnUsHyphenation.Default,
        // exactly the pre-routing behavior (LineBuilder used `hyphenator ?? EnUsHyphenation.Default`).
        var pBox = await ParagraphBox("<html><body><p>hyphenation</p></body></html>");

        Assert.Null(BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
        Assert.Same(EnUsHyphenation.Default, HyphenationRegistry.ResolveOrDefault(null));
    }

    [Fact]
    public async Task Empty_lang_attribute_overrides_an_ancestor_lang_as_unknown()
    {
        // HTML: lang="" is an explicit "language unknown" that OVERRIDES the ancestor <html lang="de"> —
        // it must NOT inherit German. Effective language is null → the default (English) hyphenator, so the
        // paragraph is NOT hyphenated with German rules.
        var pBox = await ParagraphBox("<html lang=\"de\"><body><p lang=\"\">Silbentrennung</p></body></html>");

        Assert.Null(BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
        Assert.Same(EnUsHyphenation.Default, HyphenationRegistry.ResolveOrDefault(
            BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement)));
    }

    [Fact]
    public async Task Whitespace_only_lang_attribute_is_treated_as_unknown()
    {
        var pBox = await ParagraphBox("<html lang=\"de\"><body><p lang=\"   \">Silbentrennung</p></body></html>");
        Assert.Null(BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
    }

    [Fact]
    public async Task Xml_lang_is_honored_when_lang_is_absent()
    {
        var pBox = await ParagraphBox("<html><body><p xml:lang=\"fr\">ordinateur</p></body></html>");
        Assert.Equal("fr", BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
    }

    [Fact]
    public async Task Lang_takes_priority_over_xml_lang_on_the_same_element()
    {
        var pBox = await ParagraphBox("<html><body><p lang=\"de\" xml:lang=\"fr\">Silbentrennung</p></body></html>");
        Assert.Equal("de", BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
    }

    // --- pipeline helpers (parse → cascade → var-resolve → box tree), mirroring InlineTextPolicyEndToEndTests ---

    private static async Task<Box> ParagraphBox(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var doc = await ctx.OpenAsync(req => req.Content(html));
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray<CssStylesheet>.Empty, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        var root = BoxBuilder.Build(doc, resolved);
        var p = FindFirstByTag(root, "p");
        Assert.NotNull(p);
        return p!;
    }

    private static Box? FindFirstByTag(Box root, string tag)
    {
        if (root.SourceElement?.LocalName == tag)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var found = FindFirstByTag(child, tag);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
