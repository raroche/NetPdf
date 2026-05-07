// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.UnitTests.Phase2;

/// <summary>
/// Regression tests for Phase 2 deep review recommendations 1–7. Each
/// fact pins one specific cycle-1 hole the reviewer surfaced.
/// </summary>
public sealed class Phase2DeepReviewHardeningTests
{
    private sealed class CapturingCssSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private sealed class CapturingPublicSink : IDiagnosticsSink
    {
        public List<Diagnostic> Diagnostics { get; } = new();
        public void Emit(Diagnostic d) => Diagnostics.Add(d);
    }

    // --- Rec 1: style-rule slot alignment by selector identity ----------------

    [Fact]
    public async Task Rec1_dropped_style_rule_does_not_consume_next_rules_location()
    {
        // li::marker is dropped by AngleSharp.Css 1.0.0-beta.144. The next
        // style rule is `p { color: red }`. With kind-only matching, the
        // dropped slot would consume the `p` rule, corrupting source location
        // ordinals + the recovered selector text. Selector-identity matching
        // detects the mismatch + demotes the dropped slot to opaque without
        // advancing the AngleSharp cursor.
        var sheet = await ParseSheet("li::marker { content: counter(items); color: red } p { color: red }");
        var styleRules = sheet.Rules.OfType<CssStyleRule>().ToList();
        Assert.Equal(2, styleRules.Count);
        Assert.Equal("li::marker", styleRules[0].Selector.RawText);
        Assert.Equal("p", styleRules[1].Selector.RawText);
    }

    // --- Rec 2: EmitOpaqueFromSlot recovers declarations from RawBody --------

    [Fact]
    public async Task Rec2_dropped_style_rule_preserves_declarations_from_raw_body()
    {
        // Without recovery, the dropped li::marker rule would land with empty
        // Declarations + the cascade would never see `content`/`color`, making
        // the spec-required CSS-CONTENT-FUNCTION-UNSUPPORTED-001 emission
        // unreachable through the production path.
        var sheet = await ParseSheet("li::marker { content: counter(items); color: red }");
        var rule = sheet.Rules.OfType<CssStyleRule>().Single();
        Assert.Equal("li::marker", rule.Selector.RawText);
        Assert.Equal(2, rule.Declarations.Length);
        Assert.Contains(rule.Declarations, d => d.Property == "content");
        Assert.Contains(rule.Declarations, d => d.Property == "color");
    }

    // --- Rec 3: inline style="" runs through CssPreprocessor recovery --------

    [Fact]
    public async Task Rec3_inline_style_with_modern_color_emits_diagnostic_via_production_path()
    {
        // Inline styles previously bypassed the preprocessor recovery layer,
        // so `style="color: oklch(...)"` was lost or misdiagnosed. The fix
        // routes inline styles through AdaptInlineStyleWithRecovery which
        // detects modern functions + merges the recovered raw value over
        // AngleSharp's normalized output.
        const string html = @"<!doctype html><html><body><p style=""color: oklch(0.7 0.15 250)"">x</p></body></html>";
        var sink = new CapturingPublicSink();
        var options = new HtmlPdfOptions { Diagnostics = sink };
        using var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001");
    }

    // --- Rec 4: @supports validates property values, not just names ----------

    [Fact]
    public async Task Rec4_supports_with_invalid_value_returns_false()
    {
        // Cycle-1 returned true on bare property-name match; the guarded
        // block applied even with `color: not-a-real-color`. CSS Conditional
        // L3 §4.1.3 requires both property AND value to be supported.
        const string css = @"
            @supports (color: not-a-real-color) {
                body { background-color: yellow }
            }
            @supports (color: red) {
                body { background-color: red }
            }";
        var (sheet, preprocess) = await ParseAndPreprocess(css);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);

        var doc = await ParseDoc("<!doctype html><html><body></body></html>");
        var media = CssMediaContext.DefaultPrint;
        var result = CascadeResolver.Resolve(doc, [stylesheet], media);

        var body = doc.QuerySelector("body");
        Assert.NotNull(body);
        var entries = result.StylesFor(body!);
        // background-color SHOULD be `red` from the second @supports, not
        // `yellow` from the first (which had an invalid value).
        var bg = entries.GetWinner("background-color");
        Assert.NotNull(bg);
        // AngleSharp normalizes `red` → `rgba(255, 0, 0, 1)`. The point of the
        // test is "red won, not yellow" — yellow is the value from the
        // first @supports block whose value-validation MUST have failed.
        Assert.Contains("255, 0, 0", bg!.Declaration.Value.RawText);
        Assert.DoesNotContain("yellow", bg.Declaration.Value.RawText);
        Assert.DoesNotContain("255, 255, 0", bg.Declaration.Value.RawText);
    }

    // --- Rec 5: CalcResolver finite guards on numbers + arithmetic -----------

    [Fact]
    public void Rec5_calc_with_overflowing_literal_emits_invalid_diagnostic()
    {
        // `calc(1e500 * 1px)` parses to Infinity; cycle-1 emitted the literal
        // text "Infinity" downstream instead of firing CSS-CALC-INVALID-001
        // like other syntax errors.
        var sink = new CapturingCssSink();
        var rewritten = CalcResolver.Resolve("calc(1e500 * 1px)", sink);
        Assert.Contains(sink.Diagnostics, d => d.Code == "CSS-CALC-INVALID-001");
        Assert.DoesNotContain("Infinity", rewritten);
        Assert.DoesNotContain("NaN", rewritten);
    }

    [Fact]
    public void Rec5_calc_arithmetic_overflow_emits_invalid_diagnostic()
    {
        // Two large finite operands whose product overflows.
        var sink = new CapturingCssSink();
        var rewritten = CalcResolver.Resolve("calc(1e150px * 1e200)", sink);
        Assert.Contains(sink.Diagnostics, d => d.Code == "CSS-CALC-INVALID-001");
        Assert.DoesNotContain("Infinity", rewritten);
        Assert.DoesNotContain("NaN", rewritten);
    }

    // --- Rec 6: CancellationToken inside walkers, not just stage boundaries --

    [Fact]
    public async Task Rec6_pre_cancelled_token_stops_phase2_walk_promptly()
    {
        // Even on a hostile-sized document, a pre-cancelled token must throw
        // before the walker runs the full DOM pass. Build a 1k-element doc
        // (representative; the principle applies at any scale) + assert
        // OperationCanceledException fires from inside the stage.
        var html = "<!doctype html><html><body>" + string.Concat(Enumerable.Repeat("<p>x</p>", 1000)) + "</body></html>";
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(async () =>
        {
            var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), cancellationToken: cts.Token);
            result.Dispose();
        });
    }

    // --- Rec 7: Phase2Result.Dispose returns box-owned styles to pool --------

    [Fact]
    public async Task Rec7_phase2_result_dispose_releases_box_owned_styles()
    {
        // After Dispose, every box-owned ComputedStyle should be in the
        // disposed state — verify by checking IsBoxOwned flag flips back to
        // false (ReleaseFromBox clears it before pool return).
        const string html = "<!doctype html><html><body><p>hello</p></body></html>";
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());

        // Pre-dispose: the principal <p> box's style should be box-owned.
        var pBox = WalkBoxes(result.BoxRoot)
            .FirstOrDefault(b => b.SourceElement?.LocalName == "p"
                              && b.Kind == NetPdf.Layout.Boxes.BoxKind.BlockContainer);
        Assert.NotNull(pBox);
        Assert.True(pBox!.Style.IsBoxOwned, "Style was not marked box-owned by BoxBuilder");

        result.Dispose();

        // Post-dispose: IsBoxOwned cleared. (Tests pool-return: the slot can
        // now be re-rented.)
        Assert.False(pBox.Style.IsBoxOwned);
    }

    [Fact]
    public async Task Rec7_phase2_result_dispose_is_idempotent()
    {
        const string html = "<!doctype html><html><body><p>hello</p></body></html>";
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        result.Dispose();
        result.Dispose(); // second call must not throw.
    }

    // --- Helpers -------------------------------------------------------------

    private static async Task<CssStylesheet> ParseSheet(string css)
    {
        var (sheet, preprocess) = await ParseAndPreprocess(css);
        return CssParserAdapter.Adapt(
            sheet, preprocess, href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
    }

    private static async Task<(ICssStyleSheet sheet, CssPreprocessResult preprocess)> ParseAndPreprocess(string css)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, IsKeepingSourceReferences = true });
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(parser);
        var ctx = BrowsingContext.New(config);

        var html = $"<html><head><style>{css}</style></head><body></body></html>";
        var document = await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
        var sheet = document.StyleSheets.OfType<ICssStyleSheet>().Single();
        var preprocess = CssPreprocessor.Process(css);
        return (sheet, preprocess);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseDoc(string html)
    {
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false });
        var ctx = BrowsingContext.New(config);
        return await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
    }

    private static IEnumerable<NetPdf.Layout.Boxes.Box> WalkBoxes(NetPdf.Layout.Boxes.Box root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in WalkBoxes(c))
                yield return d;
    }
}
