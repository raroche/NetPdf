// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading.Tasks;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>CSS Fragmentation L3 control — break-before / break-after (+ legacy
/// page-break-* aliases) forced breaks, break-inside / break-*:avoid, and
/// orphans / widows registration + cascade→resolver wiring. The end-to-end
/// page-placement (forced breaks split fitting ancestors) is covered by the
/// W3cConformance Fragmentation cases (frag-break-before-page, frag-break-after-page,
/// frag-page-break-before-always); these are the unit-level parse + reader + wiring tests.</summary>
public sealed class FragmentationControlTests : System.IDisposable
{
    // PR #207 Copilot review — Phase2Result.Dispose() pool-returns the box-owned
    // ComputedStyles, but a test reads box.Style AFTER parsing, so the result can't be
    // disposed mid-test. Track parsed results + dispose them all in Dispose() (xUnit runs it
    // after each test, when nothing else holds the boxes) — no leak, no re-rental race.
    private readonly System.Collections.Generic.List<System.IDisposable> _parsed = new();

    public void Dispose()
    {
        foreach (var d in _parsed) d.Dispose();
    }

    [Fact]
    public async Task Break_before_page_parses_to_a_keyword_slot_and_forces()
    {
        var b = await ParseBox("<div id='x' style='break-before:page'></div>");
        var slot = b.Style.Get(PropertyId.BreakBefore);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
        Assert.Equal(5, slot.AsKeyword()); // page (KeywordResolver index)
        Assert.True(b.Style.ForcesPageBreakBefore());
        Assert.False(b.Style.ForcesPageBreakAfter());
    }

    [Fact]
    public async Task Break_after_page_forces()
    {
        var b = await ParseBox("<div id='x' style='break-after:page'></div>");
        Assert.True(b.Style.ForcesPageBreakAfter());
        Assert.False(b.Style.ForcesPageBreakBefore());
    }

    [Theory]
    [InlineData("break-before:left")]
    [InlineData("break-before:right")]
    public async Task Break_before_left_right_force_a_page_break(string css)
    {
        // left/right force a page break; the blank-page side PARITY (actually landing on a
        // left/right page) now ships — see the Break_before_{left,right,…} page-count tests below.
        var b = await ParseBox($"<div id='x' style='{css}'></div>");
        Assert.True(b.Style.ForcesPageBreakBefore());
    }

    [Theory]
    [InlineData("break-before:recto", 8)]
    [InlineData("break-before:verso", 9)]
    [InlineData("break-before:all", 12)]
    public async Task Break_before_recto_verso_all_parse_and_force(string css, int expectedKeyword)
    {
        // fragmentation-control-residuals (narrowed) — AngleSharp.Css 1.0.0-beta.144 DROPS the
        // recto / verso / all forced-break values; the CssPreprocessor now recovers them so they
        // reach the cascade (KeywordResolver index) + the break reader honors them as a forced page
        // break. recto / verso now ALSO land on the correct page side (blank-page insertion) — see
        // the Break_before_{recto,verso}_… page-count tests below.
        var b = await ParseBox($"<div id='x' style='{css}'></div>");
        var slot = b.Style.Get(PropertyId.BreakBefore);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
        Assert.Equal(expectedKeyword, slot.AsKeyword());
        Assert.True(b.Style.ForcesPageBreakBefore());
        Assert.False(b.Style.ForcesPageBreakAfter());
    }

    [Fact]
    public async Task Break_after_verso_parses_and_forces()
    {
        // The recovery covers break-after too (the value-gated entry matches both longhands).
        var b = await ParseBox("<div id='x' style='break-after:verso'></div>");
        Assert.True(b.Style.ForcesPageBreakAfter());
        Assert.False(b.Style.ForcesPageBreakBefore());
    }

    [Theory]
    [InlineData("break-before:column")] // a column break, not a page break
    [InlineData("break-before:region")]
    [InlineData("break-before:avoid")]
    [InlineData("break-before:auto")]
    public async Task Break_before_non_page_values_do_not_force_a_page_break(string css)
    {
        var b = await ParseBox($"<div id='x' style='{css}'></div>");
        Assert.False(b.Style.ForcesPageBreakBefore());
    }

    [Fact]
    public async Task Page_break_before_always_is_a_legacy_alias_for_page()
    {
        var b = await ParseBox("<div id='x' style='page-break-before:always'></div>");
        Assert.True(b.Style.ForcesPageBreakBefore());
    }

    [Fact]
    public async Task Modern_break_before_avoid_overrides_legacy_page_break_always()
    {
        // A non-auto modern value is authoritative: break-before:avoid wins over the
        // legacy page-break-before:always, so no forced break + the avoid IS reported.
        var b = await ParseBox(
            "<div id='x' style='break-before:avoid;page-break-before:always'></div>");
        Assert.False(b.Style.ForcesPageBreakBefore());
        Assert.True(b.Style.AvoidsPageBreakBefore());
    }

    [Fact]
    public async Task Break_inside_avoid_is_reported()
    {
        var b = await ParseBox("<div id='x' style='break-inside:avoid'></div>");
        Assert.True(b.Style.AvoidsBreakInside());

        var auto = await ParseBox("<div id='x' style='break-inside:auto'></div>");
        Assert.False(auto.Style.AvoidsBreakInside());

        var col = await ParseBox("<div id='x' style='break-inside:avoid-column'></div>");
        Assert.False(col.Style.AvoidsBreakInside()); // avoid-column targets columns, not pages
    }

    [Fact]
    public async Task Orphans_and_widows_parse_and_default_to_2()
    {
        var set = await ParseBox("<div id='x' style='orphans:4;widows:3'></div>");
        Assert.Equal(4, set.Style.ReadOrphansOrDefault());
        Assert.Equal(3, set.Style.ReadWidowsOrDefault());

        var unset = await ParseBox("<div id='x'></div>");
        Assert.Equal(2, unset.Style.ReadOrphansOrDefault()); // CSS Fragmentation §4.2 initial
        Assert.Equal(2, unset.Style.ReadWidowsOrDefault());
    }

    [Fact]
    public async Task Orphans_widows_drive_the_break_resolver()
    {
        // The cascade→resolver wiring: the CSS-read values feed BreakResolver's
        // OrphansRequired / WidowsRequired (PdfRenderPipeline reads them off the body box).
        var b = await ParseBox("<div id='x' style='orphans:5;widows:4'></div>");
        using var resolver = new BreakResolver(
            orphansRequired: b.Style.ReadOrphansOrDefault(),
            widowsRequired: b.Style.ReadWidowsOrDefault());
        Assert.Equal(5, resolver.OrphansRequired);
        Assert.Equal(4, resolver.WidowsRequired);
    }

    [Fact]
    public async Task Orphans_widows_on_body_read_from_the_body_box_not_the_synthetic_root()
    {
        // PR #207 review [P2] — BoxBuilder roots the tree at a SYNTHETIC box carrying the
        // initial default 2; authored body orphans/widows live on the body box (inherited).
        // Reading the synthetic root (the pre-fix bug) always returns 2; the pipeline now reads
        // the body box. This guards that resolution: synthetic root == 2, body == authored.
        var html = "<!doctype html><html><body style='orphans:4;widows:3'><p>x</p></body></html>";
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new NetPdf.HtmlPdfOptions());
        _parsed.Add(result);
        Assert.Equal(2, result.BoxRoot.Style.ReadOrphansOrDefault());   // synthetic root → default
        Assert.Equal(2, result.BoxRoot.Style.ReadWidowsOrDefault());
        var body = FindByLocalName(result.BoxRoot, "body");
        Assert.NotNull(body);
        Assert.Equal(4, body!.Style.ReadOrphansOrDefault());            // authored, what the pipeline reads
        Assert.Equal(3, body.Style.ReadWidowsOrDefault());
    }

    // ── End-to-end forced breaks on TEXT-bearing blocks (real shaper + pipeline) ──
    // The W3cConformance harness uses shaperResolver: null + empty fixed-height divs, so it
    // can't exercise inline-only (text) blocks. These drive HtmlPdf.ConvertDetailed (the real
    // PdfRenderPipeline + shaper) and assert PDF PAGE COUNT — proving forced breaks reach
    // text-bearing blocks through the production path (PR #207 review [P1] / [P2#4]).

    [Fact]
    public void Two_text_blocks_without_a_break_stay_on_one_page()
    {
        // Control: both small text blocks fit one (Letter-sized) page.
        Assert.Equal(1, Pages("<div>first</div><div>second</div>"));
    }

    [Fact]
    public void Forced_break_before_a_text_block_creates_a_new_page()
    {
        // PR #207 review [P1] — break-before:page on a TEXT div forces page 2 even though
        // both fit one page. Pre-fix the inline-only dispatch ignored the metadata.
        Assert.Equal(2, Pages("<div>first</div><div style='break-before:page'>second</div>"));
    }

    [Fact]
    public void Forced_break_after_a_text_block_creates_a_new_page()
    {
        // break-after:page on the FIRST text div pushes the next sibling to page 2.
        Assert.Equal(2, Pages("<div style='break-after:page'>first</div><div>second</div>"));
    }

    // CSS Page L3 §3.4.1 — left/right/recto/verso land the resumed content on a page of the named
    // PARITY, inserting a blank `@page :blank` when needed. Page 1 is a recto (right-hand) page, so
    // odd pages = recto/right, even = verso/left (LTR). "first" sits on page 1 (recto).

    [Fact]
    public void Break_before_left_lands_on_a_verso_page_without_a_blank()
    {
        // `break-before:left` → "second" wants a LEFT (verso / even) page; the next page (2) IS
        // verso, so no blank is inserted → 2 pages (like a plain `page` break here).
        Assert.Equal(2, Pages("<div>first</div><div style='break-before:left'>second</div>"));
    }

    [Fact]
    public void Break_before_verso_lands_on_a_verso_page_without_a_blank()
    {
        // `verso` = left-hand = even page; same as `left` in LTR → 2 pages.
        Assert.Equal(2, Pages("<div>first</div><div style='break-before:verso'>second</div>"));
    }

    [Fact]
    public void Break_before_right_inserts_a_blank_to_land_on_a_recto_page()
    {
        // `break-before:right` → "second" wants a RIGHT (recto / odd) page; the next page (2) is
        // verso (WRONG parity), so a blank `@page :blank` is inserted as page 2 and "second" lands
        // on page 3 (recto) → 3 pages. This is the blank-page side parity that was the residual.
        Assert.Equal(3, Pages("<div>first</div><div style='break-before:right'>second</div>"));
    }

    [Fact]
    public void Break_before_recto_inserts_a_blank_to_land_on_a_recto_page()
    {
        // `recto` = right-hand = odd page; same as `right` in LTR → blank inserted → 3 pages.
        Assert.Equal(3, Pages("<div>first</div><div style='break-before:recto'>second</div>"));
    }

    [Fact]
    public void Break_after_right_inserts_a_blank_for_the_next_sibling()
    {
        // `break-after:right` on "first" → the next sibling must land on a recto (odd) page; page 2
        // is verso, so a blank is inserted and "second" lands on page 3 → 3 pages.
        Assert.Equal(3, Pages("<div style='break-after:right'>first</div><div>second</div>"));
    }

    [Fact]
    public void Break_before_page_without_parity_does_not_insert_a_blank()
    {
        // Control — `break-before:page` carries no parity, so no blank is ever inserted → 2 pages.
        Assert.Equal(2, Pages("<div>first</div><div style='break-before:page'>second</div>"));
    }

    [Fact]
    public void Break_after_right_parity_survives_a_parityless_break_before_page_on_the_next_block()
    {
        // Copilot review (PR #218) — `ResolveChildBreakMetadata` must NOT drop the prior sibling's
        // `break-after:right` parity just because the next block ALSO forces a (parity-less)
        // `break-before:page`. Both target the same break; the non-Any parity (right = recto) wins, so
        // "second" lands on a recto page: page 1 (recto) "first", page 2 (verso) is wrong → a blank is
        // inserted → "second" on page 3 → 3 pages. If the parity were dropped it would be 2.
        Assert.Equal(3, Pages(
            "<div style='break-after:right'>first</div><div style='break-before:page'>second</div>"));
    }

    [Fact]
    public void Break_before_left_on_the_first_content_starts_the_document_on_a_verso_page()
    {
        // PR #218 review [P1 #4] — CSS Page §3.6: `break-before:left` on the FIRST content selects a
        // verso (left) STARTING page (page 1), WITHOUT a leading blank. So a following
        // `break-before:right` lands on page 2 — now a RECTO, because page 1 is verso — with NO blank
        // → 2 pages. Without the starting-side offset page 1 would be recto, page 2 verso, and the
        // right break would insert a blank → 3 pages (cf. Break_before_right_inserts_a_blank, which has
        // a non-forced first block so page 1 stays recto).
        Assert.Equal(2, Pages(
            "<div style='break-before:left'>first</div><div style='break-before:right'>second</div>"));
    }

    [Fact]
    public void Without_a_first_page_side_a_right_break_after_a_left_aligned_first_block_inserts_a_blank()
    {
        // Contrast — the FIRST block has no forced break, so page 1 stays recto (no starting-side
        // offset); the second block's `break-before:right` (recto) then needs page 2 (verso) → a blank
        // is inserted → 3 pages. Confirms the offset above comes from the first block's break-before.
        Assert.Equal(3, Pages(
            "<div>first</div><div style='break-before:right'>second</div>"));
    }

    [Fact]
    public void Forced_break_on_grandchild_prose_propagates_through_a_fitting_ancestor()
    {
        // PR #207 review [P2#4] — a forced break on nested PROSE splits its fitting
        // <section> ancestor: the <p> with break-before lands on page 2.
        Assert.Equal(2, Pages(
            "<section><p>first paragraph</p>"
            + "<p style='break-before:page'>second paragraph</p></section>"));
    }

    [Theory]
    [InlineData("display:flex")]
    [InlineData("display:grid")]
    [InlineData("display:table")]
    public void Forced_break_before_a_nested_non_block_flow_container_creates_a_new_page(string display)
    {
        // PR #207 Copilot review [P1] — break-before on a nested table/flex/grid container
        // (NOT block-flow-owned) is honored too: the metadata is computed at the recursion
        // loop top for EVERY in-flow block-level child, not just block-flow ones.
        Assert.Equal(2, Pages(
            "<div>first</div><div style='" + display + ";break-before:page'><div>x</div></div>"));
    }

    [Fact]
    public void Break_after_a_nested_non_block_flow_container_moves_the_next_sibling()
    {
        // PR #207 Copilot review [P2] — break-after on a nested flex container is remembered
        // (prevInFlowChild is tracked for all child types at the loop top), so the next
        // sibling moves to page 2.
        Assert.Equal(2, Pages(
            "<div style='display:flex;break-after:page'><div>x</div></div><div>second</div>"));
    }

    [Fact]
    public void In_an_rtl_document_break_before_right_is_a_verso_page_so_no_blank_is_inserted()
    {
        // CSS Fragmentation L3 §3.1 — in RTL, page 1 (a recto) is the physical LEFT page, so a physical
        // RIGHT page is a verso (even). `break-before:right` after the recto page 1 wants an even page;
        // page 2 IS even → no blank → 2 pages. (The same markup in LTR wants an odd page → a blank is
        // inserted → 3 pages, cf. Break_before_right_inserts_a_blank.) `recto` / `verso` don't swap.
        Assert.Equal(2, RtlPages(
            "<div>first</div><div style='break-before:right'>second</div>"));
    }

    [Fact]
    public void In_an_rtl_document_break_before_left_is_a_recto_page_so_a_blank_is_inserted()
    {
        // The mirror — in RTL the physical LEFT page is the recto (odd). `break-before:left` wants an
        // odd page; page 2 is even → a blank is inserted → 3 pages. (In LTR `left` = verso = even → no
        // blank → 2 pages, cf. Break_before_left_lands_on_a_verso_page.)
        Assert.Equal(3, RtlPages(
            "<div>first</div><div style='break-before:left'>second</div>"));
    }

    [Fact]
    public void In_an_rtl_document_break_before_recto_still_needs_an_odd_page()
    {
        // `recto` / `verso` are page-NUMBER parities (page 1 is a recto), direction-independent —
        // `break-before:recto` wants an odd page in RTL too; page 2 is even → blank → 3 pages.
        Assert.Equal(3, RtlPages(
            "<div>first</div><div style='break-before:recto'>second</div>"));
    }

    [Fact]
    public void In_an_rtl_document_break_after_right_does_not_insert_a_blank()
    {
        // PR #219 review [P2 #6] — RTL `break-after`. In RTL the physical RIGHT page is a verso (even),
        // so `break-after:right` on "first" (page 1 = recto) needs the next sibling on an EVEN page; page
        // 2 IS even → no blank → 2 pages. (In LTR `break-after:right` wants odd → page 2 even → a blank →
        // 3, cf. Break_after_right_inserts_a_blank_for_the_next_sibling.)
        Assert.Equal(2, RtlPages(
            "<div style='break-after:right'>first</div><div>second</div>"));
    }

    [Fact]
    public void Html_break_before_left_starts_the_document_on_a_verso_page()
    {
        // CSS Page §3.6 + CSS Fragmentation §3.1.1 — a `break-before` on <html> (the spec's exact
        // example) propagates to the document start, selecting a verso (left) STARTING page. PR #219
        // review [P1 #3]: <html>/<body>'s OWN break-before was previously IGNORED (the walk started below
        // <body>). A following `break-before:right` then lands on page 2 (a recto, because page 1 is
        // verso) with no blank → 2 pages.
        Assert.Equal(2, PagesHtml(
            "<!doctype html><html style='break-before:left'><body style='margin:0'>"
            + "<div>first</div><div style='break-before:right'>second</div></body></html>"));
    }

    [Fact]
    public void Body_break_before_left_starts_the_document_on_a_verso_page()
    {
        // The same selecting-the-starting-side behavior via a `break-before` on <body> (PR #219 review
        // [P1 #3] — also previously ignored, since the walk began with <body>'s CHILDREN).
        Assert.Equal(2, PagesHtml(
            "<!doctype html><html><body style='margin:0;break-before:left'>"
            + "<div>first</div><div style='break-before:right'>second</div></body></html>"));
    }

    [Fact]
    public void Nested_first_child_break_before_wins_over_its_ancestor_latest_in_flow()
    {
        // CSS Fragmentation §4.3 — when side-constrained forced breaks combine at the document start,
        // "the value on the LATEST element in flow wins". The first content is a <section break-before:
        // right> whose first child <p break-before:left> is later in flow, so `left` wins → a verso
        // start. A following `break-before:right` lands on page 2 (a recto) → 2 pages. (Outer-wins would
        // give `right` → recto start → the right break needs a blank → 3; PR #219 review [P2 #4].)
        Assert.Equal(2, PagesHtml(
            "<!doctype html><html><body style='margin:0'>"
            + "<section style='break-before:right'><p style='break-before:left'>first</p></section>"
            + "<div style='break-before:right'>second</div></body></html>"));
    }

    [Fact]
    public void Parityless_break_before_on_html_does_not_shift_the_starting_side()
    {
        // CSS Page §3.6 — a parity-LESS forced break (`break-before: page`) on <html> forces a break at
        // the document start, but any leading empty page is SUPPRESSED and `:first` matches the first
        // PRINTED page, so it does NOT shift the starting side (page 1 stays recto). PR #219 review
        // [P1 #2]: `page` / `always` / `all` carry no side. The following `break-before:right` then needs
        // a blank → 3 pages (the same as no <html> break — cf. Without_a_first_page_side…).
        Assert.Equal(3, PagesHtml(
            "<!doctype html><html style='break-before:page'><body style='margin:0'>"
            + "<div>first</div><div style='break-before:right'>second</div></body></html>"));
    }

    private static int PagesHtml(string fullHtml) =>
        NetPdf.HtmlPdf.ConvertDetailed(fullHtml).PageCount;

    private static int Pages(string bodyHtml)
    {
        var html = "<!doctype html><html><body style='margin:0'>" + bodyHtml + "</body></html>";
        return NetPdf.HtmlPdf.ConvertDetailed(html).PageCount;
    }

    private static int RtlPages(string bodyHtml)
    {
        var html = "<!doctype html><html><body style='margin:0;direction:rtl'>"
            + bodyHtml + "</body></html>";
        return NetPdf.HtmlPdf.ConvertDetailed(html).PageCount;
    }

    private static Box? FindByLocalName(Box root, string localName)
    {
        if (root.SourceElement is { } el
            && el.LocalName.Equals(localName, System.StringComparison.OrdinalIgnoreCase))
            return root;
        foreach (var child in root.Children)
        {
            var hit = FindByLocalName(child, localName);
            if (hit is not null) return hit;
        }
        return null;
    }

    // --- Helpers -------------------------------------------------------------

    private async Task<Box> ParseBox(string bodyHtml)
    {
        var html = "<!doctype html><html><body style='margin:0'>" + bodyHtml + "</body></html>";
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new NetPdf.HtmlPdfOptions());
        _parsed.Add(result); // disposed in Dispose(), after this test reads box.Style.
        var box = FindById(result.BoxRoot, "x");
        Assert.NotNull(box);
        return box!;
    }

    private static Box? FindById(Box root, string id)
    {
        if (root.SourceElement?.Id == id) return root;
        foreach (var child in root.Children)
        {
            var hit = FindById(child, id);
            if (hit is not null) return hit;
        }
        return null;
    }
}
