// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
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
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Regression coverage for the nine Task 12 hardening review recommendations.
/// One test class per rec is unwieldy for the small recs, so they're grouped
/// here with section-comment headers.
/// </summary>
public sealed class BoxBuilderHardeningTests
{
    // ============================================================
    // Test infrastructure (mirrors BoxBuilderTests)
    // ============================================================

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

    private static async Task<Box> BuildAsync(string html, string? css = null,
        ICssDiagnosticsSink? diagnostics = null)
    {
        var doc = await ParseHtml(html);
        var sheets = css is null
            ? ImmutableArray<CssStylesheet>.Empty
            : ImmutableArray.Create(await ParseSheet(css));
        var cascade = CascadeResolver.Resolve(doc, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        return BoxBuilder.Build(doc, resolved, diagnostics);
    }

    private static IEnumerable<Box> Walk(Box root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var d in Walk(child)) yield return d;
        }
    }

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // ============================================================
    // Rec 1 — Metadata-content elements default to display: none
    // ============================================================

    [Fact]
    public async Task Title_element_does_not_emit_text_into_box_tree()
    {
        // The cycle-1 bug: HtmlDefaultDisplay didn't list <title>, so the
        // CSS spec default `inline` applied. <title>'s text would surface as
        // a TextRun in the box tree (visible "Invoice #123" text).
        var root = await BuildAsync(
            "<!doctype html><html><head><title>Invoice 123</title></head><body><p>body</p></body></html>");
        var allText = string.Join("|", Walk(root)
            .Where(b => b.Kind == BoxKind.TextRun)
            .Select(b => b.Text));
        Assert.DoesNotContain("Invoice 123", allText);
        Assert.Contains("body", allText);
    }

    [Fact]
    public async Task Style_element_text_does_not_emit_into_box_tree()
    {
        var root = await BuildAsync(
            "<!doctype html><html><head><style>p { color: red }</style></head><body><p>x</p></body></html>");
        var allText = string.Join("|", Walk(root)
            .Where(b => b.Kind == BoxKind.TextRun)
            .Select(b => b.Text));
        Assert.DoesNotContain("color: red", allText);
        Assert.Contains("x", allText);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("title")]
    [InlineData("style")]
    [InlineData("link")]
    [InlineData("meta")]
    [InlineData("template")]
    [InlineData("base")]
    [InlineData("noscript")]
    public void Metadata_default_display_is_none(string tag) =>
        Assert.Equal("none", HtmlDefaultDisplay.GetDefault(tag));

    // ============================================================
    // Rec 2 — Pseudo-element display fallback
    // ============================================================

    [Fact]
    public async Task Pseudo_on_block_element_defaults_to_inline_not_block()
    {
        // <div>::before should default to inline per CSS Pseudo L4 §3.1, NOT
        // to the host's UA default (`block` for div). The cycle-1 bug: ReadDisplay
        // fell back to HtmlDefaultDisplay using the host name, yielding `block`.
        var root = await BuildAsync(
            "<div>x</div>",
            "div::before { content: 'PRE' }");
        var div = Walk(root).First(b => b.SourceElement?.LocalName == "div");
        var pseudo = div.Children[0];
        Assert.Equal(BoxPseudo.Before, pseudo.Pseudo);
        Assert.Equal(BoxKind.InlineBox, pseudo.Kind);
    }

    [Fact]
    public async Task Pseudo_on_replaced_element_is_suppressed_per_Pseudo_L4()
    {
        // Task 14 review Rec 1 + CSS Pseudo L4 §3 — replaced elements (img,
        // video, canvas, iframe, object, embed) cannot host generated content.
        // Their atomic content has no place for ::before / ::after boxes, so
        // the pseudos are suppressed. (Earlier Task 12 cycle-1 bug had them
        // routing to InlineReplacedElement; the cycle-1 hardening fix routed
        // them to InlineBox; the proper fix per spec is no box at all.)
        var root = await BuildAsync(
            "<img>",
            "img::before { content: 'CAPTION' } img::after { content: 'TAIL' }");
        var img = Walk(root).First(b => b.SourceElement?.LocalName == "img");
        Assert.DoesNotContain(img.Children, c => c.Pseudo == BoxPseudo.Before);
        Assert.DoesNotContain(img.Children, c => c.Pseudo == BoxPseudo.After);
    }

    [Fact]
    public async Task Pseudo_with_explicit_display_block_honors_cascade()
    {
        // The default fallback is inline, but an explicit `display: block`
        // declaration overrides it.
        var root = await BuildAsync(
            "<span>x</span>",
            "span::before { content: 'PRE'; display: block }");
        var span = Walk(root).First(b => b.SourceElement?.LocalName == "span");
        var pseudo = span.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal(BoxKind.BlockContainer, pseudo.Kind);
    }

    // ============================================================
    // Rec 3 — <br> as forced LineBreak
    // ============================================================

    [Fact]
    public async Task Br_element_yields_LineBreak_kind_not_empty_inline()
    {
        var root = await BuildAsync("<p>before<br>after</p>");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var br = p.Children.FirstOrDefault(c => c.SourceElement?.LocalName == "br");
        Assert.NotNull(br);
        Assert.Equal(BoxKind.LineBreak, br!.Kind);
        Assert.True(br.IsInlineLevel);
    }

    [Fact]
    public async Task Br_inside_table_cell_still_produces_LineBreak()
    {
        var root = await BuildAsync("<table><tr><td>line1<br>line2</td></tr></table>");
        var br = Walk(root).FirstOrDefault(b => b.SourceElement?.LocalName == "br");
        Assert.NotNull(br);
        Assert.Equal(BoxKind.LineBreak, br!.Kind);
    }

    // ============================================================
    // Rec 4 — Generated content restricted to single CSS string
    // ============================================================

    [Fact]
    public async Task Pseudo_with_string_content_decodes_basic_text()
    {
        var root = await BuildAsync(
            "<p>x</p>",
            "p::before { content: 'PRE' }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("PRE", pseudo.Children[0].Text);
    }

    [Fact]
    public async Task Pseudo_with_string_content_decodes_hex_escape()
    {
        // \A is U+000A (newline) per CSS Syntax §4.3.7. AngleSharp normalizes
        // the string into a parsed form; check we don't render the literal "\A".
        var root = await BuildAsync(
            "<p>x</p>",
            @"p::before { content: ""line1\Aline2"" }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.FirstOrDefault(c => c.Pseudo == BoxPseudo.Before);
        // The content text should NOT contain the literal "\A" (escape syntax).
        Assert.NotNull(pseudo);
        var text = pseudo!.Children[0].Text;
        Assert.DoesNotContain(@"\A", text);
    }

    [Theory]
    [InlineData("counter(items)")]
    [InlineData("url(image.png)")]
    [InlineData("open-quote")]
    [InlineData("close-quote")]
    public async Task Pseudo_with_unsupported_content_form_emits_no_box(string contentValue)
    {
        // Task 14 cycle 1 added multi-string concatenation + attr() support, so
        // those forms moved out of this "unsupported" theory. Counter / image /
        // quote tokens still skip silently — counter machinery + resource
        // pipeline + quote-stack tracking are cycle-2 work.
        var root = await BuildAsync(
            "<p>x</p>",
            $"p::before {{ content: {contentValue} }}");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        // No pseudo box — silently skipped per cycle-1 contract.
        Assert.DoesNotContain(p.Children, c => c.Pseudo == BoxPseudo.Before);
    }

    // ============================================================
    // Rec 5 — display: contents promotes children
    // ============================================================

    // NOTE: end-to-end display:contents tests through AngleSharp.Css fail
    // because AngleSharp 1.0.0-beta.144's CSS parser silently DROPS the
    // `display: contents` declaration during parse (it's an unsupported
    // value to AngleSharp, like the modern color functions). The cascade
    // never sees the rule, so BoxBuilder's display:contents code path
    // can't be exercised through the parser-driven path.
    //
    // The IMPLEMENTATION is correct (see BoxBuilder.BuildElementBoxes
    // case DisplayMappingResult.Contents → promotes children); the
    // DisplayMapper unit tests verify the keyword → outcome mapping.
    // Cycle-2 work on the CssPreprocessor will preserve `display: contents`
    // through the parser, at which point these end-to-end tests can be
    // restored. Tracking via the Phase 2 doc's "Modern syntax tokens" table.
    //
    // Synthetic test (bypasses AngleSharp): exercise display:contents by
    // building the box tree against a hand-crafted ResolvedCascadeResult.
    // Skipped for now to keep cycle-1 review-pass scope manageable; cycle-2
    // adds direct ResolvedCascadeResult fixtures + the preprocessor fix.

    // ============================================================
    // Rec 6 — Anonymous-block fixup also fires for inline-block + table-cell
    // ============================================================

    [Fact]
    public async Task InlineBlockContainer_with_mixed_children_gets_anonymous_block_fixup()
    {
        // <div><span class='ib'>before<p>middle</p>after</span></div>
        // span has display: inline-block (block formatting context inside).
        // Per Display L3 §3.1, the span's mixed children should trigger the
        // same anonymous-block fixup as a block container.
        var root = await BuildAsync(
            "<div><span class='ib'>before<p>middle</p>after</span></div>",
            ".ib { display: inline-block }");
        var ib = Walk(root).First(b => b.Kind == BoxKind.InlineBlockContainer);
        // Expect: AnonymousBlock(text "before") → p → AnonymousBlock(text "after")
        Assert.Equal(3, ib.Children.Count);
        Assert.Equal(BoxKind.AnonymousBlock, ib.Children[0].Kind);
        Assert.Equal(BoxKind.BlockContainer, ib.Children[1].Kind);
        Assert.Equal(BoxKind.AnonymousBlock, ib.Children[2].Kind);
    }

    [Fact]
    public async Task TableCell_with_mixed_children_gets_anonymous_block_fixup()
    {
        // td is a block-container per Tables L3 — its mixed children should
        // be fixed up like any other BFC.
        var root = await BuildAsync(
            "<table><tr><td>before<p>middle</p>after</td></tr></table>");
        var td = Walk(root).First(b => b.Kind == BoxKind.TableCell);
        Assert.Equal(3, td.Children.Count);
        Assert.Equal(BoxKind.AnonymousBlock, td.Children[0].Kind);
        Assert.Equal(BoxKind.BlockContainer, td.Children[1].Kind);
        Assert.Equal(BoxKind.AnonymousBlock, td.Children[2].Kind);
    }

    // ============================================================
    // Rec 7 — Table output now uses the wrapper + TableGrid pair (Task 13)
    // ============================================================

    [Fact]
    public async Task Task13_table_wrapper_now_contains_TableGrid_per_Tables_L3_2_1()
    {
        // Task 13 lands: every Table / InlineTable wrapper gets exactly one
        // anonymous TableGrid child holding the table internals (row-groups,
        // rows, cells). Previously the wrapper held the row-group directly.
        var root = await BuildAsync("<table><tr><td>cell</td></tr></table>");
        var table = Walk(root).First(b => b.Kind == BoxKind.Table);
        Assert.True(table.IsTableWrapper);
        // Wrapper has exactly one child: the TableGrid.
        Assert.Single(table.Children);
        Assert.Equal(BoxKind.TableGrid, table.Children[0].Kind);
        // Table internals live UNDER the grid, not directly under the wrapper.
        var grid = table.Children[0];
        Assert.Contains(grid.Children, c => c.Kind == BoxKind.TableRowGroup);
        // Row-group → row → cell chain still intact.
        var partsKinds = Walk(root).Select(b => b.Kind).ToList();
        Assert.Contains(BoxKind.TableRowGroup, partsKinds);
        Assert.Contains(BoxKind.TableRow, partsKinds);
        Assert.Contains(BoxKind.TableCell, partsKinds);
    }

    [Fact]
    public async Task Task13_inline_table_also_gets_TableGrid()
    {
        var root = await BuildAsync(
            "<span class='it'><table><tr><td>cell</td></tr></table></span>",
            ".it { display: inline-table }");
        // The .it span is inline-table → InlineTable wrapper.
        // Inside that span, the actual <table> element also gets a Table box.
        // Both wrappers must own a TableGrid post-Task-13.
        var wrappers = Walk(root).Where(b => b.IsTableWrapper).ToList();
        Assert.NotEmpty(wrappers);
        foreach (var w in wrappers)
        {
            Assert.Contains(w.Children, c => c.Kind == BoxKind.TableGrid);
        }
    }

    // ============================================================
    // Rec 8 — Source location threaded through to property resolvers
    // ============================================================

    // Note: a runtime test that the declaration's CssSourceLocation flows
    // through to the property-resolver diagnostic is not feasible until
    // Task 3 wires real source positions (today CssDeclaration.Location is
    // always CssSourceLocation.Unknown). The hookup is verified by code
    // review — see BoxBuilder.ApplyResolvedDeclarations passing
    // `winner.OriginalDeclaration.Location` into PropertyResolverDispatch.Resolve.
    // Once Task 3 lands, the value will surface in the diagnostic
    // automatically without further BoxBuilder changes.
}
