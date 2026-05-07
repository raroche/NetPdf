// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.Diagnostics;
using NetPdf.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.RealDocuments.Phase2;

/// <summary>
/// Task 17 review hardening — covers the 6 recommendations addressed in the
/// post-cycle-1 hardening pass:
/// <list type="number">
///   <item>Diagnostics sink threaded through CascadeResolver + VarResolver
///     (cycle 1 only forwarded to BoxBuilder).</item>
///   <item>CssMediaContext built from HtmlPdfOptions (cycle 1 hardcoded
///     DefaultPrint).</item>
///   <item>Stylesheet <c>media</c> + <c>disabled</c> metadata preserved
///     (cycle 1 dropped both, applying every sheet during print).</item>
///   <item>PublicDiagnosticsSinkAdapter unifies HtmlPdfOptions.Diagnostics
///     with the internal CSS sink.</item>
///   <item>CancellationToken honored at every stage boundary.</item>
///   <item>OwnerNode-based stylesheet pairing replaces ordinal-index
///     pairing.</item>
/// </list>
/// </summary>
public sealed class Phase2HardeningTests
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

    // ============================================================
    // Rec 1 — sink threads through cascade + var stages
    // ============================================================

    [Fact]
    public async Task Cascade_resolver_emits_to_supplied_sink_via_pipeline()
    {
        // CSS-CONTAINER-QUERY-UNSUPPORTED-001 is emitted by CascadeResolver
        // (not BoxBuilder). Cycle 1 lost this diagnostic because the sink
        // wasn't threaded past BoxBuilder.
        const string html = """
            <!doctype html>
            <html><head><style>
                @container (min-width: 100px) { p { color: red } }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var sink = new CapturingCssSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssContainerQueryUnsupported001);
    }

    [Fact]
    public async Task Var_resolver_circular_emits_to_supplied_sink_via_pipeline()
    {
        // CSS-VAR-CIRCULAR-001 is emitted by VarResolver — also lost in
        // cycle 1's Phase2Pipeline.
        const string html = """
            <!doctype html>
            <html><head><style>
                :root { --a: var(--b); --b: var(--a); }
                p { color: var(--a, red); }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var sink = new CapturingCssSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssVarCircular001);
    }

    [Fact]
    public async Task Has_selector_emits_to_supplied_sink_via_pipeline()
    {
        // :has() is emitted by CascadeResolver (the selector path).
        const string html = """
            <!doctype html>
            <html><head><style>
                section:has(p) { color: red }
            </style></head>
            <body><section><p>x</p></section></body></html>
            """;
        var sink = new CapturingCssSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssHasRenderingNotImplemented001);
    }

    // ============================================================
    // Rec 2 — CssMediaContext built from HtmlPdfOptions
    // ============================================================

    [Fact]
    public async Task Print_media_query_applies_when_MediaType_is_Print()
    {
        const string html = """
            <!doctype html>
            <html><head><style>
                p { padding-top: 0; }
                @media print { p { padding-top: 10px } }
                @media screen { p { padding-top: 99px } }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var options = new HtmlPdfOptions { MediaType = CssMediaType.Print };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        Assert.Equal(10.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
    }

    [Fact]
    public async Task Screen_media_query_applies_when_MediaType_is_Screen()
    {
        const string html = """
            <!doctype html>
            <html><head><style>
                p { padding-top: 0; }
                @media print { p { padding-top: 10px } }
                @media screen { p { padding-top: 99px } }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var options = new HtmlPdfOptions { MediaType = CssMediaType.Screen };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        Assert.Equal(99.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
    }

    [Fact]
    public async Task Min_width_media_query_evaluates_against_PageSize_viewport()
    {
        // A4 viewport is 794×1123. min-width:1000px should NOT match;
        // min-width:500px SHOULD match.
        const string html = """
            <!doctype html>
            <html><head><style>
                p { padding-top: 0; }
                @media (min-width: 1000px) { p { padding-top: 99px } }
                @media (min-width: 500px) { p { padding-top: 7px } }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var options = new HtmlPdfOptions { PageSize = PageSize.A4 };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        Assert.Equal(7.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
    }

    [Fact]
    public async Task Tabloid_PageSize_makes_min_width_1000px_query_match()
    {
        // Tabloid is 1056×1632, so min-width:1000px DOES match.
        const string html = """
            <!doctype html>
            <html><head><style>
                p { padding-top: 0; }
                @media (min-width: 1000px) { p { padding-top: 99px } }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var options = new HtmlPdfOptions { PageSize = PageSize.Tabloid };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        Assert.Equal(99.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
    }

    // ============================================================
    // Rec 3 — stylesheet media + disabled metadata preserved
    // ============================================================

    [Fact]
    public async Task Style_element_with_media_screen_does_not_apply_during_print()
    {
        // Cycle 1's bug: ExtractStylesheets passed mediaQuery:null for
        // every <style>, so a `<style media="screen">` rule applied during
        // print. The hardening reads the owner element's `media`
        // attribute.
        const string html = """
            <!doctype html>
            <html><head>
                <style>p { padding-top: 0 }</style>
                <style media="screen">p { padding-top: 99px }</style>
            </head>
            <body><p>x</p></body></html>
            """;
        var options = new HtmlPdfOptions { MediaType = CssMediaType.Print };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        // The `media="screen"` sheet is skipped → padding stays at 0.
        Assert.Equal(0.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
    }

    [Fact]
    public async Task Style_element_with_media_print_applies_during_print()
    {
        const string html = """
            <!doctype html>
            <html><head>
                <style>p { padding-top: 0 }</style>
                <style media="print">p { padding-top: 12px }</style>
            </head>
            <body><p>x</p></body></html>
            """;
        var options = new HtmlPdfOptions { MediaType = CssMediaType.Print };
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, options);
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        Assert.Equal(12.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
    }

    // ============================================================
    // Rec 4 — Public diagnostics sink adapter
    // ============================================================

    [Fact]
    public async Task Public_sink_via_HtmlPdfOptions_receives_css_diagnostics_through_adapter()
    {
        const string html = """
            <!doctype html>
            <html><head><style>
                @container (min-width: 100px) { p { color: red } }
            </style></head>
            <body><p>x</p></body></html>
            """;
        var publicSink = new CapturingPublicSink();
        var options = new HtmlPdfOptions { Diagnostics = publicSink };
        // Pass null for the explicit ICssDiagnosticsSink — adapter wires
        // the public sink automatically per Rec 4.
        await Phase2Pipeline.RunFromHtmlAsync(html, options);

        Assert.Contains(publicSink.Diagnostics,
            d => d.Code == DiagnosticCodes.CssContainerQueryUnsupported001);
    }

    [Fact]
    public void Adapter_converts_severity_and_location_correctly()
    {
        var publicSink = new CapturingPublicSink();
        var adapter = new PublicDiagnosticsSinkAdapter(publicSink);
        var location = new NetPdf.Css.Parser.CssSourceLocation("test.css", 42, 7);
        adapter.Emit(new CssDiagnostic(
            "TEST-001",
            "test message",
            CssDiagnosticSeverity.Error,
            location));
        Assert.Single(publicSink.Diagnostics);
        var emitted = publicSink.Diagnostics[0];
        Assert.Equal("TEST-001", emitted.Code);
        Assert.Equal("test message", emitted.Message);
        Assert.Equal(DiagnosticSeverity.Error, emitted.Severity);
        Assert.Equal("test.css", emitted.Location.File);
        Assert.Equal(42, emitted.Location.Line);
        Assert.Equal(7, emitted.Location.Column);
    }

    [Fact]
    public void Adapter_for_options_returns_null_when_no_public_sink()
    {
        var options = new HtmlPdfOptions(); // no Diagnostics
        Assert.Null(PublicDiagnosticsSinkAdapter.ForOptions(options));
    }

    // ============================================================
    // Rec 5 — Cancellation honored at stage boundaries
    // ============================================================

    [Fact]
    public async Task Pre_cancelled_token_throws_OperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Phase2Pipeline.RunFromHtmlAsync(
                "<p>x</p>", new HtmlPdfOptions(),
                diagnostics: null, cancellationToken: cts.Token));
    }

    // ============================================================
    // Rec 6 — Owner-node pairing is robust to disabled / external sheets
    //
    // We can't easily inject a disabled sheet without manipulating
    // AngleSharp internals, but we verify that ordering is preserved when
    // multiple <style> elements appear, and that the media metadata flows
    // even with multiple sheets.
    // ============================================================

    [Fact]
    public async Task Multiple_style_elements_each_get_their_own_media_metadata()
    {
        // Three sheets: one print-only, one screen-only, one no-media.
        // During print, only the print + no-media sheets contribute. Each
        // sheet's media metadata must be paired with the right sheet via
        // OwnerNode (Rec 6) — ordinal pairing would have worked here too,
        // but the test pins the contract for the OwnerNode path.
        const string html = """
            <!doctype html>
            <html><head>
                <style media="print">p { padding-top: 5px }</style>
                <style media="screen">p { padding-top: 99px }</style>
                <style>p { padding-left: 3px }</style>
            </head>
            <body><p>x</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { MediaType = CssMediaType.Print });
        var p = WalkBoxes(result.BoxRoot)
            .First(b => b.Kind == BoxKind.BlockContainer
                && b.SourceElement?.LocalName == "p");
        Assert.Equal(5.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
        Assert.Equal(3.0, p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingLeft).AsLengthPx());
    }

    private static IEnumerable<Box> WalkBoxes(Box root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in WalkBoxes(c))
                yield return d;
    }
}
