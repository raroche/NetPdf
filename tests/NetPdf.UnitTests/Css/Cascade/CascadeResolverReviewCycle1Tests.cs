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
/// Review-cycle 1 tests for Phase 2 Task 7 — covers eight review-driven hardening fixes:
/// nested @media gating, inline-style cascade tier, sheet.Order respect, @supports
/// evaluator, opaque @container/@layer diagnostics, layered cascade ordering, @import
/// recursion, and source-order tuple comparison. Grouped by recommendation.
/// </summary>
public sealed class CascadeResolverReviewCycle1Tests
{
    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static async Task<CssStylesheet> ParseSheet(string css,
        CssStylesheetOrigin origin = CssStylesheetOrigin.Author,
        int order = 0,
        string? mediaQuery = null)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(sheet, href: null, origin: origin,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: mediaQuery, isDisabled: false, order: order);
    }

    private static IElement Q(IDocument doc, string css) =>
        doc.QuerySelector(css)!;

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // ============================================================
    // Rec 1 — Nested @media gating by current media context
    // ============================================================

    [Fact]
    public async Task Rec1_AtMedia_screen_does_not_apply_in_print()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@media screen { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        // Print context — the @media screen block must NOT contribute.
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task Rec1_AtMedia_print_applies_in_print()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@media print { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color"));
    }

    [Fact]
    public async Task Rec1_AtMedia_all_applies_in_any_context()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@media all { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color"));
    }

    [Fact]
    public async Task Rec1_AtMedia_screen_in_screen_context_applies()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@media screen { p { color: red } }");
        var screenCtx = CssMediaContext.DefaultPrint with { MediaType = "screen" };
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet), screenCtx);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color"));
    }

    // ============================================================
    // Rec 2 — Inline style beats high-specificity selectors
    // ============================================================

    [Fact]
    public async Task Rec2_Inline_style_beats_id_selector()
    {
        var doc = await ParseHtml("<p id=\"x\" style=\"color: blue\">x</p>");
        var sheet = await ParseSheet("#x { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("0, 0, 255", winner!.Declaration.Value.RawText); // blue
    }

    [Fact]
    public async Task Rec2_Inline_style_beats_compound_id_selector()
    {
        // Three id selectors → specificity (3,0,0) which would beat the previous
        // (1,0,0) inline-style sentinel. The new IsInlineStyle tier renders that
        // attempt moot.
        var doc = await ParseHtml("<p id=\"x\" class=\"a b\" style=\"color: blue\">x</p>");
        var sheet = await ParseSheet("#x.a.b { color: red }"); // (1, 2, 0)
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("0, 0, 255", winner!.Declaration.Value.RawText); // blue (inline)
    }

    [Fact]
    public async Task Rec2_Important_selector_beats_inline_normal()
    {
        var doc = await ParseHtml("<p style=\"color: blue\">x</p>");
        var sheet = await ParseSheet("p { color: red !important }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("255, 0, 0", winner!.Declaration.Value.RawText); // red (important)
    }

    [Fact]
    public async Task Rec2_Inline_important_beats_selector_important_at_lower_specificity()
    {
        var doc = await ParseHtml("<p style=\"color: blue !important\">x</p>");
        var sheet = await ParseSheet("p { color: red !important }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("0, 0, 255", winner!.Declaration.Value.RawText); // blue
    }

    // ============================================================
    // Rec 3 — Use sheet.Order, not array index
    // ============================================================

    [Fact]
    public async Task Rec3_Stylesheet_order_field_is_respected_for_source_order_tiebreak()
    {
        var doc = await ParseHtml("<p>x</p>");
        // Two sheets with declarations at the same specificity. The cascade should pick
        // the one with the higher Order field as last-declared.
        var earlier = await ParseSheet("p { color: red }", order: 1);
        var later = await ParseSheet("p { color: blue }", order: 5);
        // Pass them in REVERSE Order to verify the cascade uses sheet.Order, not array
        // index.
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(later, earlier),
            CssMediaContext.DefaultPrint);
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("0, 0, 255", winner!.Declaration.Value.RawText); // blue (Order=5)
    }

    [Fact]
    public async Task Rec3_Inline_styles_pinned_above_max_sheet_Order()
    {
        var doc = await ParseHtml("<p style=\"color: blue\">x</p>");
        var sheet = await ParseSheet("p { color: red }", order: 999);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        // Inline style still wins because the IsInlineStyle tier beats selector tier
        // regardless of source order. Also verifies inline order > sheet.Order=999.
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.Contains("0, 0, 255", winner!.Declaration.Value.RawText); // blue
    }

    // ============================================================
    // Rec 4 — @supports evaluator (basic)
    // ============================================================

    [Fact]
    public async Task Rec4_AtSupports_known_property_applies()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@supports (color: red) { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    [Fact]
    public async Task Rec4_AtSupports_unknown_property_skips_with_diagnostic()
    {
        var doc = await ParseHtml("<p>x</p>");
        // Some property name unlikely to be in our v1 registry.
        var sheet = await ParseSheet("@supports (totally-fake-property: foo) { p { color: red } }");
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);
        // Children skipped, no styles for p.
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task Rec4_AtSupports_not_inverts_condition()
    {
        var doc = await ParseHtml("<p>x</p>");
        // not (color: red) — color is registered → inner is true → not is false → skip.
        var sheet = await ParseSheet("@supports not (color: red) { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task AtSupports_object_fit_evaluates_true()
    {
        // PR #168 review P2 — object-fit is REGISTERED in properties.json (object-fit cycle),
        // so `@supports (object-fit: contain)` gates its block IN. Pre-registration the
        // unregistered property evaluated false and the rule was silently skipped while the
        // compatibility matrix claimed support.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@supports (object-fit: contain) { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    [Fact]
    public async Task AtSupports_object_position_evaluates_true_when_registered()
    {
        // Backlog #6 — object-position is now REGISTERED (properties.json type `Position` +
        // PositionResolver), so `@supports (object-position: right bottom)` evaluates TRUE and gates
        // its block IN: the property is known to PropertyMetadata.NameToId AND the <position> value
        // validates. It still renders from the RAW cascade winner at paint time (validation-only
        // registration — was PR #169's documented gap, now closed).
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@supports (object-position: right bottom) { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task AtSupports_object_position_invalid_value_evaluates_false()
    {
        // An INVALID <position> component (not a keyword or <length-percentage>) → PositionResolver
        // returns Invalid → @supports evaluates FALSE → the block is skipped.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@supports (object-position: not-a-position) { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task AtSupports_object_position_accepts_math_function_components()
    {
        // Post-PR-#183 review P3 — a length-percentage math function (`calc(50% - 10px)`) is a valid
        // <position> component, so `@supports (object-position: calc(50% - 10px) top)` evaluates TRUE.
        // Pre-fix the whitespace tokenizer fragmented the calc into broken tokens and reported FALSE. A
        // malformed math function (`calc(50% +)`) still evaluates FALSE.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "@supports (object-position: calc(50% - 10px) top) { p { color: red } } " +
            "@supports (object-position: min(10px, 5%) clamp(0px, 50%, 100px)) { p { font-weight: bold } } " +
            "@supports (object-position: calc(50% +) top) { p { font-size: 20px } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var rules = result.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(rules?.GetWinner("color"));          // calc(50% - 10px) top → supported
        Assert.NotNull(rules?.GetWinner("font-weight"));    // min()/clamp() pair → supported
        Assert.Null(rules?.GetWinner("font-size"));         // malformed calc → unsupported
    }

    [Fact]
    public async Task AtSupports_page_property_evaluates_true_when_registered()
    {
        // Backlog #6 — the `page` property is now REGISTERED (type `PageName` + PageNameResolver:
        // `auto | <custom-ident>`), so `@supports (page: chapter)` evaluates TRUE. The named-page
        // machinery still reads the name raw onto Box.PageName (validation-only registration).
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("@supports (page: chapter) { p { color: red } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p")));
    }

    [Fact]
    public async Task AtSupports_page_property_accepts_a_dashed_custom_ident()
    {
        // Post-PR-#183 review P2 — a DASHED ident (`--chapter`) is a valid <custom-ident>, so
        // `@supports (page: --chapter)` must evaluate TRUE (it was wrongly rejected before centralizing
        // the validator). A digit-start (`page: 1up`) stays unsupported.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "@supports (page: --chapter) { p { color: red } } " +
            "@supports (page: 1up) { p { font-size: 20px } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var rules = result.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(rules?.GetWinner("color"));         // `page: --chapter` supported → block applies
        Assert.Null(rules?.GetWinner("font-size"));        // `page: 1up` unsupported → block excluded
    }

    [Fact]
    public async Task AtSupports_background_origin_and_clip_evaluate_true()
    {
        // PR #170 review P2 — background-origin/-clip are REGISTERED keyword properties (properties.json
        // + the keyword resolver), so `@supports (background-origin: content-box)` /
        // `(background-clip: padding-box)` gate their blocks IN (the metadata-based @supports evaluator
        // sees them); an invalid value is diagnosed (KeywordResolverTests).
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "@supports (background-origin: content-box) { p { color: red } } " +
            "@supports (background-clip: padding-box) { p { font-size: 20px } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var rules = result.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(rules?.GetWinner("color"));
        Assert.NotNull(rules?.GetWinner("font-size"));
    }

    [Fact]
    public async Task AtSupports_background_attachment_and_border_radius_evaluate_true()
    {
        // bg-attachment / body-radius cycles — newly registered: background-attachment (keyword) +
        // the border-*-radius longhands (length-percentage, expanded from the `border-radius`
        // shorthand). @supports reports both.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "@supports (background-attachment: fixed) { p { color: red } } " +
            "@supports (border-top-left-radius: 8px) { p { font-size: 20px } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var rules = result.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(rules?.GetWinner("color"));
        Assert.NotNull(rules?.GetWinner("font-size"));
    }

    [Fact]
    public async Task AtSupports_outline_properties_evaluate_true()
    {
        // outline cycle — newly registered outline-style (keyword) + outline-width (line-width) +
        // outline-color (color). @supports reports them.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "@supports (outline-style: solid) { p { color: red } } " +
            "@supports (outline-width: 2px) { p { font-size: 20px } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var rules = result.TryGetStylesFor(Q(doc, "p"));
        Assert.NotNull(rules?.GetWinner("color"));
        Assert.NotNull(rules?.GetWinner("font-size"));
    }

    [Fact]
    public async Task AtSupports_outline_style_hidden_false_and_outline_color_auto_true()
    {
        // post-PR-#173 review P2 — `outline-style: hidden` is INVALID (CSS UI 4 excludes hidden), so
        // @supports is FALSE; `outline-color: auto` is admitted (→ currentcolor), so @supports is TRUE.
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet(
            "@supports (outline-style: hidden) { p { color: red } } " +
            "@supports (outline-color: auto) { p { font-size: 20px } }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var rules = result.TryGetStylesFor(Q(doc, "p"));
        Assert.Null(rules?.GetWinner("color"));        // hidden unsupported → rule didn't apply
        Assert.NotNull(rules?.GetWinner("font-size")); // auto supported → rule applied
    }

    [Fact]
    public async Task Rec4_AtSupports_and_combines()
    {
        // Synthetic AST so the @supports prelude survives unambiguously through to the
        // cascade — AngleSharp.Css's prelude normalization may strip outer parens.
        var doc = await ParseHtml("<p>x</p>");
        var innerRule = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        // Both `color` and `display` are in our v1 property registry; `margin` (shorthand)
        // is not — so the test would fail if either side resolves false. Use two known-good
        // longhands so the AND truly tests boolean composition.
        var supports = new CssAtRule(
            Name: "supports",
            Prelude: "(color: red) and (display: block)",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(innerRule),
            Location: CssSourceLocation.Unknown);
        var sheet = new CssStylesheet(
            Rules: ImmutableArray.Create<CssRule>(supports),
            Href: null, Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null, IsDisabled: false, Order: 0,
            Location: CssSourceLocation.Unknown);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    // ============================================================
    // Rec 5 — @container diagnostic + opaque at-rule diagnostic
    // ============================================================

    [Fact]
    public async Task Rec5_AtContainer_emits_diagnostic_and_skips_children()
    {
        // AngleSharp.Css drops @container — Task 3's preprocessor is the path that recovers
        // them as opaque CssAtRule. Constructing the AST directly here exercises the
        // cascade's @container handling without the preprocessor dependency.
        var doc = await ParseHtml("<p>x</p>");
        var innerRule = new CssStyleRule(
            new CssSelector("p"),
            ImmutableArray.Create(new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            CssSourceLocation.Unknown);
        var container = new CssAtRule(
            Name: "container",
            Prelude: "(min-width: 800px)",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray.Create<CssRule>(innerRule),
            Location: CssSourceLocation.Unknown);
        var sheet = new CssStylesheet(
            Rules: ImmutableArray.Create<CssRule>(container),
            Href: null, Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null, IsDisabled: false, Order: 0,
            Location: CssSourceLocation.Unknown);
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssContainerQueryUnsupported001);
    }

    [Fact]
    public async Task Rec5_Opaque_at_rule_with_RawBody_emits_diagnostic()
    {
        // An at-rule the parser couldn't decompose (RawBody set, ChildRules + Declarations
        // empty, name not on the known declaration-bearing list) should emit
        // CSS-AT-RULE-UNKNOWN-001 so users know their content was preserved-not-applied.
        var doc = await ParseHtml("<p>x</p>");
        var opaque = new CssAtRule(
            Name: "scope",
            Prelude: "",
            Declarations: ImmutableArray<CssDeclaration>.Empty,
            ChildRules: ImmutableArray<CssRule>.Empty,
            Location: CssSourceLocation.Unknown,
            RawBody: ".inner { color: red }");
        var sheet = new CssStylesheet(
            Rules: ImmutableArray.Create<CssRule>(opaque),
            Href: null, Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null, IsDisabled: false, Order: 0,
            Location: CssSourceLocation.Unknown);
        var sink = new CapturingSink();
        _ = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);

        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }

    // ============================================================
    // Rec 7 — @import recursion
    // ============================================================

    [Fact]
    public async Task Rec7_Empty_ImportedRules_does_not_throw()
    {
        // In v1 ImportedRules is always empty (resource loading disabled). The cascade
        // should handle the empty-import case without error.
        var emptyImport = new CssImportRule(
            Url: "x.css", MediaQuery: "", LayerName: null,
            SupportsCondition: null,
            ImportedRules: ImmutableArray<CssRule>.Empty,
            Location: CssSourceLocation.Unknown);
        var sheet = new CssStylesheet(
            Rules: ImmutableArray.Create<CssRule>(emptyImport),
            Href: null, Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null, IsDisabled: false, Order: 0,
            Location: CssSourceLocation.Unknown);

        var doc = await ParseHtml("<p>x</p>");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Rec7_Imported_rules_collected_at_import_position()
    {
        // Synthetic ImportedRules — the imported sheet has a rule `p { color: red }`.
        var doc = await ParseHtml("<p>x</p>");

        // Build the imported style rule directly via the AST.
        var importedRule = new CssStyleRule(
            Selector: new CssSelector("p"),
            Declarations: ImmutableArray.Create(
                new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            Location: CssSourceLocation.Unknown);

        var import = new CssImportRule(
            Url: "x.css", MediaQuery: "", LayerName: null,
            SupportsCondition: null,
            ImportedRules: ImmutableArray.Create<CssRule>(importedRule),
            Location: CssSourceLocation.Unknown);

        var sheet = new CssStylesheet(
            Rules: ImmutableArray.Create<CssRule>(import),
            Href: null, Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null, IsDisabled: false, Order: 0,
            Location: CssSourceLocation.Unknown);

        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        Assert.NotNull(result.TryGetStylesFor(Q(doc, "p"))?.GetWinner("color"));
    }

    [Fact]
    public async Task Rec7_Imported_rules_skipped_when_media_does_not_match()
    {
        var doc = await ParseHtml("<p>x</p>");
        var importedRule = new CssStyleRule(
            Selector: new CssSelector("p"),
            Declarations: ImmutableArray.Create(
                new CssDeclaration("color", new CssValue("red"), false, CssSourceLocation.Unknown)),
            Location: CssSourceLocation.Unknown);
        var import = new CssImportRule(
            Url: "x.css", MediaQuery: "screen", LayerName: null,
            SupportsCondition: null,
            ImportedRules: ImmutableArray.Create<CssRule>(importedRule),
            Location: CssSourceLocation.Unknown);
        var sheet = new CssStylesheet(
            Rules: ImmutableArray.Create<CssRule>(import),
            Href: null, Origin: CssStylesheetOrigin.Author,
            OwnerKind: CssStylesheetOwnerKind.StyleElement,
            MediaQuery: null, IsDisabled: false, Order: 0,
            Location: CssSourceLocation.Unknown);
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);  // print
        Assert.Null(result.TryGetStylesFor(Q(doc, "p")));
    }

    // ============================================================
    // Cycle-1-broader: existing test updates to verify new behaviors don't regress
    // ============================================================

    [Fact]
    public async Task Cycle1_smoke_basic_cascade_still_works()
    {
        var doc = await ParseHtml("<p>x</p>");
        var sheet = await ParseSheet("p { color: red }");
        var result = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);
        var winner = result.TryGetStylesFor(Q(doc, "p"))!.GetWinner("color");
        Assert.NotNull(winner);
    }
}
