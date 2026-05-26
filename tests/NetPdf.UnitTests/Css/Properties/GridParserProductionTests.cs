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
/// Phase 3 Task 17 cycle 0b + post-PR-#90 review F7 — production-pipeline
/// tests for the grid CSS parser/resolver. The direct
/// <see cref="GridParserTests"/> bypass stylesheet parsing + cascade
/// selection + BoxBuilder; this fixture exercises the grid AST through
/// the FULL pipeline:
///
/// <para>HTML → <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>VarResolver</c> → <c>BoxBuilder</c> → <c>box.Style.ReadGridXxx()</c>.</para>
///
/// <para><b>What this fixture catches that the direct tests don't</b>:
/// (a) CSS-wide keywords (initial / inherit / unset / revert / revert-layer)
/// that the cascade SHOULD intercept reaching the resolver; (b) preprocessor
/// drop-recovery interactions for grid declarations; (c) the cascade's
/// initial-value substitution when the resolver returns Invalid (= property
/// reverts to <c>none</c> / <c>auto</c>); (d) the <c>Deferred</c> state for
/// relative-unit declarations surviving the cascade with raw text preserved
/// in <see cref="ComputedStyle.TryGetDeferred"/>.</para>
///
/// <para>The layouter (cycle 1+) isn't invoked — these tests prove the
/// data is on the box's <see cref="ComputedStyle"/>, which is the
/// hand-off point cycle 1's <c>GridLayouter</c> will read from.</para>
/// </summary>
public sealed class GridParserProductionTests
{
    [Fact]
    public async Task Production_html_grid_template_rows_lands_AST_on_box_style()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 200px;
                    grid-template-columns: 50px 1fr;
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        var rows = grid.Style.ReadGridTemplateRows();
        Assert.Equal(2, rows.Items.Length);
        Assert.Equal(100.0, ((TrackListEntry)rows.Items[0]).Entry.LengthPx);
        Assert.Equal(200.0, ((TrackListEntry)rows.Items[1]).Entry.LengthPx);

        var cols = grid.Style.ReadGridTemplateColumns();
        Assert.Equal(2, cols.Items.Length);
        Assert.Equal(50.0, ((TrackListEntry)cols.Items[0]).Entry.LengthPx);
        Assert.Equal(GridTrackKind.Fr, ((TrackListEntry)cols.Items[1]).Entry.Kind);
    }

    [Fact]
    public async Task Production_html_grid_line_placement_lands_AST_on_item_style()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item-a { grid-row-start: 2; grid-column-start: 1; grid-column-end: span 2; }
            </style></head><body>
            <div class="grid"><div class="item-a"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item-a");
        var rowStart = item.Style.ReadGridRowStart();
        Assert.Equal(GridLineKind.LineNumber, rowStart.Kind);
        Assert.Equal(2, rowStart.LineNumber);

        var colStart = item.Style.ReadGridColumnStart();
        Assert.Equal(GridLineKind.LineNumber, colStart.Kind);
        Assert.Equal(1, colStart.LineNumber);

        var colEnd = item.Style.ReadGridColumnEnd();
        Assert.Equal(GridLineKind.Span, colEnd.Kind);
        Assert.Equal(2, colEnd.LineNumber);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("inherit")]
    [InlineData("unset")]
    public async Task Production_html_CSS_wide_keyword_does_not_pollute_grid_line_AST(string cssWide)
    {
        // Pre-hardening, `grid-row-start: initial` would silently end up as
        // a named-line "initial" GridLineValue. Whether the cascade intercepts
        // OR the resolver rejects (F3 defense-in-depth), the end state must
        // be the property's initial value (= auto).
        var html = $$"""
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row-start: {{cssWide}}; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowStart().Kind);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("inherit")]
    [InlineData("unset")]
    public async Task Production_html_CSS_wide_keyword_does_not_pollute_track_list_AST(string cssWide)
    {
        var html = $$"""
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; grid-template-rows: {{cssWide}}; }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        Assert.Same(TrackList.None, grid.Style.ReadGridTemplateRows());
    }

    [Fact]
    public async Task Production_html_relative_units_defer_with_raw_text_preserved()
    {
        // Per F5 — em / rem / vw need layout-time context. The Deferred
        // state preserves raw text via ComputedStyle.TryGetDeferred.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-columns: 12rem 1fr;
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        Assert.True(grid.Style.IsDeferred(PropertyId.GridTemplateColumns));
        Assert.True(grid.Style.TryGetDeferred(PropertyId.GridTemplateColumns, out var raw));
        Assert.Contains("12rem", raw);
        // Reader falls back to None (no AST yet); layout-time re-resolution
        // is cycle 1+ scope.
        Assert.Same(TrackList.None, grid.Style.ReadGridTemplateColumns());
    }

    [Fact]
    public async Task Production_html_spec_example_auto_fill_minmax_rem_defers_cleanly()
    {
        // The CSS Grid L1 §7.2.3 spec example. Pre-F5 hardening this was
        // diagnosed as malformed; post-F5 it defers without diagnostic.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-columns: repeat(auto-fill, minmax(25ch, 1fr));
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        Assert.True(grid.Style.IsDeferred(PropertyId.GridTemplateColumns));
    }

    [Fact]
    public async Task Production_html_invalid_declaration_reverts_to_property_default()
    {
        // Per cascade L4 §4.4 — invalid-at-computed-value-time reverts to
        // the property's initial value.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: -100px;
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        Assert.Same(TrackList.None, grid.Style.ReadGridTemplateRows());
    }

    [Fact]
    public async Task Production_html_malformed_grid_line_reverts_to_auto_without_throwing()
    {
        // Pre-F1 hardening — `grid-row-start: @` reached the validating-
        // factory which threw ArgumentException, aborting rendering.
        // Post-F1 it cleanly reverts to auto.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row-start: @; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowStart().Kind);
    }

    [Fact]
    public async Task Production_html_bare_zero_in_minmax_is_accepted()
    {
        // Per F4 — minmax(0, 1fr) is the canonical "fr distribution that
        // ignores content min-width" pattern.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        var cols = grid.Style.ReadGridTemplateColumns();
        Assert.Equal(2, cols.Items.Length);
        foreach (var item in cols.Items)
        {
            var entry = ((TrackListEntry)item).Entry;
            Assert.Equal(GridTrackKind.MinMax, entry.Kind);
            Assert.Equal(0.0, entry.MinSubLengthPx);
        }
    }

    [Fact]
    public async Task Production_html_auto_fill_with_fr_is_rejected_to_default()
    {
        // Per F2 — auto-fill restricts to <fixed-size>. fr is not fixed.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-columns: repeat(auto-fill, 1fr);
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        Assert.Same(TrackList.None, grid.Style.ReadGridTemplateColumns());
    }

    [Fact]
    public async Task Production_html_auto_fill_with_fixed_minmax_lands_AST()
    {
        // Per F2 — minmax(100px, 1fr) has a fixed min side, so it qualifies
        // as <fixed-size>. The spec-canonical responsive recipe.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-columns: repeat(auto-fill, minmax(100px, 1fr));
                }
            </style></head><body>
            <div class="grid"></div>
            </body></html>
            """;

        var grid = await FindGridContainerAsync(html);
        var cols = grid.Style.ReadGridTemplateColumns();
        var repeat = ((TrackListRepeat)Assert.Single(cols.Items)).Repeat;
        Assert.Equal(0, repeat.Count);  // auto-fill marker
    }

    // ================================================================
    //  Pipeline driver — mirrors FlexLayouterProductionTests.
    // ================================================================

    private static async Task<Box> FindGridContainerAsync(string html)
    {
        var root = await BuildBoxTreeAsync(html);
        return FindBoxOfKind(root, BoxKind.GridContainer)
            ?? throw new System.InvalidOperationException(
                "no GridContainer box found in tree");
    }

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

    private static Box? FindBoxOfKind(Box root, BoxKind kind)
    {
        if (root.Kind == kind) return root;
        foreach (var child in root.Children)
        {
            var found = FindBoxOfKind(child, kind);
            if (found is not null) return found;
        }
        return null;
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
