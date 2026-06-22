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
        // left/right force a page break (the parity refinement — actually landing on a
        // left/right page via blank-page insertion — is a documented residual; here they
        // behave like `page`). recto/verso/all are also registered + handled by the
        // reader, but AngleSharp.Css 1.0.0-beta.144 doesn't parse those keywords yet.
        var b = await ParseBox($"<div id='x' style='{css}'></div>");
        Assert.True(b.Style.ForcesPageBreakBefore());
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

    private static int Pages(string bodyHtml)
    {
        var html = "<!doctype html><html><body style='margin:0'>" + bodyHtml + "</body></html>";
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
