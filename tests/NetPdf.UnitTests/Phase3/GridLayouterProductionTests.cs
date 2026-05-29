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

    // =====================================================================
    //  PR-#92 review F1 — grid item contents are laid out
    // =====================================================================

    [Fact]
    public async Task Production_html_grid_item_with_nested_block_emits_inner_fragment()
    {
        // Per F1 — pre-fix grid items emitted only an empty rectangle;
        // nested block content was silently dropped. Post-fix the
        // sub-BlockLayouter walks each item + emits per-child fragments
        // at cell-relative offsets translated to fragmentainer
        // coordinates.
        //
        // Markup: 1x1 grid with one item containing a nested div.
        // The nested div should produce its own fragment INSIDE the
        // cell (= inline offset relative to cell start).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 200px;
                    grid-template-columns: 200px;
                }
                .item {}
                .inner { height: 50px; width: 100px; }
            </style></head><body>
            <div class="grid">
              <div class="item"><div class="inner"></div></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var item = FindByClass(sink, "item");
        var inner = FindByClass(sink, "inner");
        Assert.NotNull(item);
        Assert.NotNull(inner);

        // Item cell at (0, 0, 200, 200).
        Assert.Equal(0.0, item!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, item.Value.BlockOffset, precision: 3);
        // Inner div lives INSIDE the cell at cell-relative (0, 0); the
        // sub-BlockLayouter's TranslatingFragmentSink shifted it to the
        // cell's absolute origin.
        Assert.Equal(0.0, inner!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, inner.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, inner.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_grid_item_with_multiple_children_emits_all_fragments()
    {
        // Stacked nested blocks inside an item — sub-BlockLayouter
        // walks both + emits each at the right cell-relative offset.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 200px;
                    grid-template-columns: 200px;
                }
                .first { height: 50px; width: 200px; }
                .second { height: 60px; width: 150px; }
            </style></head><body>
            <div class="grid">
              <div class="item">
                <div class="first"></div>
                <div class="second"></div>
              </div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var first = FindByClass(sink, "first");
        var second = FindByClass(sink, "second");
        Assert.NotNull(first);
        Assert.NotNull(second);

        // First nested block: at cell origin.
        Assert.Equal(0.0, first!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, first.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, first.Value.BlockSize, precision: 3);
        // Second stacks below first inside cell.
        Assert.Equal(0.0, second!.Value.InlineOffset, precision: 3);
        Assert.Equal(50.0, second.Value.BlockOffset, precision: 3);
        Assert.Equal(60.0, second.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_two_cells_with_inner_content_each_translate_correctly()
    {
        // Two cells side-by-side, each with its own inner block. Tests
        // the TranslatingFragmentSink correctly anchors each cell's
        // inner content at its own cell's origin.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 100px 100px;
                }
                .leftInner { height: 40px; }
                .rightInner { height: 60px; }
            </style></head><body>
            <div class="grid">
              <div class="cell-a"><div class="leftInner"></div></div>
              <div class="cell-b"><div class="rightInner"></div></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var leftInner = FindByClass(sink, "leftInner");
        var rightInner = FindByClass(sink, "rightInner");
        Assert.NotNull(leftInner);
        Assert.NotNull(rightInner);

        // leftInner lives inside cell-a at (0, 0).
        Assert.Equal(0.0, leftInner!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, leftInner.Value.BlockOffset, precision: 3);
        // rightInner lives inside cell-b at (100, 0).
        Assert.Equal(100.0, rightInner!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, rightInner.Value.BlockOffset, precision: 3);
    }

    // =====================================================================
    //  PR-#92 review F2 — auto-height grid wrapper grows; no overlap
    // =====================================================================

    [Fact]
    public async Task Production_html_auto_height_grid_grows_and_does_not_overlap_following_sibling()
    {
        // Per F2 — pre-fix an auto-height grid (= no height declared)
        // kept its wrapper at chrome-only extent; a following block-flow
        // sibling overlapped the grid rows. Post-fix the pre-measure
        // grows the wrapper to the row-track sum, so the sibling lands
        // BELOW the grid's natural extent.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 200px;
                    grid-template-columns: 100px;
                }
                .item {}
                .following { height: 30px; width: 200px; }
            </style></head><body>
            <div class="grid">
              <div class="item"></div>
            </div>
            <div class="following"></div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var following = FindByClass(sink, "following");
        Assert.NotNull(following);
        // Grid's natural block extent = 100 + 200 = 300px (sum of row
        // tracks). The following sibling should land at block-offset
        // 300 (no overlap), NOT at 0.
        Assert.Equal(300.0, following!.Value.BlockOffset, precision: 3);
    }

    // =====================================================================
    //  Cycle 2 — fr tracks via §11.7
    // =====================================================================

    [Fact]
    public async Task Production_html_fr_tracks_distribute_leftover_per_spec()
    {
        // grid-template-columns: 100px 1fr 2fr in 600px container:
        // nonFlexBase = 100, leftover = 500, flexFactorSum = 3,
        // hypoFr ≈ 166.67, col2 = 166.67, col3 = 333.33.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: 100px 1fr 2fr;
                    width: 600px;
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
                .b { grid-row-start: 1; grid-column-start: 2; }
                .c { grid-row-start: 1; grid-column-start: 3; }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="b"></div>
              <div class="c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);

        // a (100px fixed) at (0, 0, 100, 50)
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, a.Value.InlineSize, precision: 2);
        // b (1fr = 500/3 ≈ 166.67) starts at 100
        Assert.Equal(100.0, b!.Value.InlineOffset, precision: 2);
        Assert.Equal(500.0 / 3, b.Value.InlineSize, precision: 2);
        // c (2fr = 1000/3 ≈ 333.33) starts at 100 + 500/3 = 800/3 ≈ 266.67
        Assert.Equal(100.0 + 500.0 / 3, c!.Value.InlineOffset, precision: 2);
        Assert.Equal(1000.0 / 3, c.Value.InlineSize, precision: 2);
    }

    // =====================================================================
    //  Cycle 3 — intrinsic sizing (auto / min-content / max-content)
    // =====================================================================

    [Fact]
    public async Task Production_html_auto_row_sizes_from_item_height()
    {
        // Per cycle 3 — auto row track size = max declared height of
        // items placed at that row.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: auto;
                    grid-template-columns: 200px;
                    height: 200px;
                    width: 200px;
                }
                .item { height: 75px; }
            </style></head><body>
            <div class="grid">
              <div class="item"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var item = FindByClass(sink, "item");
        Assert.NotNull(item);
        // Auto row sized to item's 75px height.
        Assert.Equal(75.0, item!.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_auto_plus_fr_redistributes_after_intrinsic()
    {
        // grid-template-rows: auto 1fr with explicit height 400.
        // Row 1 (auto) gets item1's height=100; row 2 (fr) gets 300.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: auto 1fr;
                    grid-template-columns: 200px;
                    width: 200px;
                    height: 400px;
                }
                .row1 { grid-row-start: 1; height: 100px; }
                .row2 { grid-row-start: 2; }
            </style></head><body>
            <div class="grid">
              <div class="row1"></div>
              <div class="row2"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var r1 = FindByClass(sink, "row1");
        var r2 = FindByClass(sink, "row2");
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(100.0, r1!.Value.BlockSize, precision: 3);
        Assert.Equal(300.0, r2!.Value.BlockSize, precision: 3);
        Assert.Equal(100.0, r2.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Production_html_auto_height_fr_row_emits_indefinite_diagnostic()
    {
        // Per PR-#93 review F3 — auto-height grid with fr rows →
        // diagnostic + fr rows collapse to 0 (= cycle 3 will ship
        // intrinsic resolution).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 1fr;
                    grid-template-columns: 100px;
                }
            </style></head><body>
            <div class="grid">
              <div class="row1"></div>
              <div class="row2"></div>
            </div>
            </body></html>
            """;

        var (sink, diag, _) = await RenderViaFullPipelineAsync(html);

        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridFrUnderIndefiniteApproximated001);
    }

    [Fact]
    public async Task Production_html_two_equal_fr_tracks_split_equally()
    {
        // grid-template-columns: 1fr 1fr in the fragmentainer's
        // available inline space (= 600px default in
        // RenderViaFullPipelineAsync) → 300/300.
        //
        // NB: cycle-1/2 BlockLayouter doesn't yet apply CSS `width` to
        // size a block container's inline extent (= TODO cycle 3 per
        // BlockLayouter.cs comments); the container takes the
        // fragmentainer-available inline space minus margins. So
        // testing fr distribution uses the fragmentainer width as the
        // container extent.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: 1fr 1fr;
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
                .b { grid-row-start: 1; grid-column-start: 2; }
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

        // 600 / 2 = 300 each.
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(300.0, a.Value.InlineSize, precision: 3);
        Assert.Equal(300.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(300.0, b.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_empty_grid_template_emits_via_implicit_only_grid()
    {
        // Per PR-#103 review F1 — no grid-template-rows / -columns
        // with items is now an implicit-only grid (cycle 6). Items
        // lay out via the seeded 1×1 implicit grid + cycle-grown
        // implicit rows.
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
        // Item fragments now emit through the implicit-only grid.
        Assert.NotNull(FindByClass(sink, "a"));
        Assert.NotNull(FindByClass(sink, "b"));
    }

    // ====================================================================
    //  Cycle 4 — minmax() / fit-content() / repeat(integer) via real CSS
    // ====================================================================

    [Fact]
    public async Task Production_html_minmax_with_length_growth_limit_clamps()
    {
        // grid-template-columns: minmax(100px, 200px) — Maximize fills
        // up to growth limit (200) but not beyond. Container is 600px
        // (fragmentainer default; cycle-3 BlockLayouter doesn't apply
        // CSS width yet to grid containers). Free space 600-100=500,
        // headroom 100, ratio=1.0 → track grows to 200; remaining
        // 400px goes unallocated (no fr / auto stretch in cycle 4).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: minmax(100px, 200px);
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
            </div>
            </body></html>
            """;

        var (sink, diag, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        Assert.NotNull(a);
        Assert.Equal(200.0, a!.Value.InlineSize, precision: 3);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    [Fact]
    public async Task Production_html_minmax_with_fr_max_distributes_via_fr()
    {
        // grid-template-columns: minmax(100px, 1fr) 1fr — fragmentainer
        // 600px. Both fr; track 1 has min=100 floor.
        //   §11.7 pass 1: nonFlexBase=0, leftover=600, sum=2, hypoFr=300.
        //     Track 1: base 100 > 300×1=300? NO. Track 2: 0 > 300? NO.
        //     Distribute: track 1 = max(100, 300) = 300. Track 2 = 300.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: minmax(100px, 1fr) 1fr;
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
                .b { grid-row-start: 1; grid-column-start: 2; }
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
        Assert.Equal(300.0, a!.Value.InlineSize, precision: 3);
        Assert.Equal(300.0, b!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_fit_content_clamps_to_limit()
    {
        // grid-template-columns: fit-content(150px) 1fr — fragmentainer 600px.
        //   Item A has declared width 250 → max-content contribution = 250.
        //   §7.2.2: min(limit=150, max-content=250) = 150. Track 1 = 150.
        //   Track 2 fr: leftover = 600 - 150 = 450 → track 2 = 450.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: fit-content(150px) 1fr;
                }
                .a { grid-row-start: 1; grid-column-start: 1; width: 250px; }
                .b { grid-row-start: 1; grid-column-start: 2; }
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
        Assert.Equal(150.0, a!.Value.InlineSize, precision: 3);
        Assert.Equal(450.0, b!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_repeat_integer_expands_inline()
    {
        // grid-template-columns: repeat(3, 100px) — expands to
        //   100px 100px 100px → 3 tracks of 100, total 300, container
        //   has 600-300=300px of unused space.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: repeat(3, 100px);
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
                .b { grid-row-start: 1; grid-column-start: 2; }
                .c { grid-row-start: 1; grid-column-start: 3; }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="b"></div>
              <div class="c"></div>
            </div>
            </body></html>
            """;

        var (sink, diag, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, a.Value.InlineSize, precision: 3);
        Assert.Equal(100.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, b.Value.InlineSize, precision: 3);
        Assert.Equal(200.0, c!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, c.Value.InlineSize, precision: 3);
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridTrackKindUnsupported001);
    }

    // ====================================================================
    //  Cycle 4 post-PR-#95 hardening — production pipeline tests for the
    //  fr-removal + invalid-CSS + percentage-diag scenarios.
    // ====================================================================

    [Fact]
    public async Task Production_html_minmax_fr_removal_step_freezes_when_min_too_large()
    {
        // grid-template-columns: minmax(300px, 1fr) 1fr in 600px fragmentainer.
        //   Track 1: base=300 (min), IsFr=true factor=1.
        //   Track 2: base=0, IsFr=true factor=1.
        //   Pass 1: nonFlex=0, leftover=600, sum=2, hypoFr=300.
        //     Track 1: base 300 > 300×1=300? NO (boundary). Distributed=300, no growth.
        //   No freeze → converged. Track 1 stays at max(300, 300)=300. Track 2 = 300.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: minmax(300px, 1fr) 1fr;
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
                .b { grid-row-start: 1; grid-column-start: 2; }
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
        Assert.Equal(300.0, a!.Value.InlineSize, precision: 3);
        Assert.Equal(300.0, b!.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Production_html_minmax_invalid_min_exceeds_max_treats_max_as_min()
    {
        // grid-template-columns: minmax(200px, 100px) — invalid per §11.5.
        //   max=min → track sits at 200.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: minmax(200px, 100px);
                }
                .a { grid-row-start: 1; grid-column-start: 1; }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        Assert.NotNull(a);
        Assert.Equal(200.0, a!.Value.InlineSize, precision: 3);
    }

    // ====================================================================
    //  Cycle 5b — BlockLayouter dispatch activation: production-pipeline
    //  multi-page grid pagination via the full HTML → cascade → BoxBuilder
    //  → BlockLayouter → GridLayouter chain.
    // ====================================================================

    // Per PR-#97 review F6+F7 — the Skip-pinned production tests that
    // existed in cycle 5b initial draft were REMOVED. They documented
    // a target state ("cycle 5c will activate this") but lived under
    // a Production_html_ prefix suggesting working coverage. Cycle 5c
    // will add real production-pipeline tests when the architectural
    // fixes (F1+F2+F3 deferrals — wrapper rollback / emitted-extent
    // contract / explicit-height handling) land.
    //
    // The cycle-5b ship is contract-additive only: DispatchGridInner
    // gains allowPagination + incomingContinuation params (safe
    // defaults); IsPaginatableGrid predicate exists; the F5
    // BlockLayouter symmetric validation for misrouted GridContinuation
    // ships. Direct GridLayouter resume tests (= the Cycle5_* series)
    // continue to verify the inner contract end-to-end.

    [Fact]
    public async Task Production_html_grid_fitting_on_one_page_stays_AllDone_no_continuation()
    {
        // Single-page-fit case — pagination active but no split needed.
        // Verifies the no-allocation path (= F4 + cycle 5b dispatch
        // doesn't allocate cache or emit continuation when grid fits).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 100px;
                }
                .r1 { grid-row-start: 1; grid-column-start: 1; }
            </style></head><body>
            <div class="grid">
              <div class="r1"></div>
            </div>
            </body></html>
            """;

        var (sink, _, result, _) = await RenderViaFullPipelineWithResultAsync(html, contentInlineSize: 100, blockSize: 800);

        Assert.Equal(LayoutAttemptOutcome.AllDone, result.Outcome);
        Assert.Null(result.Continuation);
        Assert.NotNull(FindByClass(sink, "r1"));
    }

    // ====================================================================
    //  Cycle 5c.2d — recursive-site grid pagination via production
    //  pipeline. HTML → cascade → BoxBuilder → BlockLayouter
    //  exercises the EmitBlockSubtreeRecursive grid dispatch (=
    //  the `<body><div class="grid">...</div></body>` shape hits
    //  the recursive site, not the outer site). Cycle 5c.2d wires
    //  the recursive site with the same clamp + F2 + continuation
    //  propagation pattern as the outer site (cycles 5c.2a–c).
    // ====================================================================

    [Fact]
    public async Task Cycle5c2d_recursive_auto_height_grid_paginates_at_row_boundary()
    {
        // Production-pipeline test: an auto-height grid inside
        // <body> with 4 rows of 100px each on a 350px page. The
        // grid is reached via EmitBlockSubtreeRecursive's grid
        // dispatch branch (= the recursive site wired in cycle
        // 5c.2d). The clamp shrinks the wrapper, F2 resizes it
        // to the emitted-rows extent, and the page-complete
        // BlockContinuation carries the GridContinuation up so
        // the pipeline driver can resume on the next page.
        //
        // Auto-height grid (= no `height` declared) with 4 rows
        // of 100 → natural extent 400. Page 350. Recursive-site
        // clamp shrinks borderBoxBlockSize to 350; GridLayouter
        // with allowPagination=true emits rows 0+1+2 (= 300 ≤ 350)
        // + returns PageComplete with GridContinuation(RowIndex=3,
        // EmittedBlockExtent=300). F2 resizes wrapper to 300.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px 100px 100px;
                    grid-template-columns: 100px;
                }
                .r1 { grid-row-start: 1; grid-column-start: 1; }
                .r2 { grid-row-start: 2; grid-column-start: 1; }
                .r3 { grid-row-start: 3; grid-column-start: 1; }
                .r4 { grid-row-start: 4; grid-column-start: 1; }
            </style></head><body>
            <div class="grid">
              <div class="r1"></div>
              <div class="r2"></div>
              <div class="r3"></div>
              <div class="r4"></div>
            </div>
            </body></html>
            """;

        var (sink, _, result, _) = await RenderViaFullPipelineWithResultAsync(
            html, contentInlineSize: 100, blockSize: 350);

        // Page 1 returns PageComplete with a BlockContinuation
        // chain that contains a GridContinuation somewhere (=
        // wrapped by the recursive-site propagation up through
        // the recursion chain to the outer AttemptLayout).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.NotNull(result.Continuation);

        // Walk the chain to find the GridContinuation leaf.
        GridContinuation? gridLeaf = FindGridContinuationLeaf(result.Continuation);
        Assert.NotNull(gridLeaf);
        Assert.Equal(3, gridLeaf!.RowIndex);

        // Page 1 emits rows 1-3 (= r1, r2, r3); r4 deferred to
        // the next page via the GridContinuation.
        Assert.NotNull(FindByClass(sink, "r1"));
        Assert.NotNull(FindByClass(sink, "r2"));
        Assert.NotNull(FindByClass(sink, "r3"));
        Assert.Null(FindByClass(sink, "r4"));
    }

    [Fact]
    public async Task Cycle5c2d_recursive_explicit_height_grid_stays_atomic_pending_F3()
    {
        // Per PR-#101 review P1#1 — explicit-height grids stay
        // atomic at the recursive site too (= same
        // IsHeightAuto(child) gate as the outer site).
        // Strengthened post-PR-#102 review P2#2 to also assert
        // authored wrapper geometry preserved + diagnostic on
        // unavoidable forced-overflow.
        //
        // Fixture: height: 200px grid with 2 rows of 100px on a
        // 150px page → wrapper at authored 200 > page 150 →
        // forced-overflow path (= grid is body's only direct
        // child + body is at top of fresh page so no break-
        // before is productive).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px;
                    grid-template-columns: 100px;
                    height: 200px;
                }
                .r1 { grid-row-start: 1; grid-column-start: 1; }
                .r2 { grid-row-start: 2; grid-column-start: 1; }
            </style></head><body>
            <div class="grid">
              <div class="r1"></div>
              <div class="r2"></div>
            </div>
            </body></html>
            """;

        var (sink, diag, result, _) = await RenderViaFullPipelineWithResultAsync(
            html, contentInlineSize: 100, blockSize: 150);

        // No GridContinuation in the result chain — F3 isn't
        // active for explicit-height; the grid renders atomic.
        var gridLeaf = result.Continuation is null
            ? null
            : FindGridContinuationLeaf(result.Continuation);
        Assert.Null(gridLeaf);
        // Both grid items emitted atomically (= no row drop).
        Assert.NotNull(FindByClass(sink, "r1"));
        Assert.NotNull(FindByClass(sink, "r2"));
        // Per P2#2 — authored wrapper geometry preserved (= 200,
        // not clamped to 150). Look up by class since the grid
        // div doesn't have a class; find by SourceElement tag
        // name. Easier: just assert at least one fragment has
        // BlockSize == 200 (= the grid wrapper).
        Assert.Contains(sink.Fragments, f => Math.Abs(f.BlockSize - 200.0) < 0.001);
        // Per P2#2 — unavoidable forced-overflow on a fresh
        // 150px page produces PaginationForcedOverflow001 (the
        // outer BlockLayouter's forced-overflow diagnostic; the
        // grid is dispatched atomically per IsHeightAuto gate).
        Assert.Contains(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.PaginationForcedOverflow001);
    }

    [Fact]
    public async Task Cycle5c2d_post_PR_102_P1_recursive_grid_breaks_before_after_sibling()
    {
        // Per PR-#102 review P1#1 — when a paginatable grid
        // follows a normal block sibling and the first grid row
        // can't fit the remaining space but COULD fit a fresh
        // page, the recursive site must defer the grid cleanly
        // (= no wrapper emit + no LayoutGridForcedOverflow001).
        // Pre-fix the recursive site had no F1 mechanism; the
        // forced-overflow path force-emitted the first row past
        // the page boundary.
        //
        // Fixture (Roland's exact repro):
        //   <body>
        //     <div class="before"></div>  (height: 200px)
        //     <div class="grid">          (rows: 100px 100px)
        //       <div class="r1"></div>
        //       <div class="r2"></div>
        //     </div>
        //   </body>
        //   Page 250.
        //
        // Expected post-F1-at-recursive-site:
        //   - Page 1 emits .before (the 200px sibling) but NOT
        //     the grid wrapper.
        //   - Result.Outcome == PageComplete with chained
        //     BlockContinuation → GridContinuation(RowIndex=0).
        //   - No LayoutGridForcedOverflow001 diagnostic emitted.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .before { height: 200px; }
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px;
                    grid-template-columns: 100px;
                }
                .r1 { grid-row-start: 1; grid-column-start: 1; }
                .r2 { grid-row-start: 2; grid-column-start: 1; }
            </style></head><body>
            <div class="before"></div>
            <div class="grid">
              <div class="r1"></div>
              <div class="r2"></div>
            </div>
            </body></html>
            """;

        var (sink, diag, result, _) = await RenderViaFullPipelineWithResultAsync(
            html, contentInlineSize: 100, blockSize: 250);

        // Page 1 emits the .before sibling.
        Assert.NotNull(FindByClass(sink, "before"));
        // But NOT the grid items (= clean break-before via F1).
        Assert.Null(FindByClass(sink, "r1"));
        Assert.Null(FindByClass(sink, "r2"));
        // Result is PageComplete with a chained
        // BlockContinuation → GridContinuation(RowIndex=0).
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        Assert.NotNull(result.Continuation);
        var gridLeaf = FindGridContinuationLeaf(result.Continuation!);
        Assert.NotNull(gridLeaf);
        Assert.Equal(0, gridLeaf!.RowIndex);
        // No forced-overflow diagnostic (= clean defer, NOT
        // force-emit past page edge).
        Assert.DoesNotContain(diag.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridForcedOverflow001);
    }

    [Fact]
    public async Task Cycle5c2d_post_PR_102_P2_multi_page_resume_emits_all_rows_exactly_once()
    {
        // Per PR-#102 review P2#1 — the prior cycle-5c.2d test
        // only exercised page 1. This test feeds the
        // PageComplete continuation into a second AttemptLayout
        // (= page 2) + asserts the full multi-page output:
        //   - Page 1 emits rows r1, r2, r3 (= 300px of 350px).
        //   - Page 2 emits row r4 exactly once.
        //   - Page 2 returns AllDone (= no further continuation).
        //   - No grid item appears on both pages (= no duplicate).
        //   - Wrapper on page 1 sized to 300 (emitted extent), on
        //     page 2 sized to 100 (single remaining row).
        //
        // 4-row auto-height grid inside <body> on 350px pages.
        // Drives page-loop manually until AllDone or maxPages.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px 100px 100px;
                    grid-template-columns: 100px;
                }
                .r1 { grid-row-start: 1; grid-column-start: 1; }
                .r2 { grid-row-start: 2; grid-column-start: 1; }
                .r3 { grid-row-start: 3; grid-column-start: 1; }
                .r4 { grid-row-start: 4; grid-column-start: 1; }
            </style></head><body>
            <div class="grid">
              <div class="r1"></div>
              <div class="r2"></div>
              <div class="r3"></div>
              <div class="r4"></div>
            </div>
            </body></html>
            """;

        var pages = await RenderMultiPageAsync(
            html, contentInlineSize: 100, blockSize: 350, maxPages: 4);

        // Two pages exactly.
        Assert.Equal(2, pages.Count);

        // Page 1: rows 1, 2, 3 emit; row 4 deferred.
        var page1 = pages[0];
        Assert.NotNull(FindByClass(page1.Sink, "r1"));
        Assert.NotNull(FindByClass(page1.Sink, "r2"));
        Assert.NotNull(FindByClass(page1.Sink, "r3"));
        Assert.Null(FindByClass(page1.Sink, "r4"));
        Assert.Equal(LayoutAttemptOutcome.PageComplete, page1.Result.Outcome);

        // Page 2: only row 4 emits; AllDone.
        var page2 = pages[1];
        Assert.Null(FindByClass(page2.Sink, "r1"));
        Assert.Null(FindByClass(page2.Sink, "r2"));
        Assert.Null(FindByClass(page2.Sink, "r3"));
        Assert.NotNull(FindByClass(page2.Sink, "r4"));
        Assert.Equal(LayoutAttemptOutcome.AllDone, page2.Result.Outcome);

        // F2 wrapper sizing per page: page 1 wrapper at 300 (=
        // rows 1+2+3 = 300), page 2 wrapper at 100 (= row 4 only).
        // The grid wrapper is the BoxFragment with the GridContainer
        // box kind. Find by kind across each page's sink.
        var page1Wrapper = FindGridWrapper(page1.Sink);
        var page2Wrapper = FindGridWrapper(page2.Sink);
        Assert.NotNull(page1Wrapper);
        Assert.NotNull(page2Wrapper);
        Assert.Equal(300.0, page1Wrapper!.Value.BlockSize, precision: 3);
        Assert.Equal(100.0, page2Wrapper!.Value.BlockSize, precision: 3);
    }

    /// <summary>Per Phase 3 Task 17 cycle 5c.2d post-PR-#102
    /// review P2#1 — page-loop driver. Calls
    /// <c>BlockLayouter.AttemptLayout</c> repeatedly, feeding
    /// each PageComplete's BlockContinuation back as the next
    /// page's <c>incomingContinuation</c>, until AllDone or
    /// <paramref name="maxPages"/>. Mirrors what the production
    /// document driver would do.</summary>
    private static async Task<List<(RecordingFragmentSink Sink, LayoutAttemptResult Result)>>
        RenderMultiPageAsync(
            string html,
            double contentInlineSize,
            double blockSize,
            int maxPages)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        var box = BoxBuilder.Build(document, resolved);

        var pages = new List<(RecordingFragmentSink, LayoutAttemptResult)>();
        LayoutContinuation? incoming = null;
        for (var pageIdx = 0; pageIdx < maxPages; pageIdx++)
        {
            var sink = new RecordingFragmentSink();
            var diagSink = new RecordingDiagnosticsSink();
            using var shaper = new SyntheticShaperResolver();
            using var layouter = new BlockLayouter(
                rootBox: box,
                sink: sink,
                incomingContinuation: incoming,
                diagnostics: diagSink,
                shaperResolver: shaper);
            var ctx = new FragmentainerContext(
                contentInlineSize: contentInlineSize, blockSize: blockSize);
            var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
            using var resolver = new BreakResolver();
            var result = layouter.AttemptLayout(
                ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

            pages.Add((sink, result));

            if (result.Outcome == LayoutAttemptOutcome.AllDone) break;
            if (result.Continuation is null) break;
            incoming = result.Continuation;
        }
        return pages;
    }

    /// <summary>Per Phase 3 Task 17 cycle 5c.2d post-PR-#102
    /// review P2#1 — locate the grid wrapper fragment in a
    /// sink. The wrapper is the unique BoxFragment whose
    /// <c>Box.Kind</c> is <c>GridContainer</c>.</summary>
    private static BoxFragment? FindGridWrapper(RecordingFragmentSink sink)
    {
        foreach (var f in sink.Fragments)
        {
            if (f.Box.Kind == BoxKind.GridContainer) return f;
        }
        return null;
    }

    /// <summary>Per Phase 3 Task 17 cycle 5c.2d — walk a
    /// BlockContinuation chain looking for a
    /// <see cref="GridContinuation"/> leaf. Mirrors the
    /// continuation-chain walks the production pipeline performs
    /// when resuming a paginated grid. Returns the first
    /// GridContinuation found in the chain or
    /// <see langword="null"/> when none exists.</summary>
    private static GridContinuation? FindGridContinuationLeaf(LayoutContinuation continuation)
    {
        var current = continuation;
        while (current is not null)
        {
            if (current is GridContinuation grid) return grid;
            if (current is BlockContinuation block)
            {
                current = block.LayouterState as LayoutContinuation;
                continue;
            }
            return null;
        }
        return null;
    }

    // ====================================================================
    //  Phase 3 Task 18 cycle 6 — production HTML pipeline for span /
    //  implicit tracks / grid-auto-flow
    // ====================================================================

    [Fact]
    public async Task Cycle6_production_grid_row_2_slash_span_2_spans_two_rows()
    {
        // Real HTML with `grid-row: 2 / span 2` flows through the full
        // pipeline (CSS preprocessing + cascade + BoxBuilder +
        // BlockLayouter dispatch + GridLayouter emission) and produces
        // a single fragment spanning rows 2-3 of the explicit grid.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px 100px 150px;
                    grid-template-columns: 200px;
                    width: 200px;
                    height: 300px;
                }
                .span { grid-row: 2 / span 2; grid-column: 1; }
            </style></head><body>
                <div class="grid">
                    <div class="span"></div>
                </div>
            </body></html>
            """;

        var (sink, diagSink, _) = await RenderViaFullPipelineAsync(html);

        var span = FindByClass(sink, "span");
        Assert.NotNull(span);
        // Item at row index 1 (= 1-based line 2), span 2 → rows 1+2.
        // BlockOffset = row 0's height (50). BlockSize = sum of rows
        // 1 (100) + row 2 (150) = 250.
        Assert.Equal(50, span!.Value.BlockOffset, precision: 3);
        Assert.Equal(250, span.Value.BlockSize, precision: 3);
        // Inline geometry: single 200px col, item occupies all of it.
        Assert.Equal(200, span.Value.InlineSize, precision: 3);
        Assert.DoesNotContain(diagSink.Diagnostics, d =>
            d.Code == PaginateDiagnosticCodes.LayoutGridPlacementApproximated001);
    }

    [Fact]
    public async Task Cycle6_production_grid_auto_rows_50px_sizes_implicit_rows()
    {
        // Real HTML using `grid-auto-rows: 50px` to size implicit rows.
        // Item with grid-row-start: 3 lands at row index 2 — past the
        // 1-row explicit grid; the implicit-track generator creates
        // rows 1 and 2 sized 50px each from grid-auto-rows.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 200px;
                    grid-auto-rows: 50px;
                    width: 200px;
                    height: 200px;
                }
                .implicit { grid-row-start: 3; grid-column-start: 1; }
            </style></head><body>
                <div class="grid">
                    <div class="implicit"></div>
                </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var implicitItem = FindByClass(sink, "implicit");
        Assert.NotNull(implicitItem);
        // Row 0 (100) + row 1 implicit (50) = blockOffset 150 for row 2.
        Assert.Equal(150, implicitItem!.Value.BlockOffset, precision: 3);
        Assert.Equal(50, implicitItem.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Cycle6b_production_spanning_item_defers_atomically_across_pages()
    {
        // Per Phase 3 Task 18 cycle 6b — a spanning item that would
        // straddle a page break defers entirely to the resume page.
        // 3-row auto-height grid: row 1 = single item; rows 2+3 =
        // spanning item B. Budget = 200px (= 2 rows). The naive
        // (cycle-5) computation would emit row 1 + row 2 on page 1,
        // straddling B's rectangle. Cycle 6b rewinds endRowExclusive
        // to row 1 (= B's start) so the entire B defers to page 2.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px 100px;
                    grid-template-columns: 100px;
                }
                .a    { grid-row: 1; grid-column: 1; }
                .span { grid-row: 2 / span 2; grid-column: 1; }
            </style></head><body>
            <div class="grid">
                <div class="a"></div>
                <div class="span"></div>
            </div>
            </body></html>
            """;

        var pages = await RenderMultiPageAsync(
            html, contentInlineSize: 100, blockSize: 250, maxPages: 4);

        Assert.Equal(2, pages.Count);

        // Page 1: only A emits (span deferred per cycle 6b atomicity).
        var page1 = pages[0];
        Assert.NotNull(FindByClass(page1.Sink, "a"));
        Assert.Null(FindByClass(page1.Sink, "span"));
        Assert.Equal(LayoutAttemptOutcome.PageComplete, page1.Result.Outcome);

        // Page 2: span emits entirely (rectangle spanning both rows
        // 2+3 of the grid).
        var page2 = pages[1];
        Assert.Null(FindByClass(page2.Sink, "a"));
        var spanFragment = FindByClass(page2.Sink, "span");
        Assert.NotNull(spanFragment);
        // BlockSize = sum of spanned rows (100 + 100 = 200).
        Assert.Equal(200.0, spanFragment!.Value.BlockSize, precision: 3);
        Assert.Equal(LayoutAttemptOutcome.AllDone, page2.Result.Outcome);
    }

    [Fact]
    public async Task Cycle6_production_grid_auto_flow_column_orders_items_column_major()
    {
        // Real HTML with `grid-auto-flow: column` causes 4 auto-placed
        // items to fill column-first (= items 0+1 in col 0, items 2+3
        // in col 1) rather than row-first.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px;
                    grid-template-columns: 100px 100px;
                    grid-auto-flow: column;
                    width: 200px;
                    height: 200px;
                }
                .a, .b, .c, .d { }
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

        var a = FindByClass(sink, "a"); var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c"); var d = FindByClass(sink, "d");
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(c); Assert.NotNull(d);

        // Column-major order: a@(0,0), b@(1,0), c@(0,1), d@(1,1).
        Assert.Equal(0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0, a.Value.BlockOffset, precision: 3);
        Assert.Equal(0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(100, b.Value.BlockOffset, precision: 3);
        Assert.Equal(100, c!.Value.InlineOffset, precision: 3);
        Assert.Equal(0, c.Value.BlockOffset, precision: 3);
        Assert.Equal(100, d!.Value.InlineOffset, precision: 3);
        Assert.Equal(100, d.Value.BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 18 cycle 7a — grid-template-areas + grid-area
    // ====================================================================

    [Fact]
    public async Task Cycle7a_production_grid_template_areas_lays_out_named_items()
    {
        // Real HTML with `grid-template-areas` + `grid-area: name` on
        // children. Items lay out in the named rectangles per CSS Grid
        // §7.3 + §8.4. The grid-area shorthand expander (cycle 0c)
        // routes the name to all four longhands; cycle 7a's placement
        // service resolves it via grid-template-areas.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px 50px;
                    grid-template-columns: 100px 100px;
                    grid-template-areas:
                        "head head"
                        "main side"
                        "foot foot";
                }
                .h { grid-area: head; }
                .m { grid-area: main; }
                .s { grid-area: side; }
                .f { grid-area: foot; }
            </style></head><body>
            <div class="grid">
                <div class="h"></div>
                <div class="m"></div>
                <div class="s"></div>
                <div class="f"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var h = FindByClass(sink, "h");
        var m = FindByClass(sink, "m");
        var s = FindByClass(sink, "s");
        var f = FindByClass(sink, "f");
        Assert.NotNull(h); Assert.NotNull(m); Assert.NotNull(s); Assert.NotNull(f);

        // head: row 0, cols 0+1 → 200×100 at (0, 0).
        Assert.Equal(0, h!.Value.InlineOffset, precision: 3);
        Assert.Equal(0, h.Value.BlockOffset, precision: 3);
        Assert.Equal(200, h.Value.InlineSize, precision: 3);
        Assert.Equal(100, h.Value.BlockSize, precision: 3);
        // main: row 1, col 0 → 100×100 at (0, 100).
        Assert.Equal(0, m!.Value.InlineOffset, precision: 3);
        Assert.Equal(100, m.Value.BlockOffset, precision: 3);
        Assert.Equal(100, m.Value.InlineSize, precision: 3);
        Assert.Equal(100, m.Value.BlockSize, precision: 3);
        // side: row 1, col 1 → 100×100 at (100, 100).
        Assert.Equal(100, s!.Value.InlineOffset, precision: 3);
        Assert.Equal(100, s.Value.BlockOffset, precision: 3);
        Assert.Equal(100, s.Value.InlineSize, precision: 3);
        Assert.Equal(100, s.Value.BlockSize, precision: 3);
        // foot: row 2, cols 0+1 → 200×50 at (0, 200).
        Assert.Equal(0, f!.Value.InlineOffset, precision: 3);
        Assert.Equal(200, f.Value.BlockOffset, precision: 3);
        Assert.Equal(200, f.Value.InlineSize, precision: 3);
        Assert.Equal(50, f.Value.BlockSize, precision: 3);
    }

    // ====================================================================
    //  PR-#105 review F6 — cross-feature integration: named-area
    //  placement (cycle 7a) + spanning-pagination atomicity (cycle 6b).
    // ====================================================================

    [Fact]
    public async Task F6_named_area_spanning_two_rows_defers_atomically_across_pages()
    {
        // grid-template-areas declares a `tall` area spanning rows 2+3.
        // Each row is 100px; page budget = 150px fits row 1 + half of
        // row 2 naively. Per cycle 6b's atomicity contract, the
        // `tall` spanning item defers entirely to page 2; per cycle
        // 7a's named-area resolution, the placement comes from the
        // areas map (= no fallback path). Proves both features
        // integrate cleanly.
        // No explicit grid `height` — exercises cycle 6a's auto-
        // height paginatable path (= the `grid-explicit-height-
        // paginate-deferral` is intentionally avoided here).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px 100px;
                    grid-template-columns: 100px;
                    grid-template-areas:
                        "head"
                        "tall"
                        "tall";
                    width: 100px;
                }
                .h { grid-area: head; }
                .t { grid-area: tall; }
            </style></head><body>
                <div class="grid">
                    <div class="h"></div>
                    <div class="t"></div>
                </div>
            </body></html>
            """;

        var pages = await RenderMultiPageAsync(
            html, contentInlineSize: 100, blockSize: 150, maxPages: 4);

        Assert.True(pages.Count >= 2,
            $"Expected ≥ 2 pages; got {pages.Count}");

        // Page 1: only `head` emits (tall defers atomically per cycle
        // 6b). If cycle 6b's atomicity contract had broken, tall
        // would have started on page 1 with its rectangle straddling
        // the page break.
        var headOnPage1 = FindByClass(pages[0].Sink, "h");
        var tallOnPage1 = FindByClass(pages[0].Sink, "t");
        Assert.NotNull(headOnPage1);
        Assert.Null(tallOnPage1);

        // Page 2: `tall` emits at its full 200px span. If cycle 7a's
        // named-area resolution had fallen back to auto, the item
        // would have landed at a single cell, not the 200px named
        // area.
        var tallOnPage2 = FindByClass(pages[1].Sink, "t");
        Assert.NotNull(tallOnPage2);
        Assert.Equal(200, tallOnPage2!.Value.BlockSize, precision: 3);
        Assert.Equal(100, tallOnPage2.Value.InlineSize, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 18 cycle 7d — `grid-auto-flow: dense` via real CSS.
    // ====================================================================

    [Fact]
    public async Task Cycle7d_production_dense_fills_earlier_hole()
    {
        // Real HTML with `grid-auto-flow: dense`. An explicitly-placed
        // item pinned at col 3 of row 1 leaves cells (1,1) and (1,2)
        // open; auto-placed items rewind to fill them.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px;
                    grid-template-columns: 50px 50px 50px;
                    grid-auto-flow: dense;
                    width: 150px;
                    height: 200px;
                }
                .pinned { grid-row: 1; grid-column: 3; }
            </style></head><body>
                <div class="grid">
                    <div class="pinned"></div>
                    <div class="a"></div>
                    <div class="b"></div>
                </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var pinned = FindByClass(sink, "pinned");
        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        Assert.NotNull(pinned);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // pinned at col 2 (offset 100); a + b at cols 0 + 1.
        Assert.Equal(100, pinned!.Value.InlineOffset, precision: 3);
        Assert.Equal(0, pinned.Value.BlockOffset, precision: 3);
        Assert.Equal(0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0, a.Value.BlockOffset, precision: 3);
        Assert.Equal(50, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(0, b.Value.BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 18 cycle 7c — repeat(auto-fill, …) /
    //  repeat(auto-fit, …) via real CSS.
    // ====================================================================

    [Fact]
    public async Task Cycle7c_production_repeat_auto_fill_derives_count_from_layout_extent()
    {
        // Per PR-#107 review F1 #1 — even when the grid has `width:
        // auto` (= the Width slot is Keyword(auto) rather than a
        // LengthPx), the BlockLayouter passes a FINITE content
        // extent that the GridLayouter uses for auto-fill count
        // derivation. The fragmentainer here is 600px so a
        // `repeat(auto-fill, 100px)` yields 6 columns; three items
        // fill cols 0/1/2 of row 0.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px;
                    grid-template-columns: repeat(auto-fill, 100px);
                }
            </style></head><body>
                <div class="grid">
                    <div class="a"></div>
                    <div class="b"></div>
                    <div class="c"></div>
                </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);

        // Each item lands at a separate 100px-wide column on row 0.
        Assert.Equal(100, a!.Value.InlineSize, precision: 3);
        Assert.Equal(100, b!.Value.InlineSize, precision: 3);
        Assert.Equal(100, c!.Value.InlineSize, precision: 3);
        Assert.Equal(0, a.Value.InlineOffset, precision: 3);
        Assert.Equal(100, b.Value.InlineOffset, precision: 3);
        Assert.Equal(200, c.Value.InlineOffset, precision: 3);
        // All on the explicit row 0.
        Assert.Equal(0, a.Value.BlockOffset, precision: 3);
        Assert.Equal(0, b.Value.BlockOffset, precision: 3);
        Assert.Equal(0, c.Value.BlockOffset, precision: 3);
    }

    // ====================================================================
    //  Phase 3 Task 18 cycle 7b — named-line placement via real HTML.
    // ====================================================================

    [Fact]
    public async Task Cycle7b_production_named_lines_resolve_via_real_css()
    {
        // Real HTML with `[header-start]` / `[header-end]` author named
        // lines in grid-template-rows. Items reference the lines via
        // `grid-row-start: header-start` (= cycle 7b lookup).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: [top] 100px [middle] 100px [bottom] 100px [end];
                    grid-template-columns: 100px;
                    width: 100px;
                }
                .a { grid-row: top / middle; }
                .b { grid-row: middle / bottom; }
                .c { grid-row: bottom / end; }
            </style></head><body>
                <div class="grid">
                    <div class="a"></div>
                    <div class="b"></div>
                    <div class="c"></div>
                </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        var c = FindByClass(sink, "c");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);

        // top = line 1; middle = line 2; bottom = line 3; end = line 4.
        // a: top / middle → row 0; b: middle / bottom → row 1;
        // c: bottom / end → row 2.
        Assert.Equal(0, a!.Value.BlockOffset, precision: 3);
        Assert.Equal(100, a.Value.BlockSize, precision: 3);
        Assert.Equal(100, b!.Value.BlockOffset, precision: 3);
        Assert.Equal(100, b.Value.BlockSize, precision: 3);
        Assert.Equal(200, c!.Value.BlockOffset, precision: 3);
        Assert.Equal(100, c.Value.BlockSize, precision: 3);
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

    /// <summary>Per Phase 3 Task 17 cycle 5b — pipeline driver
    /// returning the BlockLayouter result so tests can assert on
    /// PageComplete / continuation propagation.</summary>
    private static async Task<(RecordingFragmentSink sink,
        RecordingDiagnosticsSink diagnostics,
        LayoutAttemptResult result, Box root)>
        RenderViaFullPipelineWithResultAsync(
            string html,
            double contentInlineSize = 600,
            double blockSize = 800)
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

        var ctx = new FragmentainerContext(contentInlineSize: contentInlineSize, blockSize: blockSize);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);

        return (sink, diagSink, result, box);
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

        public void UpdateFragmentBlockSize(int cursor, double newBlockSize)
        {
            // Per Phase 3 Task 17 cycle 5c.2b — F2 wrapper-resize.
            // Mutate the BoxFragment at <c>cursor</c> in place so the
            // BlockLayouter's post-dispatch wrapper-resize consumer
            // can shrink a paginatable-grid / paginatable-flex
            // wrapper from the clamped budget to the actual emitted
            // extent without breaking z-order (= the wrapper stays
            // ahead of its children in the fragment list).
            if (cursor < 0 || cursor >= Fragments.Count) return;
            Fragments[cursor] = Fragments[cursor] with { BlockSize = newBlockSize };
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
