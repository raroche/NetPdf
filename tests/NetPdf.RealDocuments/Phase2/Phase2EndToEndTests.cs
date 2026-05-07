// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.Diagnostics;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Semantic;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.RealDocuments.Phase2;

/// <summary>
/// Task 17 cycle 1 — end-to-end integration tests across every Phase 2 stage
/// (HtmlParsingHost → CssPreprocessor → CssParserAdapter → CascadeResolver →
/// VarResolver → BoxBuilder + SemanticTreeBuilder). The per-stage corpus tests
/// cover each stage in isolation; these tests catch regressions where stages
/// compose incorrectly (the box tree shape, the diagnostic-emission ordering,
/// the resolver-result Materialize path, the cascade-aware semantic-tree
/// hidden-element exclusion).
/// </summary>
public sealed class Phase2EndToEndTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static IEnumerable<Box> WalkBoxes(Box root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in WalkBoxes(c))
                yield return d;
    }

    private static IEnumerable<SemanticNode> WalkSemantic(SemanticNode root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in WalkSemantic(c))
                yield return d;
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, relativePath);
        return System.IO.File.ReadAllText(path);
    }

    // ============================================================
    // Corpus end-to-end smoke tests
    // ============================================================

    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Pipeline_runs_full_phase2_on_corpus_invoice_without_throwing(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var sink = new CapturingSink();
        var result = await Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions(), sink);

        // Both trees produced; both have a root.
        Assert.Equal(BoxKind.Root, result.BoxRoot.Kind);
        Assert.Equal(SemanticKind.Document, result.SemanticRoot.Kind);
        // Box tree is non-trivial (root + html + body + content).
        Assert.True(result.BoxRoot.CountDescendants() > 3,
            $"Expected the corpus to produce a non-trivial box tree; got {result.BoxRoot.CountDescendants()} descendants.");
        // Semantic tree is non-trivial.
        Assert.True(result.SemanticRoot.CountDescendants() > 0,
            $"Expected the corpus to produce a non-trivial semantic tree; got {result.SemanticRoot.CountDescendants()} descendants.");
    }

    [Fact]
    public async Task Pipeline_classic_invoice_emits_bounded_diagnostic_count()
    {
        // Production-path diagnostic budget — the classic-pure-css invoice
        // is hand-crafted to exercise the cascade cleanly. Going through
        // EVERY stage shouldn't flood the sink with hundreds of
        // CSS-PROPERTY-VALUE-INVALID-001 noise. (Task 16 hardening Rec 4
        // also dedupes pseudo-suppressed diagnostics, so the cap is
        // tighter.)
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var sink = new CapturingSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        Assert.True(sink.Diagnostics.Count < 100,
            $"Expected fewer than 100 diagnostics on the classic invoice; got {sink.Diagnostics.Count}.");
    }

    // ============================================================
    // Cross-stage invariants — synthetic fixtures
    // ============================================================

    /// <summary>Find the principal element box for an element — filters out
    /// the TextRun children that inherit <c>SourceElement</c> from their
    /// containing inline / paragraph parent.</summary>
    private static IEnumerable<Box> FindPrincipalBoxes(Box root, string localName) =>
        WalkBoxes(root).Where(b =>
            b.Kind != BoxKind.TextRun
            && b.SourceElement?.LocalName == localName);

    [Fact]
    public async Task Display_none_element_is_absent_from_both_trees()
    {
        const string html = """
            <!doctype html>
            <html><head><style>
                .gone { display: none }
            </style></head>
            <body>
              <p>visible</p>
              <p class="gone">hidden</p>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        // Box tree: only the visible <p> survives display:none.
        var paragraphBoxes = FindPrincipalBoxes(result.BoxRoot, "p").ToList();
        Assert.Single(paragraphBoxes);
        // Semantic tree: same — display:none is the cascade-hidden filter
        // per Task 15 review Rec 1.
        var paragraphNodes = WalkSemantic(result.SemanticRoot)
            .Where(n => n.Kind == SemanticKind.Paragraph).ToList();
        Assert.Single(paragraphNodes);
    }

    [Fact]
    public async Task Aria_hidden_element_is_in_box_tree_but_not_semantic_tree()
    {
        // Box generation doesn't consult aria-hidden — the element still
        // renders. The semantic tree filters it per Task 15 review Rec 1.
        const string html = """
            <!doctype html>
            <html><body>
              <p>visible</p>
              <p aria-hidden="true">decorative</p>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());

        var pBoxes = FindPrincipalBoxes(result.BoxRoot, "p").ToList();
        Assert.Equal(2, pBoxes.Count);

        var pNodes = WalkSemantic(result.SemanticRoot)
            .Where(n => n.Kind == SemanticKind.Paragraph).ToList();
        Assert.Single(pNodes);
        Assert.Equal("visible", pNodes[0].AggregateText);
    }

    [Fact]
    public async Task Var_chain_through_calc_lands_on_box_computed_style()
    {
        // var() → calc() → typed length end-to-end. The cascade resolves
        // the custom property; VarResolver substitutes; CalcResolver
        // reduces the calc(); LengthResolver materializes onto the box's
        // ComputedStyle.
        const string html = """
            <!doctype html>
            <html><head><style>
                :root { --base: 8px; }
                .x { padding-top: calc(var(--base) * 2 + 4px); }
            </style></head>
            <body><p class="x">body</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindPrincipalBoxes(result.BoxRoot, "p").First();
        // 8 * 2 + 4 = 20px. Verify the cascade resolved the chain.
        var paddingTop = p.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop);
        Assert.Equal(20.0, paddingTop.AsLengthPx());
    }

    [Fact]
    public async Task List_item_marker_generation_composes_through_pipeline()
    {
        // BoxBuilder + cascade compose to produce list-item markers with
        // correct numbering for <ol> per Task 14 hardening Rec 3.
        const string html = """
            <!doctype html>
            <html><body>
              <ol>
                <li>first</li>
                <li>second</li>
                <li>third</li>
              </ol>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var markers = WalkBoxes(result.BoxRoot)
            .Where(b => b.Kind == BoxKind.Marker)
            .ToList();
        Assert.Equal(3, markers.Count);
        Assert.StartsWith("1.", markers[0].Children[0].Text);
        Assert.StartsWith("2.", markers[1].Children[0].Text);
        Assert.StartsWith("3.", markers[2].Children[0].Text);
    }

    [Fact]
    public async Task Replaced_element_pseudo_suppression_emits_via_full_pipeline()
    {
        // ::before on <img> end-to-end — Task 14 hardening Rec 1 + Task 16
        // cycle 1 emission compose so the diagnostic fires from the production
        // path (not just direct CssContentList tests).
        const string html = """
            <!doctype html>
            <html><head><style>
                img::before { content: 'PRE'; }
            </style></head>
            <body><img></body></html>
            """;
        var sink = new CapturingSink();
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);

        // No pseudo box on the img.
        var img = FindPrincipalBoxes(result.BoxRoot, "img").First();
        Assert.DoesNotContain(img.Children, c => c.Pseudo == BoxPseudo.Before);

        // Diagnostic emitted.
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001);
    }

    [Fact]
    public async Task Pseudo_before_with_attr_substitutes_through_pipeline()
    {
        // attr() substitution flows from cascade → CssContentList → TextRun.
        const string html = """
            <!doctype html>
            <html><head><style>
                .label::before { content: '[' attr(data-tag) '] '; }
            </style></head>
            <body><p class="label" data-tag="WIDGET">Body</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindPrincipalBoxes(result.BoxRoot, "p").First();
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("[WIDGET] ", pseudo.Children[0].Text);
    }

    [Fact]
    public async Task Hidden_attribute_excludes_from_semantic_tree_via_pipeline()
    {
        // The HTML5 `hidden` attribute (vs cascade display:none) — semantic
        // tree filters it per Task 15 review Rec 1.
        const string html = """
            <!doctype html>
            <html><body>
              <h1>visible</h1>
              <h1 hidden>invisible</h1>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var h1Nodes = WalkSemantic(result.SemanticRoot)
            .Where(n => n.Kind == SemanticKind.Heading1).ToList();
        Assert.Single(h1Nodes);
        Assert.Equal("visible", h1Nodes[0].AggregateText);
    }

    // ============================================================
    // Determinism — same input produces identical structure
    // ============================================================

    [Fact]
    public async Task Pipeline_run_twice_produces_identical_box_tree_shape()
    {
        // Phase 1 already pins byte-determinism for PDF output. Here we pin
        // box-tree determinism: same HTML → same box-kind sequence (depth-first).
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var first = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var second = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var firstShape = string.Join(",", WalkBoxes(first.BoxRoot).Select(b => b.Kind.ToString()));
        var secondShape = string.Join(",", WalkBoxes(second.BoxRoot).Select(b => b.Kind.ToString()));
        Assert.Equal(firstShape, secondShape);
    }

    [Fact]
    public async Task Pipeline_run_twice_produces_identical_semantic_tree_shape()
    {
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var first = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var second = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var firstShape = string.Join(",", WalkSemantic(first.SemanticRoot).Select(n => n.Kind.ToString()));
        var secondShape = string.Join(",", WalkSemantic(second.SemanticRoot).Select(n => n.Kind.ToString()));
        Assert.Equal(firstShape, secondShape);
    }

    [Fact]
    public async Task Pipeline_run_twice_produces_identical_diagnostic_count()
    {
        // Diagnostic dedup (Task 16 hardening Rec 4) makes counts stable
        // run-to-run. This pins that contract for consumers building
        // diagnostic-budget assertions in their CI.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var sink1 = new CapturingSink();
        var sink2 = new CapturingSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink1);
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink2);
        Assert.Equal(sink1.Diagnostics.Count, sink2.Diagnostics.Count);
    }
}
