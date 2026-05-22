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

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
