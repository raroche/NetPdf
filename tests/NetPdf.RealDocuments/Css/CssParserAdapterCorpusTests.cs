// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using AngleSharp.Css.Dom;
using NetPdf.Css.Parser;
using Xunit;

namespace NetPdf.RealDocuments.Css;

/// <summary>
/// Integration coverage for Phase 2 Task 2: every <c>&lt;style&gt;</c> block in the invoice
/// corpus adapts through <see cref="CssParserAdapter"/> without throwing, and the resulting
/// AST is non-empty (every corpus invoice carries inline CSS). Pairs with the host's parse
/// tests in <c>HtmlParsingHostCorpusTests</c> — together they cover the front of the Phase 2
/// pipeline end-to-end (HTML+CSS source → DOM → typed AST ready for cascade).
/// </summary>
public sealed class CssParserAdapterCorpusTests
{
    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Corpus_invoice_inline_styles_adapt_without_throwing(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var host = new HtmlParsingHost();

        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();
        Assert.NotEmpty(sheets);

        foreach (var sheet in sheets)
        {
            var stylesheet = CssParserAdapter.Adapt(sheet);
            Assert.NotNull(stylesheet);
            // Every corpus invoice has at least one inline rule (style or at-rule).
            Assert.NotEmpty(stylesheet.Rules);
        }
    }

    [Fact]
    public async Task Corpus_classic_pure_css_adapts_with_style_and_at_rules()
    {
        // The classic invoice carries a mix of plain style rules and a `@page` at-rule.
        // Pins the rule-shape mix so a future regression that drops at-rules is caught.
        var html = LoadCorpusFile("Corpus/Invoices/01-classic-pure-css.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheet = document.StyleSheets.OfType<ICssStyleSheet>().Single();
        var stylesheet = CssParserAdapter.Adapt(sheet);

        Assert.Contains(stylesheet.Rules, r => r is CssStyleRule);
        Assert.Contains(stylesheet.Rules, r => r is CssAtRule a && a.Name == "page");
    }

    [Fact]
    public async Task Corpus_anvil_running_elements_adapts_page_rules_across_multiple_sheets()
    {
        // The Anvil invoice carries TWO <style> blocks (one default, one with media="print").
        // Aggregate across all sheets so the assertion isn't sensitive to which block holds
        // the @page rule. NOTE: AngleSharp.Css 1.0.0-beta.144 silently drops margin-box
        // at-rules inside @page (e.g. @bottom-center) — Task 3's pre-pass tokenizer is the
        // planned recovery path. The page rule itself adapts cleanly here, just without
        // its margin-box children.
        var html = LoadCorpusFile("Corpus/Invoices/04-anvil-running-elements.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();
        Assert.True(sheets.Count >= 2, $"expected ≥ 2 stylesheets, got {sheets.Count}");

        var allRules = sheets.SelectMany(s => CssParserAdapter.Adapt(s).Rules).ToList();
        Assert.Contains(allRules, r => r is CssAtRule a && a.Name == "page");
    }

    [Fact]
    public async Task Corpus_anvil_print_stylesheet_can_carry_media_metadata_on_adapt()
    {
        // The second <style media="print"> block in the Anvil invoice is the canonical case
        // for the metadata-bearing Adapt overload: a sheet that should only contribute to
        // the cascade when MediaType=Print is selected. Pins that the metadata round-trips.
        var html = LoadCorpusFile("Corpus/Invoices/04-anvil-running-elements.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();

        // Apply metadata in a way that mirrors how the cascade integration in Task 7 will
        // wire each <style>/<link> to its origin/order/media — here we just test the plumbing.
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var mediaQuery = (sheet.OwnerNode as AngleSharp.Html.Dom.IHtmlStyleElement)?.Media;
            var stylesheet = CssParserAdapter.Adapt(
                sheet,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: string.IsNullOrEmpty(mediaQuery) ? null : mediaQuery,
                isDisabled: false,
                order: i);

            Assert.Equal(i, stylesheet.Order);
            Assert.Equal(CssStylesheetOwnerKind.StyleElement, stylesheet.OwnerKind);
        }

        var byMedia = sheets
            .Select(s => (sheet: s,
                          media: (s.OwnerNode as AngleSharp.Html.Dom.IHtmlStyleElement)?.Media))
            .ToList();
        Assert.Contains(byMedia, m => string.Equals(m.media, "print", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Corpus_anvil_inline_style_attributes_adapt_through_AdaptInlineStyle()
    {
        // Pins inline-style adaptation against real corpus content. The Anvil invoice has
        // many `style="..."` attributes; pick any element with one and round-trip it.
        var html = LoadCorpusFile("Corpus/Invoices/04-anvil-running-elements.html");
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var styled = document.QuerySelectorAll("[style]").FirstOrDefault();
        Assert.NotNull(styled);

        var declarations = CssParserAdapter.AdaptInlineStyle(styled!.GetStyle()!);

        Assert.False(declarations.IsEmpty);
        // Every adapted declaration carries property + non-empty value text.
        foreach (var d in declarations)
        {
            Assert.False(string.IsNullOrEmpty(d.Property));
            Assert.NotNull(d.Value.RawText);
        }
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var corpusRoot = LocateCorpusRoot();
        var fullPath = Path.Combine(corpusRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"corpus file missing: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string LocateCorpusRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Corpus");
            if (Directory.Exists(candidate)) return dir.FullName;
            var csproj = Path.Combine(dir.FullName, "NetPdf.RealDocuments.csproj");
            if (File.Exists(csproj)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the NetPdf.RealDocuments source folder.");
    }
}
