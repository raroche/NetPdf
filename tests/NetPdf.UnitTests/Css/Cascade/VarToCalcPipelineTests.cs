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
/// End-to-end pipeline tests proving that <see cref="VarResolver"/> hands resolved
/// values to <see cref="CalcResolver"/> per the Task 9 design — a <c>var()</c> reference
/// holding a math function (or feeding into one) reduces to a single value once
/// substitution exposes the operands. Catches drift between the two resolvers, e.g.
/// regressions where one is bypassed or the order is flipped.
/// </summary>
public sealed class VarToCalcPipelineTests
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

    private static async Task<(ResolvedCascadeResult Resolved, CapturingSink Sink, IDocument Doc)>
        Run(string html, string css)
    {
        var doc = await ParseHtml(html);
        var sheet = await ParseSheet(css);
        var sink = new CapturingSink();
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint, sink);
        var resolved = VarResolver.Resolve(cascade, doc, sink);
        return (resolved, sink, doc);
    }

    // ============================================================
    // Var holds a math function — calc() comes from the substituted value
    // ============================================================

    [Fact]
    public async Task Var_holding_calc_expression_is_reduced_after_substitution()
    {
        // --gap holds a calc() expression. After var() substitution the property text
        // becomes "calc(8px + 4px)"; CalcResolver then reduces it to "12px".
        // Note: `width` (longhand) on purpose — `padding` is a shorthand AngleSharp
        // expands into padding-* longhands, complicating winner lookup.
        var (resolved, sink, doc) = await Run(
            "<p>x</p>",
            "p { --gap: calc(8px + 4px); width: var(--gap) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.NotNull(width);
        Assert.Equal("12px", width!.ResolvedValue);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public async Task Two_vars_feeding_calc_reduce_after_substitution()
    {
        // width: calc(var(--a) + var(--b)) → calc(8px + 4px) → 12px.
        var (resolved, _, doc) = await Run(
            "<p>x</p>",
            "p { --a: 8px; --b: 4px; width: calc(var(--a) + var(--b)) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("12px", width!.ResolvedValue);
    }

    [Fact]
    public async Task Inherited_var_feeds_calc_in_descendant()
    {
        // Custom property declared on parent, calc() referencing it on child — the
        // resolved table on the child includes the inherited --base.
        var (resolved, _, doc) = await Run(
            "<html><body><div class='root'><p class='child'>x</p></div></body></html>",
            ".root { --base: 16px } .child { width: calc(var(--base) * 2) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("32px", width!.ResolvedValue);
    }

    [Fact]
    public async Task Var_with_fallback_calc_reduces_when_var_missing()
    {
        // --missing not declared → fallback "calc(16px + 4px)" used → reduced to "20px".
        var (resolved, _, doc) = await Run(
            "<p>x</p>",
            "p { width: var(--missing, calc(16px + 4px)) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("20px", width!.ResolvedValue);
    }

    [Fact]
    public async Task Nested_var_chain_feeding_calc_resolves()
    {
        // --a depends on --b (own-layer var resolution per VarResolver step 2), and
        // calc reads --a. Ensures the per-element resolution happens before calc fires.
        var (resolved, _, doc) = await Run(
            "<p>x</p>",
            "p { --b: 8px; --a: var(--b); width: calc(var(--a) * 4) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("32px", width!.ResolvedValue);
    }

    // ============================================================
    // Order of operations — var first, then calc
    // ============================================================

    [Fact]
    public async Task Calc_runs_after_var_substitution_not_before()
    {
        // The calc() function in the source contains a var() reference. If CalcResolver
        // ran first it'd see "calc(var(--n) * 4px)" and (correctly) defer; the assertion
        // here is that var substitution happened FIRST so calc can finish.
        var (resolved, _, doc) = await Run(
            "<p>x</p>",
            "p { --n: 8; width: calc(var(--n) * 4px) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("32px", width!.ResolvedValue);
    }

    [Fact]
    public async Task Var_substitution_invalid_keeps_value_invalid_no_calc_reduction()
    {
        // --circular is a cycle; per L1 §3.5 referenced declarations become invalid at
        // computed-value-time. The "unset" sentinel surfaces — calc never sees a number.
        var (resolved, sink, doc) = await Run(
            "<p>x</p>",
            "p { --a: var(--b); --b: var(--a); width: calc(var(--a) * 2) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.NotNull(width);
        // Value is the IACVT sentinel surface; no "32px" or other reduced number.
        Assert.DoesNotContain("32", width!.ResolvedValue);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    // ============================================================
    // Calc reduction shape preserved through substitution
    // ============================================================

    [Fact]
    public async Task Var_holding_min_max_clamp_reduces()
    {
        var (resolved, _, doc) = await Run(
            "<p>x</p>",
            "p { --gap: clamp(8px, 16px, 32px); width: var(--gap) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("16px", width!.ResolvedValue);
    }

    [Fact]
    public async Task Var_holding_deferred_calc_preserves_text_for_layout()
    {
        // calc(100% - 16px) is deferred (mixed unit). After var substitution the
        // value remains exactly that text — layout finalizes it.
        var (resolved, sink, doc) = await Run(
            "<p>x</p>",
            "p { --pad: calc(100% - 16px); width: var(--pad) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("calc(100% - 16px)", width!.ResolvedValue);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public async Task Var_holding_em_calc_defers_with_no_diagnostic()
    {
        // em is a context-relative unit per Rec 1 — defer through both resolvers.
        var (resolved, sink, doc) = await Run(
            "<p>x</p>",
            "p { --gap: calc(2em); width: var(--gap) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("calc(2em)", width!.ResolvedValue);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public async Task Var_feeding_div_by_zero_emits_diagnostic_and_preserves_text()
    {
        var (resolved, sink, doc) = await Run(
            "<p>x</p>",
            "p { --d: 0; width: calc(16px / var(--d)) }");

        var width = resolved.TryGetStylesFor(Q(doc, "p"))!.GetWinner("width");
        Assert.Equal("calc(16px / 0)", width!.ResolvedValue);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssCalcDivByZero001);
    }
}
