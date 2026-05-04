// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser;

/// <summary>
/// Integration tests for the <see cref="CssParserAdapter.Adapt(ICssStyleSheet, CssPreprocessResult, string?, CssStylesheetOrigin, CssStylesheetOwnerKind, string?, bool, int)"/>
/// overload — runs the full Phase 2 pipeline (raw CSS → preprocessor + AngleSharp → adapter
/// → AST) against the four Task 3 blockers from review cycle 1 and asserts the recovered
/// information is merged correctly into the emitted AST.
/// </summary>
public sealed class CssParserAdapterPreprocessTests
{
    [Fact]
    public async Task Adapt_with_preprocess_recovers_page_first_pseudo_into_prelude()
    {
        // The Task 2 review-cycle 1 blocker pin: AngleSharp drops `:first`. With preprocess
        // data merged, the `@page` rule's Prelude now carries the recovered selector.
        var (sheet, preprocess) = await ParseAndPreprocess("@page :first { margin-top: 0 }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("page", page.Name);
        Assert.Equal(":first", page.Prelude);    // Recovered from preprocess.
        Assert.NotEmpty(page.Declarations);       // AngleSharp still gives us declarations.
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_named_page_into_prelude()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("@page chapter { margin-top: 5cm }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("chapter", page.Prelude);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_page_margin_boxes_as_child_rules()
    {
        // Second Task 3 blocker: AngleSharp drops margin-boxes entirely. Preprocess
        // recovers them and the adapter re-parents them as ChildRules under @page.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page {
                margin: 1in;
                @top-center { content: "Header"; font-size: 10pt }
                @bottom-right { content: counter(page) }
            }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal(2, page.ChildRules.Length);
        var topCenter = Assert.IsType<CssAtRule>(page.ChildRules[0]);
        Assert.Equal("top-center", topCenter.Name);
        Assert.Equal(2, topCenter.Declarations.Length);
        Assert.Equal("content", topCenter.Declarations[0].Property);
        Assert.Contains("Header", topCenter.Declarations[0].Value.RawText);
        Assert.Equal("font-size", topCenter.Declarations[1].Property);
        Assert.Equal("10pt", topCenter.Declarations[1].Value.RawText);

        var bottomRight = Assert.IsType<CssAtRule>(page.ChildRules[1]);
        Assert.Equal("bottom-right", bottomRight.Name);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_import_layer_name()
    {
        // Third Task 3 blocker: AngleSharp folds `layer(name)` into a malformed media query.
        // With preprocess data, CssImportRule.LayerName is populated correctly.
        var (sheet, preprocess) = await ParseAndPreprocess("@import url(\"theme.css\") layer(framework);");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var import = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("theme.css", import.Url);
        Assert.Equal("framework", import.LayerName);
        // Authored had no media query — recovered MediaQuery should be empty (NOT AngleSharp's "not all").
        Assert.Equal(string.Empty, import.MediaQuery);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_import_supports_condition()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("@import url(\"grid.css\") supports(display: grid);");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var import = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("display: grid", import.SupportsCondition);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_import_layer_supports_and_media_together()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(
            "@import url(\"all.css\") layer(theme) supports(display: grid) screen;");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var import = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("all.css", import.Url);
        Assert.Equal("theme", import.LayerName);
        Assert.Equal("display: grid", import.SupportsCondition);
        Assert.Contains("screen", import.MediaQuery);
    }

    [Fact]
    public async Task Adapt_with_preprocess_backfills_source_locations_on_rules()
    {
        // Fourth Task 3 blocker: Location was Unknown for every rule until preprocess
        // tracked positions. After merge, each rule's Location reflects its line in source.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            .a { color: red }
            .b { color: blue }
            @page { margin: 1in }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(3, stylesheet.Rules.Length);
        // Each rule should have a populated Location (line > 0).
        foreach (var rule in stylesheet.Rules)
        {
            var location = ExtractLocation(rule);
            Assert.True(location.Line > 0,
                $"rule {rule.GetType().Name} expected populated Location but got Line={location.Line}");
        }
    }

    [Fact]
    public async Task Adapt_with_empty_preprocess_falls_back_to_unrecovered_data()
    {
        // Backwards compat: passing empty preprocess (or omitting it) gives Task 2 behavior.
        var (sheet, _) = await ParseAndPreprocess("@page :first { margin-top: 0 }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, CssPreprocessResult.Empty, null, CssStylesheetOrigin.Author,
            CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Empty(page.Prelude); // No preprocess → no recovery, AngleSharp's loss visible.
    }

    [Fact]
    public async Task Adapt_existing_two_arg_overload_still_works()
    {
        // The original Task 2 entry point continues to work without preprocess.
        var (sheet, _) = await ParseAndPreprocess(".a { color: red }");
        var stylesheet = CssParserAdapter.Adapt(sheet);
        Assert.NotEmpty(stylesheet.Rules);
    }

    [Fact]
    public async Task Adapt_with_preprocess_handles_mixed_rule_set_correctly()
    {
        // Style rules + at-rules + page rules + imports all preserve order and recover
        // the bits AngleSharp loses in one pass.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @import url("a.css") layer(framework);
            .a { color: red }
            @media print { .b { color: black } }
            @page :first { @top-left { content: "Cover" } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(4, stylesheet.Rules.Length);
        var import = Assert.IsType<CssImportRule>(stylesheet.Rules[0]);
        Assert.Equal("framework", import.LayerName);

        Assert.IsType<CssStyleRule>(stylesheet.Rules[1]);

        var media = Assert.IsType<CssAtRule>(stylesheet.Rules[2]);
        Assert.Equal("media", media.Name);

        var page = Assert.IsType<CssAtRule>(stylesheet.Rules[3]);
        Assert.Equal("page", page.Name);
        Assert.Equal(":first", page.Prelude);
        Assert.Single(page.ChildRules);
        Assert.Equal("top-left", Assert.IsType<CssAtRule>(page.ChildRules[0]).Name);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    private static async Task<(ICssStyleSheet sheet, CssPreprocessResult preprocess)> ParseAndPreprocess(string css)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, IsKeepingSourceReferences = true });
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(parser);
        var ctx = BrowsingContext.New(config);

        var html = $"<html><head><style>{css}</style></head><body></body></html>";
        var document = await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
        var sheet = document.StyleSheets.OfType<ICssStyleSheet>().Single();
        var preprocess = CssPreprocessor.Process(css);
        return (sheet, preprocess);
    }

    private static CssSourceLocation ExtractLocation(CssRule rule) => rule switch
    {
        CssStyleRule s => s.Location,
        CssAtRule a => a.Location,
        CssImportRule i => i.Location,
        _ => CssSourceLocation.Unknown,
    };
}
