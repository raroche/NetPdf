// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
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
using NetPdf.Layout.Inline;
using NetPdf.Layout.Layouters;
using NetPdf.Paginate;
using NetPdf.Paginate.Diagnostics;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 17 cycle 1 (Hello World) — production-pipeline tests
/// for <see cref="GridLayouter"/>. The existing
/// <see cref="GridLayouterTests"/> construct box trees directly
/// (bypassing BoxBuilder); this fixture exercises grid through the
/// FULL pipeline:
///
/// <para>HTML → <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>VarResolver</c> → <c>BoxBuilder</c> → <c>BlockLayouter</c>
/// (which dispatches into <c>GridLayouter</c> for any
/// <see cref="BoxKind.GridContainer"/> /
/// <see cref="BoxKind.InlineGridContainer"/> child).</para>
///
/// <para>Coverage delivered: <c>DisplayMapper</c> resolves
/// <c>display: grid</c> into <see cref="BoxKind.GridContainer"/>; the
/// BoxBuilder produces a GridContainer box for the grid element + a
/// BlockContainer for each item; the BlockLayouter dispatch's
/// <c>IsGridContainer</c> predicate fires + the GridLayouter emits
/// per-item fragments at the expected cell offsets.</para>
/// </summary>
public sealed class GridLayouterProductionTests
{
    [Fact]
    public async Task Production_html_div_with_display_grid_lays_out_explicit_2x2()
    {
        // Per cycle 1 — a real HTML <div> with `display: grid` containing
        // 4 explicitly-placed items flows through every stage of the
        // pipeline + emits per-item content fragments at the expected
        // cell offsets.
        //
        // Grid: rows = 100px 200px, columns = 50px 150px.
        // Items at (1,1), (1,2), (2,1), (2,2).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 200px;
                    grid-template-columns: 50px 150px;
                    width: 200px;
                    height: 300px;
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
                .b { grid-row-start: 1; grid-column-start: 2; }
                .c { grid-row-start: 2; grid-column-start: 1; }
                .d { grid-row-start: 2; grid-column-start: 2; }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="b"></div>
              <div class="c"></div>
              <div class="d"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var gridFragment = FindByClass(sink, "grid", BoxKind.GridContainer);
        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c");
        var d = FindByClass(sink, "d");

        Assert.NotNull(gridFragment);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.NotNull(d);

        // Grid is at the page origin (no margins/borders on .grid).
        // a: (1, 1) → (0, 0, 50, 100)
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, a.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, a.Value.InlineSize, precision: 3);
        Assert.Equal(100.0, a.Value.BlockSize, precision: 3);
        // b: (1, 2) → (50, 0, 150, 100)
        Assert.Equal(50.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, b.Value.BlockOffset, precision: 3);
        Assert.Equal(150.0, b.Value.InlineSize, precision: 3);
        Assert.Equal(100.0, b.Value.BlockSize, precision: 3);
        // c: (2, 1) → (0, 100, 50, 200)
        Assert.Equal(0.0, c!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, c.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, c.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, c.Value.BlockSize, precision: 3);
        // d: (2, 2) → (50, 100, 150, 200)
        Assert.Equal(50.0, d!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, d.Value.BlockOffset, precision: 3);
        Assert.Equal(150.0, d.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, d.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_sparse_auto_placement_fills_row_major()
    {
        // 4 auto-placed items (no grid-row-start / grid-column-start)
        // in a 2×2 grid → fill row-major per §8.5 sparse mode.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 200px;
                    grid-template-columns: 50px 150px;
                    width: 200px;
                    height: 300px;
                }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="b"></div>
              <div class="c"></div>
              <div class="d"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c");
        var d = FindByClass(sink, "d");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.NotNull(d);

        // a → (1, 1) = (0, 0, 50, 100)
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, a.Value.BlockOffset, precision: 3);
        // b → (1, 2) = (50, 0, 150, 100)
        Assert.Equal(50.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, b.Value.BlockOffset, precision: 3);
        // c → (2, 1) = (0, 100, 50, 200)
        Assert.Equal(0.0, c!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, c.Value.BlockOffset, precision: 3);
        // d → (2, 2) = (50, 100, 150, 200)
        Assert.Equal(50.0, d!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, d.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Production_html_grid_shorthand_lands_placement_via_cycle_0c_expander()
    {
        // Tests the cycle-0c shorthand interaction: `grid-row: 1 / 2`
        // expands to `grid-row-start: 1; grid-row-end: 2`. Cycle 1's
        // layouter uses only grid-row-start, ignoring -end (= cycle 6+
        // ships span semantics). So this tests that grid-row-start: 1
        // correctly applies after shorthand expansion.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 200px;
                    grid-template-columns: 50px 150px;
                }
                .item { grid-row: 2; grid-column: 1; }
            </style></head><body>
            <div class="grid">
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var item = FindByClass(sink, "item");
        Assert.NotNull(item);
        // grid-row: 2 → grid-row-start: 2 → row index 1 → block offset 100.
        // grid-column: 1 → grid-column-start: 1 → col index 0 → inline offset 0.
        Assert.Equal(0.0, item!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, item.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, item.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, item.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_mixed_explicit_and_auto_placement()
    {
        // Item A is explicitly placed at (1, 2). The auto-placed items
        // fill the remaining cells per §8.5 sparse mode (skipping the
        // pre-occupied cell).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px;
                    grid-template-columns: 100px 100px;
                }
                .a { grid-row-start: 1; grid-column-start: 2; }
            </style></head><body>
            <div class="grid">
              <div class="x"></div>
              <div class="a"></div>
              <div class="y"></div>
              <div class="z"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var x = FindByClass(sink, "x");
        var a = FindByClass(sink, "a");
        var y = FindByClass(sink, "y");
        var z = FindByClass(sink, "z");
        Assert.NotNull(x);
        Assert.NotNull(a);
        Assert.NotNull(y);
        Assert.NotNull(z);

        // a (explicit) → (1, 2) = (100, 0)
        Assert.Equal(100.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, a.Value.BlockOffset, precision: 3);
        // x (auto) → finds (1, 1) free first = (0, 0)
        Assert.Equal(0.0, x!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, x.Value.BlockOffset, precision: 3);
        // y (auto) → cursor after (1,1) → (1,2) occupied → (2,1) = (0, 100)
        Assert.Equal(0.0, y!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, y.Value.BlockOffset, precision: 3);
        // z (auto) → (2, 2) = (100, 100)
        Assert.Equal(100.0, z!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, z.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Production_html_grid_with_padding_offsets_items_from_chrome()
    {
        // The grid container has padding; the per-item content fragments
        // should land INSIDE the padding (= content-box offset).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 100px 100px;
                    padding: 20px;
                    width: 200px;
                    height: 100px;
                }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="b"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        Assert.NotNull(a);
        Assert.NotNull(b);

        // Padding 20 on each side → content area starts at (20, 20).
        // a (auto → 1,1) = (20, 20)
        Assert.Equal(20.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(20.0, a.Value.BlockOffset, precision: 3);
        // b (auto → 1,2) = (120, 20)
        Assert.Equal(120.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(20.0, b.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Production_html_empty_grid_template_emits_no_item_fragments()
    {
        // No grid-template-rows / -columns → 0 explicit tracks → items
        // drop (cycle 1 doesn't generate implicit tracks).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="b"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // The grid wrapper fragment IS emitted by BlockLayouter.
        var gridFragment = FindByClass(sink, "grid", BoxKind.GridContainer);
        Assert.NotNull(gridFragment);
        // No item fragments though.
        Assert.Null(FindByClass(sink, "a"));
        Assert.Null(FindByClass(sink, "b"));
    }

    // ====================================================================
    //  Pipeline driver — mirrors FlexLayouterProductionTests.
    // ====================================================================

    private static BoxFragment? FindByClass(RecordingFragmentSink sink, string className, BoxKind? kind = null)
    {
        foreach (var f in sink.Fragments)
        {
            var el = f.Box.SourceElement;
            if (el is null) continue;
            var classAttr = el.GetAttribute("class");
            if (classAttr is null) continue;
            // Match the class as a whole-word among space-separated values.
            var found = false;
            foreach (var token in classAttr.Split(' '))
            {
                if (token == className) { found = true; break; }
            }
            if (!found) continue;
            if (kind is not null && f.Box.Kind != kind.Value) continue;
            return f;
        }
        return null;
    }

    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics, Box root)>
        RenderViaFullPipelineAsync(string html, double contentInlineSize = 600)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var box = BoxBuilder.Build(document, resolved);

        var sink = new RecordingFragmentSink();
        var diagSink = new RecordingDiagnosticsSink();
        using var shaper = new SyntheticShaperResolver();

        using var layouter = new BlockLayouter(
            rootBox: box,
            sink: sink,
            incomingContinuation: null,
            diagnostics: diagSink,
            shaperResolver: shaper);

        var ctx = new FragmentainerContext(contentInlineSize: contentInlineSize, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        return (sink, diagSink, box);
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

    private sealed class RecordingFragmentSink : IBlockFragmentSink
    {
        public List<BoxFragment> Fragments { get; } = new();
        public int Cursor => Fragments.Count;
        public void Emit(BoxFragment fragment) => Fragments.Add(fragment);
        public void RollbackTo(int cursor)
        {
            if (cursor < Fragments.Count)
            {
                Fragments.RemoveRange(cursor, Fragments.Count - cursor);
            }
        }
    }

    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    private sealed class SyntheticShaperResolver : IShaperResolver, System.IDisposable
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
