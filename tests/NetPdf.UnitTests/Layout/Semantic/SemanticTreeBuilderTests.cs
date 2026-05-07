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
using NetPdf.Css.Parser;
using NetPdf.Layout.Semantic;
using Xunit;

namespace NetPdf.UnitTests.Layout.Semantic;

/// <summary>
/// Task 15 cycle 1 + hardening — SemanticTreeBuilder walks an HTML document
/// and emits a parallel tree of accessibility / structural roles for v1.1's
/// PDF/UA tagged-PDF emission.
/// </summary>
public sealed class SemanticTreeBuilderTests
{
    // ============================================================
    // Test infrastructure
    // ============================================================

    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static async Task<SemanticNode> BuildAsync(string html)
    {
        var doc = await ParseHtml(html);
        return SemanticTreeBuilder.Build(doc);
    }

    private static async Task<SemanticNode> BuildWithCascadeAsync(string html, string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var doc = await ctx.OpenAsync(req => req.Content(html));
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        var adapted = CssParserAdapter.Adapt(sheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(adapted),
            CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        return SemanticTreeBuilder.Build(doc, resolved);
    }

    private static IEnumerable<SemanticNode> Walk(SemanticNode root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in Walk(c))
                yield return d;
    }

    // ============================================================
    // Headings — H1..H6 each get the right Kind
    // ============================================================

    [Theory]
    [InlineData("h1", "Heading1")]
    [InlineData("h2", "Heading2")]
    [InlineData("h3", "Heading3")]
    [InlineData("h4", "Heading4")]
    [InlineData("h5", "Heading5")]
    [InlineData("h6", "Heading6")]
    public async Task Each_heading_level_gets_its_own_kind(string tag, string expectedKind)
    {
        var root = await BuildAsync($"<{tag}>Title</{tag}>");
        var expected = System.Enum.Parse<SemanticKind>(expectedKind);
        var node = Walk(root).First(n => n.Kind == expected);
        Assert.Equal("Title", node.AggregateText);
    }

    // ============================================================
    // Paragraph + leaf text capture
    // ============================================================

    [Fact]
    public async Task Paragraph_captures_normalized_text_via_inline_span()
    {
        var root = await BuildAsync("<p>Hello   world\n  again</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // Per Rec 7 — text lives in InlineText children, not p.Text.
        Assert.Equal("", p.Text);
        Assert.Single(p.Children);
        Assert.Equal(SemanticKind.InlineText, p.Children[0].Kind);
        Assert.Equal("Hello world again", p.Children[0].Text);
        // AggregateText is the convenience derived view.
        Assert.Equal("Hello world again", p.AggregateText);
    }

    [Fact]
    public async Task Container_kind_does_not_carry_text_content()
    {
        var root = await BuildAsync("<section><p>Body</p></section>");
        var section = Walk(root).First(n => n.Kind == SemanticKind.Section);
        Assert.Equal("", section.Text);
        // Body is in p's InlineText child.
        Assert.Equal("Body", section.AggregateText);
    }

    // ============================================================
    // Lists
    // ============================================================

    [Fact]
    public async Task Unordered_list_emits_List_with_ListItem_children()
    {
        var root = await BuildAsync("<ul><li>Apple</li><li>Banana</li></ul>");
        var list = Walk(root).First(n => n.Kind == SemanticKind.List);
        Assert.Equal(2, list.Children.Count);
        Assert.All(list.Children, c => Assert.Equal(SemanticKind.ListItem, c.Kind));
        Assert.Equal("Apple", list.Children[0].AggregateText);
        Assert.Equal("Banana", list.Children[1].AggregateText);
    }

    [Fact]
    public async Task Ordered_list_emits_same_List_kind_as_ul()
    {
        var root = await BuildAsync("<ol><li>One</li></ol>");
        var list = Walk(root).First(n => n.Kind == SemanticKind.List);
        Assert.Single(list.Children);
    }

    // ============================================================
    // Tables — thead / tbody / tfoot are transparent
    // ============================================================

    [Fact]
    public async Task Table_skips_tbody_thead_tfoot_and_attaches_rows_directly()
    {
        var root = await BuildAsync("""
            <table>
              <thead><tr><th>H</th></tr></thead>
              <tbody><tr><td>D</td></tr></tbody>
              <tfoot><tr><td>F</td></tr></tfoot>
            </table>
            """);
        var table = Walk(root).First(n => n.Kind == SemanticKind.Table);
        Assert.Equal(3, table.Children.Count);
        Assert.All(table.Children, c => Assert.Equal(SemanticKind.TableRow, c.Kind));
    }

    [Fact]
    public async Task Table_caption_becomes_TableCaption_kind()
    {
        var root = await BuildAsync("<table><caption>My Table</caption><tr><td>x</td></tr></table>");
        var caption = Walk(root).First(n => n.Kind == SemanticKind.TableCaption);
        Assert.Equal("My Table", caption.AggregateText);
    }

    [Fact]
    public async Task Th_and_td_get_distinct_kinds()
    {
        var root = await BuildAsync("<table><tr><th>Name</th><td>Value</td></tr></table>");
        var th = Walk(root).First(n => n.Kind == SemanticKind.TableHeaderCell);
        var td = Walk(root).First(n => n.Kind == SemanticKind.TableCell);
        Assert.Equal("Name", th.AggregateText);
        Assert.Equal("Value", td.AggregateText);
    }

    // ============================================================
    // Rec 5 — Table-cell metadata
    // ============================================================

    [Fact]
    public async Task TableCell_default_metadata_is_rowspan_1_colspan_1()
    {
        var root = await BuildAsync("<table><tr><td>x</td></tr></table>");
        var td = Walk(root).First(n => n.Kind == SemanticKind.TableCell);
        Assert.NotNull(td.Cell);
        Assert.Equal(1, td.Cell!.Value.RowSpan);
        Assert.Equal(1, td.Cell.Value.ColSpan);
        Assert.Null(td.Cell.Value.Scope);
        Assert.Null(td.Cell.Value.Headers);
        Assert.Null(td.Cell.Value.Abbr);
    }

    [Fact]
    public async Task TableCell_extracts_rowspan_and_colspan()
    {
        var root = await BuildAsync(
            "<table><tr><td rowspan='2' colspan='3'>x</td></tr></table>");
        var td = Walk(root).First(n => n.Kind == SemanticKind.TableCell);
        Assert.Equal(2, td.Cell!.Value.RowSpan);
        Assert.Equal(3, td.Cell.Value.ColSpan);
    }

    [Fact]
    public async Task TableHeaderCell_extracts_scope_and_abbr()
    {
        var root = await BuildAsync(
            "<table><tr><th scope='col' abbr='Q1'>Quarter 1</th></tr></table>");
        var th = Walk(root).First(n => n.Kind == SemanticKind.TableHeaderCell);
        Assert.Equal("col", th.Cell!.Value.Scope);
        Assert.Equal("Q1", th.Cell.Value.Abbr);
    }

    [Fact]
    public async Task TableCell_extracts_headers_attribute()
    {
        var root = await BuildAsync(
            "<table><tr><td headers='h1 h2'>x</td></tr></table>");
        var td = Walk(root).First(n => n.Kind == SemanticKind.TableCell);
        Assert.Equal("h1 h2", td.Cell!.Value.Headers);
    }

    [Fact]
    public async Task Span_attribute_with_invalid_value_falls_back_to_one()
    {
        var root = await BuildAsync(
            "<table><tr><td rowspan='abc' colspan='-5'>x</td></tr></table>");
        var td = Walk(root).First(n => n.Kind == SemanticKind.TableCell);
        Assert.Equal(1, td.Cell!.Value.RowSpan);
        // -5 parses as int but clamps to 1.
        Assert.Equal(1, td.Cell.Value.ColSpan);
    }

    [Fact]
    public async Task Non_cell_kinds_have_null_Cell_metadata()
    {
        var root = await BuildAsync("<p>x</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        Assert.Null(p.Cell);
    }

    // ============================================================
    // Rec 2 — <a> without href is transparent
    // ============================================================

    [Fact]
    public async Task Anchor_without_href_is_transparent_per_Rec_2()
    {
        var root = await BuildAsync("<p>See <a>plain</a>.</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // <a> without href is NOT a link — its content flattens into p.
        Assert.DoesNotContain(Walk(root), n => n.Kind == SemanticKind.Link);
        // Concatenated InlineText: "See plain."
        Assert.Equal("See plain.", p.AggregateText);
    }

    [Fact]
    public async Task Anchor_with_href_emits_Link_with_href()
    {
        var root = await BuildAsync("<p>See <a href='https://example.com'>here</a>.</p>");
        var link = Walk(root).First(n => n.Kind == SemanticKind.Link);
        Assert.Equal("https://example.com", link.Href);
        Assert.Equal("here", link.AggregateText);
    }

    // ============================================================
    // Rec 6 — Image alt missing vs decorative vs present
    // ============================================================

    [Fact]
    public async Task Image_with_alt_text_carries_accessible_name()
    {
        var root = await BuildAsync("<img src='x.png' alt='A red square'>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Equal("A red square", img.AltText);
        Assert.False(img.HasExplicitDecorativeAlt);
    }

    [Fact]
    public async Task Image_with_empty_alt_is_explicitly_decorative()
    {
        var root = await BuildAsync("<img src='x.png' alt=''>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        // alt="" → explicitly decorative per HTML5 §4.8.3.
        Assert.Equal("", img.AltText);
        Assert.True(img.HasExplicitDecorativeAlt);
    }

    [Fact]
    public async Task Image_with_missing_alt_has_null_AltText_and_no_decorative_flag()
    {
        var root = await BuildAsync("<img src='x.png'>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Null(img.AltText);
        Assert.False(img.HasExplicitDecorativeAlt);
    }

    [Fact]
    public async Task Image_falls_back_to_aria_label_when_alt_missing()
    {
        var root = await BuildAsync("<img src='x.png' aria-label='Logo'>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Equal("Logo", img.AltText);
        Assert.False(img.HasExplicitDecorativeAlt);
    }

    [Fact]
    public async Task Image_alt_text_is_normalized()
    {
        var root = await BuildAsync("<img src='x.png' alt='  spaced   out  '>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Equal("spaced out", img.AltText);
    }

    [Fact]
    public async Task Figure_with_aria_label_uses_it_directly()
    {
        var root = await BuildAsync("<figure aria-label='Sales chart'><img src='c.png'></figure>");
        var fig = Walk(root).First(n => n.Kind == SemanticKind.Figure);
        Assert.Equal("Sales chart", fig.AltText);
    }

    [Fact]
    public async Task Figure_without_aria_falls_back_to_figcaption_text()
    {
        var root = await BuildAsync(
            "<figure><img src='c.png'><figcaption>Q3 revenue</figcaption></figure>");
        var fig = Walk(root).First(n => n.Kind == SemanticKind.Figure);
        Assert.Equal("Q3 revenue", fig.AltText);
        Assert.Contains(fig.Children, c => c.Kind == SemanticKind.FigureCaption);
    }

    // ============================================================
    // Sectioning
    // ============================================================

    [Theory]
    [InlineData("header", "Header")]
    [InlineData("footer", "Footer")]
    [InlineData("nav", "Nav")]
    [InlineData("main", "Main")]
    [InlineData("aside", "Aside")]
    [InlineData("article", "Article")]
    [InlineData("section", "Section")]
    public async Task Each_HTML5_sectioning_element_has_its_own_kind(string tag, string expectedKind)
    {
        var root = await BuildAsync($"<{tag}>x</{tag}>");
        var expected = System.Enum.Parse<SemanticKind>(expectedKind);
        Assert.Contains(Walk(root), n => n.Kind == expected);
    }

    // ============================================================
    // Transparent elements + Rec 3 (preserve loose text)
    // ============================================================

    [Fact]
    public async Task Div_is_transparent_so_children_attach_to_parent()
    {
        var root = await BuildAsync("<section><div><p>Body</p></div></section>");
        var section = Walk(root).First(n => n.Kind == SemanticKind.Section);
        Assert.Single(section.Children);
        Assert.Equal(SemanticKind.Paragraph, section.Children[0].Kind);
    }

    [Fact]
    public async Task Span_inside_paragraph_is_transparent_text_concatenates()
    {
        var root = await BuildAsync("<p>Hello <span>world</span>!</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // Span is transparent; its text becomes a sibling InlineText of p's
        // other text children. AggregateText concatenates with proper spacing.
        Assert.Equal("Hello world!", p.AggregateText);
    }

    [Fact]
    public async Task Loose_text_in_transparent_container_becomes_InlineText_per_Rec_3()
    {
        // Rec 3 — direct text under a transparent container must NOT be lost.
        // Previously CollectChildSemantics walked only element children.
        var root = await BuildAsync("<body>Loose text<p>Para</p></body>");
        var loose = Walk(root)
            .Where(n => n.Kind == SemanticKind.InlineText)
            .ToList();
        Assert.Contains(loose, n => n.Text == "Loose text");
    }

    // ============================================================
    // Rec 7 — Interleaved text + nested structure (no double-tagging)
    // ============================================================

    [Fact]
    public async Task Link_inside_paragraph_gets_interleaved_text_and_link_children()
    {
        var root = await BuildAsync("<p>See <a href='/x'>link</a>.</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // Children: [InlineText("See "), Link("link"), InlineText(".")]
        Assert.Equal(3, p.Children.Count);
        Assert.Equal(SemanticKind.InlineText, p.Children[0].Kind);
        Assert.Equal("See ", p.Children[0].Text);
        Assert.Equal(SemanticKind.Link, p.Children[1].Kind);
        Assert.Equal("link", p.Children[1].AggregateText);
        Assert.Equal(SemanticKind.InlineText, p.Children[2].Kind);
        Assert.Equal(".", p.Children[2].Text);
        // No double-tagging — p.Text stays empty per Rec 7.
        Assert.Equal("", p.Text);
        // AggregateText reads the full thing.
        Assert.Equal("See link.", p.AggregateText);
    }

    // ============================================================
    // Rec 8 — Whitespace preserved in pre/code
    // ============================================================

    [Fact]
    public async Task Pre_preserves_whitespace_in_text_content()
    {
        var root = await BuildAsync("<pre>line one\n  line two\n    indented</pre>");
        var code = Walk(root).First(n => n.Kind == SemanticKind.Code);
        // Aggregate preserves the LF and leading indentation per Rec 8.
        Assert.Contains("\n  line two", code.AggregateText);
        Assert.Contains("    indented", code.AggregateText);
    }

    [Fact]
    public async Task Code_preserves_whitespace_in_text_content()
    {
        var root = await BuildAsync("<code>  spaced  out  </code>");
        var code = Walk(root).First(n => n.Kind == SemanticKind.Code);
        Assert.Equal("  spaced  out  ", code.AggregateText);
    }

    [Fact]
    public async Task Paragraph_normalizes_whitespace_unlike_pre()
    {
        var root = await BuildAsync("<p>  spaced  out  </p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // Inline-text normalizer collapses internal runs to single spaces +
        // preserves boundary whitespace as a single space.
        Assert.Equal(" spaced out ", p.AggregateText);
    }

    // ============================================================
    // Rec 1 — Hidden elements are excluded
    // ============================================================

    [Fact]
    public async Task Element_with_hidden_attribute_is_excluded()
    {
        var root = await BuildAsync("<p>visible</p><p hidden>invisible</p>");
        var paragraphs = Walk(root)
            .Where(n => n.Kind == SemanticKind.Paragraph)
            .ToList();
        Assert.Single(paragraphs);
        Assert.Equal("visible", paragraphs[0].AggregateText);
    }

    [Fact]
    public async Task Element_with_aria_hidden_true_is_excluded()
    {
        var root = await BuildAsync(
            "<p>visible</p><p aria-hidden='true'>invisible</p>");
        var paragraphs = Walk(root)
            .Where(n => n.Kind == SemanticKind.Paragraph)
            .ToList();
        Assert.Single(paragraphs);
    }

    [Fact]
    public async Task Element_with_aria_hidden_false_is_included()
    {
        var root = await BuildAsync(
            "<p aria-hidden='false'>shown</p>");
        Assert.Contains(Walk(root), n => n.Kind == SemanticKind.Paragraph);
    }

    [Fact]
    public async Task Aria_hidden_subtree_is_fully_excluded()
    {
        var root = await BuildAsync(
            "<section aria-hidden='true'><p>dropped</p><h1>also dropped</h1></section>");
        Assert.DoesNotContain(Walk(root), n =>
            n.Kind == SemanticKind.Paragraph || n.Kind == SemanticKind.Heading1);
    }

    [Fact]
    public async Task Style_and_script_text_does_not_leak_into_semantic_tree()
    {
        // Rec 3 added text-node walking — Rec 1's metadata-element filter
        // ensures <style>/<script>/<title> contents don't become InlineText.
        var root = await BuildAsync("""
            <html>
              <head>
                <title>Doc title</title>
                <style>p { color: red }</style>
              </head>
              <body><p>Body</p></body>
            </html>
            """);
        var allText = string.Join("\n", Walk(root).Select(n => n.Text));
        Assert.DoesNotContain("Doc title", allText);
        Assert.DoesNotContain("color: red", allText);
        Assert.DoesNotContain("p {", allText);
    }

    [Fact]
    public async Task Cascade_resolved_display_none_excludes_element_per_Rec_1()
    {
        var root = await BuildWithCascadeAsync(
            "<p>shown</p><p class='gone'>dropped</p>",
            ".gone { display: none }");
        var paragraphs = Walk(root)
            .Where(n => n.Kind == SemanticKind.Paragraph)
            .ToList();
        Assert.Single(paragraphs);
        Assert.Equal("shown", paragraphs[0].AggregateText);
    }

    [Fact]
    public async Task Cascade_resolved_visibility_hidden_excludes_element()
    {
        var root = await BuildWithCascadeAsync(
            "<p>shown</p><p class='gone'>dropped</p>",
            ".gone { visibility: hidden }");
        var paragraphs = Walk(root)
            .Where(n => n.Kind == SemanticKind.Paragraph)
            .ToList();
        Assert.Single(paragraphs);
    }

    // ============================================================
    // Code + blockquote + content
    // ============================================================

    [Fact]
    public async Task Pre_and_code_both_map_to_Code_kind()
    {
        var root = await BuildAsync("<pre>x</pre><code>y</code>");
        var codes = Walk(root).Where(n => n.Kind == SemanticKind.Code).ToList();
        Assert.Equal(2, codes.Count);
    }

    [Fact]
    public async Task Blockquote_emits_BlockQuote_kind()
    {
        var root = await BuildAsync("<blockquote>Cogito ergo sum.</blockquote>");
        var bq = Walk(root).First(n => n.Kind == SemanticKind.BlockQuote);
        Assert.Equal("Cogito ergo sum.", bq.AggregateText);
    }

    // ============================================================
    // Document root + nesting
    // ============================================================

    [Fact]
    public async Task Build_always_returns_a_Document_root()
    {
        var root = await BuildAsync("<p>x</p>");
        Assert.Equal(SemanticKind.Document, root.Kind);
        Assert.Null(root.SourceElement);
    }

    [Fact]
    public async Task Empty_html_produces_just_the_document_root()
    {
        var root = await BuildAsync("");
        Assert.Equal(SemanticKind.Document, root.Kind);
        Assert.Empty(root.Children);
    }

    [Fact]
    public async Task Nested_h1_inside_article_inside_main_is_preserved()
    {
        var root = await BuildAsync(
            "<main><article><h1>Title</h1><p>Body</p></article></main>");
        var main = Walk(root).First(n => n.Kind == SemanticKind.Main);
        var article = main.Children.First();
        Assert.Equal(SemanticKind.Article, article.Kind);
        Assert.Equal(2, article.Children.Count);
        Assert.Equal(SemanticKind.Heading1, article.Children[0].Kind);
        Assert.Equal(SemanticKind.Paragraph, article.Children[1].Kind);
    }
}
