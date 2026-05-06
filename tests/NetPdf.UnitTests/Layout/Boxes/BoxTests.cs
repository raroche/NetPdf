// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Unit tests for <see cref="Box"/> — covers the construction patterns Task 12's
/// BoxBuilder will use, the parent/child invariants enforced by AppendChild +
/// RemoveChild + InsertChild, and the convenience predicates over BoxKind that
/// Phase 3 layout dispatches on.
/// </summary>
public sealed class BoxTests
{
    private static async Task<IElement> AnyElement()
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content("<p>x</p>"));
        return doc.QuerySelector("p")!;
    }

    private static ComputedStyle FreshStyle() => ComputedStyle.Rent();

    // ============================================================
    // Factory constructors
    // ============================================================

    [Fact]
    public async Task ForElement_attaches_source_and_no_pseudo()
    {
        var el = await AnyElement();
        using var style = FreshStyle();
        var box = Box.ForElement(BoxKind.BlockContainer, style, el);
        Assert.Equal(BoxKind.BlockContainer, box.Kind);
        Assert.Same(el, box.SourceElement);
        Assert.Equal(BoxPseudo.None, box.Pseudo);
        Assert.False(box.IsAnonymous);
        Assert.False(box.IsPseudoElement);
    }

    [Fact]
    public async Task ForPseudo_requires_a_pseudo_value()
    {
        var el = await AnyElement();
        using var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            Box.ForPseudo(BoxKind.InlineBox, style, el, BoxPseudo.None));
    }

    [Fact]
    public async Task ForPseudo_attaches_source_and_pseudo()
    {
        var el = await AnyElement();
        using var style = FreshStyle();
        var box = Box.ForPseudo(BoxKind.InlineBox, style, el, BoxPseudo.Before);
        Assert.Same(el, box.SourceElement);
        Assert.Equal(BoxPseudo.Before, box.Pseudo);
        Assert.True(box.IsPseudoElement);
        Assert.False(box.IsAnonymous);
    }

    [Fact]
    public void Anonymous_has_no_source_or_pseudo()
    {
        using var style = FreshStyle();
        var box = Box.Anonymous(BoxKind.AnonymousBlock, style);
        Assert.Null(box.SourceElement);
        Assert.Equal(BoxPseudo.None, box.Pseudo);
        Assert.True(box.IsAnonymous);
        Assert.False(box.IsPseudoElement);
    }

    [Fact]
    public void TextRun_carries_text_and_style()
    {
        using var style = FreshStyle();
        var box = Box.TextRun("hello world", style);
        Assert.Equal(BoxKind.TextRun, box.Kind);
        Assert.Equal("hello world", box.Text);
        Assert.True(box.IsInlineLevel);
        Assert.False(box.IsBlockLevel);
    }

    [Fact]
    public void TextRun_rejects_null_text()
    {
        using var style = FreshStyle();
        Assert.Throws<ArgumentNullException>(() => Box.TextRun(null!, style));
    }

    [Fact]
    public void CreateRoot_has_root_kind_and_no_parent()
    {
        using var style = FreshStyle();
        var root = Box.CreateRoot(style);
        Assert.Equal(BoxKind.Root, root.Kind);
        Assert.Null(root.SourceElement);
        Assert.Null(root.Parent);
        // The root is not "anonymous" in the box-generation sense — it's structural.
        Assert.False(root.IsAnonymous);
        Assert.True(root.IsBlockLevel);
    }

    [Fact]
    public void Constructor_rejects_pseudo_box_without_source_element()
    {
        // Direct constructor (bypassing the factory) — defensive guard ensures
        // anonymous + pseudo combo is rejected.
        using var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            new Box(BoxKind.InlineBox, style, sourceElement: null, BoxPseudo.Before, string.Empty));
    }

    [Fact]
    public void Constructor_rejects_null_style()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Box(BoxKind.BlockContainer, null!, null, BoxPseudo.None, string.Empty));
    }

    [Fact]
    public void Constructor_rejects_null_text()
    {
        using var style = FreshStyle();
        Assert.Throws<ArgumentNullException>(() =>
            new Box(BoxKind.BlockContainer, style, null, BoxPseudo.None, null!));
    }

    // ============================================================
    // Parent/child mutation
    // ============================================================

    [Fact]
    public void AppendChild_links_parent_pointer()
    {
        using var rootStyle = FreshStyle();
        using var childStyle = FreshStyle();
        var root = Box.CreateRoot(rootStyle);
        var child = Box.Anonymous(BoxKind.BlockContainer, childStyle);
        root.AppendChild(child);
        Assert.Same(root, child.Parent);
        Assert.Single(root.Children);
        Assert.Same(child, root.Children[0]);
        Assert.Same(child, root.FirstChild);
        Assert.Same(child, root.LastChild);
    }

    [Fact]
    public void AppendChild_rejects_double_attach()
    {
        using var s = FreshStyle();
        var parent1 = Box.CreateRoot(s);
        var parent2 = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.BlockContainer, s);
        parent1.AppendChild(child);
        Assert.Throws<InvalidOperationException>(() => parent2.AppendChild(child));
    }

    [Fact]
    public void AppendChild_rejects_self()
    {
        using var s = FreshStyle();
        var box = Box.CreateRoot(s);
        Assert.Throws<InvalidOperationException>(() => box.AppendChild(box));
    }

    [Fact]
    public void AppendChild_rejects_null()
    {
        using var s = FreshStyle();
        var box = Box.CreateRoot(s);
        Assert.Throws<ArgumentNullException>(() => box.AppendChild(null!));
    }

    [Fact]
    public void InsertChild_at_specific_position()
    {
        using var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var a = Box.Anonymous(BoxKind.BlockContainer, s);
        var b = Box.Anonymous(BoxKind.BlockContainer, s);
        var c = Box.Anonymous(BoxKind.BlockContainer, s);
        parent.AppendChild(a);
        parent.AppendChild(c);
        parent.InsertChild(1, b);   // a, b, c
        Assert.Same(a, parent.Children[0]);
        Assert.Same(b, parent.Children[1]);
        Assert.Same(c, parent.Children[2]);
        Assert.Same(parent, b.Parent);
    }

    [Fact]
    public void InsertChild_rejects_out_of_range()
    {
        using var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.BlockContainer, s);
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.InsertChild(5, child));
    }

    [Fact]
    public void InsertChild_at_count_appends()
    {
        using var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var first = Box.Anonymous(BoxKind.BlockContainer, s);
        var second = Box.Anonymous(BoxKind.BlockContainer, s);
        parent.AppendChild(first);
        parent.InsertChild(1, second);  // index == count → append
        Assert.Same(second, parent.LastChild);
    }

    [Fact]
    public void RemoveChild_clears_parent_pointer()
    {
        using var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.BlockContainer, s);
        parent.AppendChild(child);
        parent.RemoveChild(child);
        Assert.Null(child.Parent);
        Assert.Empty(parent.Children);
    }

    [Fact]
    public void RemoveChild_after_removal_can_re_attach()
    {
        // Detach + re-attach to a different parent — this is the table-fixup pattern.
        using var s = FreshStyle();
        var p1 = Box.CreateRoot(s);
        var p2 = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.BlockContainer, s);
        p1.AppendChild(child);
        p1.RemoveChild(child);
        p2.AppendChild(child);
        Assert.Same(p2, child.Parent);
        Assert.Empty(p1.Children);
        Assert.Single(p2.Children);
    }

    [Fact]
    public void RemoveChild_rejects_non_child()
    {
        using var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var stranger = Box.Anonymous(BoxKind.BlockContainer, s);
        Assert.Throws<ArgumentException>(() => parent.RemoveChild(stranger));
    }

    // ============================================================
    // Predicates over BoxKind
    // ============================================================

    // Note: [Theory] / [InlineData] cannot enumerate internal BoxKind values
    // (xUnit needs public test signatures). Each block/inline/table classification
    // case is its own [Fact] — verbose but keeps the assertions strongly-typed.

    private static void AssertBlockLevel(BoxKind kind)
    {
        using var s = FreshStyle();
        var box = Box.Anonymous(kind, s);
        Assert.True(box.IsBlockLevel);
        Assert.False(box.IsInlineLevel);
    }

    private static void AssertInlineLevel(BoxKind kind)
    {
        using var s = FreshStyle();
        var box = Box.Anonymous(kind, s);
        Assert.True(box.IsInlineLevel);
        Assert.False(box.IsBlockLevel);
    }

    private static void AssertNeither(BoxKind kind)
    {
        using var s = FreshStyle();
        var box = Box.Anonymous(kind, s);
        Assert.False(box.IsBlockLevel);
        Assert.False(box.IsInlineLevel);
    }

    private static void AssertTablePart(BoxKind kind)
    {
        using var s = FreshStyle();
        var box = Box.Anonymous(kind, s);
        Assert.True(box.IsTablePart);
    }

    private static void AssertNotTablePart(BoxKind kind)
    {
        using var s = FreshStyle();
        var box = Box.Anonymous(kind, s);
        Assert.False(box.IsTablePart);
    }

    [Fact] public void Root_is_block_level()             => AssertBlockLevel(BoxKind.Root);
    [Fact] public void BlockContainer_is_block_level()   => AssertBlockLevel(BoxKind.BlockContainer);
    [Fact] public void ListItem_is_block_level()         => AssertBlockLevel(BoxKind.ListItem);
    [Fact] public void AnonymousBlock_is_block_level()   => AssertBlockLevel(BoxKind.AnonymousBlock);
    [Fact] public void Table_is_block_level()            => AssertBlockLevel(BoxKind.Table);
    [Fact] public void FlexContainer_is_block_level()    => AssertBlockLevel(BoxKind.FlexContainer);
    [Fact] public void GridContainer_is_block_level()    => AssertBlockLevel(BoxKind.GridContainer);

    [Fact] public void InlineBox_is_inline_level()       => AssertInlineLevel(BoxKind.InlineBox);
    [Fact] public void AtomicInline_is_inline_level()    => AssertInlineLevel(BoxKind.AtomicInline);
    [Fact] public void TextRun_is_inline_level()         => AssertInlineLevel(BoxKind.TextRun);
    [Fact] public void AnonymousInline_is_inline_level() => AssertInlineLevel(BoxKind.AnonymousInline);

    [Fact] public void LineBox_is_neither_block_nor_inline() => AssertNeither(BoxKind.LineBox);
    [Fact] public void Marker_is_neither_block_nor_inline() => AssertNeither(BoxKind.Marker);
    [Fact] public void ReplacedElement_is_neither_block_nor_inline() => AssertNeither(BoxKind.ReplacedElement);

    [Fact] public void Table_kind_is_table_part()             => AssertTablePart(BoxKind.Table);
    [Fact] public void TableRowGroup_is_table_part()          => AssertTablePart(BoxKind.TableRowGroup);
    [Fact] public void TableHeaderGroup_is_table_part()       => AssertTablePart(BoxKind.TableHeaderGroup);
    [Fact] public void TableFooterGroup_is_table_part()       => AssertTablePart(BoxKind.TableFooterGroup);
    [Fact] public void TableRow_is_table_part()               => AssertTablePart(BoxKind.TableRow);
    [Fact] public void TableCell_is_table_part()              => AssertTablePart(BoxKind.TableCell);
    [Fact] public void TableColumnGroup_is_table_part()       => AssertTablePart(BoxKind.TableColumnGroup);
    [Fact] public void TableColumn_is_table_part()            => AssertTablePart(BoxKind.TableColumn);
    [Fact] public void TableCaption_is_table_part()           => AssertTablePart(BoxKind.TableCaption);

    [Fact] public void BlockContainer_is_not_table_part()     => AssertNotTablePart(BoxKind.BlockContainer);
    [Fact] public void InlineBox_is_not_table_part()          => AssertNotTablePart(BoxKind.InlineBox);
    [Fact] public void FlexContainer_is_not_table_part()      => AssertNotTablePart(BoxKind.FlexContainer);
    [Fact] public void Root_is_not_table_part()               => AssertNotTablePart(BoxKind.Root);

    // ============================================================
    // CountDescendants traversal
    // ============================================================

    [Fact]
    public void CountDescendants_counts_full_subtree()
    {
        using var s = FreshStyle();
        var root = Box.CreateRoot(s);
        var a = Box.Anonymous(BoxKind.BlockContainer, s);
        var b = Box.Anonymous(BoxKind.BlockContainer, s);
        var c = Box.Anonymous(BoxKind.InlineBox, s);
        var d = Box.Anonymous(BoxKind.TextRun, s);
        root.AppendChild(a);
        root.AppendChild(b);
        a.AppendChild(c);
        c.AppendChild(d);
        Assert.Equal(4, root.CountDescendants());
        Assert.Equal(2, a.CountDescendants());  // c + d
        Assert.Equal(0, b.CountDescendants());
        Assert.Equal(1, c.CountDescendants());  // d
    }

    [Fact]
    public void Empty_box_has_no_first_or_last_child()
    {
        using var s = FreshStyle();
        var box = Box.CreateRoot(s);
        Assert.Null(box.FirstChild);
        Assert.Null(box.LastChild);
        Assert.Empty(box.Children);
    }

    // ============================================================
    // Style sharing
    // ============================================================

    [Fact]
    public void Anonymous_box_shares_parent_style_instance()
    {
        // Per the design: anonymous boxes inherit by sharing the parent's
        // ComputedStyle reference. This test pins that contract.
        using var rootStyle = FreshStyle();
        var root = Box.CreateRoot(rootStyle);
        var anon = Box.Anonymous(BoxKind.AnonymousBlock, rootStyle);
        root.AppendChild(anon);
        Assert.Same(rootStyle, anon.Style);
        Assert.Same(rootStyle, root.Style);
    }
}
