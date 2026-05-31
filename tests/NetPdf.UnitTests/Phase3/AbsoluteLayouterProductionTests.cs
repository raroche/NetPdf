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
/// Phase 3 Task 19 cycle 1 — production-pipeline tests for
/// <c>position: absolute</c>. Drives real HTML through
/// HTML → CssPreprocessor → CssParserAdapter → CascadeResolver →
/// BoxBuilder → BlockLayouter and asserts the emitted fragment
/// geometry. Cycle 1 anchors abspos boxes to the establishing block's
/// content box (= the fragmentainer content area / initial containing
/// block for the top-level case), explicit top/left/width/height only.
/// </summary>
public sealed class AbsoluteLayouterProductionTests
{
    [Fact]
    public async Task Cycle1_production_abspos_anchors_to_initial_containing_block()
    {
        // A position:absolute box as a direct child of <body> with no
        // positioned ancestor → anchored to the initial containing
        // block (= the fragmentainer content area). top:10 left:20
        // width:50 height:30 → fragment at (20, 10, 50, 30).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .abs {
                    position: absolute;
                    top: 10px; left: 20px;
                    width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="abs"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        Assert.Equal(20.0, abs!.Value.InlineOffset, precision: 3);
        Assert.Equal(10.0, abs.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, abs.Value.InlineSize, precision: 3);
        Assert.Equal(30.0, abs.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Cycle1_production_abspos_removed_from_flow()
    {
        // An abspos box between two in-flow blocks must not displace
        // them. flow1 (height 100) at block 0; flow2 (height 100) at
        // block 100 — the abspos box in between is out-of-flow.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flow1 { height: 100px; }
                .flow2 { height: 100px; }
                .abs {
                    position: absolute;
                    top: 300px; left: 0px;
                    width: 40px; height: 40px;
                }
            </style></head><body>
            <div class="flow1"></div>
            <div class="abs"></div>
            <div class="flow2"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var flow1 = FindByClass(sink, "flow1");
        var flow2 = FindByClass(sink, "flow2");
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(flow1);
        Assert.NotNull(flow2);
        Assert.NotNull(abs);
        // In-flow blocks stack contiguously; the abspos box doesn't
        // push flow2 down.
        Assert.Equal(0.0, flow1!.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, flow2!.Value.BlockOffset, precision: 3);
        // abspos at its anchored block offset.
        Assert.Equal(300.0, abs!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Cycle2b_production_abspos_auto_offsets_use_static_position()
    {
        // Per Phase 3 Task 19 cycle 2b — no top/left (both auto) is no
        // longer a deferral: both insets auto → static position (CB /
        // ICB origin 0,0). The box emits at (0, 0) sized 50×30.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .abs {
                    position: absolute;
                    width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="abs"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        Assert.Equal(0.0, abs!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, abs.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, abs.Value.InlineSize, precision: 3);
        Assert.Equal(30.0, abs.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Cycle2b_production_abspos_right_anchored()
    {
        // right:20 + width:50, left auto → left = ICB(600) - 20 - 50 =
        // 530. End-to-end right-anchoring through the §6 solver.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .abs {
                    position: absolute;
                    top: 5px; right: 20px;
                    width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="abs"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        Assert.Equal(530.0, abs!.Value.InlineOffset, precision: 3);
        Assert.Equal(5.0, abs.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, abs.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Cycle1_production_abspos_emitted_on_multipage_first_page()
    {
        // Per post-PR-#112 review C2 — when the in-flow content
        // overflows page 1 (forcing a PageComplete), the abspos box
        // must STILL be emitted on page 1. Pre-fix the abspos pass ran
        // only before the AllDone return, so any multi-page document
        // dropped every abspos fragment.
        //
        // Fixture: a tall in-flow block (1200px) on an 800px page →
        // page 1 returns PageComplete. The abspos box (top:5 left:5)
        // must appear in page 1's fragments.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .tall { height: 1200px; }
                .abs {
                    position: absolute;
                    top: 5px; left: 5px;
                    width: 40px; height: 40px;
                }
            </style></head><body>
            <div class="tall"></div>
            <div class="abs"></div>
            </body></html>
            """;

        var (sink, _, result) = await RenderWithResultAsync(html);
        // The in-flow tall block overflows → page 1 PageComplete.
        Assert.Equal(LayoutAttemptOutcome.PageComplete, result.Outcome);
        // The abspos box is STILL emitted on page 1 (C2 fix).
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        Assert.Equal(5.0, abs!.Value.InlineOffset, precision: 3);
        Assert.Equal(5.0, abs.Value.BlockOffset, precision: 3);
        Assert.Equal(40.0, abs.Value.InlineSize, precision: 3);
    }

    [Fact]
    public async Task Cycle2a_production_abspos_anchors_to_relative_parent()
    {
        // Per Phase 3 Task 19 cycle 2a — `position: absolute` inside a
        // `position: relative` parent anchors to the PARENT's padding
        // box, not the initial containing block. The relative parent
        // sits at block 100 (after a 100px spacer); the abspos child's
        // top:10 left:20 resolve relative to the parent's content/
        // padding-box origin (parent has no border → padding box ==
        // border box origin = (0, 100)).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .spacer { height: 100px; }
                .rel { position: relative; height: 300px; }
                .abs {
                    position: absolute;
                    top: 10px; left: 20px;
                    width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="spacer"></div>
            <div class="rel">
              <div class="abs"></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        // Anchored to .rel's padding box (origin 0, 100) + top/left.
        Assert.Equal(20.0, abs!.Value.InlineOffset, precision: 3); // 0 + 20
        Assert.Equal(110.0, abs.Value.BlockOffset, precision: 3);  // 100 + 10
        Assert.Equal(50.0, abs.Value.InlineSize, precision: 3);
        Assert.Equal(30.0, abs.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Cycle2a_production_abspos_in_grid_emits_once_no_displacement()
    {
        // Per post-PR-#113 review P1#2 — an abspos child of a grid
        // container is out-of-flow: it must NOT occupy a grid cell or
        // be emitted by the grid (it's emitted ONCE by the abspos
        // pass). The two real grid items (.a, .b) fill cols 1+2 of row
        // 1 undisplaced; the abspos box anchors to the ICB.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 100px 100px;
                    width: 200px;
                }
                .abs {
                    position: absolute;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="abs"></div>
              <div class="b"></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        // .a + .b are the only two grid items → cols 0 and 1. The
        // abspos box did NOT take a cell (else .b would be at row 2).
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, a.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, b.Value.BlockOffset, precision: 3);
        // The abspos box is emitted exactly once, at the ICB anchor.
        var absFrags = sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains("abs") == true)
            .ToList();
        Assert.Single(absFrags);
        Assert.Equal(5.0, absFrags[0].InlineOffset, precision: 3);
        Assert.Equal(5.0, absFrags[0].BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Cycle2a_production_abspos_in_flex_emits_once_no_displacement()
    {
        // Per post-PR-#113 review P1#2 — abspos child of a flex
        // container is out-of-flow: not a flex item, emitted once.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex { display: flex; width: 300px; }
                .a { width: 100px; height: 50px; }
                .b { width: 100px; height: 50px; }
                .abs {
                    position: absolute;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="flex">
              <div class="a"></div>
              <div class="abs"></div>
              <div class="b"></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        // .a at main-start 0; .b directly after at 100 (the abspos box
        // didn't consume a flex slot, else .b would be at 200).
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, b!.Value.InlineOffset, precision: 3);
        var absFrags = sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains("abs") == true)
            .ToList();
        Assert.Single(absFrags);
    }

    [Fact]
    public async Task PostPr114_abspos_in_positioned_grid_item_emits_once_anchored_to_item()
    {
        // Per post-PR-#114 review P2#4 — a POSITIONED grid item containing
        // an absolute child. The item's content is laid out by a NESTED
        // BlockLayouter (GridLayouter spawns one per item) whose abspos
        // pass owns the child; the outer pass must NOT also emit it. The
        // item is the second (row-2) grid cell → block origin 50, so the
        // abspos (top:5 left:5) anchored to the ITEM lands at (5, 55) —
        // proving it emitted ONCE and anchored to the positioned item
        // (not the page origin, which would be (5, 5)).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 50px 100px;
                    grid-template-columns: 100px;
                    width: 100px;
                }
                .item { position: relative; }
                .abs {
                    position: absolute;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="grid">
              <div class="pad"></div>
              <div class="item"><div class="abs"></div></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var absFrags = sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains("abs") == true)
            .ToList();
        Assert.Single(absFrags);
        Assert.Equal(5.0, absFrags[0].InlineOffset, precision: 3);
        Assert.Equal(55.0, absFrags[0].BlockOffset, precision: 3);  // item at block 50 + top 5
    }

    [Fact]
    public async Task PostPr114_abspos_in_grid_item_content_emits_once_not_doubled()
    {
        // Per post-PR-#114 review P2#4 — the genuine double-emit case: an
        // abspos box inside a (non-positioned) grid item's CONTENT with no
        // positioned ancestor. Pre-fix BOTH the grid item's nested
        // BlockLayouter AND the outer pass (which recursed into the item
        // subtree) emitted it → two fragments. The delegation boundary
        // stops the outer recursion at the grid item → exactly one.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 100px;
                    width: 100px;
                }
                .abs {
                    position: absolute;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="grid">
              <div class="item"><div class="abs"></div></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var absFrags = sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains("abs") == true)
            .ToList();
        Assert.Single(absFrags);
    }

    [Fact]
    public async Task PostPr114_abspos_in_flex_item_content_emits_once_not_dropped()
    {
        // Per post-PR-#114 review P2#4 — flex is DELIBERATELY NOT a
        // delegation boundary: FlexLayouter spawns no per-item nested
        // BlockLayouter (it emits item border boxes only), so an abspos
        // box inside a flex item's content is walked + emitted by the
        // OUTER pass. This guards against regressing flex into the
        // boundary (which would DROP the box → zero fragments). Exactly
        // one fragment expected, anchored to the page ICB at (5, 5).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex { display: flex; width: 300px; }
                .item { width: 100px; height: 50px; }
                .abs {
                    position: absolute;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="flex">
              <div class="item"><div class="abs"></div></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var absFrags = sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains("abs") == true)
            .ToList();
        Assert.Single(absFrags);
    }

    // ================================================================
    //  Phase 3 Task 20 cycle 1 — position: fixed (production pipeline).
    // ================================================================

    [Fact]
    public async Task Task20Cycle1_production_fixed_anchors_to_page_and_removed_from_flow()
    {
        // A `position: fixed` box is out-of-flow (siblings stack
        // contiguously, undisplaced) and anchored to the page / ICB
        // (top:10 left:20 → (20, 10)), exactly like abspos-against-ICB
        // but via the separate fixed pass.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flow1 { height: 100px; }
                .flow2 { height: 100px; }
                .fix {
                    position: fixed;
                    top: 10px; left: 20px; width: 50px; height: 30px;
                }
            </style></head><body>
            <div class="flow1"></div>
            <div class="fix"></div>
            <div class="flow2"></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var flow1 = FindByClass(sink, "flow1");
        var flow2 = FindByClass(sink, "flow2");
        var fix = FindByClass(sink, "fix");
        Assert.NotNull(flow1);
        Assert.NotNull(flow2);
        Assert.NotNull(fix);
        // The fixed box doesn't push flow2 down (out-of-flow).
        Assert.Equal(0.0, flow1!.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, flow2!.Value.BlockOffset, precision: 3);
        // Anchored to the page ICB.
        Assert.Equal(20.0, fix!.Value.InlineOffset, precision: 3);
        Assert.Equal(10.0, fix.Value.BlockOffset, precision: 3);
        Assert.Equal(50.0, fix.Value.InlineSize, precision: 3);
        Assert.Equal(30.0, fix.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task Task20Cycle1_production_fixed_inside_grid_item_anchors_to_page_once()
    {
        // A `position: fixed` box nested inside a grid ITEM's content
        // must anchor to the PAGE (ICB), NOT the grid item, and emit
        // exactly once. The grid item sits in row 2 (block origin 100);
        // a page-anchored fixed box (top:7 left:7) lands at (7, 7) — if
        // it had wrongly anchored to the item it would be at (7, 107).
        // The nested grid-item BlockLayouter (non-Root root) does NOT run
        // the fixed pass; the page-root layouter walks into the item.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px 100px;
                    grid-template-columns: 100px;
                    width: 100px;
                }
                .fix {
                    position: fixed;
                    top: 7px; left: 7px; width: 20px; height: 20px;
                }
            </style></head><body>
            <div class="grid">
              <div class="pad"></div>
              <div class="item"><div class="fix"></div></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var fixFrags = sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains("fix") == true)
            .ToList();
        Assert.Single(fixFrags);
        Assert.Equal(7.0, fixFrags[0].InlineOffset, precision: 3);  // page-anchored
        Assert.Equal(7.0, fixFrags[0].BlockOffset, precision: 3);   // NOT 107 (item)
    }

    // ----- post-PR-#115 review: collector exclusion + nested fixed -----

    [Fact]
    public async Task PostPr115_fixed_direct_child_of_grid_is_not_an_item_emits_once()
    {
        // P1 — a `position: fixed` direct child of a grid container is
        // out-of-flow: NOT a grid item (must not occupy a cell or pollute
        // track sizing) and emitted exactly ONCE by the fixed pass
        // (page-anchored). Pre-fix it was placed as an item (pushing .b
        // to the implicit row 2) AND emitted again by the fixed pass.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid {
                    display: grid;
                    grid-template-rows: 100px;
                    grid-template-columns: 100px 100px;
                    width: 200px;
                }
                .fix {
                    position: fixed;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="grid">
              <div class="a"></div>
              <div class="fix"></div>
              <div class="b"></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        Assert.NotNull(a);
        Assert.NotNull(b);
        // .a + .b are the ONLY grid items → row 1, cols 0 and 1 (the
        // fixed box took no cell, else .b would be at row 2 / block 100).
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, a.Value.BlockOffset, precision: 3);
        Assert.Equal(100.0, b!.Value.InlineOffset, precision: 3);
        Assert.Equal(0.0, b.Value.BlockOffset, precision: 3);
        var fixFrags = FragmentsByClass(sink, "fix");
        Assert.Single(fixFrags);
        Assert.Equal(5.0, fixFrags[0].InlineOffset, precision: 3);
        Assert.Equal(5.0, fixFrags[0].BlockOffset, precision: 3);
    }

    [Fact]
    public async Task PostPr115_fixed_direct_child_of_flex_is_not_an_item_emits_once()
    {
        // P1 — a `position: fixed` direct child of a flex container is
        // out-of-flow: not a flex item (takes no main-axis slot) and
        // emitted once. Pre-fix .b sat at main 200 (fixed consumed a
        // slot at 100); post-fix .b is directly after .a at 100.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex { display: flex; width: 300px; }
                .a { width: 100px; height: 50px; }
                .b { width: 100px; height: 50px; }
                .fix {
                    position: fixed;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="flex">
              <div class="a"></div>
              <div class="fix"></div>
              <div class="b"></div>
            </div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var a = FindByClass(sink, "a");
        var b = FindByClass(sink, "b");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(0.0, a!.Value.InlineOffset, precision: 3);
        Assert.Equal(100.0, b!.Value.InlineOffset, precision: 3);  // not 200
        Assert.Single(FragmentsByClass(sink, "fix"));
    }

    [Fact]
    public async Task PostPr115_fixed_inside_absolute_anchors_to_page_once()
    {
        // P2#1 — a `position: fixed` box nested inside a
        // `position: absolute` box must still be PAGE-anchored + emitted
        // exactly once (not dropped). The abspos box is at (200, 200);
        // the fixed box (top:5 left:5) anchors to the PAGE → (5, 5), NOT
        // (205, 205).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .abs {
                    position: absolute;
                    top: 200px; left: 200px; width: 100px; height: 100px;
                }
                .fix {
                    position: fixed;
                    top: 5px; left: 5px; width: 30px; height: 30px;
                }
            </style></head><body>
            <div class="abs"><div class="fix"></div></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var fixFrags = FragmentsByClass(sink, "fix");
        Assert.Single(fixFrags);
        Assert.Equal(5.0, fixFrags[0].InlineOffset, precision: 3);  // page, not 205
        Assert.Equal(5.0, fixFrags[0].BlockOffset, precision: 3);
    }

    [Fact]
    public async Task PostPr115_fixed_inside_fixed_both_emitted_page_anchored()
    {
        // P2#1 — a `position: fixed` box nested inside another fixed box
        // must also be page-anchored + emitted (not dropped). The outer
        // box is at (50, 50); the inner box (top:5 left:5) anchors to the
        // PAGE → (5, 5), NOT (55, 55).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .outer {
                    position: fixed;
                    top: 50px; left: 50px; width: 200px; height: 200px;
                }
                .inner {
                    position: fixed;
                    top: 5px; left: 5px; width: 20px; height: 20px;
                }
            </style></head><body>
            <div class="outer"><div class="inner"></div></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var outer = FragmentsByClass(sink, "outer");
        var inner = FragmentsByClass(sink, "inner");
        Assert.Single(outer);
        Assert.Single(inner);
        Assert.Equal(50.0, outer[0].InlineOffset, precision: 3);
        Assert.Equal(5.0, inner[0].InlineOffset, precision: 3);   // page, not 55
        Assert.Equal(5.0, inner[0].BlockOffset, precision: 3);
    }

    [Fact]
    public async Task Task20Cycle2_fixed_box_content_exceeding_height_overflows_at_natural_position()
    {
        // Task 20 cycle 2 — CSS Position L3 §6.3: fixed-positioned boxes
        // are NOT paginated, so content taller than the box OVERFLOWS at
        // its natural position (CSS overflow: visible), it is NOT clipped.
        // A fixed box (height 50) with two 40px children (80 > 50): BOTH
        // children emit — c1 at block 0, c2 at block 40 (overflowing the
        // 50px box) — via SuppressBlockPagination (no break), not an
        // inflated budget.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .fix {
                    position: fixed;
                    top: 0; left: 0; width: 100px; height: 50px;
                }
                .c1 { height: 40px; }
                .c2 { height: 40px; }
            </style></head><body>
            <div class="fix"><div class="c1"></div><div class="c2"></div></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var c1 = FindByClass(sink, "c1");
        var c2 = FindByClass(sink, "c2");
        Assert.NotNull(c1);
        Assert.NotNull(c2);                                    // NOT dropped — overflows
        Assert.Equal(0.0, c1!.Value.BlockOffset, precision: 3);
        Assert.Equal(40.0, c2!.Value.BlockOffset, precision: 3);  // overflows the 50px box
    }

    [Fact]
    public async Task PostPr116_abspos_child_of_fixed_box_resolves_against_box_not_budget()
    {
        // Post-PR-#116 review P1 — an absolutely-positioned child of a
        // fixed box must resolve against the FIXED BOX's content height,
        // NOT the (no-pagination) break budget. The fixed box is 50px
        // tall; .abs has bottom:0; height:10px → it anchors to the box
        // BOTTOM: block offset = 50 - 10 = 40. (With the rejected
        // inflated-budget approach it would have resolved near 1e9.)
        const string html = """
            <!DOCTYPE html><html><head><style>
                .fix {
                    position: fixed;
                    top: 100px; left: 0; width: 100px; height: 50px;
                }
                .abs {
                    position: absolute;
                    bottom: 0; left: 0; width: 20px; height: 10px;
                }
            </style></head><body>
            <div class="fix"><div class="abs"></div></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        // Fixed box at block 100; .abs bottom:0 height:10 → box-bottom -
        // height = 100 + (50 - 10) = 140. NOT ~1e9.
        Assert.Equal(140.0, abs!.Value.BlockOffset, precision: 3);
        Assert.Equal(10.0, abs.Value.BlockSize, precision: 3);
    }

    [Fact]
    public async Task PostPr116_abspos_child_of_fixed_box_height_percent_resolves_against_box()
    {
        // Post-PR-#116 review P1 — `height: 100%` on an abspos child of a
        // fixed box resolves against the FIXED BOX's content height (50),
        // not the no-pagination budget. So .abs height = 50, NOT ~1e9.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .fix {
                    position: fixed;
                    top: 0; left: 0; width: 100px; height: 50px;
                }
                .abs {
                    position: absolute;
                    top: 0; left: 0; width: 20px; height: 100%;
                }
            </style></head><body>
            <div class="fix"><div class="abs"></div></div>
            </body></html>
            """;

        var (sink, _) = await RenderAsync(html);
        var abs = FindByClass(sink, "abs");
        Assert.NotNull(abs);
        Assert.Equal(50.0, abs!.Value.BlockSize, precision: 3);   // 100% of box height, NOT 1e9
        Assert.Equal(0.0, abs.Value.BlockOffset, precision: 3);
    }

    private static System.Collections.Generic.List<BoxFragment> FragmentsByClass(
        RecordingFragmentSink sink, string className) =>
        sink.Fragments.Where(f =>
            f.Box.SourceElement?.GetAttribute("class")?.Split(' ').Contains(className) == true)
            .ToList();

    // NB: the PADDING-box border inset (CB = positioned ancestor's
    // padding box, not border box) is proven deterministically by the
    // BlockLayouterTests integration test
    // `Cycle2a_abspos_anchors_to_positioned_ancestor_padding_box`
    // (which sets BorderTopWidth/etc explicitly). A production-CSS
    // version is entangled with `border` shorthand + border-style
    // cascade interaction; positioned GRID/FLEX containers as CB
    // establishers are a cycle-2b item (only block-flow positioned
    // ancestors record their geometry today — see deferrals.md).

    // ================================================================
    //  Pipeline driver — mirrors GridLayouterProductionTests.
    // ================================================================

    private static async Task<(RecordingFragmentSink sink, RecordingDiagnosticsSink diag,
        LayoutAttemptResult result)>
        RenderWithResultAsync(string html)
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
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        var result = layouter.AttemptLayout(
            ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        return (sink, diagSink, result);
    }

    private static async Task<(RecordingFragmentSink sink, RecordingDiagnosticsSink diag)>
        RenderAsync(string html)
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
        var ctx = new FragmentainerContext(contentInlineSize: 600, blockSize: 800);
        var layoutCtx = new LayoutContext(ctx) { Diagnostics = diagSink };
        using var resolver = new BreakResolver();
        layouter.AttemptLayout(ctx, ref layoutCtx, resolver, LayoutAttemptStrategy.LastResort);
        return (sink, diagSink);
    }

    private static BoxFragment? FindByClass(RecordingFragmentSink sink, string className)
    {
        foreach (var f in sink.Fragments)
        {
            var el = f.Box.SourceElement;
            if (el is null) continue;
            var classAttr = el.GetAttribute("class");
            if (classAttr is null) continue;
            if (classAttr.Split(' ').Any(t => t == className)) return f;
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
