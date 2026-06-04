// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using NetPdf.Css.Cascade;
using NetPdf.Css.PagedMedia;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.PagedMedia;

/// <summary>
/// Unit tests for <see cref="AtPageMarginResolver"/> — Phase 3 Task 21 cycle 1, the resolution
/// of bare <c>@page { margin… }</c> declarations into absolute px. Drives the real CSS parse +
/// adapt path so the longhand expansion + value strings match production.
/// </summary>
public sealed class AtPageMarginResolverTests
{
    // ---- TryParseAbsoluteLengthPx (pure) ----

    [Theory]
    [InlineData("96px", 96.0)]
    [InlineData("1in", 96.0)]
    [InlineData("0", 0.0)]
    [InlineData("12pt", 16.0)]
    [InlineData("1pc", 16.0)]
    [InlineData("-1in", -96.0)]
    public void TryParseAbsoluteLengthPx_resolves_absolute_lengths(string text, double expectedPx)
    {
        Assert.True(AtPageMarginResolver.TryParseAbsoluteLengthPx(text, out var px));
        Assert.Equal(expectedPx, px, 3);
    }

    [Fact]
    public void TryParseAbsoluteLengthPx_resolves_cm()
    {
        Assert.True(AtPageMarginResolver.TryParseAbsoluteLengthPx("2cm", out var px));
        Assert.Equal(75.59, px, 1);   // 2 × (96 / 2.54)
    }

    [Theory]
    [InlineData("50%")]
    [InlineData("calc(1px)")]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("1em")]
    [InlineData("abc")]
    [InlineData("px")]
    public void TryParseAbsoluteLengthPx_rejects_non_absolute_or_malformed(string text)
    {
        Assert.False(AtPageMarginResolver.TryParseAbsoluteLengthPx(text, out _));
    }

    // ---- Resolve (parse → adapt → resolve) ----

    [Fact]
    public async Task Resolve_uniform_margin_applies_to_all_sides()
    {
        var m = await ResolveCss("@page { margin: 96px }");
        Assert.True(m.HasAny);
        Assert.Equal(96.0, m.TopPx!.Value, 3);
        Assert.Equal(96.0, m.RightPx!.Value, 3);
        Assert.Equal(96.0, m.BottomPx!.Value, 3);
        Assert.Equal(96.0, m.LeftPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_four_value_margin_maps_to_top_right_bottom_left()
    {
        var m = await ResolveCss("@page { margin: 10px 20px 30px 40px }");
        Assert.Equal(10.0, m.TopPx!.Value, 3);
        Assert.Equal(20.0, m.RightPx!.Value, 3);
        Assert.Equal(30.0, m.BottomPx!.Value, 3);
        Assert.Equal(40.0, m.LeftPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_unit_conversion_inch_to_px()
    {
        var m = await ResolveCss("@page { margin: 1in }");
        Assert.Equal(96.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_partial_margin_leaves_other_sides_unspecified()
    {
        var m = await ResolveCss("@page { margin-top: 8px }");
        Assert.Equal(8.0, m.TopPx!.Value, 3);
        Assert.Null(m.RightPx);
        Assert.Null(m.BottomPx);
        Assert.Null(m.LeftPx);
    }

    [Fact]
    public async Task Resolve_applies_first_selector_on_the_single_page()
    {
        // Task 21 selectors: the single page IS the first page, so `@page :first` applies.
        var m = await ResolveCss("@page :first { margin: 5px }");
        Assert.True(m.HasAny);
        Assert.Equal(5.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_first_selector_overrides_the_bare_page()
    {
        // :first beats bare by specificity.
        var m = await ResolveCss("@page { margin: 1px } @page :first { margin: 2px }");
        Assert.Equal(2.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_first_selector_beats_bare_regardless_of_source_order()
    {
        // :first sourced BEFORE bare still wins (specificity, not source order).
        var m = await ResolveCss("@page :first { margin: 2px } @page { margin: 1px }");
        Assert.Equal(2.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_bare_important_beats_first_normal()
    {
        // Importance outranks specificity: a bare !important wins over a :first normal.
        var m = await ResolveCss("@page { margin: 1px !important } @page :first { margin: 2px }");
        Assert.Equal(1.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_defers_left_right_blank_and_named_selectors()
    {
        // :left/:right/:blank/named-page selectors are recognized but not applied (multi-page-gated).
        Assert.False((await ResolveCss("@page :left { margin: 5px }")).HasAny);
        Assert.False((await ResolveCss("@page :right { margin: 5px }")).HasAny);
        Assert.False((await ResolveCss("@page :blank { margin: 5px }")).HasAny);
        Assert.False((await ResolveCss("@page chapter { margin: 5px }")).HasAny);
    }

    [Fact]
    public async Task Resolve_applies_a_selector_list_containing_first()
    {
        // CSS Page 3: a comma-separated page-selector list applies if ANY selector matches — so a list
        // that includes :first wins on the first page (in either order). A list WITHOUT :first defers.
        Assert.Equal(2.0, (await ResolveCss("@page { margin: 1px } @page :first, :left { margin: 2px }")).TopPx!.Value, 3);
        Assert.Equal(2.0, (await ResolveCss("@page { margin: 1px } @page :left, :first { margin: 2px }")).TopPx!.Value, 3);
        Assert.False((await ResolveCss("@page :left, :right { margin: 5px }")).HasAny);
    }

    [Fact]
    public void ClassifyPageSelector_maps_the_recovered_prelude()
    {
        // Bare (empty / whitespace), :first (case + whitespace tolerant), everything else deferred.
        Assert.Equal(AtPageRules.PageSelectorKind.Bare, AtPageRules.ClassifyPageSelector(""));
        Assert.Equal(AtPageRules.PageSelectorKind.Bare, AtPageRules.ClassifyPageSelector("   "));
        Assert.Equal(AtPageRules.PageSelectorKind.First, AtPageRules.ClassifyPageSelector(":first"));
        Assert.Equal(AtPageRules.PageSelectorKind.First, AtPageRules.ClassifyPageSelector(" :FIRST "));
        Assert.Equal(AtPageRules.PageSelectorKind.Deferred, AtPageRules.ClassifyPageSelector(":left"));
        Assert.Equal(AtPageRules.PageSelectorKind.Deferred, AtPageRules.ClassifyPageSelector(":right"));
        Assert.Equal(AtPageRules.PageSelectorKind.Deferred, AtPageRules.ClassifyPageSelector(":blank"));
        Assert.Equal(AtPageRules.PageSelectorKind.Deferred, AtPageRules.ClassifyPageSelector("chapter"));
        Assert.Equal(AtPageRules.PageSelectorKind.Deferred, AtPageRules.ClassifyPageSelector("chapter:first"));
    }

    [Fact]
    public async Task Resolve_later_bare_rule_wins_per_side()
    {
        var m = await ResolveCss("@page { margin: 1px } @page { margin: 2px }");
        Assert.Equal(2.0, m.TopPx!.Value, 3);
        Assert.Equal(2.0, m.LeftPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_no_page_rule_yields_nothing()
    {
        var m = await ResolveCss(".a { color: red }");
        Assert.False(m.HasAny);
        Assert.Null(m.TopPx);
    }

    // ---- Applicability: media + disabled (mirrors the cascade) ----

    [Fact]
    public async Task Resolve_ignores_a_screen_media_sheet_in_print()
    {
        var m = await ResolveCss("@page { margin: 8px }", sheetMedia: "screen");
        Assert.False(m.HasAny);
    }

    [Fact]
    public async Task Resolve_applies_a_print_media_sheet()
    {
        var m = await ResolveCss("@page { margin: 8px }", sheetMedia: "print");
        Assert.Equal(8.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_applies_at_media_print_block()
    {
        var m = await ResolveCss("@media print { @page { margin: 8px } }");
        Assert.Equal(8.0, m.TopPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_ignores_at_media_screen_block()
    {
        var m = await ResolveCss("@media screen { @page { margin: 8px } }");
        Assert.False(m.HasAny);
    }

    [Fact]
    public async Task Resolve_ignores_a_disabled_sheet()
    {
        var m = await ResolveCss("@page { margin: 8px }", isDisabled: true);
        Assert.False(m.HasAny);
    }

    // ---- Percentages (per-axis) + !important ----

    [Fact]
    public async Task Resolve_percentage_margins_are_per_axis()
    {
        // PrintContext is 1000 × 2000 px. Per CSS Page 3: left/right % → width, top/bottom % → height.
        var m = await ResolveCss("@page { margin: 10% }");
        Assert.Equal(200.0, m.TopPx!.Value, 3);     // 10% × 2000 (height)
        Assert.Equal(200.0, m.BottomPx!.Value, 3);  // 10% × 2000 (height)
        Assert.Equal(100.0, m.LeftPx!.Value, 3);    // 10% × 1000 (width)
        Assert.Equal(100.0, m.RightPx!.Value, 3);   // 10% × 1000 (width)
    }

    [Fact]
    public async Task Resolve_important_beats_a_later_normal_declaration()
    {
        var m = await ResolveCss("@page { margin: 1in !important } @page { margin: 0 }");
        Assert.Equal(96.0, m.TopPx!.Value, 3);   // the !important 1in wins over the later 0
        Assert.Equal(96.0, m.LeftPx!.Value, 3);
    }

    [Fact]
    public async Task Resolve_later_important_beats_an_earlier_important()
    {
        var m = await ResolveCss("@page { margin: 1in !important } @page { margin: 2in !important }");
        Assert.Equal(192.0, m.TopPx!.Value, 3);   // among equal importance, source order wins
    }

    private static readonly CssMediaContext PrintContext = new(
        MediaType: "print", ViewportWidthPx: 1000, ViewportHeightPx: 2000,
        DevicePixelRatio: 1.0, PreferredColorScheme: "light");

    private static async Task<AtPageMarginResolver.ResolvedPageMargins> ResolveCss(
        string css, string? sheetMedia = null, bool isDisabled = false, CssMediaContext? media = null)
    {
        var sheet = await ParseSheet(css);
        // Mirror the production path (Phase2Pipeline): run the pre-pass over the raw CSS so the
        // @page selector AngleSharp drops is recovered, then adapt with the recoveries merged in.
        var preprocess = CssPreprocessor.Process(css);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, href: null, origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.Unknown, mediaQuery: sheetMedia, isDisabled: isDisabled, order: 0);
        return AtPageMarginResolver.Resolve(new[] { stylesheet }, media ?? PrintContext);
    }

    private static async Task<ICssStyleSheet> ParseSheet(string css)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, IsKeepingSourceReferences = true });
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(parser);
        var ctx = BrowsingContext.New(config);
        var html = $"<html><head><style>{css}</style></head><body></body></html>";
        var document = await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
        return document.StyleSheets.OfType<ICssStyleSheet>().Single();
    }
}
