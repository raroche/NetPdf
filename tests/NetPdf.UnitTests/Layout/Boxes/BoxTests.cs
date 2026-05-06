// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Unit tests for <see cref="Box"/> — covers the construction patterns Task 12's
/// BoxBuilder will use, the parent/child invariants enforced by AppendChild +
/// RemoveChild + InsertChild, and the convenience predicates over BoxKind that
/// Phase 3 layout dispatches on. The hardening review's structural changes
/// (Recs 1+2+3+4+5+6+7) are exercised here.
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

    /// <summary>Synchronous in-memory document — avoids the xUnit1031 analyzer
    /// rule against blocking task waits in test bodies.</summary>
    private static IDocument SyncDoc() =>
        new HtmlParser().ParseDocument("<!doctype html><html><body></body></html>");

    // ============================================================
    // Factory constructors
    // ============================================================

    [Fact]
    public async Task ForElement_attaches_source_and_no_pseudo()
    {
        var el = await AnyElement();
        var style = FreshStyle();
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
        var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            Box.ForPseudo(BoxKind.InlineBox, style, el, BoxPseudo.None));
    }

    [Fact]
    public async Task ForPseudo_attaches_source_and_pseudo()
    {
        var el = await AnyElement();
        var style = FreshStyle();
        var box = Box.ForPseudo(BoxKind.InlineBox, style, el, BoxPseudo.Before);
        Assert.Same(el, box.SourceElement);
        Assert.Equal(BoxPseudo.Before, box.Pseudo);
        Assert.True(box.IsPseudoElement);
        Assert.False(box.IsAnonymous);
    }

    [Fact]
    public void Anonymous_with_AnonymousBlock_succeeds()
    {
        var style = FreshStyle();
        var box = Box.Anonymous(BoxKind.AnonymousBlock, style);
        Assert.Null(box.SourceElement);
        Assert.Equal(BoxPseudo.None, box.Pseudo);
        Assert.True(box.IsAnonymous);
    }

    [Fact]
    public void Anonymous_with_TableGrid_succeeds()
    {
        // TableGrid is always anonymous per Tables L3 §2.1.
        var style = FreshStyle();
        var box = Box.Anonymous(BoxKind.TableGrid, style);
        Assert.Equal(BoxKind.TableGrid, box.Kind);
        Assert.Null(box.SourceElement);
    }

    [Fact]
    public void Anonymous_with_LineBox_succeeds()
    {
        var style = FreshStyle();
        var box = Box.Anonymous(BoxKind.LineBox, style);
        Assert.Equal(BoxKind.LineBox, box.Kind);
    }

    [Fact]
    public void Anonymous_with_non_anonymous_kind_throws()
    {
        // Per Rec 4: Anonymous() factory must reject kinds that should carry
        // a source element (BlockContainer, InlineBox, etc.).
        var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            Box.Anonymous(BoxKind.BlockContainer, style));
        Assert.Throws<ArgumentException>(() =>
            Box.Anonymous(BoxKind.InlineBox, style));
        Assert.Throws<ArgumentException>(() =>
            Box.Anonymous(BoxKind.Table, style));
    }

    [Fact]
    public void TextRun_carries_text_and_style()
    {
        var style = FreshStyle();
        var box = Box.TextRun("hello world", style);
        Assert.Equal(BoxKind.TextRun, box.Kind);
        Assert.Equal("hello world", box.Text);
        Assert.True(box.IsInlineLevel);
        Assert.False(box.IsBlockLevel);
    }

    [Fact]
    public void TextRun_with_empty_text_is_legal()
    {
        // An empty text run is degenerate but legal — the constructor validates
        // that non-empty text only appears on TextRun, not the reverse.
        var style = FreshStyle();
        var box = Box.TextRun(string.Empty, style);
        Assert.Equal(BoxKind.TextRun, box.Kind);
        Assert.Empty(box.Text);
    }

    [Fact]
    public void TextRun_rejects_null_text()
    {
        var style = FreshStyle();
        Assert.Throws<ArgumentNullException>(() => Box.TextRun(null!, style));
    }

    [Fact]
    public void CreateRoot_has_root_kind_and_no_parent()
    {
        var style = FreshStyle();
        var root = Box.CreateRoot(style);
        Assert.Equal(BoxKind.Root, root.Kind);
        Assert.Null(root.SourceElement);
        Assert.Null(root.Parent);
        // The root is not "anonymous" in the box-generation sense — it's structural.
        Assert.False(root.IsAnonymous);
        Assert.True(root.IsBlockLevel);
    }

    // ============================================================
    // Rec 4 — Constructor invariants
    // ============================================================

    [Fact]
    public async Task Constructor_rejects_Root_kind_with_source_element()
    {
        // Rec 4: Root is always anonymous — must come through CreateRoot, never carry a source.
        var el = await AnyElement();
        var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            new Box(BoxKind.Root, style, el, BoxPseudo.None, string.Empty));
    }

    [Fact]
    public void Constructor_rejects_LineBox_with_source_element()
    {
        // Direct constructor — LineBox must be anonymous.
        var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
        {
            // Synthesize an element via a fresh DOM since we can't test without one.
            var ctx = BrowsingContext.New(Configuration.Default);
            var doc = ctx.OpenNewAsync().GetAwaiter().GetResult();
            new Box(BoxKind.LineBox, style, doc.DocumentElement, BoxPseudo.None, string.Empty);
        });
    }

    [Fact]
    public async Task Constructor_rejects_Marker_pseudo_on_non_Marker_kind()
    {
        // Rec 4: Marker pseudo must pair with Marker kind.
        var el = await AnyElement();
        var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            new Box(BoxKind.InlineBox, style, el, BoxPseudo.Marker, string.Empty));
    }

    [Fact]
    public async Task Constructor_accepts_Marker_pseudo_on_Marker_kind()
    {
        var el = await AnyElement();
        var style = FreshStyle();
        var box = new Box(BoxKind.Marker, style, el, BoxPseudo.Marker, string.Empty);
        Assert.Equal(BoxKind.Marker, box.Kind);
        Assert.Equal(BoxPseudo.Marker, box.Pseudo);
    }

    [Fact]
    public async Task Constructor_rejects_non_empty_text_on_non_TextRun()
    {
        // Rec 4: only TextRun may carry non-empty text.
        var el = await AnyElement();
        var style = FreshStyle();
        Assert.Throws<ArgumentException>(() =>
            new Box(BoxKind.InlineBox, style, el, BoxPseudo.None, "stray text"));
    }

    [Fact]
    public void Constructor_rejects_pseudo_box_without_source_element()
    {
        var style = FreshStyle();
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
        var style = FreshStyle();
        Assert.Throws<ArgumentNullException>(() =>
            new Box(BoxKind.BlockContainer, style, null, BoxPseudo.None, null!));
    }

    // ============================================================
    // Parent/child mutation
    // ============================================================

    [Fact]
    public void AppendChild_links_parent_pointer()
    {
        var rootStyle = FreshStyle();
        var childStyle = FreshStyle();
        var root = Box.CreateRoot(rootStyle);
        var child = Box.Anonymous(BoxKind.AnonymousBlock, childStyle);
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
        var s = FreshStyle();
        var parent1 = Box.CreateRoot(s);
        var parent2 = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.AnonymousBlock, s);
        parent1.AppendChild(child);
        Assert.Throws<InvalidOperationException>(() => parent2.AppendChild(child));
    }

    [Fact]
    public void AppendChild_rejects_self()
    {
        var s = FreshStyle();
        var box = Box.CreateRoot(s);
        Assert.Throws<InvalidOperationException>(() => box.AppendChild(box));
    }

    [Fact]
    public void AppendChild_rejects_null()
    {
        var s = FreshStyle();
        var box = Box.CreateRoot(s);
        Assert.Throws<ArgumentNullException>(() => box.AppendChild(null!));
    }

    // ============================================================
    // Rec 2 — Ancestor cycle prevention
    // ============================================================

    [Fact]
    public void AppendChild_rejects_attaching_grandparent_under_grandchild()
    {
        // Build A → B → C, then try C.AppendChild(A) — would create A → B → C → A.
        // The cycle-1 self-and-double-attach check missed this because A.Parent
        // is null (A is the root). Hardening Rec 2 closes the gap with an
        // ancestor walk.
        var s = FreshStyle();
        var a = Box.CreateRoot(s);
        var b = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var c = Box.Anonymous(BoxKind.AnonymousBlock, s);
        a.AppendChild(b);
        b.AppendChild(c);

        var ex = Assert.Throws<InvalidOperationException>(() => c.AppendChild(a));
        Assert.Contains("Cycle detected", ex.Message);
    }

    [Fact]
    public void AppendChild_rejects_attaching_parent_under_direct_child()
    {
        // B is a direct child of A. Try B.AppendChild(A). A.Parent is null
        // (root), so the parent-null check passes; the ancestor walk catches it.
        var s = FreshStyle();
        var a = Box.CreateRoot(s);
        var b = Box.Anonymous(BoxKind.AnonymousBlock, s);
        a.AppendChild(b);
        Assert.Throws<InvalidOperationException>(() => b.AppendChild(a));
    }

    [Fact]
    public void InsertChild_rejects_ancestor_cycle_too()
    {
        var s = FreshStyle();
        var a = Box.CreateRoot(s);
        var b = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var c = Box.Anonymous(BoxKind.AnonymousBlock, s);
        a.AppendChild(b);
        b.AppendChild(c);
        Assert.Throws<InvalidOperationException>(() => c.InsertChild(0, a));
    }

    // ============================================================
    // InsertChild + RemoveChild
    // ============================================================

    [Fact]
    public void InsertChild_at_specific_position()
    {
        var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var a = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var b = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var c = Box.Anonymous(BoxKind.AnonymousBlock, s);
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
        var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.AnonymousBlock, s);
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.InsertChild(5, child));
    }

    [Fact]
    public void InsertChild_at_count_appends()
    {
        var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var first = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var second = Box.Anonymous(BoxKind.AnonymousBlock, s);
        parent.AppendChild(first);
        parent.InsertChild(1, second);  // index == count → append
        Assert.Same(second, parent.LastChild);
    }

    [Fact]
    public void RemoveChild_clears_parent_pointer()
    {
        var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.AnonymousBlock, s);
        parent.AppendChild(child);
        parent.RemoveChild(child);
        Assert.Null(child.Parent);
        Assert.Empty(parent.Children);
    }

    [Fact]
    public void RemoveChild_after_removal_can_re_attach()
    {
        var s = FreshStyle();
        var p1 = Box.CreateRoot(s);
        var p2 = Box.CreateRoot(s);
        var child = Box.Anonymous(BoxKind.AnonymousBlock, s);
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
        var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var stranger = Box.Anonymous(BoxKind.AnonymousBlock, s);
        Assert.Throws<ArgumentException>(() => parent.RemoveChild(stranger));
    }

    // ============================================================
    // Rec 3 — Children is truly read-only (cast to IList<T> rejected)
    // ============================================================

    [Fact]
    public void Children_cannot_be_mutated_via_IList_cast()
    {
        // Rec 3 fix: Children is a ReadOnlyCollection<Box> — IList<T> mutation
        // methods throw NotSupportedException so consumers can't bypass the
        // parent-pointer invariant by casting.
        var s = FreshStyle();
        var parent = Box.CreateRoot(s);
        var rogue = Box.Anonymous(BoxKind.AnonymousBlock, s);

        IList<Box> asList = parent.Children;
        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList.Add(rogue));
        Assert.Throws<NotSupportedException>(() => asList.Insert(0, rogue));
        Assert.Throws<NotSupportedException>(() => asList.Remove(rogue));
        Assert.Throws<NotSupportedException>(() => asList.RemoveAt(0));
    }

    // ============================================================
    // Rec 6 — ComputedStyle box-ownership prevents pool re-rental
    // ============================================================

    [Fact]
    public void Constructing_a_Box_marks_its_style_as_box_owned()
    {
        var style = ComputedStyle.Rent();
        Assert.False(style.IsBoxOwned);
        var box = Box.CreateRoot(style);
        Assert.True(style.IsBoxOwned);
        Assert.Same(style, box.Style);
    }

    [Fact]
    public void Box_owned_style_does_not_return_to_pool_on_Dispose()
    {
        // Rec 6: a box-owned style refuses pool re-rental even after Dispose.
        // Otherwise the next Rent() could clear the slots while the box still reads them.
        var style = ComputedStyle.Rent();
        _ = Box.CreateRoot(style);
        style.Dispose();
        // Rent should return a fresh instance (or another pooled one), NOT
        // the box-owned `style` we just disposed.
        for (var i = 0; i < 10; i++)
        {
            var rented = ComputedStyle.Rent();
            Assert.NotSame(style, rented);
            rented.Dispose();
        }
    }

    [Fact]
    public void Multiple_boxes_can_share_one_style_idempotently()
    {
        var style = FreshStyle();
        var a = Box.CreateRoot(style);
        var b = Box.Anonymous(BoxKind.AnonymousBlock, style);
        Assert.True(style.IsBoxOwned);
        Assert.Same(style, a.Style);
        Assert.Same(style, b.Style);
    }

    // ============================================================
    // Predicates over BoxKind
    // ============================================================

    private static void AssertBlockLevel(BoxKind kind)
    {
        var s = FreshStyle();
        var box = kind == BoxKind.Root
            ? Box.CreateRoot(s)
            : InstantiateForPredicateTest(kind, s);
        Assert.True(box.IsBlockLevel, $"{kind} should be block-level");
        Assert.False(box.IsInlineLevel, $"{kind} should not be inline-level");
    }

    private static void AssertInlineLevel(BoxKind kind)
    {
        var s = FreshStyle();
        var box = InstantiateForPredicateTest(kind, s);
        Assert.True(box.IsInlineLevel, $"{kind} should be inline-level");
        Assert.False(box.IsBlockLevel, $"{kind} should not be block-level");
    }

    private static void AssertNeither(BoxKind kind)
    {
        var s = FreshStyle();
        var box = InstantiateForPredicateTest(kind, s);
        Assert.False(box.IsBlockLevel, $"{kind} should not be block-level");
        Assert.False(box.IsInlineLevel, $"{kind} should not be inline-level");
    }

    private static void AssertTablePart(BoxKind kind)
    {
        var s = FreshStyle();
        var box = InstantiateForPredicateTest(kind, s);
        Assert.True(box.IsTablePart, $"{kind} should be a table part");
    }

    private static void AssertNotTablePart(BoxKind kind)
    {
        var s = FreshStyle();
        var box = kind == BoxKind.Root ? Box.CreateRoot(s) : InstantiateForPredicateTest(kind, s);
        Assert.False(box.IsTablePart, $"{kind} should not be a table part");
    }

    private static Box InstantiateForPredicateTest(BoxKind kind, ComputedStyle style)
    {
        // For predicate testing we don't need a real DOM element — synthesize via
        // ForElement on kinds that allow source, Anonymous on kinds that require it.
        if (kind is BoxKind.Root) return Box.CreateRoot(style);
        if (kind is BoxKind.LineBox or BoxKind.AnonymousBlock or BoxKind.AnonymousInline or BoxKind.TableGrid)
            return Box.Anonymous(kind, style);
        if (kind is BoxKind.TextRun) return Box.TextRun("x", style);
        // Element-bearing kinds: borrow a quick DOM element.
        var doc = SyncDoc();
        var el = doc.CreateElement("div");
        return Box.ForElement(kind, style, el);
    }

    [Fact] public void Root_is_block_level()                    => AssertBlockLevel(BoxKind.Root);
    [Fact] public void BlockContainer_is_block_level()          => AssertBlockLevel(BoxKind.BlockContainer);
    [Fact] public void ListItem_is_block_level()                => AssertBlockLevel(BoxKind.ListItem);
    [Fact] public void AnonymousBlock_is_block_level()          => AssertBlockLevel(BoxKind.AnonymousBlock);
    [Fact] public void Table_is_block_level()                   => AssertBlockLevel(BoxKind.Table);
    [Fact] public void FlexContainer_is_block_level()           => AssertBlockLevel(BoxKind.FlexContainer);
    [Fact] public void GridContainer_is_block_level()           => AssertBlockLevel(BoxKind.GridContainer);
    [Fact] public void BlockReplacedElement_is_block_level()    => AssertBlockLevel(BoxKind.BlockReplacedElement);

    [Fact] public void InlineBox_is_inline_level()              => AssertInlineLevel(BoxKind.InlineBox);
    [Fact] public void InlineBlockContainer_is_inline_level()   => AssertInlineLevel(BoxKind.InlineBlockContainer);
    [Fact] public void InlineFlexContainer_is_inline_level()    => AssertInlineLevel(BoxKind.InlineFlexContainer);
    [Fact] public void InlineGridContainer_is_inline_level()    => AssertInlineLevel(BoxKind.InlineGridContainer);
    [Fact] public void InlineTable_is_inline_level()            => AssertInlineLevel(BoxKind.InlineTable);
    [Fact] public void InlineReplacedElement_is_inline_level()  => AssertInlineLevel(BoxKind.InlineReplacedElement);
    [Fact] public void TextRun_is_inline_level()                => AssertInlineLevel(BoxKind.TextRun);
    [Fact] public void AnonymousInline_is_inline_level()        => AssertInlineLevel(BoxKind.AnonymousInline);

    [Fact] public void LineBox_is_neither_block_nor_inline()    => AssertNeither(BoxKind.LineBox);
    [Fact] public void Marker_is_neither_block_nor_inline()     => AssertNeither(BoxKind.Marker);
    [Fact] public void TableGrid_is_neither_block_nor_inline()  => AssertNeither(BoxKind.TableGrid);

    [Fact] public void Table_is_table_part()                    => AssertTablePart(BoxKind.Table);
    [Fact] public void InlineTable_is_table_part()              => AssertTablePart(BoxKind.InlineTable);
    [Fact] public void TableGrid_is_table_part()                => AssertTablePart(BoxKind.TableGrid);
    [Fact] public void TableRowGroup_is_table_part()            => AssertTablePart(BoxKind.TableRowGroup);
    [Fact] public void TableHeaderGroup_is_table_part()         => AssertTablePart(BoxKind.TableHeaderGroup);
    [Fact] public void TableFooterGroup_is_table_part()         => AssertTablePart(BoxKind.TableFooterGroup);
    [Fact] public void TableRow_is_table_part()                 => AssertTablePart(BoxKind.TableRow);
    [Fact] public void TableCell_is_table_part()                => AssertTablePart(BoxKind.TableCell);
    [Fact] public void TableColumnGroup_is_table_part()         => AssertTablePart(BoxKind.TableColumnGroup);
    [Fact] public void TableColumn_is_table_part()              => AssertTablePart(BoxKind.TableColumn);
    [Fact] public void TableCaption_is_table_part()             => AssertTablePart(BoxKind.TableCaption);

    [Fact] public void BlockContainer_is_not_table_part()       => AssertNotTablePart(BoxKind.BlockContainer);
    [Fact] public void InlineBox_is_not_table_part()            => AssertNotTablePart(BoxKind.InlineBox);
    [Fact] public void FlexContainer_is_not_table_part()        => AssertNotTablePart(BoxKind.FlexContainer);
    [Fact] public void Root_is_not_table_part()                 => AssertNotTablePart(BoxKind.Root);

    // ============================================================
    // Rec 1 — Inline-* atomic predicates
    // ============================================================

    [Fact]
    public void IsAtomicInline_covers_inline_block_inline_flex_inline_grid_inline_table_inline_replaced()
    {
        var s = FreshStyle();
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineBlockContainer, s).IsAtomicInline);
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineFlexContainer, s).IsAtomicInline);
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineGridContainer, s).IsAtomicInline);
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineTable, s).IsAtomicInline);
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineReplacedElement, s).IsAtomicInline);

        // Non-atomic inline:
        Assert.False(InstantiateForPredicateTest(BoxKind.InlineBox, s).IsAtomicInline);
        Assert.False(InstantiateForPredicateTest(BoxKind.TextRun, s).IsAtomicInline);
        // Block-level kinds:
        Assert.False(InstantiateForPredicateTest(BoxKind.BlockContainer, s).IsAtomicInline);
        Assert.False(InstantiateForPredicateTest(BoxKind.FlexContainer, s).IsAtomicInline);
    }

    [Fact]
    public void IsReplaced_covers_block_and_inline_replaced_only()
    {
        var s = FreshStyle();
        Assert.True(InstantiateForPredicateTest(BoxKind.BlockReplacedElement, s).IsReplaced);
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineReplacedElement, s).IsReplaced);
        Assert.False(InstantiateForPredicateTest(BoxKind.BlockContainer, s).IsReplaced);
        Assert.False(InstantiateForPredicateTest(BoxKind.InlineBox, s).IsReplaced);
    }

    // ============================================================
    // Rec 5 — Table wrapper vs grid distinction
    // ============================================================

    [Fact]
    public void IsTableWrapper_returns_true_for_Table_and_InlineTable_only()
    {
        var s = FreshStyle();
        Assert.True(InstantiateForPredicateTest(BoxKind.Table, s).IsTableWrapper);
        Assert.True(InstantiateForPredicateTest(BoxKind.InlineTable, s).IsTableWrapper);
        // The grid is NOT the wrapper.
        Assert.False(InstantiateForPredicateTest(BoxKind.TableGrid, s).IsTableWrapper);
        // Other table parts are NOT wrappers.
        Assert.False(InstantiateForPredicateTest(BoxKind.TableRow, s).IsTableWrapper);
        Assert.False(InstantiateForPredicateTest(BoxKind.TableCell, s).IsTableWrapper);
    }

    [Fact]
    public void Table_wrapper_can_contain_TableGrid_anonymous_child()
    {
        // The intended structural pattern per Tables L3 §2.1: wrapper contains
        // the anonymous grid box.
        var s = FreshStyle();
        var doc = SyncDoc();
        var el = doc.CreateElement("table");
        var wrapper = Box.ForElement(BoxKind.Table, s, el);
        var grid = Box.Anonymous(BoxKind.TableGrid, s);
        wrapper.AppendChild(grid);
        Assert.Same(wrapper, grid.Parent);
        Assert.True(wrapper.IsTableWrapper);
        Assert.True(grid.IsTablePart);
        Assert.False(grid.IsTableWrapper);
    }

    // ============================================================
    // CountDescendants traversal
    // ============================================================

    [Fact]
    public void CountDescendants_counts_full_subtree()
    {
        var s = FreshStyle();
        var root = Box.CreateRoot(s);
        var a = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var b = Box.Anonymous(BoxKind.AnonymousBlock, s);
        var c = Box.Anonymous(BoxKind.AnonymousInline, s);
        var d = Box.TextRun("x", s);
        root.AppendChild(a);
        root.AppendChild(b);
        a.AppendChild(c);
        c.AppendChild(d);
        Assert.Equal(4, root.CountDescendants());
        Assert.Equal(2, a.CountDescendants());
        Assert.Equal(0, b.CountDescendants());
        Assert.Equal(1, c.CountDescendants());
    }

    [Fact]
    public void Empty_box_has_no_first_or_last_child()
    {
        var s = FreshStyle();
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
        var rootStyle = FreshStyle();
        var root = Box.CreateRoot(rootStyle);
        var anon = Box.Anonymous(BoxKind.AnonymousBlock, rootStyle);
        root.AppendChild(anon);
        Assert.Same(rootStyle, anon.Style);
        Assert.Same(rootStyle, root.Style);
    }
}
