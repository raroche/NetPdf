// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Semantic;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.RealDocuments.Phase2;

/// <summary>
/// Task 17 cycle 1 — synthetic-fixture composition tests for cross-stage
/// invariants that are hard to express in per-stage corpus tests:
/// <list type="bullet">
///   <item>var() chains feeding calc() math feeding length resolution</item>
///   <item>::before / ::after content composing with the cascade's
///     resolved value text</item>
///   <item>Modern color function rejection emitting from the production
///     path (Task 16 hardening Rec 2)</item>
///   <item>Multi-arg attr() rejection emitting from the production path
///     (Task 16 hardening Rec 1)</item>
///   <item>Tagged-PDF semantics surviving every transparent / hidden
///     filter (Task 15 hardening Recs 1+3)</item>
/// </list>
/// </summary>
public sealed class Phase2CompositionTests
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

    private static IEnumerable<Box> FindPrincipalBoxes(Box root, string localName) =>
        WalkBoxes(root).Where(b =>
            b.Kind != BoxKind.TextRun
            && b.SourceElement?.LocalName == localName);

    // ============================================================
    // var() → calc() math composition
    // ============================================================

    [Fact]
    public async Task Nested_var_chain_resolves_through_calc_to_typed_length()
    {
        // Three-level var() chain feeding a calc() expression. Tests that
        // cascade order (custom property assignment before consumer rule)
        // + var-substitution + calc-reduction all compose deterministically.
        const string html = """
            <!doctype html>
            <html><head><style>
                :root { --base: 4px; --doubled: calc(var(--base) * 2); }
                .t { padding-left: calc(var(--doubled) * 3 + 2px); }
            </style></head>
            <body><p class="t">x</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindPrincipalBoxes(result.BoxRoot, "p").First();
        // 4 * 2 = 8; 8 * 3 + 2 = 26.
        Assert.Equal(26.0, p.Style.Get(PropertyId.PaddingLeft).AsLengthPx());
    }

    [Fact]
    public async Task Var_with_fallback_when_unset_uses_fallback_then_calc_reduces()
    {
        const string html = """
            <!doctype html>
            <html><head><style>
                .t { padding-top: calc(var(--undefined, 7px) + 3px); }
            </style></head>
            <body><p class="t">x</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindPrincipalBoxes(result.BoxRoot, "p").First();
        Assert.Equal(10.0, p.Style.Get(PropertyId.PaddingTop).AsLengthPx());
    }

    // ============================================================
    // Pseudo-element content composition
    // ============================================================

    [Fact]
    public async Task Pseudo_before_and_after_both_materialize_with_attr_substitution()
    {
        // Both pseudos on one element, both consuming attr(). Tests cascade
        // pseudo-bucket separation + CssContentList parsing × 2 +
        // BoxBuilder pseudo ordering (before-content-after).
        const string html = """
            <!doctype html>
            <html><head><style>
                .label::before { content: '[' attr(data-tag) ']'; }
                .label::after { content: '/' attr(data-version); }
            </style></head>
            <body><p class="label" data-tag="A" data-version="v1">body</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindPrincipalBoxes(result.BoxRoot, "p").First();

        // Order: ::before, body, ::after.
        Assert.Equal(3, p.Children.Count);
        Assert.Equal(BoxPseudo.Before, p.Children[0].Pseudo);
        Assert.Equal("[A]", p.Children[0].Children[0].Text);
        Assert.Equal(BoxPseudo.None, p.Children[1].Pseudo);
        Assert.Equal(BoxPseudo.After, p.Children[2].Pseudo);
        Assert.Equal("/v1", p.Children[2].Children[0].Text);
    }

    [Fact]
    public async Task Pseudo_content_with_unsupported_function_skips_pseudo_via_pipeline()
    {
        // counter() is unsupported in cycle 1 — CssContentList rejects +
        // emits CSS-CONTENT-FUNCTION-UNSUPPORTED-001 + the pseudo box is
        // suppressed. End-to-end through the pipeline.
        const string html = """
            <!doctype html>
            <html><head><style>
                .x::before { content: counter(items); }
            </style></head>
            <body><p class="x">body</p></body></html>
            """;
        var sink = new CapturingSink();
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        var p = FindPrincipalBoxes(result.BoxRoot, "p").First();
        Assert.DoesNotContain(p.Children, c => c.Pseudo == BoxPseudo.Before);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssContentFunctionUnsupported001);
    }

    // ============================================================
    // Diagnostic emission via production path (Task 16 hardening Recs 1+2)
    // ============================================================

    [Theory]
    [InlineData("oklch(0.7 0.15 200)")]
    [InlineData("oklab(0.7 0.1 -0.05)")]
    [InlineData("color-mix(in srgb, red, blue)")]
    [InlineData("light-dark(white, black)")]
    public async Task Modern_color_value_emits_diagnostic_via_pipeline(string colorValue)
    {
        var html = $$"""
            <!doctype html>
            <html><head><style>
                .x { color: {{colorValue}}; }
            </style></head>
            <body><p class="x">body</p></body></html>
            """;
        var sink = new CapturingSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssModernColorFunctionUnsupported001);
    }

    [Fact]
    public async Task Multi_arg_attr_in_pseudo_content_emits_via_pipeline()
    {
        // Modern attr(name type, fallback) — preprocessor recovery (Task 16
        // hardening Rec 1) preserves the raw text past AngleSharp's
        // normalization so CssContentList sees + rejects it.
        const string html = """
            <!doctype html>
            <html><head><style>
                .x::before { content: attr(data-x string, 'fallback'); }
            </style></head>
            <body><p class="x" data-x="real">body</p></body></html>
            """;
        var sink = new CapturingSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        Assert.Contains(sink.Diagnostics,
            d => d.Code == CssDiagnosticCodes.CssAttrMultiArgUnsupported001);
    }

    // ============================================================
    // Tagged-PDF semantics survive every filter (Task 15 hardening Recs 1+3)
    // ============================================================

    [Fact]
    public async Task Aria_hidden_subtree_drops_descendants_from_semantic_tree()
    {
        const string html = """
            <!doctype html>
            <html><body>
              <article>
                <h1>visible heading</h1>
                <section aria-hidden="true">
                  <h2>hidden heading</h2>
                  <p>hidden body</p>
                </section>
                <p>visible body</p>
              </article>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var headings = WalkSemantic(result.SemanticRoot)
            .Where(n => n.Kind is SemanticKind.Heading1 or SemanticKind.Heading2)
            .ToList();
        Assert.Single(headings);
        Assert.Equal(SemanticKind.Heading1, headings[0].Kind);

        var paragraphs = WalkSemantic(result.SemanticRoot)
            .Where(n => n.Kind == SemanticKind.Paragraph).ToList();
        Assert.Single(paragraphs);
        Assert.Equal("visible body", paragraphs[0].AggregateText);
    }

    [Fact]
    public async Task Style_block_text_does_not_leak_into_semantic_tree()
    {
        // <style> is metadata-content; its CSS-rule text must not surface
        // as InlineText in the semantic tree even now that text-node walking
        // is enabled per Task 15 hardening Rec 3.
        const string html = """
            <!doctype html>
            <html><head><style>
                .x { color: red }
                @page { margin: 1in }
            </style></head>
            <body><p>body</p></body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var allText = string.Join("\n", WalkSemantic(result.SemanticRoot)
            .Select(n => n.Text));
        Assert.DoesNotContain("@page", allText);
        Assert.DoesNotContain("color: red", allText);
        Assert.DoesNotContain(".x", allText);
    }

    // ============================================================
    // Table fixup composition (Task 13)
    // ============================================================

    [Fact]
    public async Task Table_wrapper_and_grid_present_in_box_tree_via_pipeline()
    {
        const string html = """
            <!doctype html>
            <html><body>
              <table>
                <thead><tr><th>Header</th></tr></thead>
                <tbody><tr><td>Cell</td></tr></tbody>
              </table>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var wrappers = WalkBoxes(result.BoxRoot).Where(b => b.IsTableWrapper).ToList();
        Assert.Single(wrappers);
        var wrapper = wrappers[0];
        // Wrapper has exactly one TableGrid child holding all internals.
        Assert.Single(wrapper.Children);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[0].Kind);
    }

    [Fact]
    public async Task Table_semantic_tree_skips_thead_tbody_tfoot()
    {
        // SemanticTreeBuilder treats thead/tbody/tfoot as transparent —
        // the Table's children flatten to TableRow.
        const string html = """
            <!doctype html>
            <html><body>
              <table>
                <thead><tr><th>H</th></tr></thead>
                <tbody><tr><td>D</td></tr></tbody>
                <tfoot><tr><td>F</td></tr></tfoot>
              </table>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var table = WalkSemantic(result.SemanticRoot).First(n => n.Kind == SemanticKind.Table);
        Assert.Equal(3, table.Children.Count);
        Assert.All(table.Children, c => Assert.Equal(SemanticKind.TableRow, c.Kind));
    }

    // ============================================================
    // List-item marker composition (Tasks 13 + 14)
    // ============================================================

    [Fact]
    public async Task Ordered_list_with_css_only_list_items_numbers_continuously()
    {
        // Mix of <li> + <div display:list-item> children — Task 14 hardening
        // Rec 3 says position counting goes by computed display:list-item,
        // not just <li>.
        const string html = """
            <!doctype html>
            <html><head><style>
                .as-li { display: list-item; }
            </style></head>
            <body>
              <ol>
                <li>one</li>
                <div class="as-li">two</div>
                <li>three</li>
              </ol>
            </body></html>
            """;
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var markers = WalkBoxes(result.BoxRoot).Where(b => b.Kind == BoxKind.Marker).ToList();
        Assert.Equal(3, markers.Count);
        Assert.StartsWith("1.", markers[0].Children[0].Text);
        Assert.StartsWith("2.", markers[1].Children[0].Text);
        Assert.StartsWith("3.", markers[2].Children[0].Text);
    }

    // ============================================================
    // Multiple stages compose without diagnostic interference
    // ============================================================

    [Fact]
    public async Task Pipeline_emits_dedup_diagnostics_for_replaced_pseudo_across_many_imgs()
    {
        // 5 images + 1 broad selector → 1 diagnostic per Task 16 hardening
        // Rec 4. Dedupe survives the full pipeline.
        const string html = """
            <!doctype html>
            <html><head><style>
                img::before { content: 'X'; }
            </style></head>
            <body>
              <img><img><img><img><img>
            </body></html>
            """;
        var sink = new CapturingSink();
        await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions(), sink);
        var emitted = sink.Diagnostics
            .Where(d => d.Code == CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001)
            .ToList();
        Assert.Single(emitted);
    }
}
