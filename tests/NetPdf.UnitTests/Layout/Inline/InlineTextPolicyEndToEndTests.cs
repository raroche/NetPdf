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
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using Xunit;
// AngleSharp.Dom defines its own WordBreak enum; alias the NetPdf
// one for unambiguous use in the assertions.
using NpWordBreak = NetPdf.Layout.Inline.WordBreak;

namespace NetPdf.UnitTests.Layout.Inline;

/// <summary>Per Phase 3 Task 10 cycle 2 + post-cycle-2 review
/// hardening (User #5) — end-to-end integration test that exercises
/// the FULL CSS pipeline:
/// <list type="number">
///   <item>HTML parse via AngleSharp.</item>
///   <item>CSS parse + adapt to <c>CssStylesheet</c>.</item>
///   <item>Cascade resolve → produce per-element
///   <see cref="ComputedStyle"/>.</item>
///   <item>Var resolve → final styles.</item>
///   <item><see cref="BoxBuilder.Build"/> → box tree.</item>
///   <item><see cref="InlineTextPolicyMaterializer.ReadInlineTextPolicy"/>
///   on the principal &lt;p&gt; box's style.</item>
/// </list>
///
/// <para><b>Cycle 2 hardening finding (pinned via these tests).</b>
/// AngleSharp.Css's ICssParser does NOT yet emit declarations for
/// the new properties (<c>overflow-wrap</c>, <c>word-break</c>,
/// <c>hyphens</c>) — the parser drops them before they reach our
/// adapter, so the cascade never sees them. The keyword-tables +
/// materializer + corpus-allowlist work shipped in cycle 2 are all
/// CORRECT, but the property pipeline currently relies on
/// AngleSharp.Css 1.0.0-beta.144's grammar coverage. Cycle 3 will
/// either upgrade AngleSharp or add a CSS-token-level fallback
/// adapter so authored documents using these properties resolve
/// through the production cascade. Until then, the
/// <see cref="InlineTextPolicy"/> materializer + the synthetic-
/// ComputedStyle unit tests cover the layout-side correctness;
/// these end-to-end tests pin the AngleSharp gap so a future
/// AngleSharp upgrade is detected (the asserts below assume the
/// gap; the cycle-3 fix flips them to expect the resolved values).</para>
/// </summary>
public sealed class InlineTextPolicyEndToEndTests
{
    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static CssStylesheet ParseSheet(string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(sheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
    }

    private static async Task<ComputedStyle?> GetParagraphComputedStyle(string html, string css)
    {
        var doc = await ParseHtml(html);
        var sheets = ImmutableArray.Create(ParseSheet(css));
        var cascade = CascadeResolver.Resolve(doc, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        var root = BoxBuilder.Build(doc, resolved);
        return FindFirstByTag(root, "p")?.Style;
    }

    private static Box? FindFirstByTag(Box root, string tag)
    {
        if (root.SourceElement?.LocalName == tag) return root;
        foreach (var child in root.Children)
        {
            var found = FindFirstByTag(child, tag);
            if (found is not null) return found;
        }
        return null;
    }

    [Fact]
    public async Task EndToEnd_unstyled_p_returns_default_policy()
    {
        // No declarations → default initial values per CSS Text L3.
        // This test validates the integration smoke flow: HTML →
        // CSS parse → cascade → ComputedStyle → ReadInlineTextPolicy
        // — all four properties default correctly.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(WhiteSpace.Normal, policy.WhiteSpace);
        Assert.Equal(OverflowWrap.Normal, policy.OverflowWrap);
        Assert.Equal(NpWordBreak.Normal, policy.WordBreak);
        Assert.Equal(Hyphens.Manual, policy.Hyphens);
    }

    [Fact]
    public async Task EndToEnd_AngleSharp_drops_overflow_wrap_anywhere()
    {
        // Pin: AngleSharp.Css 1.0.0-beta.144's ICssParser drops the
        // overflow-wrap declaration before our adapter sees it, so
        // ComputedStyle holds the default keyword id (0 = normal).
        // The materializer correctly returns OverflowWrap.Normal —
        // the gap is at the CSS parser layer, not the layout layer.
        // When AngleSharp upgrades to recognize overflow-wrap, this
        // test flips: expected becomes OverflowWrap.Anywhere +
        // keyword id 1.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { overflow-wrap: anywhere; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var owSlot = pStyle.Get(PropertyId.OverflowWrap);
        Assert.Equal(ComputedSlotTag.Keyword, owSlot.Tag);
        Assert.Equal(0, owSlot.AsKeyword()); // cycle-2 pinned: declaration dropped

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.Normal, policy.OverflowWrap);
    }

    [Fact]
    public async Task EndToEnd_word_break_break_all_flows_through_pipeline()
    {
        // AngleSharp.Css 1.0.0-beta.144 DOES recognize word-break.
        // The full pipeline (parse → adapt → cascade → ComputedStyle
        // → ReadInlineTextPolicy) correctly produces
        // WordBreak.BreakAll for the authored declaration. This test
        // pins the working path so a future AngleSharp regression
        // is detected.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { word-break: break-all; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(NpWordBreak.BreakAll, policy.WordBreak);
    }

    [Fact]
    public async Task EndToEnd_AngleSharp_drops_hyphens_auto()
    {
        // Same pin as above — hyphens declaration is dropped at the
        // AngleSharp parser layer.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { hyphens: auto; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(Hyphens.Manual, policy.Hyphens);
    }
}
