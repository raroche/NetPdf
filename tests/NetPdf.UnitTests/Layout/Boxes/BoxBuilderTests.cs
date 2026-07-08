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
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
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
    // RC-9 — collapsible inter-tag whitespace generates NO box
    // (CSS 2.1 §9.2.2.1). Pretty-printed HTML must not synthesize
    // phantom AnonymousBlocks between block siblings.
    // ============================================================

    [Fact]
    public async Task Whitespace_only_run_between_block_siblings_generates_no_anonymous_block()
    {
        // Pretty-printed: newline + indent between each pair of <p>. Those
        // collapsible-whitespace runs must NOT become AnonymousBlocks.
        var root = await BuildAsync("<div>\n  <p>a</p>\n  <p>b</p>\n  <p>c</p>\n</div>");
        var div = FindFirst(root, "div")!;
        Assert.Equal(3, div.Children.Count);
        foreach (var child in div.Children)
        {
            Assert.Equal(BoxKind.BlockContainer, child.Kind);
            Assert.Equal("p", child.SourceElement!.LocalName);
        }
    }

    [Fact]
    public async Task Pretty_printed_and_tight_markup_produce_the_same_box_tree()
    {
        var pretty = await BuildAsync("<div>\n  <p>a</p>\n  <p>b</p>\n</div>");
        var tight = await BuildAsync("<div><p>a</p><p>b</p></div>");
        var prettyDiv = FindFirst(pretty, "div")!;
        var tightDiv = FindFirst(tight, "div")!;
        Assert.Equal(tightDiv.Children.Count, prettyDiv.Children.Count);
        Assert.Equal(2, prettyDiv.Children.Count);
    }

    [Fact]
    public async Task Whitespace_run_is_preserved_under_white_space_pre()
    {
        // white-space: pre preserves the inter-tag whitespace, so the run
        // between the blocks DOES generate an anonymous block.
        var root = await BuildAsync(
            "<div>\n  <p>a</p>\n  <p>b</p>\n</div>",
            "div { white-space: pre }");
        var div = FindFirst(root, "div")!;
        Assert.Contains(div.Children, c => c.Kind == BoxKind.AnonymousBlock);
    }

    [Fact]
    public async Task Run_with_inline_elements_and_spaces_is_preserved_between_blocks()
    {
        // `<span>a</span> <span>b</span>` between two blocks is NOT
        // whitespace-only (it carries inline element boxes + a significant
        // space), so it must be wrapped, not dropped.
        var root = await BuildAsync(
            "<div><p>x</p><span>a</span> <span>b</span><p>y</p></div>");
        var div = FindFirst(root, "div")!;
        var wrapper = div.Children.Single(c => c.Kind == BoxKind.AnonymousBlock);
        // The wrapper keeps both spans (and the separating space run).
        Assert.Contains(wrapper.Children, c => c.SourceElement?.LocalName == "span");
        Assert.True(wrapper.Children.Count >= 2);
    }

    [Fact]
    public async Task Inline_only_table_cell_still_wraps_its_text_after_whitespace_fix()
    {
        // Regression guard: the RC-9 whitespace drop must not remove a cell's
        // real text. `<td>Widget</td>` content stays wrapped in an anonymous
        // block (CSS Tables L3 §11.5.3 cell content is block-level).
        var root = await BuildAsync("<table><tr><td>Widget</td></tr></table>");
        var cell = FindFirst(root, "td")!;
        var anon = cell.Children.Single(c => c.Kind == BoxKind.AnonymousBlock);
        Assert.Equal("Widget", anon.Children[0].Text);
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

    // ============================================================
    // text-align: match-parent (CSS Text 3 §7.1) — resolved at box-build
    // ============================================================

    [Theory]
    // parent direction / parent text-align → child's resolved physical text-align keyword
    // (KeywordResolver: 2=left 3=right 4=center 5=justify). match-parent takes the PARENT's
    // text-align, resolving start/end against the PARENT's direction.
    [InlineData("ltr", "start", 2)]   // start in LTR → left
    [InlineData("rtl", "start", 3)]   // start in RTL → right (the direction-aware case)
    [InlineData("ltr", "end", 3)]     // end in LTR → right
    [InlineData("rtl", "end", 2)]     // end in RTL → left
    [InlineData("ltr", "center", 4)]  // physical values pass through unchanged
    [InlineData("ltr", "right", 3)]
    [InlineData("ltr", "justify", 5)]
    public async Task Match_parent_text_align_resolves_against_the_parent(
        string parentDir, string parentAlign, int expectedChildKeyword)
    {
        // The child declares its OWN direction:ltr to prove match-parent reads the PARENT's direction,
        // not the child's (a plain inherited `start` would resolve against the child's ltr instead).
        var html =
            $"<div style=\"direction:{parentDir};text-align:{parentAlign}\">" +
            "<p style=\"direction:ltr;text-align:match-parent\">x</p></div>";
        var root = await BuildAsync(html);
        var p = FindFirst(root, "p")!;
        Assert.Equal(expectedChildKeyword, p.Style.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0));
    }

    [Fact]
    public async Task Match_parent_text_align_resolves_on_a_before_pseudo_box()
    {
        // PR #212 review P2 — a generated ::before box with `text-align: match-parent` resolves against
        // its HOST's used text-align the same way an element box does: host `direction:rtl;
        // text-align:start` → used RIGHT, so the block pseudo aligns RIGHT (keyword 3), NOT the defensive
        // layout-time left fallback.
        var root = await BuildAsync(
            "<div></div>",
            "div { direction:rtl; text-align:start } " +
            "div::before { content:'A'; display:block; text-align:match-parent }");
        var before = FindFirstPseudo(root, BoxPseudo.Before)!;
        Assert.Equal(3, before.Style.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0));
    }

    // ============================================================
    // dir HTML attribute → direction (HTML §3.2.6.4 presentational hint)
    // ============================================================

    [Theory]
    [InlineData("<div dir=\"rtl\"><p>x</p></div>", 1)]               // rtl → keyword 1, inherited by <p>
    [InlineData("<div dir=\"ltr\"><p>x</p></div>", 0)]               // ltr → keyword 0
    [InlineData("<div dir=\"RTL\"><p>x</p></div>", 1)]               // case-insensitive
    [InlineData("<div dir=\"rtl\"><p dir=\"ltr\">x</p></div>", 0)]   // inner dir overrides inherited rtl
    [InlineData("<div dir=\"auto\"><p>x</p></div>", 0)]              // auto → inherited default (ltr)
    [InlineData("<div dir=\" rtl \"><p>x</p></div>", 1)]            // PR #213 — surrounding ASCII whitespace trimmed
    [InlineData("<div dir=\"\tltr\"><p>x</p></div>", 0)]            // leading tab trimmed
    public async Task Dir_attribute_maps_to_the_direction_property(string html, int expectedDirectionKeyword)
    {
        var root = await BuildAsync(html);
        var p = FindFirst(root, "p")!;
        Assert.Equal(expectedDirectionKeyword, p.Style.ReadKeywordOrDefault(PropertyId.Direction, defaultIndex: 0));
    }

    [Fact]
    public async Task Css_direction_wins_over_the_dir_attribute()
    {
        // The `dir` attribute is a UA-origin presentational hint, so an author CSS `direction` wins.
        var root = await BuildAsync("<div dir=\"rtl\" style=\"direction:ltr\"><p>x</p></div>");
        var p = FindFirst(root, "p")!;
        Assert.Equal(0, p.Style.ReadKeywordOrDefault(PropertyId.Direction, defaultIndex: 0));   // ltr — CSS wins
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("unset")]
    [InlineData("initial")]
    public async Task Css_wide_direction_declaration_beats_the_dir_attribute(string cssWide)
    {
        // PR #213 review — a CSS-wide `direction` keyword is resolved by the CASCADE, not a leaf
        // resolver, so it never reaches a keyword slot for ApplyResolvedDeclarations to materialize. The
        // `dir` presentational hint (UA origin) must still step aside for it, so `direction:inherit` on
        // an LTR-parented element keeps the inherited LTR — NOT the `dir="rtl"` hint. (`direction` is an
        // inherited property, so inherit/unset/initial all resolve to the parent's LTR here, which makes
        // the assertion robust against the tracked initial/revert invalid-fallback gap.)
        var root = await BuildAsync($"<div><p dir=\"rtl\" style=\"direction:{cssWide}\">x</p></div>");
        var p = FindFirst(root, "p")!;
        Assert.Equal(0, p.Style.ReadKeywordOrDefault(PropertyId.Direction, defaultIndex: 0));   // stays LTR
    }

    /// <summary>First descendant box with the given pseudo marker (e.g. ::before), depth-first.</summary>
    private static Box? FindFirstPseudo(Box root, BoxPseudo pseudo)
    {
        if (root.Pseudo == pseudo) return root;
        foreach (var child in root.Children)
        {
            var hit = FindFirstPseudo(child, pseudo);
            if (hit is not null) return hit;
        }
        return null;
    }

    // ============================================================
    // line-height: <percentage> inherits as a length (CSS Inline 3 §4.2)
    // ============================================================

    [Fact]
    public async Task Percentage_line_height_inherits_as_a_length_from_the_declaring_element()
    {
        // A % line-height computes to a LENGTH at the DECLARING element's font-size, and that length
        // inherits. Parent declares line-height:150% at font-size:20px → 30px; the child has a DIFFERENT
        // font-size:40px but must keep the parent's 30px length (NOT re-resolve 150% × 40 = 60px).
        var html = "<div style=\"font-size:20px;line-height:150%\">" +
                   "<p style=\"font-size:40px\">x</p></div>";
        var root = await BuildAsync(html);
        var div = FindFirst(root, "div")!;
        var p = FindFirst(root, "p")!;
        // ReadLineHeightPx returns a LengthPx slot's px directly (ignoring the passed font-size), so
        // both the declaring element AND the differently-sized child resolve to the same 30px length.
        Assert.Equal(30.0, div.Style.ReadLineHeightPx(20.0));   // declaring element: 150% × 20
        Assert.Equal(30.0, p.Style.ReadLineHeightPx(40.0));     // inherited AS 30px, not 150% × 40 = 60
    }

    [Fact]
    public async Task Percentage_line_height_on_zero_font_size_is_zero_not_a_16px_fallback()
    {
        // PR #212 Copilot review — a resolved `font-size: 0` must give a 0 line-height (collapsed line
        // box), NOT a 16px-based fallback. The conversion guards on a resolved LengthPx font-size and
        // multiplies by it directly (0 → 0); only a still-deferred font-size is left for the read path.
        var root = await BuildAsync("<p style=\"font-size:0;line-height:150%\">x</p>");
        var p = FindFirst(root, "p")!;
        Assert.Equal(0.0, p.Style.ReadLineHeightPx(0.0));   // 150% × 0, not 150% × 16
    }

    [Fact]
    public async Task Percentage_line_height_on_same_font_size_child_is_unchanged()
    {
        // Control: when the child's font-size matches the declaring element's, the inherited length and
        // the (old) re-resolved percentage coincide — so this common case is unaffected.
        var html = "<div style=\"font-size:20px;line-height:150%\"><p>x</p></div>";
        var root = await BuildAsync(html);
        var p = FindFirst(root, "p")!;
        Assert.Equal(30.0, p.Style.ReadLineHeightPx(20.0));
    }
}
