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
/// Unit tests for <see cref="AtPageMarginBoxResolver"/> — Phase 3 Task 21 cycle 3, resolving the
/// <c>@page</c> margin boxes (recovered by the pre-pass, since AngleSharp.Css drops them) to
/// (name, raw <c>content</c>) pairs. Drives the real parse → pre-pass → adapt → resolve path.
/// </summary>
public sealed class AtPageMarginBoxResolverTests
{
    [Fact]
    public async Task Resolve_returns_a_content_bearing_box()
    {
        var boxes = await Resolve("@page { @bottom-center { content: \"Footer\" } }");
        var box = Assert.Single(boxes);
        Assert.Equal("bottom-center", box.Name);
        Assert.Equal("\"Footer\"", box.ContentRawValue); // raw (quoted); the orchestrator decodes it
    }

    [Fact]
    public async Task Resolve_omits_a_box_without_content()
    {
        // A margin box with only decoration (no `content`) paints no text this cycle.
        var boxes = await Resolve("@page { @top-center { color: red } }");
        Assert.Empty(boxes);
    }

    [Fact]
    public async Task Resolve_no_page_rule_yields_nothing() =>
        Assert.Empty(await Resolve(".a { color: red }"));

    [Fact]
    public async Task Resolve_last_content_wins_within_a_box()
    {
        var boxes = await Resolve("@page { @top-center { content: \"A\"; content: \"B\" } }");
        Assert.Equal("\"B\"", Assert.Single(boxes).ContentRawValue);
    }

    [Fact]
    public async Task Resolve_last_page_rule_wins_for_a_box()
    {
        var boxes = await Resolve(
            "@page { @top-center { content: \"A\" } } @page { @top-center { content: \"B\" } }");
        Assert.Equal("\"B\"", Assert.Single(boxes).ContentRawValue);
    }

    [Fact]
    public async Task Resolve_ignores_a_screen_media_sheet_in_print() =>
        Assert.Empty(await Resolve("@page { @bottom-center { content: \"x\" } }", sheetMedia: "screen"));

    [Fact]
    public async Task Resolve_emits_boxes_in_canonical_order()
    {
        // Declared bottom-then-top; canonical paint order is top before bottom.
        var boxes = await Resolve(
            "@page { @bottom-center { content: \"B\" } @top-center { content: \"T\" } }");
        Assert.Equal(new[] { "top-center", "bottom-center" }, boxes.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task Resolve_ignores_unknown_margin_box_names()
    {
        // `@middle-center` is not one of the 16 CSS Page 3 §6.4 names.
        Assert.Empty(await Resolve("@page { @middle-center { content: \"x\" } }"));
    }

    // ---- !important cascade (review P1) ----

    [Fact]
    public async Task Resolve_important_content_beats_a_later_normal()
    {
        var boxes = await Resolve("@page { @top-center { content: \"A\" !important; content: \"B\" } }");
        Assert.Equal("\"A\"", Assert.Single(boxes).ContentRawValue);
    }

    [Fact]
    public async Task Resolve_later_important_content_wins()
    {
        var boxes = await Resolve("@page { @top-center { content: \"A\" !important; content: \"B\" !important } }");
        Assert.Equal("\"B\"", Assert.Single(boxes).ContentRawValue);
    }

    [Fact]
    public async Task Resolve_important_content_beats_a_later_normal_across_page_rules()
    {
        var boxes = await Resolve(
            "@page { @top-center { content: \"A\" !important } } @page { @top-center { content: \"B\" } }");
        Assert.Equal("\"A\"", Assert.Single(boxes).ContentRawValue);
    }

    // ---- none / normal suppression, no diagnostic (review P2) ----

    [Theory]
    [InlineData("@page { @top-center { content: none } }")]
    [InlineData("@page { @top-center { content: normal } }")]
    [InlineData("@page { @top-center { content: NONE } }")]   // keyword is case-insensitive
    public async Task Resolve_none_or_normal_suppresses_the_box(string css) =>
        Assert.Empty(await Resolve(css));

    [Fact]
    public async Task Resolve_later_none_suppresses_an_earlier_string() =>
        Assert.Empty(await Resolve("@page { @top-center { content: \"A\"; content: none } }"));

    [Fact]
    public async Task Resolve_important_none_suppresses_a_later_normal_string() =>
        Assert.Empty(await Resolve(
            "@page { @top-center { content: none !important } } @page { @top-center { content: \"B\" } }"));

    [Fact]
    public async Task Resolve_quoted_none_is_a_literal_string_not_suppression()
    {
        // Only the BARE keyword suppresses; "none" (quoted) is the literal text "none".
        var boxes = await Resolve("@page { @top-center { content: \"none\" } }");
        Assert.Equal("\"none\"", Assert.Single(boxes).ContentRawValue);
    }

    private static readonly CssMediaContext PrintContext = new(
        MediaType: "print", ViewportWidthPx: 800, ViewportHeightPx: 1000,
        DevicePixelRatio: 1.0, PreferredColorScheme: "light");

    private static async Task<System.Collections.Immutable.ImmutableArray<AtPageMarginBoxResolver.ResolvedMarginBox>>
        Resolve(string css, string? sheetMedia = null)
    {
        var sheet = await ParseSheet(css);
        var preprocess = CssPreprocessor.Process(css);   // recovers the dropped margin boxes
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, href: null, origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.Unknown, mediaQuery: sheetMedia, isDisabled: false, order: 0);
        return AtPageMarginBoxResolver.Resolve(new[] { stylesheet }, PrintContext);
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
