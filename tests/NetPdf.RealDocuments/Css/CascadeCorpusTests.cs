// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.RealDocuments.Css;

/// <summary>
/// Integration tests for Phase 2 Task 7: every invoice in the corpus is parsed end-to-end
/// (HTML → DOM → CSSOM → preprocessor + adapter → cascade) without throwing, and every
/// element ends up with a (possibly empty) per-element matched-rule-set lookup.
/// </summary>
/// <remarks>
/// <b>Preprocessor path.</b> These tests run the full end-to-end pipeline including
/// <see cref="CssPreprocessor.Process"/> + the preprocess-aware
/// <see cref="CssParserAdapter.Adapt(ICssStyleSheet, CssPreprocessResult, string?, CssStylesheetOrigin, CssStylesheetOwnerKind, string?, bool, int)"/>
/// overload. This exercises modern-syntax recovery (<c>oklch</c>, <c>color-mix</c>,
/// <c>@layer</c>, <c>@container</c>, <c>@import</c> with <c>layer()</c>/<c>supports()</c>),
/// rather than the bare-Adapt path that would hide whether the preprocessor is wired in
/// correctly.
/// </remarks>
public sealed class CascadeCorpusTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public System.Collections.Generic.List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    /// <summary>Adapt every <c>&lt;style&gt;</c> block + linked sheet in the document
    /// through the preprocessor + adapter pipeline. Mirrors the eventual production
    /// wireup that <c>HtmlPdf.ConvertAsync</c> will use.</summary>
    private static ImmutableArray<CssStylesheet> AdaptAllSheetsViaPreprocessor(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;

        // Walk the DOM for <style> elements so we can pair the raw text with each
        // ICssStyleSheet — the preprocessor needs the original text, AngleSharp's
        // CSSOM only carries the parsed result.
        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            string rawText;
            if (styleIdx < styleElements.Length)
            {
                rawText = styleElements[styleIdx].TextContent ?? string.Empty;
                styleIdx++;
            }
            else
            {
                rawText = string.Empty; // External / synthetic sheet: skip preprocess.
            }
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null,
                isDisabled: false,
                order: order++));
        }
        return output.ToImmutable();
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

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(document, sheets,
            CssMediaContext.DefaultPrint, sink);

        Assert.NotNull(result);
        Assert.True(result.ElementCount > 0,
            $"corpus '{relativePath}' should produce at least one styled element");
    }

    [Fact]
    public async Task Corpus_classic_pure_css_picks_up_typography_styles_on_body()
    {
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var result = CascadeResolver.Resolve(document, sheets,
            CssMediaContext.DefaultPrint);

        var body = document.Body!;
        var bodyStyles = result.TryGetStylesFor(body);
        Assert.NotNull(bodyStyles);
        Assert.True(bodyStyles!.Count > 0);
    }

    [Fact]
    public async Task Corpus_anvil_running_elements_inline_styles_apply()
    {
        var html = LoadCorpusFile("Corpus/Invoices/04-anvil-running-elements.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var result = CascadeResolver.Resolve(document, sheets,
            CssMediaContext.DefaultPrint);

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

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var sink = new CapturingSink();
        _ = CascadeResolver.Resolve(document, sheets,
            CssMediaContext.DefaultPrint, sink);

        Assert.DoesNotContain(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssHasRenderingNotImplemented001);
    }

    [Fact]
    public async Task Corpus_synthetic_atContainer_emits_diagnostic_through_preprocessor_path()
    {
        // Synthetic HTML with @container in a <style> block — the preprocessor recovers
        // the at-rule (AngleSharp.Css drops it), the cascade emits the unsupported-rendering
        // diagnostic. Demonstrates the preprocessor → cascade end-to-end recovery path.
        var html = """
            <html><head><style>
              .card { color: red; }
              @container (min-width: 400px) {
                .card { color: blue; }
              }
            </style></head><body><div class="card">x</div></body></html>
            """;
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(document, sheets,
            CssMediaContext.DefaultPrint, sink);

        // The .card { color: red } rule outside @container should apply.
        var card = document.QuerySelector(".card")!;
        Assert.NotNull(result.TryGetStylesFor(card)?.GetWinner("color"));
        // The @container rule should emit the unsupported diagnostic.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssContainerQueryUnsupported001);
    }

    [Fact]
    public async Task Corpus_synthetic_atLayer_block_form_through_preprocessor_path()
    {
        // Synthetic HTML with @layer block-form. The preprocessor preserves the body as
        // RawBody (AngleSharp.Css doesn't decompose @layer); the cascade emits
        // CSS-AT-RULE-UNKNOWN-001 since the body wasn't reparsed. v1 limitation
        // documented in the compatibility matrix.
        var html = """
            <html><head><style>
              p { color: red; }
              @layer typography {
                p { color: blue; }
              }
            </style></head><body><p>x</p></body></html>
            """;
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var sink = new CapturingSink();
        var result = CascadeResolver.Resolve(document, sheets,
            CssMediaContext.DefaultPrint, sink);

        // Outer p { color: red } applies. The @layer body's inner rule does not (v1 gap).
        var p = document.QuerySelector("p")!;
        Assert.NotNull(result.TryGetStylesFor(p)?.GetWinner("color"));
        // Diagnostic emitted for the opaque @layer body.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAtRuleUnknown001);
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, relativePath);
        return System.IO.File.ReadAllText(path);
    }
}
