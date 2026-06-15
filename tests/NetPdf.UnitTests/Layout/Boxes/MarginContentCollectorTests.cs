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
        // Preprocess-aware adapt (content-pseudo cycle) — the production pipeline runs the
        // CssPreprocessor recovery, so the harness must too (an AngleSharp-dropped
        // `string-set: … content(before)` declaration only reaches the cascade via recovery).
        var sheet = CssParserAdapter.Adapt(parser.ParseStyleSheet(css),
            NetPdf.Css.Parser.Preprocessing.CssPreprocessor.Process(css), href: null,
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
    public async Task CollectPerPage_carries_a_named_string_forward_until_re_set()
    {
        // Cycle 5 — cross-page carry-forward. #a sets title="A" (laid out on page 0), #c sets
        // title="C" (page 2); page 1 sets nothing. Per page the named string is the value CURRENT
        // on that page: page 0 = "A", page 1 = the carried "A", page 2 = "C" (re-set).
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        const string css = "#a { string-set: title \"A\" } #c { string-set: title \"C\" }";
        const string html =
            "<html><body><div id='a'></div><div id='b'></div><div id='c'></div></body></html>";
        var doc = await ctx.OpenAsync(req => req.Content(html));
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = CssParserAdapter.Adapt(parser.ParseStyleSheet(css),
            NetPdf.Css.Parser.Preprocessing.CssPreprocessor.Process(css), href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
        var cascade = CascadeResolver.Resolve(
            doc, ImmutableArray.Create(sheet), CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);

        var elementToPage = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#a")!] = 0,
            [doc.QuerySelector("#c")!] = 2,
        };
        var pages = MarginContentCollector.CollectPerPage(
            doc.DocumentElement!, resolved, elementToPage, pageCount: 3);

        Assert.Equal(3, pages.Length);
        // string(title) default reads the FIRST-on-page value, carried when the page sets nothing.
        Assert.Equal("A", pages[0].NamedStringsFirst?["title"]);   // set on page 0
        Assert.Equal("A", pages[1].NamedStringsFirst?["title"]);   // carried forward (page 1 sets nothing)
        Assert.Equal("C", pages[2].NamedStringsFirst?["title"]);   // re-set on page 2
        Assert.Equal("C", pages[2].NamedStrings?["title"]);        // the exit (last) value too
    }

    /// <summary>Parse <paramref name="html"/> + <paramref name="css"/> through the real cascade + var
    /// resolver (mirroring the render pipeline), returning the document + resolved cascade so a test can
    /// build its own element→page map for <see cref="MarginContentCollector.CollectPerPage"/>.</summary>
    private static async Task<(IDocument Doc, ResolvedCascadeResult Cascade)> ResolveAsync(
        string html, string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var doc = await ctx.OpenAsync(req => req.Content(html));
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = CssParserAdapter.Adapt(parser.ParseStyleSheet(css),
            NetPdf.Css.Parser.Preprocessing.CssPreprocessor.Process(css), href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
        var cascade = CascadeResolver.Resolve(doc, ImmutableArray.Create(sheet), CssMediaContext.DefaultPrint);
        return (doc, VarResolver.Resolve(cascade, doc));
    }

    [Fact]
    public async Task CollectPerPage_distinguishes_first_and_last_assignment_on_one_page()
    {
        // PR #177 review P3 — exact GCPM first/last semantics WITHIN one page. #a sets title="A" then
        // #b sets title="B", BOTH on page 0: string(title)/first reads the FIRST ("A"), string(title,
        // last) reads the LAST ("B").
        var (doc, resolved) = await ResolveAsync(
            "<html><body><div id='a'></div><div id='b'></div></body></html>",
            "#a { string-set: title \"A\" } #b { string-set: title \"B\" }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#a")!] = 0,
            [doc.QuerySelector("#b")!] = 0,
        };
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 1);

        Assert.Equal("A", pages[0].NamedStringsFirst?["title"]);   // string(title) / string(title, first)
        Assert.Equal("B", pages[0].NamedStrings?["title"]);        // string(title, last)
    }

    [Fact]
    public async Task CollectPerPage_buckets_an_inline_string_set_to_its_rendered_ancestors_page()
    {
        // PR #177 review P2 — GCPM string-set applies to INLINE elements too. A <span> setter produces
        // no fragment of its own, so the pipeline's element→page map carries only the block <p> (here
        // on page 1). ResolvePage walks span → <p>, so the span's title lands on page 1 — not dropped,
        // not mis-bucketed to page 0.
        var (doc, resolved) = await ResolveAsync(
            "<html><body><p id='p'><span id='s'>x</span></p></body></html>",
            "#s { string-set: title \"B\" }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#p")!] = 1,   // only the block <p> rendered (the span has no fragment)
        };
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 2);

        Assert.Null(pages[0].NamedStringsFirst);                   // nothing set on page 0
        Assert.Equal("B", pages[1].NamedStringsFirst?["title"]);   // the inline setter, via its <p> ancestor's page
    }

    [Fact]
    public async Task CollectPerPage_buckets_running_elements_per_page_cycle5b()
    {
        // Cycle 5b — cross-page element() running content. Two `position: running(rh)` headings, each in
        // its own section: section 1 (laid out on page 0) holds rh "A", section 2 (page 1) holds rh "B".
        // A running element is removed from flow (no fragment of its own), so its page comes from its
        // nearest rendered ANCESTOR (the section). element(rh)/first must read the FIRST running element ON
        // that page — "A" on page 0, "B" on page 1 — not the whole-document first ("A") on every page.
        var (doc, resolved) = await ResolveAsync(
            "<html><body><section id='s1'><div class='rh'>A</div></section>" +
            "<section id='s2'><div class='rh'>B</div></section></body></html>",
            ".rh { position: running(rh) }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#s1")!] = 0,   // section 1 rendered on page 0
            [doc.QuerySelector("#s2")!] = 1,   // section 2 on page 1
        };
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 2);

        Assert.Equal(2, pages.Length);
        Assert.Equal("A", pages[0].RunningElementsFirst?["rh"]);   // element(rh) / first on page 0
        Assert.Equal("A", pages[0].RunningElements?["rh"]);        // element(rh, last) on page 0
        Assert.Equal("B", pages[1].RunningElementsFirst?["rh"]);   // re-set on page 1
        Assert.Equal("B", pages[1].RunningElements?["rh"]);
    }

    [Fact]
    public async Task CollectPerPage_carries_a_running_element_forward_until_re_set()
    {
        // Cycle 5b — carry-forward (GCPM L3), mirroring the named-string carry-forward. The rh header set
        // on page 0 persists onto page 1 (which has no running element) and is re-set on page 2.
        var (doc, resolved) = await ResolveAsync(
            "<html><body><section id='s1'><div class='rh'>A</div></section>" +
            "<section id='s2'></section>" +
            "<section id='s3'><div class='rh'>C</div></section></body></html>",
            ".rh { position: running(rh) }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#s1")!] = 0,
            [doc.QuerySelector("#s3")!] = 2,
        };
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 3);

        Assert.Equal("A", pages[0].RunningElementsFirst?["rh"]);   // set on page 0
        Assert.Equal("A", pages[1].RunningElementsFirst?["rh"]);   // carried (page 1 has no running element)
        Assert.Equal("C", pages[2].RunningElementsFirst?["rh"]);   // re-set on page 2
    }

    [Fact]
    public async Task CollectPerPage_distinguishes_first_and_last_running_element_on_one_page()
    {
        // Cycle 5b — first/last WITHIN one page. Two running elements sharing `rh` on page 0:
        // element(rh)/first reads the FIRST ("A"), element(rh, last) reads the LAST ("B").
        var (doc, resolved) = await ResolveAsync(
            "<html><body><section id='s1'><div class='rh'>A</div><div class='rh'>B</div></section></body></html>",
            ".rh { position: running(rh) }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#s1")!] = 0,
        };
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 2);

        Assert.Equal("A", pages[0].RunningElementsFirst?["rh"]);   // element(rh) / first
        Assert.Equal("B", pages[0].RunningElements?["rh"]);        // element(rh, last)
    }

    [Fact]
    public async Task CollectPerPage_keeps_running_element_style_in_lockstep_with_text_per_page()
    {
        // Cycle 5b — the whole occurrence (text + own style + segments + containers) buckets TOGETHER, so
        // per page the style tracks the text. Page 0's rh is the red "A", page 1's is the unstyled "B": the
        // style dictionaries must carry A's colour on page 0 and B's EMPTY style on page 1 (not A's stale
        // red) — the PR #151 lockstep, now per page.
        var (doc, resolved) = await ResolveAsync(
            "<html><body><section id='s1'><div class='r1'>A</div></section>" +
            "<section id='s2'><div class='r2'>B</div></section></body></html>",
            ".r1 { position: running(rh); color: #ff0000 } .r2 { position: running(rh) }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>
        {
            [doc.QuerySelector("#s1")!] = 0,
            [doc.QuerySelector("#s2")!] = 1,
        };
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 2);

        Assert.Equal("A", pages[0].RunningElementsFirst?["rh"]);
        Assert.Contains(pages[0].RunningElementStylesFirst!["rh"], kv => kv.Key == "color");  // A's red
        Assert.Equal("B", pages[1].RunningElementsFirst?["rh"]);
        Assert.Empty(pages[1].RunningElementStylesFirst!["rh"]);                               // B unstyled, not A's red
    }

    [Fact]
    public async Task CollectPerPage_single_page_running_element_is_the_whole_document_value()
    {
        // Cycle 5b — single-page short-circuit. With one page, the whole-document first/last IS the page-0
        // value, so element() resolves to it directly (byte-identical to the pre-cross-page path). Pins that
        // the common single-page running header keeps working through the new per-page path.
        var (doc, resolved) = await ResolveAsync(
            "<html><body><div class='rh'>A</div></body></html>",
            ".rh { position: running(rh) }");
        var map = new System.Collections.Generic.Dictionary<IElement, int>();   // body-only running content
        var pages = MarginContentCollector.CollectPerPage(doc.DocumentElement!, resolved, map, pageCount: 1);

        Assert.Single(pages);
        Assert.Equal("A", pages[0].RunningElementsFirst?["rh"]);   // resolved even with an empty element→page map
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

    [Fact]
    public async Task Collect_caps_total_running_content_across_many_block_children()
    {
        // Review P1 / Copilot: the running-element content is bounded to MaxRunningTextChars (64 KiB) TOTAL,
        // NOT per block child — a SINGLE budget is shared, so five 100 KiB block children can't store
        // 5 × 64 KiB. (Before the fix each block read up to the cap independently and they were joined.)
        var block = "<div>" + new string('x', 100_000) + "</div>";
        var ctx = await CollectAsync(
            "<div class='rh'>" + block + block + block + block + block + "</div>",
            ".rh { position: running(rh) }");

        Assert.True(ctx.RunningElementsFirst!["rh"].Length <= 64 * 1024,
            $"expected ≤ 64 KiB TOTAL across the block children; got {ctx.RunningElementsFirst!["rh"].Length}");
    }

    // ---- display classification via the production DisplayMapper (review P3) ----

    [Theory]
    [InlineData("block")]
    [InlineData("flex")]
    [InlineData("grid")]
    [InlineData("table")]
    [InlineData("list-item")]
    public async Task Collect_stacks_a_block_level_display_child(string display)
    {
        // Review P3: every BLOCK-level outer display (incl. flex/grid/table/list-item — set on a normally-
        // inline <span>) forces a stacked line, via the production DisplayMapper + the shared
        // BoxKindFacts.IsBlockLevelOuter (aligned with the box tree).
        var ctx = await CollectAsync(
            "<div class='rh'><span class='c'>AA</span><span class='c'>BB</span></div>",
            ".rh { position: running(rh) } .c { display: " + display + " }");

        Assert.Equal("AA\nBB", ctx.RunningElementsFirst!["rh"]);
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("inline-block")]
    [InlineData("inline-flex")]
    [InlineData("inline-grid")]
    [InlineData("inline-table")]
    [InlineData("contents")]
    [InlineData("ruby")]   // unsupported display → NOT defaulted to block (the pre-fix bug)
    public async Task Collect_does_not_stack_a_non_block_level_child(string display)
    {
        // Review P3: an inline-level / contents / unsupported display child does NOT force a stacked line
        // (no U+000A) — only block-level outers do. Fixes the old classifier that defaulted unknowns to block.
        var ctx = await CollectAsync(
            "<div class='rh'><span class='c'>AA</span><span class='c'>BB</span></div>",
            ".rh { position: running(rh) } .c { display: " + display + " }");

        Assert.DoesNotContain('\n', ctx.RunningElementsFirst!["rh"]);
    }

    [Fact]
    public async Task Collect_excludes_a_display_none_child_from_running_content()
    {
        // Review P3: a `display: none` child generates no box — its text is NOT included in the running
        // content (only the visible block child contributes).
        var ctx = await CollectAsync(
            "<div class='rh'><div>AA</div><div class='hidden'>SECRET</div></div>",
            ".rh { position: running(rh) } .hidden { display: none }");

        Assert.Equal("AA", ctx.RunningElementsFirst!["rh"]);
    }

    // ---- content(before|after) (content-pseudo cycle) ----

    [Fact]
    public async Task StringSet_content_before_resolves_the_pseudo_content()
    {
        // GCPM §2.4 — content(before) pulls the host's ::before pseudo content, composing with
        // content() (the element's own text) in one content-list.
        var ctx = await CollectAsync(
            "<h1 id='h'>Intro</h1>",
            "h1 { string-set: title content(before) content() } h1::before { content: 'Ch. ' }");

        Assert.Equal("Ch. Intro", ctx.NamedStringsFirst!["title"]);
    }

    [Fact]
    public async Task StringSet_content_after_resolves_attr_in_the_pseudo_content()
    {
        // The pseudo's own content-list resolves fully (literals + attr()).
        var ctx = await CollectAsync(
            "<h1 id='h' data-mark='*'>Intro</h1>",
            "h1 { string-set: title content() content(after) } h1::after { content: attr(data-mark) }");

        Assert.Equal("Intro*", ctx.NamedStringsFirst!["title"]);
    }

    [Fact]
    public async Task StringSet_content_before_with_no_pseudo_resolves_empty()
    {
        // A missing ::before (or content: none/normal) contributes the EMPTY string — the
        // assignment still succeeds (GCPM treats an absent pseudo as empty).
        var ctx = await CollectAsync(
            "<h1 id='h'>Intro</h1>",
            "h1 { string-set: title content(before) content() }");

        Assert.Equal("Intro", ctx.NamedStringsFirst!["title"]);
    }

    [Fact]
    public async Task StringSet_content_first_letter_stays_unsupported()
    {
        // The typographic targets keep the unsupported-bail path — the assignment is dropped.
        var ctx = await CollectAsync(
            "<h1 id='h'>Intro</h1>",
            "h1 { string-set: title content(first-letter) }");

        Assert.True(ctx.NamedStringsFirst is null || !ctx.NamedStringsFirst.ContainsKey("title"));
    }

    [Fact]
    public async Task Collect_segment_margins_capture_absolute_px()
    {
        // Segment-margins cycle — the leaf's own vertical margins capture in used px. (The
        // post-PR-#163 review P3 unit normalization in CaptureSegmentMargins matches
        // SegmentLineHeightPx, but an UPPERCASE-unit declaration can't be exercised through this
        // harness: AngleSharp.Css 1.0.0-beta.144 DROPS `margin-top: 16PX` entirely before the
        // cascade — see the facade canary pin in HtmlPdfConvertTests.)
        var ctx = await CollectAsync(
            "<div class='rh'><div>One</div><div class='gap'>Two</div></div>",
            ".rh { position: running(rh) } .gap { margin-top: 16px; margin-bottom: 8px }");

        var segs = ctx.RunningElementSegmentsFirst!["rh"];
        Assert.Equal(2, segs.Count);
        Assert.Equal(16.0, segs[1].MarginTopPx, 3);
        Assert.Equal(8.0, segs[1].MarginBottomPx, 3);
    }

    // ---- container vertical padding / §4.3-gated borders (container-vpad cycle) ----

    [Fact]
    public async Task Collect_container_vertical_padding_extends_the_band_inside()
    {
        // container-vpad cycle — a decorated container's vertical padding is the part of the
        // boundary gap INSIDE its band (Leading/TrailingInsidePx), so the band extends over its
        // padding strip: padding:10px → 10px leading + 10px trailing (the leaf's own gap is 0).
        var ctx = await CollectAsync(
            "<div class='rh'><div class='c'><div>AB</div></div></div>",
            ".rh { position: running(rh) } .c { background-color: red; padding: 10px }");

        var rec = Assert.Single(ctx.RunningElementContainersFirst!["rh"]);
        Assert.Equal(10.0, rec.LeadingInsidePx, 3);
        Assert.Equal(10.0, rec.TrailingInsidePx, 3);
    }

    [Theory]
    [InlineData("2px solid", 2.0)]     // an absolute width paints as declared
    [InlineData("thin solid", 1.0)]    // §4.3 keyword map
    [InlineData("medium solid", 3.0)]
    [InlineData("thick solid", 5.0)]
    [InlineData("solid", 3.0)]         // a painting edge with no declared width → medium default
    [InlineData("0 solid", 0.0)]       // EXPLICIT zero → 0, not the medium default (PR #169 review P1)
    [InlineData("0px solid", 0.0)]     // explicit 0px → 0
    [InlineData("10px none", 0.0)]     // none → 0 even with a width
    [InlineData("10px hidden", 0.0)]   // hidden → 0
    public async Task Collect_container_border_top_width_is_gated_by_style(string borderTop, double expectedPx)
    {
        // CaptureSegmentBorderWidths' §4.3 gate: a border-top edge contributes its width to the
        // band's leading inside extent only when its style PAINTS; none/hidden → 0; an UNSET width
        // on a painting edge defaults to medium (3px), but an EXPLICIT 0 stays 0 (review P1).
        var ctx = await CollectAsync(
            "<div class='rh'><div class='c'><div>AB</div></div></div>",
            ".rh { position: running(rh) } .c { border-top: " + borderTop + " }");

        var rec = Assert.Single(ctx.RunningElementContainersFirst!["rh"]);
        Assert.Equal(expectedPx, rec.LeadingInsidePx, 3);
    }

    [Fact]
    public async Task Collect_container_vertical_padding_adds_to_the_boundary_gap_not_collapses()
    {
        // container-vpad cycle — vertical padding BLOCKS the §8.3.1 margin collapse and ADDS: a
        // container with margin-top:20px collapses its first line's gap to 20px (Leading 0 — an
        // empty padding strip); adding padding-top:10px blocks the collapse → the gap becomes
        // 20+10 = 30px (the padding ADDED, not collapsed away) and Leading = 10px (band extends).
        async Task<(double GapPx, double LeadingPx)> Probe(string cRule)
        {
            var ctx = await CollectAsync(
                "<div class='rh'><div class='c'><div>AB</div></div></div>",
                ".rh { position: running(rh) } .c { background-color: red; " + cRule + " }");
            var seg = ctx.RunningElementSegmentsFirst!["rh"][0];
            var rec = Assert.Single(ctx.RunningElementContainersFirst!["rh"]);
            return (seg.MarginTopPx, rec.LeadingInsidePx);
        }
        var collapsed = await Probe("margin-top: 20px");
        var added = await Probe("margin-top: 20px; padding-top: 10px");
        Assert.Equal(20.0, collapsed.GapPx, 3);
        Assert.Equal(0.0, collapsed.LeadingPx, 3);
        Assert.Equal(30.0, added.GapPx, 3);
        Assert.Equal(10.0, added.LeadingPx, 3);
    }

    // ---- per-line SEGMENTS (segment-style cycle) ----

    [Fact]
    public async Task Collect_records_one_segment_per_leaf_block_with_its_own_style()
    {
        // Each stacked line carries the style of the leaf element that produced it: the h1 line gets
        // its own font-size PLUS the running root's inherited color (the ancestor walk makes each
        // record self-contained); the plain div line gets the color only.
        var ctx = await CollectAsync(
            "<div class='rh'><h1>Title</h1><div>Sub</div></div>",
            ".rh { position: running(rh); color: #112233 } .rh h1 { font-size: 32px }");

        var segs = ctx.RunningElementSegmentsFirst!["rh"];
        Assert.Equal(2, segs.Count);
        Assert.Equal("Title", segs[0].Text);
        Assert.Contains(segs[0].OwnStyle, kv => kv.Key == "font-size" && kv.Value == "32px");
        Assert.Contains(segs[0].OwnStyle, kv => kv.Key == "color");
        Assert.Equal("Sub", segs[1].Text);
        Assert.DoesNotContain(segs[1].OwnStyle, kv => kv.Key == "font-size");
        Assert.Contains(segs[1].OwnStyle, kv => kv.Key == "color");
    }

    [Fact]
    public async Task Collect_records_a_single_segment_for_a_flat_header()
    {
        // The common flat header: ONE segment, its text equal to the joined running text — the
        // painter's single-run path stays byte-identical.
        var ctx = await CollectAsync(
            "<div class='rh'>Chapter One</div>",
            ".rh { position: running(rh); color: #112233 }");

        var segs = ctx.RunningElementSegmentsFirst!["rh"];
        var seg = Assert.Single(segs);
        Assert.Equal(ctx.RunningElementsFirst!["rh"], seg.Text);
        Assert.Contains(seg.OwnStyle, kv => kv.Key == "color");
    }

    [Fact]
    public async Task Collect_records_segments_in_lockstep_with_the_occurrence()
    {
        // First/last segments mirror the first/last text (the PR #151 lockstep rule extended): the
        // first occurrence's segments describe ITS lines, the last's describe the last's.
        var ctx = await CollectAsync(
            "<div class='rh'><div>A1</div><div>A2</div></div><div class='rh'><div>B1</div></div>",
            ".rh { position: running(rh) }");

        Assert.Equal(2, ctx.RunningElementSegmentsFirst!["rh"].Count);
        Assert.Equal("A1", ctx.RunningElementSegmentsFirst!["rh"][0].Text);
        Assert.Equal("B1", Assert.Single(ctx.RunningElementSegments!["rh"]).Text);
    }

    [Fact]
    public async Task Collect_segments_concatenate_to_the_capped_text_at_the_budget_boundary()
    {
        // Post-PR-#160 review P2 — the recursive budget reserves the parent's pending '\n'
        // separator. The first block consumes all but 2 chars of the 64 KiB budget; the nested
        // second block then has 1 char of real room (separator + 1). Pre-fix the nested call
        // recorded 2 chars of segment text while the joined string could only fit 1 — the
        // segments must concatenate to EXACTLY the stored capped text.
        var first = new string('A', (64 * 1024) - 2);
        var ctx = await CollectAsync(
            "<div class='rh'><div>" + first + "</div><div><div>XYZ</div></div></div>",
            ".rh { position: running(rh) }");

        var segs = ctx.RunningElementSegmentsFirst!["rh"];
        var joined = new System.Text.StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            if (i > 0) joined.Append('\n');
            joined.Append(segs[i].Text);
        }
        Assert.Equal(ctx.RunningElementsFirst!["rh"], joined.ToString());
        Assert.Equal(64 * 1024, joined.Length);   // budget exactly exhausted: 65534 + '\n' + "X"
    }

    [Fact]
    public async Task Collect_records_nested_recursion_segments_per_nested_block()
    {
        // Deep recursion: each NESTED block is its own segment, styled by ITS element — the inner
        // styled div's line carries the override, the outer sibling keeps the root's inheritance.
        var ctx = await CollectAsync(
            "<div class='rh'><div><div class='big'>A</div><div>B</div></div><div>C</div></div>",
            ".rh { position: running(rh) } .big { font-size: 30px }");

        var segs = ctx.RunningElementSegmentsFirst!["rh"];
        Assert.Equal(3, segs.Count);
        Assert.Equal("A", segs[0].Text);
        Assert.Contains(segs[0].OwnStyle, kv => kv.Key == "font-size" && kv.Value == "30px");
        Assert.Equal("B", segs[1].Text);
        Assert.DoesNotContain(segs[1].OwnStyle, kv => kv.Key == "font-size");
        Assert.Equal("C", segs[2].Text);
    }
}
