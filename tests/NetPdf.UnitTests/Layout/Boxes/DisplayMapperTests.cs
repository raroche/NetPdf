// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

public sealed class DisplayMapperTests
{
    private static BoxKind MapOk(string display, string? element = null)
    {
        var result = DisplayMapper.Map(display, element, out var kind);
        Assert.Equal(DisplayMapper.DisplayMappingResult.Resolved, result);
        return kind;
    }

    // ============================================================
    // Block-level outer
    // ============================================================

    [Fact] public void block_maps_to_BlockContainer()       => Assert.Equal(BoxKind.BlockContainer, MapOk("block"));
    [Fact] public void flow_root_maps_to_BlockContainer()   => Assert.Equal(BoxKind.BlockContainer, MapOk("flow-root"));
    [Fact] public void list_item_maps_to_ListItem()         => Assert.Equal(BoxKind.ListItem, MapOk("list-item"));
    [Fact] public void flex_maps_to_FlexContainer()         => Assert.Equal(BoxKind.FlexContainer, MapOk("flex"));
    [Fact] public void grid_maps_to_GridContainer()         => Assert.Equal(BoxKind.GridContainer, MapOk("grid"));
    [Fact] public void table_maps_to_Table()                => Assert.Equal(BoxKind.Table, MapOk("table"));

    // ============================================================
    // Inline-level outer
    // ============================================================

    [Fact] public void inline_maps_to_InlineBox()                 => Assert.Equal(BoxKind.InlineBox, MapOk("inline"));
    [Fact] public void inline_block_maps_to_InlineBlockContainer()=> Assert.Equal(BoxKind.InlineBlockContainer, MapOk("inline-block"));
    [Fact] public void inline_flex_maps_to_InlineFlexContainer()  => Assert.Equal(BoxKind.InlineFlexContainer, MapOk("inline-flex"));
    [Fact] public void inline_grid_maps_to_InlineGridContainer()  => Assert.Equal(BoxKind.InlineGridContainer, MapOk("inline-grid"));
    [Fact] public void inline_table_maps_to_InlineTable()         => Assert.Equal(BoxKind.InlineTable, MapOk("inline-table"));

    // ============================================================
    // Replaced detection — inline OR block via element name
    // ============================================================

    [Theory]
    [InlineData("img")]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("canvas")]
    [InlineData("iframe")]
    [InlineData("object")]
    [InlineData("embed")]
    [InlineData("IMG")]   // case-insensitive
    public void Replaced_element_with_display_inline_maps_to_InlineReplacedElement(string tag)
    {
        Assert.Equal(BoxKind.InlineReplacedElement, MapOk("inline", tag));
    }

    [Theory]
    [InlineData("img")]
    [InlineData("video")]
    public void Replaced_element_with_display_block_maps_to_BlockReplacedElement(string tag)
    {
        Assert.Equal(BoxKind.BlockReplacedElement, MapOk("block", tag));
    }

    [Fact]
    public void Replaced_element_with_inline_block_collapses_to_InlineReplacedElement()
    {
        // Replaced + inline-block: the inner FC is moot for an atomic replaced
        // element, so collapse to InlineReplacedElement.
        Assert.Equal(BoxKind.InlineReplacedElement, MapOk("inline-block", "img"));
    }

    [Fact]
    public void Non_replaced_element_with_display_inline_stays_InlineBox()
    {
        Assert.Equal(BoxKind.InlineBox, MapOk("inline", "span"));
    }

    [Fact]
    public void Null_element_treats_as_non_replaced()
    {
        Assert.Equal(BoxKind.InlineBox, MapOk("inline", null));
        Assert.Equal(BoxKind.BlockContainer, MapOk("block", null));
    }

    // ============================================================
    // Table internals
    // ============================================================

    // Per-display [Fact]s (BoxKind is internal so [InlineData] cannot reference its
    // members — xUnit needs public test signatures).
    [Fact] public void table_row_group_maps_to_TableRowGroup()       => Assert.Equal(BoxKind.TableRowGroup, MapOk("table-row-group"));
    [Fact] public void table_header_group_maps_to_TableHeaderGroup() => Assert.Equal(BoxKind.TableHeaderGroup, MapOk("table-header-group"));
    [Fact] public void table_footer_group_maps_to_TableFooterGroup() => Assert.Equal(BoxKind.TableFooterGroup, MapOk("table-footer-group"));
    [Fact] public void table_row_maps_to_TableRow()                  => Assert.Equal(BoxKind.TableRow, MapOk("table-row"));
    [Fact] public void table_cell_maps_to_TableCell()                => Assert.Equal(BoxKind.TableCell, MapOk("table-cell"));
    [Fact] public void table_column_group_maps_to_TableColumnGroup() => Assert.Equal(BoxKind.TableColumnGroup, MapOk("table-column-group"));
    [Fact] public void table_column_maps_to_TableColumn()            => Assert.Equal(BoxKind.TableColumn, MapOk("table-column"));
    [Fact] public void table_caption_maps_to_TableCaption()          => Assert.Equal(BoxKind.TableCaption, MapOk("table-caption"));

    // ============================================================
    // Special outcomes
    // ============================================================

    [Fact]
    public void none_returns_None_outcome()
    {
        var result = DisplayMapper.Map("none", null, out _);
        Assert.Equal(DisplayMapper.DisplayMappingResult.None, result);
    }

    [Fact]
    public void contents_returns_Contents_outcome()
    {
        var result = DisplayMapper.Map("contents", null, out _);
        Assert.Equal(DisplayMapper.DisplayMappingResult.Contents, result);
    }

    [Theory]
    [InlineData("ruby")]
    [InlineData("ruby-base")]
    [InlineData("ruby-text")]
    [InlineData("ruby-base-container")]
    [InlineData("ruby-text-container")]
    [InlineData("foobarbaz")]   // unknown values
    public void Unsupported_or_unknown_returns_Unsupported_outcome(string display)
    {
        var result = DisplayMapper.Map(display, null, out _);
        Assert.Equal(DisplayMapper.DisplayMappingResult.Unsupported, result);
    }
}
