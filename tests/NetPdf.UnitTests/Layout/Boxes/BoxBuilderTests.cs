// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// End-to-end tests for <see cref="BoxBuilder"/>: parses small HTML + CSS,
/// runs the cascade + var resolver, then asserts on the produced box tree.
/// Covers the cycle-1 surface — DOM walk, display dispatch, anonymous-block
/// insertion, ::before / ::after pseudo materialization, replaced elements,
/// display: none skip, text run handling.
/// </summary>
public sealed class BoxBuilderTests
{
    // ============================================================
    // Test infrastructure
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

    private static async Task<Box> BuildAsync(string html, string? css = null)
    {
        var doc = await ParseHtml(html);
        var sheets = css is null
            ? ImmutableArray<CssStylesheet>.Empty
            : ImmutableArray.Create(await ParseSheet(css));
        var cascade = CascadeResolver.Resolve(doc, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        return BoxBuilder.Build(doc, resolved);
    }

    /// <summary>Find the first descendant box generated for <paramref name="tag"/>
    /// (lower-case match against <c>SourceElement.LocalName</c>).</summary>
    private static Box? FindFirst(Box root, string tag)
    {
        if (root.SourceElement?.LocalName.Equals(tag, System.StringComparison.OrdinalIgnoreCase) == true)
            return root;
        foreach (var child in root.Children)
        {
            var hit = FindFirst(child, tag);
            if (hit is not null) return hit;
        }
        return null;
    }

    // ============================================================
    // Basic DOM walk + display dispatch
    // ============================================================

    [Fact]
    public async Task Empty_document_returns_root_with_html_child()
    {
        var root = await BuildAsync("<!doctype html><html></html>");
        Assert.Equal(BoxKind.Root, root.Kind);
        Assert.Single(root.Children);
        var html = root.Children[0];
        Assert.Equal(BoxKind.BlockContainer, html.Kind);
        Assert.Equal("html", html.SourceElement!.LocalName);
    }

    [Fact]
    public async Task Block_element_uses_HtmlDefaultDisplay_block()
    {
        var root = await BuildAsync("<div></div>");
        var div = FindFirst(root, "div");
        Assert.NotNull(div);
        Assert.Equal(BoxKind.BlockContainer, div!.Kind);
    }

    [Fact]
    public async Task Inline_element_uses_HtmlDefaultDisplay_inline()
    {
        var root = await BuildAsync("<span></span>");
        var span = FindFirst(root, "span");
        Assert.NotNull(span);
        Assert.Equal(BoxKind.InlineBox, span!.Kind);
    }

    [Fact]
    public async Task Explicit_display_overrides_HtmlDefaultDisplay()
    {
        var root = await BuildAsync(
            "<div><span class='inline-flex'>x</span></div>",
            ".inline-flex { display: inline-flex }");
        var span = FindFirst(root, "span");
        Assert.NotNull(span);
        Assert.Equal(BoxKind.InlineFlexContainer, span!.Kind);
    }

    [Fact]
    public async Task ListItem_li_maps_to_ListItem_kind()
    {
        var root = await BuildAsync("<ul><li>x</li></ul>");
        var li = FindFirst(root, "li");
        Assert.NotNull(li);
        Assert.Equal(BoxKind.ListItem, li!.Kind);
    }

    [Fact]
    public async Task Table_default_display_yields_Table_kind()
    {
        var root = await BuildAsync("<table></table>");
        var table = FindFirst(root, "table");
        Assert.NotNull(table);
        Assert.Equal(BoxKind.Table, table!.Kind);
    }

    [Fact]
    public async Task Tr_default_display_yields_TableRow_kind()
    {
        var root = await BuildAsync("<table><tr><td>x</td></tr></table>");
        var tr = FindFirst(root, "tr");
        var td = FindFirst(root, "td");
        Assert.NotNull(tr);
        Assert.NotNull(td);
        Assert.Equal(BoxKind.TableRow, tr!.Kind);
        Assert.Equal(BoxKind.TableCell, td!.Kind);
    }

    // ============================================================
    // display: none skip
    // ============================================================

    [Fact]
    public async Task Display_none_element_is_skipped()
    {
        var root = await BuildAsync(
            "<div><p class='hide'>gone</p><p>shown</p></div>",
            ".hide { display: none }");
        var div = FindFirst(root, "div")!;
        // Two <p> siblings in DOM but only one box.
        var pCount = 0;
        foreach (var child in div.Children)
            if (child.SourceElement?.LocalName == "p") pCount++;
        Assert.Equal(1, pCount);
    }

    // ============================================================
    // Replaced elements
    // ============================================================

    [Fact]
    public async Task Img_default_inline_yields_InlineReplacedElement()
    {
        var root = await BuildAsync("<div><img></div>");
        var img = FindFirst(root, "img");
        Assert.NotNull(img);
        Assert.Equal(BoxKind.InlineReplacedElement, img!.Kind);
    }

    [Fact]
    public async Task Img_with_display_block_yields_BlockReplacedElement()
    {
        var root = await BuildAsync(
            "<div><img class='b'></div>",
            ".b { display: block }");
        var img = FindFirst(root, "img");
        Assert.NotNull(img);
        Assert.Equal(BoxKind.BlockReplacedElement, img!.Kind);
    }

    // ============================================================
    // Text runs
    // ============================================================

    [Fact]
    public async Task Text_node_becomes_TextRun_box()
    {
        var root = await BuildAsync("<p>hello</p>");
        var p = FindFirst(root, "p")!;
        // After anonymous-block fixup: a block-level <p> with only inline
        // children (the text run) does NOT need wrapping. So the text run is
        // a direct child of <p>.
        Assert.Single(p.Children);
        var text = p.Children[0];
        Assert.Equal(BoxKind.TextRun, text.Kind);
        Assert.Equal("hello", text.Text);
    }

    // ============================================================
    // ::before / ::after pseudo-element materialization
    // ============================================================

    [Fact]
    public async Task Before_pseudo_with_string_content_emits_pseudo_box_with_text_run()
    {
        var root = await BuildAsync(
            "<p>x</p>",
            "p::before { content: 'PRE' }");
        var p = FindFirst(root, "p")!;
        // First child is the pseudo box, then the text run.
        var first = p.Children[0];
        Assert.True(first.IsPseudoElement);
        Assert.Equal(BoxPseudo.Before, first.Pseudo);
        Assert.Same(p.SourceElement, first.SourceElement);
        Assert.Single(first.Children);
        var pseudoText = first.Children[0];
        Assert.Equal(BoxKind.TextRun, pseudoText.Kind);
        Assert.Equal("PRE", pseudoText.Text);
    }

    [Fact]
    public async Task After_pseudo_with_string_content_emits_pseudo_box_at_end()
    {
        var root = await BuildAsync(
            "<p>x</p>",
            "p::after { content: 'POST' }");
        var p = FindFirst(root, "p")!;
        // Last child is the pseudo box.
        var last = p.Children[p.Children.Count - 1];
        Assert.True(last.IsPseudoElement);
        Assert.Equal(BoxPseudo.After, last.Pseudo);
        Assert.Equal("POST", last.Children[0].Text);
    }

    [Fact]
    public async Task Pseudo_with_content_none_emits_no_box()
    {
        var root = await BuildAsync(
            "<p>x</p>",
            "p::before { content: none }");
        var p = FindFirst(root, "p")!;
        // No pseudo-element box, only the text.
        foreach (var child in p.Children)
            Assert.False(child.IsPseudoElement);
    }

    [Fact]
    public async Task Pseudo_with_content_normal_emits_no_box_for_before_or_after()
    {
        // Per Pseudo L4 §3.1: ::before / ::after with `content: normal` produce
        // no content (different from list-item ::marker where `normal` defaults).
        var root = await BuildAsync(
            "<p>x</p>",
            "p::before { content: normal }");
        var p = FindFirst(root, "p")!;
        foreach (var child in p.Children)
            Assert.False(child.IsPseudoElement);
    }

    // ============================================================
    // Anonymous-block insertion (Display L3 §3.1)
    // ============================================================

    [Fact]
    public async Task Block_with_only_inline_children_does_not_trigger_anonymous_block()
    {
        // <p> has only the text run "hello" — no anonymous block needed.
        var root = await BuildAsync("<p>hello world</p>");
        var p = FindFirst(root, "p")!;
        // No AnonymousBlock children.
        foreach (var child in p.Children)
            Assert.NotEqual(BoxKind.AnonymousBlock, child.Kind);
    }

    [Fact]
    public async Task Block_with_only_block_children_does_not_trigger_anonymous_block()
    {
        var root = await BuildAsync("<div><p>a</p><p>b</p></div>");
        var div = FindFirst(root, "div")!;
        foreach (var child in div.Children)
            Assert.NotEqual(BoxKind.AnonymousBlock, child.Kind);
    }

    [Fact]
    public async Task Block_with_mixed_children_wraps_inline_runs_in_AnonymousBlock()
    {
        // <div>text<p>block</p>more text</div>
        // Per Display L3 §3.1: the inline "text" + "more text" runs each need
        // wrapping in an AnonymousBlock since the div has block-level children.
        var root = await BuildAsync("<div>before<p>middle</p>after</div>");
        var div = FindFirst(root, "div")!;

        // Expected order: AnonymousBlock(text "before") → p → AnonymousBlock(text "after")
        Assert.Equal(3, div.Children.Count);

        var first = div.Children[0];
        Assert.Equal(BoxKind.AnonymousBlock, first.Kind);
        Assert.Single(first.Children);
        Assert.Equal(BoxKind.TextRun, first.Children[0].Kind);
        Assert.Equal("before", first.Children[0].Text);

        var middle = div.Children[1];
        Assert.Equal(BoxKind.BlockContainer, middle.Kind);
        Assert.Equal("p", middle.SourceElement!.LocalName);

        var last = div.Children[2];
        Assert.Equal(BoxKind.AnonymousBlock, last.Kind);
        Assert.Equal(BoxKind.TextRun, last.Children[0].Kind);
        Assert.Equal("after", last.Children[0].Text);
    }

    [Fact]
    public async Task Block_with_inline_then_block_emits_two_children_wrapper_then_block()
    {
        var root = await BuildAsync("<div><span>inline</span><p>block</p></div>");
        var div = FindFirst(root, "div")!;
        Assert.Equal(2, div.Children.Count);
        Assert.Equal(BoxKind.AnonymousBlock, div.Children[0].Kind);
        Assert.Equal(BoxKind.BlockContainer, div.Children[1].Kind);
    }

    [Fact]
    public async Task Multiple_contiguous_inlines_share_one_AnonymousBlock_wrapper()
    {
        var root = await BuildAsync(
            "<div><span>a</span><em>b</em><p>block</p></div>");
        var div = FindFirst(root, "div")!;
        // Two inlines + one block = one wrapper containing both inlines, then the block.
        Assert.Equal(2, div.Children.Count);
        var wrapper = div.Children[0];
        Assert.Equal(BoxKind.AnonymousBlock, wrapper.Kind);
        Assert.Equal(2, wrapper.Children.Count);
        Assert.Equal("span", wrapper.Children[0].SourceElement!.LocalName);
        Assert.Equal("em", wrapper.Children[1].SourceElement!.LocalName);
    }

    // ============================================================
    // Style sharing + box-ownership
    // ============================================================

    [Fact]
    public async Task Boxes_have_box_owned_styles()
    {
        var root = await BuildAsync("<p>x</p>");
        var p = FindFirst(root, "p")!;
        Assert.True(p.Style.IsBoxOwned);
        Assert.True(root.Style.IsBoxOwned);
    }
}
