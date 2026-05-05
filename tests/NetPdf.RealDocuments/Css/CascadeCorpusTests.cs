// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using AngleSharp.Css.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.RealDocuments.Css;

/// <summary>
/// Integration tests for Phase 2 Task 7: every invoice in the corpus is parsed end-to-end
/// (HTML → DOM → CSSOM → adapted stylesheet → cascade) without throwing, and every
/// element ends up with a (possibly empty) per-element matched-rule-set lookup. The Tailwind
/// CDN samples additionally exercise the diagnostic emission for the Tailwind CSS-in-JS
/// gap (no rules apply because the JS-generated utility classes never produce any CSSOM
/// rules at parse time — corpus comments document this limitation).
/// </summary>
public sealed class CascadeCorpusTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public System.Collections.Generic.List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Corpus_invoice_runs_through_cascade_without_throwing(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            sheets.Add(CssParserAdapter.Adapt(
                rawSheet, href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null, isDisabled: false, order: order++));
        }

        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(document, sheets.ToImmutable(),
            CssMediaContext.DefaultPrint, sink);

        Assert.NotNull(result);
        // Every corpus document has a styled root element — at minimum, body picks up
        // some styles (margin/padding from author rules).
        Assert.True(result.ElementCount > 0,
            $"corpus '{relativePath}' should produce at least one styled element");
    }

    [Fact]
    public async Task Corpus_classic_pure_css_picks_up_typography_styles_on_body()
    {
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheet = CssParserAdapter.Adapt(document.StyleSheets.OfType<ICssStyleSheet>().Single());
        var result = CascadeResolver.Resolve(document, ImmutableArray.Create(sheet),
            CssMediaContext.DefaultPrint);

        var body = document.Body!;
        var bodyStyles = result.TryGetStylesFor(body);
        Assert.NotNull(bodyStyles);
        // The classic invoice's body has font-family / color / margin declarations.
        // We don't pin specific properties (corpus may evolve) — just that body is styled.
        Assert.True(bodyStyles!.Count > 0);
    }

    [Fact]
    public async Task Corpus_anvil_running_elements_inline_styles_apply()
    {
        var html = LoadCorpusFile("Corpus/Invoices/04-anvil-running-elements.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            sheets.Add(CssParserAdapter.Adapt(
                rawSheet, href: null, origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null, isDisabled: false, order: order++));
        }

        var result = CascadeResolver.Resolve(document, sheets.ToImmutable(),
            CssMediaContext.DefaultPrint);

        // The Anvil invoice uses many style="…" attributes throughout its layout; the
        // cascade should pick those up via the inline-style branch.
        var inlineStyledElements = document.QuerySelectorAll("[style]");
        var matchedInline = 0;
        foreach (var el in inlineStyledElements)
        {
            if (result.TryGetStylesFor(el)?.Count > 0) matchedInline++;
        }
        Assert.True(matchedInline > 0,
            "expected at least one inline-styled element to surface in the cascade");
    }

    [Fact]
    public async Task Corpus_tailwind_cdn_emits_no_has_diagnostic_when_no_has_used()
    {
        var html = LoadCorpusFile("Corpus/Invoices/02-tailwind-cdn.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            sheets.Add(CssParserAdapter.Adapt(
                rawSheet, href: null, origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null, isDisabled: false, order: order++));
        }

        var sink = new CapturingSink();
        _ = CascadeResolver.Resolve(document, sheets.ToImmutable(),
            CssMediaContext.DefaultPrint, sink);

        // Tailwind utility CSS doesn't use :has(); ensure we don't spuriously emit the
        // unsupported-rendering diagnostic for sheets that don't actually use it.
        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssHasRenderingNotImplemented001);
    }

    private static string LoadCorpusFile(string relativePath)
    {
        // BaseDirectory is the IL3000-clean accessor; Assembly.Location is empty in
        // single-file deployments and the trim analyzer rejects it as a build error.
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, relativePath);
        return System.IO.File.ReadAllText(path);
    }
}
