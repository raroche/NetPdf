// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using NetPdf;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Phase 3 Task 17 cycle 0c — production-pipeline tests for the grid
/// shorthand expansions (<c>grid-row</c> / <c>grid-column</c> /
/// <c>grid-area</c>). Exercises the FULL pipeline (HTML →
/// <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>BoxBuilder</c> → <c>box.Style.ReadGridXxx()</c>) to verify the
/// shorthand expansion lands the right longhand values at the cascade.
/// </summary>
public sealed class GridShorthandProductionTests
{
    // =====================================================================
    //  grid-row shorthand
    // =====================================================================

    [Fact]
    public async Task Grid_row_with_two_integers_lands_both_longhands()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2 / 4; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridRowStart();
        Assert.Equal(GridLineKind.LineNumber, start.Kind);
        Assert.Equal(2, start.LineNumber);

        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.LineNumber, end.Kind);
        Assert.Equal(4, end.LineNumber);
    }

    [Fact]
    public async Task Grid_row_with_single_integer_pairs_to_auto()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
    }

    [Fact]
    public async Task Grid_row_with_single_custom_ident_duplicates_to_end()
    {
        // Per §8.4 — a bare custom-ident in the shorthand duplicates to
        // the omitted end longhand.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: header; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridRowStart();
        Assert.Equal(GridLineKind.NamedLine, start.Kind);
        Assert.Equal("header", start.NamedLine);

        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.NamedLine, end.Kind);
        Assert.Equal("header", end.NamedLine);
    }

    [Fact]
    public async Task Grid_row_with_span_in_end_position()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2 / span 3; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);

        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.Span, end.Kind);
        Assert.Equal(3, end.LineNumber);
    }

    // =====================================================================
    //  grid-column shorthand (= same grammar as grid-row)
    // =====================================================================

    [Fact]
    public async Task Grid_column_with_two_integers_lands_both_longhands()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-column: 1 / 3; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(1, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnEnd().LineNumber);
    }

    [Fact]
    public async Task Grid_column_single_value_with_omitted_pair()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-column: span 2; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridColumnStart();
        Assert.Equal(GridLineKind.Span, start.Kind);
        Assert.Equal(2, start.LineNumber);
        // span is reserved; omitted end falls back to auto.
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    // =====================================================================
    //  grid-area shorthand
    // =====================================================================

    [Fact]
    public async Task Grid_area_four_values_lands_all_four_longhands()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3 / 4 / 5; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
        Assert.Equal(5, item.Style.ReadGridColumnEnd().LineNumber);
    }

    [Fact]
    public async Task Grid_area_single_ident_duplicates_to_all_four()
    {
        // Per §8.4 fallback — a bare custom-ident replicates to all 4
        // longhands. Named-area resolution (= matching against
        // grid-template-areas) is cycle 7's scope; cycle 0c just gets
        // the ident to all four longhands so the named-line resolver
        // sees it.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: header; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal("header", item.Style.ReadGridRowStart().NamedLine);
        Assert.Equal("header", item.Style.ReadGridColumnStart().NamedLine);
        Assert.Equal("header", item.Style.ReadGridRowEnd().NamedLine);
        Assert.Equal("header", item.Style.ReadGridColumnEnd().NamedLine);
    }

    [Fact]
    public async Task Grid_area_two_values_omit_per_spec()
    {
        // 2 / 3 → row-start: 2; column-start: 3; row-end: <2> = auto;
        // column-end: <3> = auto (= integers don't duplicate).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    [Fact]
    public async Task Grid_area_two_idents_duplicate_per_spec()
    {
        // foo / bar → row-start: foo; column-start: bar; row-end: foo;
        // column-end: bar (= idents duplicate).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: foo / bar; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal("foo", item.Style.ReadGridRowStart().NamedLine);
        Assert.Equal("bar", item.Style.ReadGridColumnStart().NamedLine);
        Assert.Equal("foo", item.Style.ReadGridRowEnd().NamedLine);
        Assert.Equal("bar", item.Style.ReadGridColumnEnd().NamedLine);
    }

    [Fact]
    public async Task Grid_area_three_values_omit_column_end_per_spec()
    {
        // 2 / 3 / 4 → column-end omitted, falls back to <3> = auto.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3 / 4; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    // =====================================================================
    //  Cascade interaction — shorthand vs explicit longhand source order
    // =====================================================================

    [Fact]
    public async Task Explicit_longhand_after_shorthand_wins_per_cascade_order()
    {
        // Per CSS Cascade §7.4 — later declaration wins at the same
        // specificity / origin. Shorthand expands first, then explicit
        // longhand overrides. Mirrors L17's pattern (= ExplicitLonghandRef
        // tracking in the preprocessor).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row: 2 / 4;
                    grid-row-end: 6;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Shorthand set start to 2 (still valid).
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        // Explicit grid-row-end: 6 should override the shorthand's 4.
        Assert.Equal(6, item.Style.ReadGridRowEnd().LineNumber);
    }

    [Fact]
    public async Task Shorthand_after_explicit_longhand_wins()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row-end: 6;
                    grid-row: 2 / 4;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Shorthand wins both longhands since it came last.
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
    }

    // ================================================================
    //  Pipeline driver — mirrors GridParserProductionTests.
    // ================================================================

    private static async Task<Box> FindBoxByClassAsync(string html, string className)
    {
        var root = await BuildBoxTreeAsync(html);
        return FindBoxByClass(root, className)
            ?? throw new System.InvalidOperationException(
                $"no box with class '{className}' found in tree");
    }

    private static async Task<Box> BuildBoxTreeAsync(string html)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        return BoxBuilder.Build(document, resolved);
    }

    private static Box? FindBoxByClass(Box root, string className)
    {
        var el = root.SourceElement;
        if (el is not null)
        {
            var classAttr = el.GetAttribute("class");
            if (classAttr is not null
                && System.Array.IndexOf(classAttr.Split(' '), className) >= 0)
            {
                return root;
            }
        }
        foreach (var child in root.Children)
        {
            var found = FindBoxByClass(child, className);
            if (found is not null) return found;
        }
        return null;
    }

    private static ImmutableArray<CssStylesheet> AdaptAllSheetsViaPreprocessor(IDocument document)
    {
        var output = ImmutableArray.CreateBuilder<CssStylesheet>();
        var order = 0;
        var styleElements = document.QuerySelectorAll("style");
        var styleIdx = 0;
        foreach (var rawSheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            string rawText;
            if (styleIdx < styleElements.Length)
            {
                rawText = styleElements[styleIdx].TextContent ?? string.Empty;
                styleIdx++;
            }
            else
            {
                rawText = string.Empty;
            }
            var preprocess = string.IsNullOrEmpty(rawText)
                ? CssPreprocessResult.Empty
                : CssPreprocessor.Process(rawText);
            output.Add(CssParserAdapter.Adapt(
                rawSheet, preprocess,
                href: null,
                origin: CssStylesheetOrigin.Author,
                ownerKind: CssStylesheetOwnerKind.StyleElement,
                mediaQuery: null,
                isDisabled: false,
                order: order++));
        }
        return output.ToImmutable();
    }
}
