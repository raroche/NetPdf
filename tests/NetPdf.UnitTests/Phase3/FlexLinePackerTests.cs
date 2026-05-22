// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 16 cycle 4c — direct unit tests for the
/// <see cref="FlexLinePacker"/> shared helper. Pre-cycle-4c the
/// line-packing algorithm lived as two separate implementations
/// inside <see cref="FlexLayouter"/> + <see cref="BlockLayouter"/>;
/// these tests exercise the consolidated helper directly so any
/// future change to <see cref="FlexLinePacker.Pack"/> surfaces here
/// without depending on the layouters' emission paths.
/// </summary>
public sealed class FlexLinePackerTests
{
    [Fact]
    public void Empty_children_returns_empty_line_list()
    {
        // No items → no lines. The layouters short-circuit emission
        // when this happens; the packer's contract is to return an
        // empty list (not null, not a single empty line).
        var container = BuildFlexContainer();
        var sortedIndices = new List<int>();

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 600,
            isWrapping: false,
            CancellationToken.None);

        Assert.Empty(lines);
    }

    [Fact]
    public void Nowrap_packs_all_items_into_one_line_with_sum_main_and_max_cross()
    {
        // nowrap (= the L1-L5 single-line algorithm): one line; main
        // = sum of items' hypothetical main-sizes; cross = max of
        // items' cross-sizes.
        var container = BuildFlexContainer();
        AppendItem(container, width: 100, height: 50);
        AppendItem(container, width: 150, height: 80);
        AppendItem(container, width: 100, height: 60);
        var sortedIndices = new List<int> { 0, 1, 2 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 1000,  // generous, no wrap needed anyway
            isWrapping: false,
            CancellationToken.None);

        Assert.Single(lines);
        var line = lines[0];
        Assert.Equal(0, line.FirstItemIndex);
        Assert.Equal(3, line.ItemCount);
        Assert.Equal(100.0 + 150.0 + 100.0, line.LineMainSize, precision: 3);
        Assert.Equal(80.0, line.LineCrossSize, precision: 3);
    }

    [Fact]
    public void Wrap_greedy_packs_items_first_item_always_lands()
    {
        // Per CSS Flexbox L1 §9.3 — the FIRST item on a line ALWAYS
        // lands even if it overflows alone; subsequent items wrap
        // when adding would exceed containerMainSize. Fixture: 3
        // items of width 200 in a 300-px container → line 1 = item 0
        // (200), line 2 = item 1 (200; item 0+item 1 = 400 > 300),
        // line 3 = item 2 (200).
        var container = BuildFlexContainer();
        AppendItem(container, width: 200, height: 50);
        AppendItem(container, width: 200, height: 50);
        AppendItem(container, width: 200, height: 50);
        var sortedIndices = new List<int> { 0, 1, 2 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);

        Assert.Equal(3, lines.Count);
        Assert.Equal(1, lines[0].ItemCount);
        Assert.Equal(1, lines[1].ItemCount);
        Assert.Equal(1, lines[2].ItemCount);
        // FirstItemIndex tracks the sorted-sequence position.
        Assert.Equal(0, lines[0].FirstItemIndex);
        Assert.Equal(1, lines[1].FirstItemIndex);
        Assert.Equal(2, lines[2].FirstItemIndex);
    }

    [Fact]
    public void Wrap_combines_items_that_fit_together_on_one_line()
    {
        // 3 items × 100 wide in a 300-px container → all fit on one
        // line (= 100+100+100 = 300, edge of wrap threshold).
        var container = BuildFlexContainer();
        AppendItem(container, width: 100, height: 50);
        AppendItem(container, width: 100, height: 80);
        AppendItem(container, width: 100, height: 60);
        var sortedIndices = new List<int> { 0, 1, 2 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);

        Assert.Single(lines);
        Assert.Equal(3, lines[0].ItemCount);
        Assert.Equal(300.0, lines[0].LineMainSize, precision: 3);
        Assert.Equal(80.0, lines[0].LineCrossSize, precision: 3);
    }

    [Fact]
    public void Column_direction_uses_height_as_main_and_width_as_cross()
    {
        // For column direction the main axis is the block axis (=
        // Height); cross is the inline axis (= Width). The packer's
        // direction-aware property selection means a column container
        // wraps when sum(item heights) exceeds containerMainSize, NOT
        // sum(widths).
        var container = BuildFlexContainer();
        AppendItem(container, width: 100, height: 200);
        AppendItem(container, width: 80, height: 200);
        AppendItem(container, width: 60, height: 200);
        var sortedIndices = new List<int> { 0, 1, 2 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Column,
            containerMainSize: 300,  // each item is 200 tall — only 1 fits
            isWrapping: true,
            CancellationToken.None);

        // 200 fits; 200+200 = 400 > 300 → wrap. Each item on its own
        // line.
        Assert.Equal(3, lines.Count);
        // Cross extent (= width in column) is each item's width.
        Assert.Equal(100.0, lines[0].LineCrossSize, precision: 3);
        Assert.Equal(80.0, lines[1].LineCrossSize, precision: 3);
        Assert.Equal(60.0, lines[2].LineCrossSize, precision: 3);
    }

    [Fact]
    public void Wrap_packs_by_flex_basis_not_declared_width()
    {
        // Per PR-#84 review P2 #2 — historical drift point #1
        // (= L8 post-PR-#68 F#1 hardening). The packer must use
        // each item's HYPOTHETICAL main-size (= flex-basis-driven
        // per CSS Flexbox L1 §9.2) for the wrap decision, NOT the
        // raw declared width. Without this contract, an item with
        // `width: 300; flex-basis: 0` would wrap into its own line
        // (because the packer would see 300 > containerMainSize)
        // when the spec says it should pack alongside other
        // flex-basis: 0 items + grow to fill at §9.7 time.
        //
        // Fixture: 3 items × width: 300 + flex-basis: 0 in a 300-px
        // container. flex-basis: 0 makes each item contribute 0 to
        // packing → all three fit on a single line. Pre-L8-F#1 the
        // packer would have produced 3 lines (= 3×300 in a 300-px
        // budget). This test pins the spec-correct single-line
        // outcome through FlexLinePacker directly.
        var container = BuildFlexContainer();
        AppendItemWithFlexBasis(container, width: 300, height: 50, flexBasisPx: 0);
        AppendItemWithFlexBasis(container, width: 300, height: 50, flexBasisPx: 0);
        AppendItemWithFlexBasis(container, width: 300, height: 50, flexBasisPx: 0);
        var sortedIndices = new List<int> { 0, 1, 2 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);

        // All 3 items pack onto one line because flex-basis: 0
        // overrides width: 300 for the wrap decision.
        Assert.Single(lines);
        Assert.Equal(3, lines[0].ItemCount);
        Assert.Equal(0.0, lines[0].LineMainSize, precision: 3);  // 0+0+0
        Assert.Equal(50.0, lines[0].LineCrossSize, precision: 3);
    }

    [Fact]
    public void Pack_walks_sortedChildIndices_not_DOM_order()
    {
        // Per PR-#84 review P2 #2 — historical drift point #2
        // (= L10 sort-by-effective-order refactor). FlexLine.FirstItemIndex
        // is a POSITION IN THE SORTED SEQUENCE, NOT a DOM-children
        // index. The packer must walk sortedChildIndices in the
        // order the caller supplies, NOT re-sort or use DOM order.
        // Without this contract, items with non-zero `order` would
        // pack differently between pre-measure (= sorted) and emit
        // (= sorted) — but a future drift could break the
        // contract.
        //
        // Fixture: 3 items × 100 wide. DOM indices [0, 1, 2] but
        // the caller's sortedChildIndices = [2, 0, 1] (= a
        // non-DOM permutation that simulates the
        // `order: -1` on item 2 case). Per the L10 contract the
        // line's FirstItemIndex is 0 (= the FIRST POSITION in the
        // sorted list, NOT DOM idx 0). The emission loop walks
        // sortedChildIndices[FirstItemIndex + i] which yields
        // 2, 0, 1 in visual order.
        var container = BuildFlexContainer();
        AppendItem(container, width: 100, height: 50);   // DOM idx 0
        AppendItem(container, width: 100, height: 50);   // DOM idx 1
        AppendItem(container, width: 100, height: 50);   // DOM idx 2
        // Non-DOM permutation: item 2 first, then 0, then 1.
        var sortedIndices = new List<int> { 2, 0, 1 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);

        // All 3 fit on one line (3 × 100 = 300 budget).
        Assert.Single(lines);
        // FirstItemIndex is the sorted-sequence position (0), NOT
        // the DOM index of the first item (which would be 2).
        Assert.Equal(0, lines[0].FirstItemIndex);
        Assert.Equal(3, lines[0].ItemCount);
        Assert.Equal(300.0, lines[0].LineMainSize, precision: 3);
    }

    [Fact]
    public void SumCrossExtent_returns_same_total_as_Pack_then_sum()
    {
        // Per PR-#84 review P2 #1 — the streaming SumCrossExtent
        // entry point must return the EXACT same sum-of-cross-extents
        // that Pack(...).Sum(line => line.LineCrossSize) would
        // produce. Without algorithm parity between the two entry
        // points, the BlockLayouter pre-measure (which calls
        // SumCrossExtent) + the FlexLayouter emission (which calls
        // Pack) could disagree on line boundaries = sibling boxes
        // land wrong.
        //
        // Fixture: 5 items with varying widths + heights in a
        // 300-px wrap container. Items: 100×30, 200×40 (line 1
        // overflow → starts line 2), 100×50, 200×60 (line 3),
        // 100×70 (line 4 alone since prior line had 200, +100 =
        // 300 — boundary case).
        //
        // Actually a cleaner fixture: 4 items each 200×varying-height
        // in a 300-px container → each on its own line (200+200 >
        // 300 always). Sum of cross-extents = sum of all heights.
        var container = BuildFlexContainer();
        AppendItem(container, width: 200, height: 30);
        AppendItem(container, width: 200, height: 40);
        AppendItem(container, width: 200, height: 50);
        AppendItem(container, width: 200, height: 60);
        var sortedIndices = new List<int> { 0, 1, 2, 3 };

        var lines = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);
        var packSum = 0.0;
        foreach (var line in lines)
        {
            packSum += line.LineCrossSize;
        }

        var streamingSum = FlexLinePacker.SumCrossExtent(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);

        // Streaming variant matches the Pack-then-sum.
        Assert.Equal(packSum, streamingSum, precision: 3);
        // Pin the exact total too: 4 items each on own line,
        // cross-extents 30+40+50+60 = 180.
        Assert.Equal(180.0, streamingSum, precision: 3);
    }

    [Fact]
    public void SumCrossExtent_nowrap_returns_max_cross_not_sum()
    {
        // Per PR-#84 review P2 #1 — the streaming variant must
        // honor the nowrap contract: a single line whose cross
        // extent is max(item cross-size), NOT the sum.
        var container = BuildFlexContainer();
        AppendItem(container, width: 100, height: 30);
        AppendItem(container, width: 100, height: 80);
        AppendItem(container, width: 100, height: 50);
        var sortedIndices = new List<int> { 0, 1, 2 };

        var streamingSum = FlexLinePacker.SumCrossExtent(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 1000,  // generous; no wrap
            isWrapping: false,
            CancellationToken.None);

        // Nowrap → one line → cross = max(30, 80, 50) = 80.
        Assert.Equal(80.0, streamingSum, precision: 3);
    }

    [Fact]
    public void Pack_returns_same_lines_for_BlockLayouter_premeasure_and_FlexLayouter_PackLines()
    {
        // Sanity proof: the cycle-4c shared helper is what BOTH
        // BlockLayouter pre-measure + FlexLayouter call. The output
        // for identical input is structurally identical (= no
        // duplicate-algorithm drift). This test calls Pack twice +
        // asserts the line lists are equal.
        var container = BuildFlexContainer();
        AppendItem(container, width: 150, height: 50);
        AppendItem(container, width: 150, height: 50);
        AppendItem(container, width: 150, height: 50);
        AppendItem(container, width: 150, height: 50);
        var sortedIndices = new List<int> { 0, 1, 2, 3 };

        var linesA = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);
        var linesB = FlexLinePacker.Pack(
            container, sortedIndices,
            FlexDirectionValue.Row,
            containerMainSize: 300,
            isWrapping: true,
            CancellationToken.None);

        // 2 items × 150 = 300 fits; third item wraps. So lines: [0,1],
        // [2,3] — 2 lines of 2.
        Assert.Equal(linesA.Count, linesB.Count);
        for (var i = 0; i < linesA.Count; i++)
        {
            Assert.Equal(linesA[i].FirstItemIndex, linesB[i].FirstItemIndex);
            Assert.Equal(linesA[i].ItemCount, linesB[i].ItemCount);
            Assert.Equal(linesA[i].LineMainSize, linesB[i].LineMainSize, precision: 3);
            Assert.Equal(linesA[i].LineCrossSize, linesB[i].LineCrossSize, precision: 3);
        }
        // Pin the exact partition too — 2+2 split at boundary 300.
        Assert.Equal(2, linesA.Count);
        Assert.Equal(2, linesA[0].ItemCount);
        Assert.Equal(2, linesA[1].ItemCount);
    }

    // ====================================================================
    //  Test helpers — local to this fixture; mirror FlexLayouterTests'.
    // ====================================================================

    private static Box BuildFlexContainer()
    {
        var style = MakeStyle();
        var container = Box.ForElement(BoxKind.FlexContainer, style, MakeElement());
        return container;
    }

    private static void AppendItem(Box flexContainer, double width, double height)
    {
        var style = MakeStyle();
        style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(width));
        style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(height));
        var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
        flexContainer.AppendChild(item);
    }

    private static void AppendItemWithFlexBasis(
        Box flexContainer, double width, double height, double flexBasisPx)
    {
        var style = MakeStyle();
        style.Set(PropertyId.Width, ComputedSlot.FromLengthPx(width));
        style.Set(PropertyId.Height, ComputedSlot.FromLengthPx(height));
        style.Set(PropertyId.FlexBasis, ComputedSlot.FromLengthPx(flexBasisPx));
        var item = Box.ForElement(BoxKind.BlockContainer, style, MakeElement());
        flexContainer.AppendChild(item);
    }

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
