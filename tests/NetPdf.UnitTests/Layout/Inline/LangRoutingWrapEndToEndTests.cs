// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Parser;
using NetPdf.Hyphenation;
using NetPdf.Languages.European;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Text.Bidi;
using NetPdf.Text.Hyphenation;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Layout.Inline;

/// <summary>
/// Phase-5 — end-to-end proof that <c>lang</c>-routed hyphenation reaches the PRODUCTION wrap path, not just
/// the registry lookup. Per PR #265 review [P3]. Each test builds a real <see cref="BoxBuilder"/> box tree,
/// runs the exact two steps <see cref="BlockLayouter"/> runs at its inline-layout call site
/// (<see cref="BlockLayouter.ResolveEffectiveLanguage"/> then <see cref="HyphenationRegistry.ResolveOrDefault"/>),
/// and feeds the resolved hyphenator into <see cref="LineBuilder.Wrap"/> (the production wrap) with
/// <c>hyphens: auto</c> under a constrained width — then asserts the wrapped lines.
/// </summary>
/// <remarks>
/// The test shaper uses a synthetic font (every glyph is .notdef, 7.2 px at 12 px font-size), so real German
/// letter widths aren't available — but the WRAP decision uses the hyphenator's break positions on the
/// actual text, which is exactly what routing changes. "Geschwindigkeit" is a German exception word breaking
/// at [2, 8, 11], whereas the bundled English hyphenator finds NO break in it — so German wraps it into
/// several lines while English can't break it at all (one overflowing line). At a 65 px budget (fits 9
/// glyphs) the German wrap snaps at position 8 ("Geschwin-").
/// </remarks>
public sealed class LangRoutingWrapEndToEndTests
{
    private const string LatnScript = "Latn";
    private const string Word = "Geschwindigkeit"; // de=[2,8,11], en=[] — German breaks, English cannot
    private const double BudgetPx = 65; // fits 9 synthetic glyphs (9 × 7.2 = 64.8 px)

    public LangRoutingWrapEndToEndTests() => EuropeanHyphenation.Register(); // idempotent

    [Fact]
    public async Task German_document_hyphens_auto_wraps_at_the_German_break_through_the_wrap_path()
    {
        var pBox = await ParagraphBox($"<html lang=\"de\"><body><p>{Word}</p></body></html>");

        // The exact routing BlockLayouter performs at its LayoutPerRun call site.
        var hyphenator = HyphenationRegistry.ResolveOrDefault(
            BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
        Assert.NotSame(EnUsHyphenation.Default, hyphenator); // routed to German, not the default

        var lines = Wrap(Word, BudgetPx, hyphenator);

        Assert.True(lines.Length >= 2, "German hyphenation must split the word under the constrained width.");
        Assert.True(lines[0].EndsWithHyphenationBreak, "line 0 must end at a hyphenation break.");
        Assert.Equal(8, DrawableGlyphs(lines[0])); // German break at [8] → "Geschwin-"
    }

    [Fact]
    public async Task Unregistered_language_document_wraps_with_the_english_default_not_german()
    {
        var pBox = await ParagraphBox($"<html lang=\"zz\"><body><p>{Word}</p></body></html>");

        var routed = HyphenationRegistry.ResolveOrDefault(
            BlockLayouter.ResolveEffectiveLanguage(pBox.SourceElement));
        Assert.Same(EnUsHyphenation.Default, routed); // unregistered "zz" → the bundled English default

        // The bundled English hyphenator finds no break in this German word, so it stays one overflowing
        // line — visibly different from the German wrap, which splits it.
        var english = Wrap(Word, BudgetPx, routed);
        Assert.Single(english);
        Assert.False(english[0].EndsWithHyphenationBreak);

        var german = Wrap(Word, BudgetPx, HyphenationRegistry.ResolveOrDefault("de"));
        Assert.True(german.Length >= 2, "the German wrap must differ from the English/default one.");
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static LineFragment[] Wrap(string text, double availableInlineSize, Hyphenator hyphenator)
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new(text, ComputedStyle.RentForExclusiveTesting()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, "de");
        return LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize, hyphens: Hyphens.Auto, hyphenator: hyphenator);
    }

    private static int DrawableGlyphs(LineFragment line)
    {
        var count = 0;
        foreach (var s in line.Slices)
        {
            count += s.GlyphLength;
        }

        return count;
    }

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

    private sealed class TestShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);

        public HbShaper Resolve(ComputedStyle style) => _shaper;

        public void Dispose() => _shaper.Dispose();
    }
}
