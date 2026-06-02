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
/// Unit tests for <see cref="AtPageSizeResolver"/> — Phase 3 Task 21 cycle 2, resolution of the
/// bare <c>@page { size }</c> descriptor (recovered by the pre-pass, since AngleSharp.Css drops
/// it). Drives the real parse → pre-pass → adapt → resolve path.
/// </summary>
public sealed class AtPageSizeResolverTests
{
    // ---- TryResolveSize (pure) ----

    [Theory]
    [InlineData("A4", 794, 1123)]                 // 210×297mm
    [InlineData("A4 landscape", 1123, 794)]
    [InlineData("landscape A4", 1123, 794)]       // order-independent
    [InlineData("A5", 559, 794)]                  // 148×210mm
    [InlineData("letter", 816, 1056)]             // 8.5×11in
    [InlineData("letter landscape", 1056, 816)]
    [InlineData("legal", 816, 1344)]
    [InlineData("210mm 297mm", 794, 1123)]
    [InlineData("5in", 480, 480)]                 // one length → square
    [InlineData("8.5in 11in", 816, 1056)]
    public void TryResolveSize_resolves_known_forms(string text, double w, double h)
    {
        Assert.True(AtPageSizeResolver.TryResolveSize(text, 800, 1000, out var rw, out var rh, out var isAuto));
        Assert.False(isAuto);
        Assert.Equal(w, rw, 0);
        Assert.Equal(h, rh, 0);
    }

    [Theory]
    [InlineData("portrait", 800, 1000)]   // re-orients the configured 800×1000 page
    [InlineData("landscape", 1000, 800)]
    public void TryResolveSize_orientation_alone_reorients_the_configured_page(string text, double w, double h)
    {
        Assert.True(AtPageSizeResolver.TryResolveSize(text, 800, 1000, out var rw, out var rh, out _));
        Assert.Equal(w, rw, 0);
        Assert.Equal(h, rh, 0);
    }

    [Fact]
    public void TryResolveSize_auto_signals_no_override()
    {
        Assert.True(AtPageSizeResolver.TryResolveSize("auto", 800, 1000, out _, out _, out var isAuto));
        Assert.True(isAuto);
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo")]
    [InlineData("10%")]        // percentages aren't valid for `size`
    [InlineData("calc(1px)")]
    [InlineData("0")]          // a zero / non-positive length is invalid
    public void TryResolveSize_rejects_unsupported(string text)
    {
        Assert.False(AtPageSizeResolver.TryResolveSize(text, 800, 1000, out _, out _, out _));
    }

    // ---- Resolve (parse → pre-pass recover → adapt → resolve) ----

    [Fact]
    public async Task Resolve_named_size_keyword()
    {
        var s = await ResolveSize("@page { size: A5 }");
        Assert.NotNull(s);
        Assert.Equal(559, s!.Value.WidthPx, 0);
        Assert.Equal(794, s.Value.HeightPx, 0);
    }

    [Fact]
    public async Task Resolve_keyword_with_landscape_orientation()
    {
        var s = await ResolveSize("@page { size: A4 landscape }");
        Assert.NotNull(s);
        Assert.True(s!.Value.WidthPx > s.Value.HeightPx);   // landscape
        Assert.Equal(1123, s.Value.WidthPx, 0);
    }

    [Fact]
    public async Task Resolve_auto_yields_no_override() =>
        Assert.Null(await ResolveSize("@page { size: auto }"));

    [Fact]
    public async Task Resolve_no_size_yields_no_override() =>
        Assert.Null(await ResolveSize("@page { margin: 0 }"));

    [Fact]
    public async Task Resolve_ignores_screen_media_sheet_in_print() =>
        Assert.Null(await ResolveSize("@page { size: A4 }", sheetMedia: "screen"));

    [Fact]
    public async Task Resolve_later_bare_rule_wins()
    {
        var s = await ResolveSize("@page { size: A4 } @page { size: A5 }");
        Assert.Equal(559, s!.Value.WidthPx, 0);   // A5 width — the later rule
    }

    // ---- Duplicate / invalid grammar (review P2) ----

    [Theory]
    [InlineData("A4 letter")]              // two page-size keywords
    [InlineData("portrait landscape")]     // two orientations
    [InlineData("landscape portrait")]
    [InlineData("A4 portrait landscape")]  // keyword + two orientations
    [InlineData("A4 A5 landscape")]        // two keywords + an orientation
    public void TryResolveSize_rejects_duplicate_keyword_or_orientation(string text)
    {
        Assert.False(AtPageSizeResolver.TryResolveSize(text, 800, 1000, out _, out _, out _));
    }

    // ---- !important (review P2) ----

    [Fact]
    public async Task Resolve_important_size_beats_a_later_normal_rule()
    {
        // CSS Cascade §5 — an earlier !important is not overridden by a later normal declaration.
        var s = await ResolveSize("@page { size: A5 !important } @page { size: A4 }");
        Assert.Equal(559, s!.Value.WidthPx, 0);   // A5 wins despite the later A4
    }

    [Fact]
    public async Task Resolve_later_important_size_wins_over_earlier_important()
    {
        var s = await ResolveSize("@page { size: A5 !important } @page { size: A4 !important }");
        Assert.Equal(794, s!.Value.WidthPx, 0);   // A4 width — later, equal importance
    }

    [Fact]
    public async Task Resolve_important_size_alone_is_honored_not_rejected()
    {
        // Pre-fix the "!important" token leaked into the value text + TryResolveSize rejected it.
        var s = await ResolveSize("@page { size: A5 !important }");
        Assert.Equal(559, s!.Value.WidthPx, 0);
    }

    // ---- Paper-size-conditioned @media is ignored, CSS Page 3 §3.3 (review P1) ----

    [Theory]
    [InlineData("@media print and (min-width: 1px) { @page { size: A4 } }")]
    [InlineData("@media print and (max-width: 9999px) { @page { size: A4 } }")]
    [InlineData("@media print and (min-height: 1px) { @page { size: A4 } }")]
    [InlineData("@media print and (orientation: portrait) { @page { size: A4 } }")]
    [InlineData("@media (aspect-ratio: 4/3) { @page { size: A4 } }")]
    public async Task Resolve_ignores_size_qualified_by_a_paper_size_media_query(string css)
    {
        // The query MATCHES the print context (800 × 1000), but the `size` must still be ignored
        // because it's conditioned on a paper-size feature (circular page-size dependency).
        Assert.Null(await ResolveSize(css));
    }

    [Fact]
    public async Task Resolve_applies_size_under_a_non_paper_size_media_query()
    {
        // Positive control — a matching @media NOT conditioned on a paper-size feature still
        // applies the size. (min-resolution matches: DPR 1.0 ≥ 1dppx.)
        var s = await ResolveSize("@media print and (min-resolution: 1dppx) { @page { size: A5 } }");
        Assert.Equal(559, s!.Value.WidthPx, 0);
    }

    [Fact]
    public async Task Resolve_ignores_size_qualified_by_a_paper_size_sheet_media_query()
    {
        // The conditioning also applies at the sheet level (a `<style media="…">` / `<link>`).
        Assert.Null(await ResolveSize("@page { size: A4 }", sheetMedia: "print and (min-width: 1px)"));
    }

    [Fact]
    public async Task Resolve_size_inside_supports_is_not_yet_applied_tracked_followup()
    {
        // Task 21 traverses only sheet media + @media (see AtPageRules remarks) — a bare @page
        // nested in a matching @supports does not yet contribute. Tracked follow-up
        // (deferrals.md#layout-to-pdf-pipeline); this pins the current documented scope.
        Assert.Null(await ResolveSize("@supports (display: grid) { @page { size: A5 } }"));
    }

    [Theory]
    [InlineData("print and (min-width: 1px)", true)]
    [InlineData("(max-width: 40em)", true)]
    [InlineData("screen and (min-height: 1px)", true)]
    [InlineData("(orientation: landscape)", true)]
    [InlineData("(aspect-ratio: 4/3)", true)]
    [InlineData("(device-width: 800px)", true)]
    [InlineData("(min-device-height: 1px)", true)]
    [InlineData("print", false)]
    [InlineData("screen and (min-resolution: 2dppx)", false)]
    [InlineData("(prefers-color-scheme: dark)", false)]
    [InlineData("print and (color)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPaperSizeConditioned_flags_size_dependent_media_features(string? query, bool expected)
    {
        Assert.Equal(expected, AtPageRules.IsPaperSizeConditioned(query));
    }

    private static readonly CssMediaContext PrintContext = new(
        MediaType: "print", ViewportWidthPx: 800, ViewportHeightPx: 1000,
        DevicePixelRatio: 1.0, PreferredColorScheme: "light");

    private static async Task<AtPageSizeResolver.ResolvedPageSize?> ResolveSize(string css, string? sheetMedia = null)
    {
        var sheet = await ParseSheet(css);
        var preprocess = CssPreprocessor.Process(css);   // recovers the dropped `size` descriptor
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, href: null, origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.Unknown, mediaQuery: sheetMedia, isDisabled: false, order: 0);
        return AtPageSizeResolver.Resolve(new[] { stylesheet }, PrintContext);
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
