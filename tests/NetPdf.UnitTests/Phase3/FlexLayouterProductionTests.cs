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
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: safe center;
                    width: 600px;
                    height: 60px;
                }
                .item { width: 200px; height: 50px; }
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
        const string html = """
            <!DOCTYPE html><html><head><style>
                .flex {
                    display: flex;
                    justify-content: unsafe flex-end;
                    width: 600px;
                    height: 60px;
                }
                .item { width: 200px; height: 50px; }
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
        RenderViaFullPipelineAsync(string html)
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
