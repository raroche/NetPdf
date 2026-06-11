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
