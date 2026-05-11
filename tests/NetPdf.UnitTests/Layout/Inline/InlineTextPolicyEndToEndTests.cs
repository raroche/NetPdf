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
using NetPdf.Css.Parser.Preprocessing;
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
        // Per Phase 3 Task 10 cycle 3 review (User #1) — run the
        // CssPreprocessor BEFORE handing the parsed AngleSharp tree
        // to the adapter. Production (Phase2Pipeline) does this; the
        // earlier test helper bypassed it which is why the
        // recovered-property paths weren't being exercised end-to-end.
        var preprocess = CssPreprocessor.Process(css);
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(sheet, preprocess, href: null,
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
    public async Task EndToEnd_overflow_wrap_anywhere_recovered_via_preprocessor()
    {
        // Per Phase 3 Task 10 cycle 3 review (User #1) — the
        // CssPreprocessor now recovers overflow-wrap declarations
        // that AngleSharp.Css drops. The full production pipeline
        // (preprocess → parse → adapt → cascade → ComputedStyle)
        // correctly resolves overflow-wrap:anywhere to keyword id 1
        // (anywhere) → OverflowWrap.Anywhere.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { overflow-wrap: anywhere; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var owSlot = pStyle.Get(PropertyId.OverflowWrap);
        Assert.Equal(ComputedSlotTag.Keyword, owSlot.Tag);
        Assert.Equal(1, owSlot.AsKeyword()); // anywhere

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.Anywhere, policy.OverflowWrap);
    }

    [Fact]
    public async Task EndToEnd_overflow_wrap_break_word_recovered_and_maps_to_BreakWord()
    {
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
        // overflow-wrap: break-word now maps to a DISTINCT BreakWord
        // enum variant (pre-fix folded to Anywhere). Line-wrap
        // behavior is identical to Anywhere; only intrinsic-sizing
        // measurement (auto-table-layout's min-content pass)
        // distinguishes the two per CSS Text L3 §5.1.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { overflow-wrap: break-word; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.BreakWord, policy.OverflowWrap);
    }

    [Fact]
    public async Task EndToEnd_word_break_break_all_flows_through_pipeline()
    {
        // AngleSharp.Css 1.0.0-beta.144 DOES recognize word-break.
        // No recovery needed — declaration flows natively. This test
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
    public async Task EndToEnd_hyphens_auto_recovered_via_preprocessor()
    {
        // Per Phase 3 Task 10 cycle 3 review (User #1) — recovery
        // closes the hyphens drop at the AngleSharp parser layer.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { hyphens: auto; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(Hyphens.Auto, policy.Hyphens);
    }

    [Fact]
    public async Task EndToEnd_white_space_break_spaces_no_longer_silently_folds_to_Normal()
    {
        // Per Phase 3 Task 10 cycle 3 review (User #3) — pre-fix:
        // break-spaces silently folded to Normal, collapsing
        // authored spaces. Post-fix: maps to WhiteSpace.BreakSpaces
        // (preserve + wrap), which is the spec-compliant
        // approximation pending the "wrap at every preserved
        // space" detail in a later cycle.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { white-space: break-spaces; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(WhiteSpace.BreakSpaces, policy.WhiteSpace);
    }

    [Fact]
    public async Task EndToEnd_repeated_white_space_declaration_last_wins()
    {
        // Per Phase 3 Task 10 cycle 3b review (User #1) — when a
        // rule has repeated declarations like
        // `white-space: normal; white-space: break-spaces`, the LAST
        // one wins per CSS cascade rules. Earlier "first match"
        // FindRecovery semantics broke this for known-dropped
        // properties.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { white-space: normal; white-space: break-spaces; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(WhiteSpace.BreakSpaces, policy.WhiteSpace);
    }

    [Fact]
    public async Task EndToEnd_repeated_overflow_wrap_declaration_last_wins()
    {
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { overflow-wrap: anywhere; overflow-wrap: normal; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        // Last decl is `normal` — should win.
        Assert.Equal(OverflowWrap.Normal, policy.OverflowWrap);
    }

    [Fact]
    public async Task EndToEnd_word_wrap_then_overflow_wrap_alias_pair_last_wins()
    {
        // word-wrap normalizes to overflow-wrap. Authored:
        //   word-wrap: normal; overflow-wrap: anywhere;
        // After alias normalization both target overflow-wrap;
        // last-decl-wins picks anywhere.
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { word-wrap: normal; overflow-wrap: anywhere; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.Anywhere, policy.OverflowWrap);
    }

    [Fact]
    public async Task EndToEnd_overflow_wrap_then_word_wrap_alias_pair_last_wins()
    {
        // Reverse order — last-decl-wins picks word-wrap (which
        // normalizes to overflow-wrap: normal).
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { overflow-wrap: anywhere; word-wrap: normal; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.Normal, policy.OverflowWrap);
    }

    [Fact]
    public async Task EndToEnd_repeated_hyphens_declaration_last_wins()
    {
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { hyphens: auto; hyphens: none; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(Hyphens.None, policy.Hyphens);
    }

    [Fact]
    public async Task EndToEnd_word_wrap_legacy_alias_normalizes_to_overflow_wrap()
    {
        // Per Phase 3 Task 10 cycle 3 review (User #2) — `word-wrap`
        // is the CSS Text 2 legacy alias for `overflow-wrap`. The
        // CssPreprocessor's central LegacyPropertyAliases map
        // normalizes word-wrap → overflow-wrap at recovery time, so
        // authored documents using `word-wrap: break-word` resolve
        // through the production cascade as `overflow-wrap:
        // break-word`.
        //
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
        // the materializer now maps overflow-wrap:break-word to a
        // distinct BreakWord enum variant (line-wrap behavior
        // identical to Anywhere; intrinsic-sizing-pass distinction
        // per CSS Text L3 §5.1).
        const string html = "<html><body><p>x</p></body></html>";
        const string css = "p { word-wrap: break-word; }";

        var pStyle = await GetParagraphComputedStyle(html, css);
        Assert.NotNull(pStyle);

        var policy = pStyle.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.BreakWord, policy.OverflowWrap);
    }
}
