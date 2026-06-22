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
public sealed class FragmentationControlTests
{
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
        // OrphansRequired / WidowsRequired (PdfRenderPipeline reads them off the root box).
        var b = await ParseBox("<div id='x' style='orphans:5;widows:4'></div>");
        using var resolver = new BreakResolver(
            orphansRequired: b.Style.ReadOrphansOrDefault(),
            widowsRequired: b.Style.ReadWidowsOrDefault());
        Assert.Equal(5, resolver.OrphansRequired);
        Assert.Equal(4, resolver.WidowsRequired);
    }

    // --- Helpers -------------------------------------------------------------

    private static async Task<Box> ParseBox(string bodyHtml)
    {
        var html = "<!doctype html><html><body style='margin:0'>" + bodyHtml + "</body></html>";
        // NOT disposed: Phase2Result.Dispose() pool-returns the box-owned ComputedStyles,
        // and the caller reads box.Style after this returns. Box-owned styles are never
        // re-rented (only RETURNED styles are), so leaving them is safe in a short-lived test.
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new NetPdf.HtmlPdfOptions());
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
