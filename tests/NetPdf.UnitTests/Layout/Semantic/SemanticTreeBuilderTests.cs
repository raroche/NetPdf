// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using NetPdf.Layout.Semantic;
using Xunit;

namespace NetPdf.UnitTests.Layout.Semantic;

/// <summary>
/// Task 15 cycle 1 — SemanticTreeBuilder walk produces a parallel tree of
/// HTML semantic / accessibility roles that v1.1 will use to emit PDF/UA
/// tagged-PDF structure.
/// </summary>
public sealed class SemanticTreeBuilderTests
{
    // ============================================================
    // Test infrastructure
    // ============================================================

    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static async Task<SemanticNode> BuildAsync(string html)
    {
        var doc = await ParseHtml(html);
        return SemanticTreeBuilder.Build(doc);
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
        Assert.Equal("Title", node.Text);
    }

    // ============================================================
    // Paragraph + leaf text capture
    // ============================================================

    [Fact]
    public async Task Paragraph_captures_normalized_text()
    {
        var root = await BuildAsync("<p>Hello   world\n  again</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // Whitespace runs collapse to single spaces.
        Assert.Equal("Hello world again", p.Text);
    }

    [Fact]
    public async Task Container_kind_does_not_carry_text_content()
    {
        var root = await BuildAsync("<section><p>Body</p></section>");
        var section = Walk(root).First(n => n.Kind == SemanticKind.Section);
        // Section is a container, not a text leaf — Text stays empty.
        Assert.Equal("", section.Text);
        // Paragraph child carries the text.
        var p = Walk(section).First(n => n.Kind == SemanticKind.Paragraph);
        Assert.Equal("Body", p.Text);
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
        Assert.Equal("Apple", list.Children[0].Text);
        Assert.Equal("Banana", list.Children[1].Text);
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
        // thead/tbody/tfoot are transparent → table has 3 TR children directly.
        Assert.Equal(3, table.Children.Count);
        Assert.All(table.Children, c => Assert.Equal(SemanticKind.TableRow, c.Kind));
    }

    [Fact]
    public async Task Table_caption_becomes_TableCaption_kind()
    {
        var root = await BuildAsync("<table><caption>My Table</caption><tr><td>x</td></tr></table>");
        var caption = Walk(root).First(n => n.Kind == SemanticKind.TableCaption);
        Assert.Equal("My Table", caption.Text);
    }

    [Fact]
    public async Task Th_and_td_get_distinct_kinds()
    {
        var root = await BuildAsync("<table><tr><th>Name</th><td>Value</td></tr></table>");
        var th = Walk(root).First(n => n.Kind == SemanticKind.TableHeaderCell);
        var td = Walk(root).First(n => n.Kind == SemanticKind.TableCell);
        Assert.Equal("Name", th.Text);
        Assert.Equal("Value", td.Text);
    }

    // ============================================================
    // Links — href captured + text captured
    // ============================================================

    [Fact]
    public async Task Anchor_with_href_emits_Link_with_href()
    {
        var root = await BuildAsync("<p>See <a href='https://example.com'>here</a>.</p>");
        var link = Walk(root).First(n => n.Kind == SemanticKind.Link);
        Assert.Equal("https://example.com", link.Href);
        Assert.Equal("here", link.Text);
    }

    [Fact]
    public async Task Anchor_without_href_still_emits_Link_with_null_href()
    {
        var root = await BuildAsync("<a>plain</a>");
        var link = Walk(root).First(n => n.Kind == SemanticKind.Link);
        Assert.Null(link.Href);
        Assert.Equal("plain", link.Text);
    }

    // ============================================================
    // Images + figures — alt resolution
    // ============================================================

    [Fact]
    public async Task Image_uses_alt_attribute_for_accessible_name()
    {
        var root = await BuildAsync("<img src='x.png' alt='A red square'>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Equal("A red square", img.AltText);
    }

    [Fact]
    public async Task Image_falls_back_to_aria_label_when_alt_missing()
    {
        var root = await BuildAsync("<img src='x.png' aria-label='Logo'>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Equal("Logo", img.AltText);
    }

    [Fact]
    public async Task Image_with_no_alt_or_aria_has_null_AltText()
    {
        var root = await BuildAsync("<img src='x.png'>");
        var img = Walk(root).First(n => n.Kind == SemanticKind.Image);
        Assert.Null(img.AltText);
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
        // The figcaption itself also exists as a child.
        Assert.Contains(fig.Children, c => c.Kind == SemanticKind.FigureCaption);
    }

    // ============================================================
    // Sectioning — each HTML5 sectioning element keeps its own kind
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
    // Transparent elements — div/span/etc. flatten children
    // ============================================================

    [Fact]
    public async Task Div_is_transparent_so_children_attach_to_parent()
    {
        var root = await BuildAsync("<section><div><p>Body</p></div></section>");
        var section = Walk(root).First(n => n.Kind == SemanticKind.Section);
        // The div produces no node — its <p> child attaches directly to section.
        Assert.Single(section.Children);
        Assert.Equal(SemanticKind.Paragraph, section.Children[0].Kind);
    }

    [Fact]
    public async Task Span_is_transparent_in_paragraph()
    {
        var root = await BuildAsync("<p>Hello <span>world</span>!</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        // Span produces no semantic node, but its text contributes to p.Text.
        Assert.Equal("Hello world!", p.Text);
        Assert.Empty(p.Children);
    }

    // ============================================================
    // Code + blockquote
    // ============================================================

    [Fact]
    public async Task Pre_and_code_both_map_to_Code_kind()
    {
        var root = await BuildAsync("<pre>x</pre><code>y</code>");
        var codes = Walk(root).Where(n => n.Kind == SemanticKind.Code).ToList();
        Assert.Equal(2, codes.Count);
    }

    [Fact]
    public async Task Blockquote_emits_BlockQuote_kind_with_text()
    {
        var root = await BuildAsync("<blockquote>Cogito ergo sum.</blockquote>");
        var bq = Walk(root).First(n => n.Kind == SemanticKind.BlockQuote);
        Assert.Equal("Cogito ergo sum.", bq.Text);
    }

    // ============================================================
    // Document root
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
        // <html> + <head> + <body> are transparent so they emit no semantic
        // nodes — only their recognized descendants do.
        Assert.Empty(root.Children);
    }

    // ============================================================
    // Nested structure preserved
    // ============================================================

    [Fact]
    public async Task Nested_structure_h1_inside_article_inside_main_is_preserved()
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

    [Fact]
    public async Task Link_inside_paragraph_appears_as_child_with_paragraph_text_overlapping()
    {
        var root = await BuildAsync("<p>See <a href='/x'>link</a>.</p>");
        var p = Walk(root).First(n => n.Kind == SemanticKind.Paragraph);
        Assert.Single(p.Children);
        Assert.Equal(SemanticKind.Link, p.Children[0].Kind);
        // Cycle-1 documented behavior: paragraph text includes link text.
        Assert.Equal("See link.", p.Text);
        Assert.Equal("link", p.Children[0].Text);
    }
}
