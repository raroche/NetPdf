// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// Tests for <see cref="MarginContentCollector"/>: the bounded running-element text read — the
/// allocation guard (post-PR-#150 review P2) for <c>content: element(name)</c> running content — plus
/// the own-style capture for element()'s first-cut own-style rendering: in LOCKSTEP with the text per
/// occurrence (post-PR-#151 review P1) and walking ancestors for inherited font/color (review P2).
/// </summary>
public sealed class MarginContentCollectorTests
{
    private static async Task<IElement> MakeHost(string html, string id)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content(html));
        return doc.QuerySelector("#" + id)!;
    }

    /// <summary>Parse <paramref name="html"/> + <paramref name="css"/>, run the real cascade + var
    /// resolver, then <see cref="MarginContentCollector.Collect"/> from the document element — mirroring
    /// the render pipeline (<c>PdfRenderPipeline</c>), so the returned context is exactly what the
    /// page-margin painter would consume.</summary>
    private static async Task<CssContentList.MarginContentContext> CollectAsync(string html, string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var doc = await ctx.OpenAsync(req => req.Content(html));
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = CssParserAdapter.Adapt(parser.ParseStyleSheet(css), href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet), CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        return MarginContentCollector.Collect(doc.DocumentElement!, resolved);
    }

    [Fact]
    public async Task ReadBoundedDescendantText_caps_a_huge_element_to_maxChars()
    {
        // Review P2: a megabyte running element must NOT materialize its whole textContent — the read
        // stops at the cap, so the allocation is bounded regardless of the subtree size.
        var host = await MakeHost("<div id='h'>" + new string('A', 200_000) + "</div>", "h");
        Assert.Equal(64 * 1024, MarginContentCollector.ReadBoundedDescendantText(host, 64 * 1024).Length);
    }

    [Fact]
    public async Task ReadBoundedDescendantText_concatenates_nested_descendant_text_in_document_order()
    {
        // textContent is ALL descendant text in document order, not just direct children.
        var host = await MakeHost("<div id='h'>a<span>b<em>c</em>d</span>e</div>", "h");
        Assert.Equal("abcde", MarginContentCollector.ReadBoundedDescendantText(host, 64 * 1024));
    }

    [Fact]
    public async Task ReadBoundedDescendantText_under_the_cap_returns_the_full_text()
    {
        var host = await MakeHost("<div id='h'>Chapter One</div>", "h");
        Assert.Equal("Chapter One", MarginContentCollector.ReadBoundedDescendantText(host, 64 * 1024));
    }

    [Fact]
    public async Task ReadBoundedDescendantText_truncates_mid_text_node_at_the_cap()
    {
        // The cap can fall in the middle of a single text node — only `maxChars` are taken.
        var host = await MakeHost("<div id='h'>" + new string('A', 100) + "</div>", "h");
        Assert.Equal(new string('A', 10), MarginContentCollector.ReadBoundedDescendantText(host, 10));
    }

    // ---- own-style capture: lockstep with text (review P1) + inherited (review P2) ----

    [Fact]
    public async Task Collect_records_running_element_style_in_lockstep_when_last_is_unstyled()
    {
        // Review P1: two running elements share `rh` — the FIRST is styled (colour), the LAST is unstyled.
        // The style dictionaries must track the text dictionaries occurrence-for-occurrence: the LAST entry
        // (what element(rh, last) reads) pairs the last text with an EMPTY style, so the box's own style is
        // used — NOT the first occurrence's stale colour.
        var ctx = await CollectAsync(
            "<div class='r1'>A</div><div class='r2'>B</div>",
            ".r1 { position: running(rh); color: #ff0000 } .r2 { position: running(rh) }");

        Assert.Equal("B", ctx.RunningElements!["rh"]);        // last text
        Assert.Equal("A", ctx.RunningElementsFirst!["rh"]);   // first text
        Assert.Empty(ctx.RunningElementStyles!["rh"]);                                  // last (r2) unstyled → empty marker
        Assert.Contains(ctx.RunningElementStylesFirst!["rh"], kv => kv.Key == "color"); // first (r1) styled → colour
    }

    [Fact]
    public async Task Collect_records_running_element_style_in_lockstep_when_first_is_unstyled()
    {
        // Review P1, the converse: the FIRST is unstyled, the SECOND styled. The FIRST entry (what
        // element(rh)/first reads) must be the EMPTY marker — the later occurrence's colour must NOT be
        // recorded as the "first" style.
        var ctx = await CollectAsync(
            "<div class='r1'>A</div><div class='r2'>B</div>",
            ".r1 { position: running(rh) } .r2 { position: running(rh); color: #0000ff }");

        Assert.Empty(ctx.RunningElementStylesFirst!["rh"]);                          // first (r1) unstyled → empty marker
        Assert.Contains(ctx.RunningElementStyles!["rh"], kv => kv.Key == "color");   // last (r2) styled → colour
    }

    [Fact]
    public async Task Collect_captures_inherited_ancestor_color_for_a_running_element()
    {
        // Review P2: `color` is CSS-inherited, so an ancestor's colour is the running element's own colour
        // even though the element declares none — the collector walks ancestors for the nearest winner.
        var ctx = await CollectAsync(
            "<div class='section'><div class='rh'>AB</div></div>",
            ".section { color: #ff0000 } .rh { position: running(rh) }");

        Assert.Contains(ctx.RunningElementStylesFirst!["rh"], kv => kv.Key == "color");
    }

    [Fact]
    public async Task Collect_captures_inherited_ancestor_font_size_for_a_running_element()
    {
        // Review P2: font-size is inherited too — an ancestor's font-size reaches the running element's
        // own-style.
        var ctx = await CollectAsync(
            "<div class='section'><div class='rh'>AB</div></div>",
            ".section { font-size: 24px } .rh { position: running(rh) }");

        Assert.Contains(ctx.RunningElementStylesFirst!["rh"], kv => kv.Key == "font-size");
    }

    // ---- own DECORATION capture (background / border) — non-inherited, self-only (Task 23 full-block) ----

    [Fact]
    public async Task Collect_captures_the_running_elements_own_background_and_border()
    {
        // Task 23 full-block: the element's OWN background-color + border-* longhands are captured for the
        // box decoration. A normal element's `border` shorthand is expanded to longhands by the cascade.
        var ctx = await CollectAsync(
            "<div class='rh'>AB</div>",
            ".rh { position: running(rh); background-color: red; border: 2px solid blue }");

        var own = ctx.RunningElementStyles!["rh"];
        Assert.Contains(own, kv => kv.Key == "background-color");
        Assert.Contains(own, kv => kv.Key == "border-top-style");
        Assert.Contains(own, kv => kv.Key == "border-top-width");
    }

    [Fact]
    public async Task Collect_does_not_capture_an_ancestor_background_for_a_running_element()
    {
        // background-color is NON-inherited, so the decoration capture is SELF-ONLY — an ancestor's
        // background must NOT be captured for the running element (unlike the inherited color/font).
        var ctx = await CollectAsync(
            "<div class='section'><div class='rh'>AB</div></div>",
            ".section { background-color: red } .rh { position: running(rh) }");

        Assert.DoesNotContain(ctx.RunningElementStyles!["rh"], kv => kv.Key == "background-color");
    }

    // ---- own padding (non-inherited, self-only) + text-align (inherited) — Task 23 ----

    [Fact]
    public async Task Collect_captures_the_running_elements_own_padding()
    {
        // Task A: the element's OWN padding-* longhands are captured (the `padding` shorthand is cascade-
        // expanded). They inset the element's text in the margin box.
        var ctx = await CollectAsync(
            "<div class='rh'>AB</div>",
            ".rh { position: running(rh); padding: 4px }");

        Assert.Contains(ctx.RunningElementStyles!["rh"], kv => kv.Key == "padding-top");
        Assert.Contains(ctx.RunningElementStyles!["rh"], kv => kv.Key == "padding-left");
    }

    [Fact]
    public async Task Collect_does_not_capture_an_ancestor_padding_for_a_running_element()
    {
        // padding is NON-inherited → self-only: an ancestor's padding must NOT be captured for the element.
        var ctx = await CollectAsync(
            "<div class='section'><div class='rh'>AB</div></div>",
            ".section { padding: 4px } .rh { position: running(rh) }");

        Assert.DoesNotContain(ctx.RunningElementStyles!["rh"], kv => kv.Key == "padding-top");
    }

    [Fact]
    public async Task Collect_captures_an_inherited_ancestor_text_align_for_a_running_element()
    {
        // Task B: text-align IS inherited, so an ancestor's text-align reaches the running element (walked
        // like color/font). It aligns the element's line within the margin box.
        var ctx = await CollectAsync(
            "<div class='section'><div class='rh'>AB</div></div>",
            ".section { text-align: right } .rh { position: running(rh) }");

        Assert.Contains(ctx.RunningElementStylesFirst!["rh"], kv => kv.Key == "text-align");
    }

    [Fact]
    public async Task Collect_resolves_an_inherit_text_align_to_the_ancestor_value()
    {
        // Review P2: a running element's OWN `text-align: inherit` resolves to the DOM ancestor's value (the
        // walk continues past inherit/unset/revert per CSS Cascade L5 §7), so the captured value is the
        // ancestor's concrete `right` — NOT the literal `inherit`, which would map to no alignment factor
        // downstream and wrongly fall back to the margin box's name-derived default.
        var ctx = await CollectAsync(
            "<div class='section'><div class='rh'>AB</div></div>",
            ".section { text-align: right } .rh { position: running(rh); text-align: inherit }");

        Assert.Contains(ctx.RunningElementStylesFirst!["rh"], kv => kv.Key == "text-align" && kv.Value == "right");
    }

    // ---- nested BLOCK children — stacked lines (Task 23 first cut) ----

    [Fact]
    public async Task Collect_stacks_a_running_elements_block_children_as_newline_separated_lines()
    {
        // Task 23 nested BLOCK children first cut: a running element with BLOCK-level children yields one
        // U+000A-separated LINE per block child (the page-margin painter stacks them as `white-space: pre`),
        // instead of concatenating their text onto one line ("AlphaBeta").
        var ctx = await CollectAsync(
            "<div class='rh'><div>Alpha</div><div>Beta</div></div>",
            ".rh { position: running(rh) }");

        Assert.Equal("Alpha\nBeta", ctx.RunningElementsFirst!["rh"]);
    }

    [Fact]
    public async Task Collect_keeps_a_plain_inline_running_header_on_one_line()
    {
        // No block-level child (text + an inline <span>) → flat single-line text, NO U+000A — byte-identical
        // to the pre-first-cut behavior (the painter keeps its single-line `nowrap` path).
        var ctx = await CollectAsync(
            "<div class='rh'>Alpha <span>Beta</span></div>",
            ".rh { position: running(rh) }");

        Assert.Equal("Alpha Beta", ctx.RunningElementsFirst!["rh"]);
        Assert.DoesNotContain('\n', ctx.RunningElementsFirst!["rh"]);
    }

    [Fact]
    public async Task Collect_drops_inter_block_whitespace_when_stacking_block_children()
    {
        // Indented real-world HTML has whitespace text nodes between the block children; those collapse away
        // (not spurious blank stacked lines), so two indented block divs still yield exactly two lines.
        var ctx = await CollectAsync(
            "<div class='rh'>\n  <div>Alpha</div>\n  <div>Beta</div>\n</div>",
            ".rh { position: running(rh) }");

        Assert.Equal("Alpha\nBeta", ctx.RunningElementsFirst!["rh"]);
    }
}
