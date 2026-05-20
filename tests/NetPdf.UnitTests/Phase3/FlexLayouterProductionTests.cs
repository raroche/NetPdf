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
/// Phase 3 Task 15 cycle 1 (Hello World) — production-pipeline tests
/// for <see cref="FlexLayouter"/>. The existing
/// <see cref="FlexLayouterTests"/> construct box trees directly
/// (bypassing BoxBuilder); this fixture exercises flex through the
/// FULL pipeline:
///
/// <para>HTML → <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>VarResolver</c> → <c>BoxBuilder</c> → <c>BlockLayouter</c>
/// (which dispatches into <c>FlexLayouter</c> for any
/// <see cref="BoxKind.FlexContainer"/> /
/// <see cref="BoxKind.InlineFlexContainer"/> child).</para>
///
/// <para>Coverage delivered: <c>DisplayMapper</c> resolves
/// <c>display: flex</c> into <see cref="BoxKind.FlexContainer"/>; the
/// BoxBuilder produces a FlexContainer box for the flex element + a
/// BlockContainer for each item; the BlockLayouter dispatch's
/// <c>IsFlexContainer</c> predicate fires + the FlexLayouter emits
/// per-item fragments at the expected inline offsets.</para>
/// </summary>
public sealed class FlexLayouterProductionTests
{
    [Fact]
    public async Task Production_html_div_with_display_flex_lays_out_items_in_row()
    {
        // Per Phase 3 Task 15 cycle 1 (Hello World) — a real HTML
        // <div> with `display: flex` containing two block-level
        // children with explicit widths flows through every stage of
        // the pipeline + emits per-item content fragments at the
        // expected inline offsets.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    width: 400px;
                    height: 60px;
                }
                .item-a { width: 100px; height: 50px; }
                .item-b { width: 80px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find the flex container + the two item fragments.
        BoxFragment? flexFragment = null;
        BoxFragment? itemAFragment = null;
        BoxFragment? itemBFragment = null;
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
            }
            else if (classAttr == "item-a")
            {
                itemAFragment = f;
            }
            else if (classAttr == "item-b")
            {
                itemBFragment = f;
            }
        }

        // The flex wrapper must be emitted as a FlexContainer box
        // (= display: flex resolved through DisplayMapper).
        Assert.NotNull(flexFragment);
        Assert.Equal(BoxKind.FlexContainer, flexFragment!.Value.Box.Kind);

        // Both items must be emitted by the FlexLayouter dispatch.
        Assert.NotNull(itemAFragment);
        Assert.NotNull(itemBFragment);

        // Cycle 1 (Hello World) — item A at the container's content-
        // inline-start (= 0 in this fixture; no border / padding on
        // the flex container). Item B packs immediately after at
        // inline-offset = itemA.InlineSize.
        Assert.Equal(0.0, itemAFragment!.Value.InlineOffset, precision: 3);
        Assert.Equal(itemAFragment.Value.InlineSize,
            itemBFragment!.Value.InlineOffset, precision: 3);

        // Both items land at the same block-axis offset (= the flex
        // container's content-block-start; cycle 1 is flex-start
        // equivalent regardless of align-items value).
        Assert.Equal(itemAFragment.Value.BlockOffset,
            itemBFragment.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task L2_production_html_justify_content_space_between()
    {
        // Per Phase 3 Task 15 L2 — a real HTML <div> with
        // `display: flex; justify-content: space-between` containing
        // three block-level children with explicit widths flows
        // through every stage of the pipeline + emits per-item content
        // fragments at the L2-spec'd offsets (0, 275, 550 for
        // freeSpace = 600 - 150 = 450; betweenSpacing = 450 / 2 = 225;
        // so 0, 50 + 225 = 275, 50 + 225 + 50 + 225 = 550).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: space-between;
                    width: 600px;
                    height: 60px;
                }
                .item-a { width: 50px; height: 50px; }
                .item-b { width: 50px; height: 50px; }
                .item-c { width: 50px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find the flex container + three item fragments.
        BoxFragment? flexFragment = null;
        BoxFragment? itemAFragment = null;
        BoxFragment? itemBFragment = null;
        BoxFragment? itemCFragment = null;
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
            }
            else if (classAttr == "item-a")
            {
                itemAFragment = f;
            }
            else if (classAttr == "item-b")
            {
                itemBFragment = f;
            }
            else if (classAttr == "item-c")
            {
                itemCFragment = f;
            }
        }

        Assert.NotNull(flexFragment);
        Assert.NotNull(itemAFragment);
        Assert.NotNull(itemBFragment);
        Assert.NotNull(itemCFragment);

        // L2 — justify-content: space-between with 3 items of width
        // 50 in a 600px container. totalItemSize = 150, freeSpace =
        // 450, betweenSpacing = 450 / (3 - 1) = 225. Expected
        // inline-offsets: 0, 275, 550 (relative to the flex
        // container's content-inline-start).
        Assert.Equal(0.0, itemAFragment!.Value.InlineOffset, precision: 3);
        Assert.Equal(275.0, itemBFragment!.Value.InlineOffset, precision: 3);
        Assert.Equal(550.0, itemCFragment!.Value.InlineOffset, precision: 3);

        // All three items share the container's content-block-start
        // (cycle 1 align-items is flex-start equivalent).
        Assert.Equal(itemAFragment.Value.BlockOffset,
            itemBFragment.Value.BlockOffset, precision: 3);
        Assert.Equal(itemAFragment.Value.BlockOffset,
            itemCFragment.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task L2_production_html_justify_content_center()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#4 —
        // production-pipeline test proving `justify-content: center`
        // survives the parser → cascade → BoxBuilder → BlockLayouter
        // → FlexLayouter chain. Pre-fix coverage was direct-
        // construction only via `ComputedSlot.FromKeyword(5)` — the
        // KeywordResolver index was never exercised.
        //
        // 3 items of width 100 in a 600px container, center →
        // freeSpace = 300, startOffset = 150. Expected offsets:
        // 150, 250, 350.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: center;
                    width: 600px;
                    height: 60px;
                }
                .item-a { width: 100px; height: 50px; }
                .item-b { width: 100px; height: 50px; }
                .item-c { width: 100px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);
        var (a, b, c) = FindThreeItems(sink);

        Assert.Equal(150.0, a.InlineOffset, precision: 3);
        Assert.Equal(250.0, b.InlineOffset, precision: 3);
        Assert.Equal(350.0, c.InlineOffset, precision: 3);
    }

    [Fact]
    public async Task L2_production_html_justify_content_flex_end()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#4 —
        // production-pipeline test for `justify-content: flex-end`.
        //
        // 3 items of width 100 in a 600px container, flex-end →
        // freeSpace = 300, startOffset = 300. Expected offsets:
        // 300, 400, 500.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: flex-end;
                    width: 600px;
                    height: 60px;
                }
                .item-a { width: 100px; height: 50px; }
                .item-b { width: 100px; height: 50px; }
                .item-c { width: 100px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);
        var (a, b, c) = FindThreeItems(sink);

        Assert.Equal(300.0, a.InlineOffset, precision: 3);
        Assert.Equal(400.0, b.InlineOffset, precision: 3);
        Assert.Equal(500.0, c.InlineOffset, precision: 3);
    }

    [Fact]
    public async Task L2_production_html_justify_content_safe_center_with_overflow_falls_back_to_start()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#4 (the
        // test family that would have caught F#1 in review) —
        // production-pipeline test for `justify-content: safe center`.
        // Pre-F#1 fix this compound keyword decoded to flex-start
        // regardless of overflow; post-fix the `safe` modifier
        // forces safe-start fallback ONLY when free-space is
        // negative, otherwise transparent.
        //
        // 4 items of width 200 in a 600px container (overflow:
        // totalItemSize=800, freeSpace=-200), safe center →
        // safe modifier forces safe-start. Expected offsets:
        // 0, 200, 400, 600.
        // Per Phase 3 Task 15 L8 — declare `flex-shrink: 0` on each
        // item so the §9.7 shrink resolution doesn't absorb the -200
        // overflow. Pre-L8 items effectively had no flex-shrink (= 0
        // behavior); post-L8 the cascade default is 1 so we must opt
        // out explicitly to preserve the overflow assertion.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: safe center;
                    width: 600px;
                    height: 60px;
                }
                .item { width: 200px; height: 50px; flex-shrink: 0; }
            </style></head><body>
            <div class="flex">
              <div class="item a"></div>
              <div class="item b"></div>
              <div class="item c"></div>
              <div class="item d"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr != null && classAttr.StartsWith("item"))
            {
                items.Add(f);
            }
        }

        Assert.Equal(4, items.Count);
        Assert.Equal(0.0, items[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, items[1].InlineOffset, precision: 3);
        Assert.Equal(400.0, items[2].InlineOffset, precision: 3);
        Assert.Equal(600.0, items[3].InlineOffset, precision: 3);
    }

    [Fact]
    public async Task L2_production_html_justify_content_unsafe_flex_end_with_overflow_honors_alignment()
    {
        // Per Phase 3 Task 15 L2 post-PR-#62 hardening F#4 —
        // production-pipeline test for `justify-content: unsafe
        // flex-end`. Pre-F#1 fix this compound keyword decoded to
        // flex-start; post-fix the `unsafe` modifier preserves the
        // base alignment EVEN ON OVERFLOW. Items are pushed past
        // the container's start edge into negative offsets.
        //
        // 4 items of width 200 in a 600px container (overflow:
        // freeSpace=-200), unsafe flex-end → honors flex-end →
        // startOffset = -200. Expected offsets: -200, 0, 200, 400.
        // Per Phase 3 Task 15 L8 — declare `flex-shrink: 0` on each
        // item so the §9.7 shrink resolution doesn't absorb the -200
        // overflow.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: unsafe flex-end;
                    width: 600px;
                    height: 60px;
                }
                .item { width: 200px; height: 50px; flex-shrink: 0; }
            </style></head><body>
            <div class="flex">
              <div class="item a"></div>
              <div class="item b"></div>
              <div class="item c"></div>
              <div class="item d"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr != null && classAttr.StartsWith("item"))
            {
                items.Add(f);
            }
        }

        Assert.Equal(4, items.Count);
        Assert.Equal(-200.0, items[0].InlineOffset, precision: 3);
        Assert.Equal(0.0, items[1].InlineOffset, precision: 3);
        Assert.Equal(200.0, items[2].InlineOffset, precision: 3);
        Assert.Equal(400.0, items[3].InlineOffset, precision: 3);
    }

    [Fact]
    public async Task L3_production_html_align_items_center()
    {
        // Per Phase 3 Task 15 L3 — production-pipeline test proving
        // `align-items: center` survives the parser → cascade →
        // BoxBuilder → BlockLayouter → FlexLayouter chain. Pre-L3 the
        // layouter emitted all items at contentBlockOffset regardless
        // of align-items; post-L3 the items center on the cross axis.
        //
        // 3 items of height 50 in a 200px-tall flex container,
        // align-items: center. crossSpace = 150 → block-offset = 75.
        // Expected: all 3 items at the same block offset (= the
        // container's contentBlockOffset + 75).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    align-items: center;
                    width: 600px;
                    height: 200px;
                }
                .item-a { width: 100px; height: 50px; }
                .item-b { width: 100px; height: 50px; }
                .item-c { width: 100px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Find the flex container wrapper fragment + the 3 item fragments.
        BoxFragment? flexFragment = null;
        var (a, b, c) = FindThreeItems(sink);
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
                break;
            }
        }
        Assert.NotNull(flexFragment);

        // L3 — align-items: center with items of height 50 in a 200px
        // container. The center block-offset relative to the
        // container's content-block-start is (200-50)/2 = 75. The flex
        // container's content-block-start = the wrapper's BlockOffset
        // (no border / padding on the .flex container in this fixture).
        var expectedBlockOffset = flexFragment!.Value.BlockOffset + 75.0;
        Assert.Equal(expectedBlockOffset, a.BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, b.BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, c.BlockOffset, precision: 3);
        // Items keep their declared block-size (positional alignment).
        Assert.Equal(50.0, a.BlockSize, precision: 3);
        Assert.Equal(50.0, b.BlockSize, precision: 3);
        Assert.Equal(50.0, c.BlockSize, precision: 3);
    }

    [Fact]
    public async Task L3_production_html_align_items_safe_center_with_overflow_falls_back_to_flex_start()
    {
        // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#3 —
        // production-pipeline test for compound `align-items: safe
        // center`. AngleSharp.Css 1.0.0-beta.144 may drop compound
        // keywords (= the modern 2022 grammar additions) the same way
        // it dropped `justify-content` compounds in PR #62 — if so,
        // the CssPreprocessor recovers the declaration via
        // KnownDroppedProperties + the KeywordResolver decodes the
        // compound keyword index in the cascade.
        //
        // 3 items of height 250 in a 200px-tall container (overflow:
        // crossSpace = -50), align-items: `safe center` → safe
        // modifier forces safe-start fallback → block-offset = 0.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    align-items: safe center;
                    width: 600px;
                    height: 200px;
                }
                .item-a { width: 100px; height: 250px; }
                .item-b { width: 100px; height: 250px; }
                .item-c { width: 100px; height: 250px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? flexFragment = null;
        var (a, b, c) = FindThreeItems(sink);
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
                break;
            }
        }
        Assert.NotNull(flexFragment);

        // Safe modifier forces safe-start (= contentBlockOffset) on
        // overflow. All 3 items at the same offset as the flex
        // wrapper's content-block-start.
        var expectedBlockOffset = flexFragment!.Value.BlockOffset;
        Assert.Equal(expectedBlockOffset, a.BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, b.BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, c.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task L3_production_html_align_items_unsafe_flex_end_with_overflow_honors_alignment()
    {
        // Per Phase 3 Task 15 L3 post-PR-#63 hardening F#3 —
        // production-pipeline test for compound `align-items: unsafe
        // flex-end`. Unsafe modifier honors the natural alignment
        // even on overflow.
        //
        // 3 items of height 250 in a 200px container (crossSpace
        // = -50), align-items: `unsafe flex-end` → natural offset =
        // contentBlockOffset + (-50) = -50.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    align-items: unsafe flex-end;
                    width: 600px;
                    height: 200px;
                }
                .item-a { width: 100px; height: 250px; }
                .item-b { width: 100px; height: 250px; }
                .item-c { width: 100px; height: 250px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? flexFragment = null;
        var (a, b, c) = FindThreeItems(sink);
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
                break;
            }
        }
        Assert.NotNull(flexFragment);

        // Unsafe modifier honors flex-end on overflow — items pushed
        // 50px PAST the container's start edge (natural offset =
        // contentBlockOffset + crossSpace = contentBlockOffset - 50).
        var expectedBlockOffset = flexFragment!.Value.BlockOffset - 50.0;
        Assert.Equal(expectedBlockOffset, a.BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, b.BlockOffset, precision: 3);
        Assert.Equal(expectedBlockOffset, c.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task L4_production_html_flex_direction_column()
    {
        // Per Phase 3 Task 15 L4 — real HTML <div> with
        // `display: flex; flex-direction: column; align-items: center`
        // containing 3 children with explicit widths + heights flows
        // through every stage of the pipeline + emits per-item content
        // fragments stacked vertically (= main axis = block) with
        // cross-axis (= inline) center alignment.
        //
        // Container declared width matches the test fragmentainer's
        // contentInlineSize (= 600) because the cycle-1 block-flow
        // sizing inherits the available inline range as the wrapper's
        // inline-size, regardless of the declared `width`. Container
        // height = 300; items each height 50, width 100.
        //
        // align-items: center on cross axis (inline);
        // crossSpace = 600 - 100 = 500 → InlineOffset per item = 250
        // (relative to the flex wrapper's content-inline-start).
        // BlockOffsets advance along main axis: 0, 50, 100 (relative
        // to the wrapper's content-block-start).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    width: 600px;
                    height: 300px;
                }
                .item-a { width: 100px; height: 50px; }
                .item-b { width: 100px; height: 50px; }
                .item-c { width: 100px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? flexFragment = null;
        var (a, b, c) = FindThreeItems(sink);
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
                break;
            }
        }
        Assert.NotNull(flexFragment);

        // BlockOffsets advance along the main axis (= block for column).
        // Per PR #64 Copilot review — `BoxFragment.BlockOffset` is the
        // BORDER-BOX start of the wrapper (not the content-box start).
        // The two coincide in this fixture only because the flex
        // container has no padding/border; future fixtures with
        // padding will need to add `borderTop + paddingTop` to derive
        // the content-block-start.
        var wrapperBorderBoxBlockStart = flexFragment!.Value.BlockOffset;
        Assert.Equal(wrapperBorderBoxBlockStart + 0.0, a.BlockOffset, precision: 3);
        Assert.Equal(wrapperBorderBoxBlockStart + 50.0, b.BlockOffset, precision: 3);
        Assert.Equal(wrapperBorderBoxBlockStart + 100.0, c.BlockOffset, precision: 3);

        // InlineOffsets center on the cross axis (= inline for column).
        // crossSpace = 600 - 100 = 500 → offset 250 per item.
        var wrapperBorderBoxInlineStart = flexFragment.Value.InlineOffset;
        Assert.Equal(wrapperBorderBoxInlineStart + 250.0, a.InlineOffset, precision: 3);
        Assert.Equal(wrapperBorderBoxInlineStart + 250.0, b.InlineOffset, precision: 3);
        Assert.Equal(wrapperBorderBoxInlineStart + 250.0, c.InlineOffset, precision: 3);

        // Items keep declared sizes — positional alignment never resizes;
        // the L4 stretch path is exercised by the unit tests.
        Assert.Equal(100.0, a.InlineSize, precision: 3);
        Assert.Equal(50.0, a.BlockSize, precision: 3);
    }

    [Fact]
    public async Task L5_production_html_flex_direction_row_reverse()
    {
        // Per Phase 3 Task 15 L5 — real HTML <div> with
        // `display: flex; flex-direction: row-reverse` containing 3
        // children with explicit widths flows through every stage of
        // the pipeline + emits per-item content fragments packed
        // against the inline-end (right) edge in reverse DOM order
        // per CSS Flexbox L1 §5.1.
        //
        // Container declared width = 600 (matches the test fragmentainer
        // contentInlineSize so the cycle-1 block-flow sizing applies
        // 600 as the wrapper inline-size); 3 items of width 50 + height
        // 50; default justify-content packs at the new main-start =
        // the right edge.
        //   - item-a (DOM 0): InlineOffset = 600 - 50 - 0 = 550
        //   - item-b (DOM 1): InlineOffset = 500
        //   - item-c (DOM 2): InlineOffset = 450
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    flex-direction: row-reverse;
                    width: 600px;
                    height: 100px;
                }
                .item-a { width: 50px; height: 50px; }
                .item-b { width: 50px; height: 50px; }
                .item-c { width: 50px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? flexFragment = null;
        var (a, b, c) = FindThreeItems(sink);
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
                break;
            }
        }
        Assert.NotNull(flexFragment);

        // Items pack against the inline-end (right) edge in reverse
        // DOM order. The wrapper's content-inline-start coincides with
        // the page content-area origin in this fixture (no padding /
        // border / float on the wrapper).
        var wrapperBorderBoxInlineStart = flexFragment!.Value.InlineOffset;
        Assert.Equal(wrapperBorderBoxInlineStart + 550.0, a.InlineOffset, precision: 3);
        Assert.Equal(wrapperBorderBoxInlineStart + 500.0, b.InlineOffset, precision: 3);
        Assert.Equal(wrapperBorderBoxInlineStart + 450.0, c.InlineOffset, precision: 3);

        // Items keep their declared inline-size 50.
        Assert.Equal(50.0, a.InlineSize, precision: 3);
        Assert.Equal(50.0, b.InlineSize, precision: 3);
        Assert.Equal(50.0, c.InlineSize, precision: 3);
    }

    [Fact]
    public async Task L5_production_html_flex_direction_column_reverse()
    {
        // Per Phase 3 Task 15 L5 post-PR-#65 review F#2 — production-
        // pipeline coverage for `flex-direction: column-reverse`
        // mirroring the existing row-reverse production test + the L4
        // column production test patterns. Pre-fix only unit tests
        // (which set raw keyword IDs in ComputedSlot) covered column-
        // reverse; a parser / cascade / keyword-index regression for
        // column-reverse could ship without detection.
        //
        // Fixture: 3 items of height 50 in a 400px-tall column-reverse
        // container; `justify-content` defaults to `flex-start`.
        // Container width 200 fits inside the page's 600px content
        // inline range, so the FlexLayouter sees:
        //   - main axis = block, containerMainSize = 400
        //   - freeSpace = 400 - 3*50 = 250
        //   - startOffset = 0; non-reverse cursor walks 0/50/100
        // Apply the L5 flip transform:
        //   - DOM 0 (item-a): wrapperBlockStart + 400 - 0 - 50 = +350
        //   - DOM 1 (item-b): wrapperBlockStart + 400 - 50 - 50 = +300
        //   - DOM 2 (item-c): wrapperBlockStart + 400 - 100 - 50 = +250
        // Items pack at the bottom edge in reverse DOM order per CSS
        // Flexbox L1 §5.1. Cross-axis (inline) is unchanged.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    flex-direction: column-reverse;
                    height: 400px;
                    width: 200px;
                }
                .item-a { width: 100px; height: 50px; }
                .item-b { width: 100px; height: 50px; }
                .item-c { width: 100px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        BoxFragment? flexFragment = null;
        var (a, b, c) = FindThreeItems(sink);
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
                break;
            }
        }
        Assert.NotNull(flexFragment);

        // Items pack against the block-end (bottom) edge in reverse
        // DOM order. The wrapper's content-block-start equals
        // BlockOffset here (no padding / border on the wrapper).
        var wrapperBlockStart = flexFragment!.Value.BlockOffset;
        Assert.Equal(wrapperBlockStart + 350.0, a.BlockOffset, precision: 3);
        Assert.Equal(wrapperBlockStart + 300.0, b.BlockOffset, precision: 3);
        Assert.Equal(wrapperBlockStart + 250.0, c.BlockOffset, precision: 3);

        // Items keep their declared block-size 50.
        Assert.Equal(50.0, a.BlockSize, precision: 3);
        Assert.Equal(50.0, b.BlockSize, precision: 3);
        Assert.Equal(50.0, c.BlockSize, precision: 3);

        // Cross-axis (InlineOffset) is unaffected by the main-axis
        // reversal. All 3 items share the same InlineOffset (=
        // wrapper's content-inline-start since the default
        // `align-items: stretch` resolves the cross axis identically
        // for all items; with explicit item widths the stretch path
        // honors the declared width per CSS Flexbox §7.2).
        Assert.Equal(a.InlineOffset, b.InlineOffset, precision: 3);
        Assert.Equal(a.InlineOffset, c.InlineOffset, precision: 3);
    }

    [Fact]
    public async Task L6_production_html_flex_wrap_with_4_items_two_per_line()
    {
        // Phase 3 Task 15 L6 — production-pipeline coverage for
        // `flex-wrap: wrap`. 4 items of width 100 in a 250px container.
        // Greedy line packing produces:
        //   Line 1: item-a + item-b (200px); item-c would push to 300 >
        //     250 so wrap.
        //   Line 2: item-c + item-d (200px).
        // Cross-extent per line = 50; line 2 lands at BlockOffset 50.
        //
        // Per Phase 3 Task 15 L7 — the CSS now declares explicit
        // `align-content: flex-start` to pin L6 natural-stacking
        // behavior. The §8.4 default `normal` resolves to `stretch`
        // which would grow each line to 100 (= 200/2) shifting line 2
        // to BlockOffset 100; the dedicated
        // `L7_production_html_align_content_stretch_default` test
        // covers the stretch case end-to-end.
        //
        // Uses a 250px-wide fragmentainer so the wrapper's effective
        // content-inline-size matches the declared 250px width (the
        // BlockLayouter doesn't yet honor declared `width` as a shrink-
        // to-fit constraint — L4 deferral pinned at
        // `L4_hardening_known_gap_column_flex_ignores_declared_width`).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    flex-wrap: wrap;
                    align-content: flex-start;
                    width: 250px;
                    height: 200px;
                }
                .item-a, .item-b, .item-c, .item-d {
                    width: 100px;
                    height: 50px;
                }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
              <div class="item-d"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html, contentInlineSize: 250);

        BoxFragment? flexFragment = null;
        BoxFragment? itemA = null;
        BoxFragment? itemB = null;
        BoxFragment? itemC = null;
        BoxFragment? itemD = null;
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
            }
            else if (classAttr == "item-a") itemA = f;
            else if (classAttr == "item-b") itemB = f;
            else if (classAttr == "item-c") itemC = f;
            else if (classAttr == "item-d") itemD = f;
        }
        Assert.NotNull(flexFragment);
        Assert.NotNull(itemA);
        Assert.NotNull(itemB);
        Assert.NotNull(itemC);
        Assert.NotNull(itemD);

        // The wrapper's content-block-start equals BlockOffset (no
        // padding / border on the wrapper). Same for inline-start.
        var wrapperBlockStart = flexFragment!.Value.BlockOffset;
        var wrapperInlineStart = flexFragment!.Value.InlineOffset;

        // Line 1: items a + b at InlineOffset start + (0, 100); same
        // BlockOffset (= line 1 cross-start = wrapper content-block-
        // start).
        Assert.Equal(wrapperInlineStart, itemA!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperInlineStart + 100.0, itemB!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperBlockStart, itemA!.Value.BlockOffset, precision: 3);
        Assert.Equal(wrapperBlockStart, itemB!.Value.BlockOffset, precision: 3);

        // Line 2: items c + d at InlineOffset start + (0, 100);
        // BlockOffset wrapperBlockStart + 50 (= line 1's cross-extent).
        Assert.Equal(wrapperInlineStart, itemC!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperInlineStart + 100.0, itemD!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperBlockStart + 50.0, itemC!.Value.BlockOffset, precision: 3);
        Assert.Equal(wrapperBlockStart + 50.0, itemD!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task L7_production_html_align_content_stretch_default()
    {
        // Phase 3 Task 15 L7 — production-pipeline coverage for
        // `align-content: stretch` (the §8.4 default value resolved
        // from `normal`). 4 items of 100x50 in a 250x200 container
        // wrap into 2 lines of 50px each; freeCrossSpace = 100;
        // stretchAddend = 50; lines grow to 100px each. Line 2 lands
        // at BlockOffset 100 (= the stretched line 1's cross-end),
        // NOT 50 (= the L1-L6 natural stack).
        //
        // No `align-content` declaration on the .flex selector —
        // the cascade carries the initial `normal` value which §8.4
        // resolves to `stretch` for flex containers. This verifies
        // the end-to-end cascade-through-layout pipeline picks up
        // the spec default.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    flex-wrap: wrap;
                    width: 250px;
                    height: 200px;
                }
                .item-a, .item-b, .item-c, .item-d {
                    width: 100px;
                    height: 50px;
                }
            </style></head><body>
            <div class="flex">
              <div class="item-a"></div>
              <div class="item-b"></div>
              <div class="item-c"></div>
              <div class="item-d"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html, contentInlineSize: 250);

        BoxFragment? flexFragment = null;
        BoxFragment? itemA = null;
        BoxFragment? itemB = null;
        BoxFragment? itemC = null;
        BoxFragment? itemD = null;
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "flex" && f.Box.Kind == BoxKind.FlexContainer)
            {
                flexFragment = f;
            }
            else if (classAttr == "item-a") itemA = f;
            else if (classAttr == "item-b") itemB = f;
            else if (classAttr == "item-c") itemC = f;
            else if (classAttr == "item-d") itemD = f;
        }
        Assert.NotNull(flexFragment);
        Assert.NotNull(itemA);
        Assert.NotNull(itemB);
        Assert.NotNull(itemC);
        Assert.NotNull(itemD);

        var wrapperBlockStart = flexFragment!.Value.BlockOffset;
        var wrapperInlineStart = flexFragment!.Value.InlineOffset;

        // Line 1 (stretched to 100px) — items a + b at BlockOffset
        // wrapperBlockStart (= cross-start).
        Assert.Equal(wrapperInlineStart, itemA!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperInlineStart + 100.0, itemB!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperBlockStart, itemA!.Value.BlockOffset, precision: 3);
        Assert.Equal(wrapperBlockStart, itemB!.Value.BlockOffset, precision: 3);

        // Line 2 (stretched to 100px) — items c + d at BlockOffset
        // wrapperBlockStart + 100 (= the stretched first line's
        // cross-end). The L1-L6 natural stack would place them at
        // +50; the §8.4 stretch default doubles the gap.
        Assert.Equal(wrapperInlineStart, itemC!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperInlineStart + 100.0, itemD!.Value.InlineOffset, precision: 3);
        Assert.Equal(wrapperBlockStart + 100.0, itemC!.Value.BlockOffset, precision: 3);
        Assert.Equal(wrapperBlockStart + 100.0, itemD!.Value.BlockOffset, precision: 3);
    }

    [Fact]
    public async Task L6_hardening_known_gap_narrow_flex_in_wide_page_does_not_wrap_yet()
    {
        // Per Phase 3 Task 15 L6 post-PR-#66 review F#2 — the L6
        // production test masked this by narrowing the fragmentainer
        // contentInlineSize to match the declared flex width. On a
        // default 600px page, `.flex { width: 250px; flex-wrap: wrap;
        // }` SHOULD wrap 4×100px items into 2 lines (4*100=400 > 250)
        // per spec. Because BlockLayouter doesn't honor declared
        // `width` on block containers (the
        // `BlockLayouter-flex-explicit-width` deferral from PR #64
        // F#2), the wrapper is 600px wide and 4×100=400 < 600 → no
        // wrap. This test PINS the current incomplete behavior. When
        // the BlockLayouter explicit-width fix lands, this test
        // should flip to assert 2 lines.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    flex-wrap: wrap;
                    width: 250px;
                }
                .item { width: 100px; height: 50px; }
            </style></head><body>
            <div class="flex">
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
              <div class="item"></div>
            </div>
            </body></html>
            """;

        // Use default 600px page width (no contentInlineSize override).
        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        // Collect item fragments. Currently — because BlockLayouter
        // doesn't honor the 250px declared width — wrapper is 600px
        // so all 4 items fit on one line.
        var items = sink.Fragments.Where(f =>
            f.Box.SourceElement is not null &&
            f.Box.SourceElement.GetAttribute("class") == "item")
            .OrderBy(f => f.BlockOffset).ThenBy(f => f.InlineOffset)
            .ToList();
        Assert.Equal(4, items.Count);

        // KNOWN-GAP PIN — current behavior: all 4 items on one line
        // at BlockOffset 0 (NOT the spec-correct 2 lines at 0/50).
        foreach (var item in items)
        {
            Assert.Equal(0.0, item.BlockOffset, precision: 3);
        }
        // When BlockLayouter-flex-explicit-width is fixed, this
        // should change to: items[0..1] at BlockOffset 0, items[2..3]
        // at BlockOffset 50.
    }

    /// <summary>Per Phase 3 Task 15 L2 post-PR-#62 hardening F#4 —
    /// shared finder for the .item-a / .item-b / .item-c production
    /// fixture used by the bare-position tests
    /// (<see cref="L2_production_html_justify_content_center"/> +
    /// <see cref="L2_production_html_justify_content_flex_end"/>).</summary>
    private static (BoxFragment a, BoxFragment b, BoxFragment c)
        FindThreeItems(RecordingFragmentSink sink)
    {
        BoxFragment? a = null;
        BoxFragment? b = null;
        BoxFragment? c = null;
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr == "item-a") a = f;
            else if (classAttr == "item-b") b = f;
            else if (classAttr == "item-c") c = f;
        }
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        return (a!.Value, b!.Value, c!.Value);
    }

    // ====================================================================
    //  Pipeline driver — mirrors MulticolLayouterProductionTests.
    // ====================================================================

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

    // ====================================================================
    //  Test doubles — same shape as MulticolLayouterProductionTests'.
    // ====================================================================

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

    [Fact]
    public async Task L8_production_html_flex_grow_one_equally_partitions_container()
    {
        // Phase 3 Task 15 L8 — production-pipeline coverage for the
        // canonical `flex: 1 1 0` recipe (longhand: flex-grow: 1 +
        // flex-shrink: 1 + flex-basis: 0). 3 items of declared
        // width: 100 in a 600px container, but with flex-basis: 0 +
        // flex-grow: 1, each item's hypothetical = 0 + grows by 200
        // of the 600 free-space → resolved = 200. The items
        // partition the container equally regardless of declared
        // widths. Cursors: 0, 200, 400.
        //
        // Exercises the full HTML → CSS → cascade → BoxBuilder →
        // BlockLayouter → FlexLayouter pipeline including the
        // newly-wired FlexBasis grammar in LengthResolver and the
        // §9.7 algorithm in FlexLayouter.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    width: 600px;
                    height: 60px;
                }
                .item {
                    width: 100px;
                    height: 50px;
                    flex-grow: 1;
                    flex-basis: 0;
                }
            </style></head><body>
            <div class="flex">
              <div class="item a"></div>
              <div class="item b"></div>
              <div class="item c"></div>
            </div>
            </body></html>
            """;

        var (sink, _, _) = await RenderViaFullPipelineAsync(html);

        var items = new List<BoxFragment>();
        foreach (var f in sink.Fragments)
        {
            var srcEl = f.Box.SourceElement;
            if (srcEl is null) continue;
            var classAttr = srcEl.GetAttribute("class");
            if (classAttr != null && classAttr.StartsWith("item"))
            {
                items.Add(f);
            }
        }

        Assert.Equal(3, items.Count);
        // Each item resolves to 200px (= container 600 / 3 items).
        Assert.Equal(200.0, items[0].InlineSize, precision: 3);
        Assert.Equal(200.0, items[1].InlineSize, precision: 3);
        Assert.Equal(200.0, items[2].InlineSize, precision: 3);
        // Equally partitioned across the container.
        Assert.Equal(0.0, items[0].InlineOffset, precision: 3);
        Assert.Equal(200.0, items[1].InlineOffset, precision: 3);
        Assert.Equal(400.0, items[2].InlineOffset, precision: 3);
    }

    private sealed class RecordingDiagnosticsSink : IPaginateDiagnosticsSink
    {
        public List<PaginateDiagnostic> Diagnostics { get; } = new();
        public void Emit(PaginateDiagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    private sealed class SyntheticShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
