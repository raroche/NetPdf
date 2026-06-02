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
