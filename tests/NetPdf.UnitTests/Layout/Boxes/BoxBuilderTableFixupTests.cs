// Copyright 2026 Roland Aroche and NetPdf contributors.
// Linq is intentionally avoided in production code per CLAUDE.md, but tests
// freely use it.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Task 13 — table fixup per CSS Tables L3 §3 + §2.1: wrapper + grid split,
/// anonymous row-group / row / cell synthesis for bare table internals,
/// whitespace-only text stripping between table internals.
/// </summary>
public sealed class BoxBuilderTableFixupTests
{
    // ============================================================
    // Test infrastructure (mirrors BoxBuilderTests / BoxBuilderHardeningTests)
    // ============================================================

    private static async Task<IDocument> ParseHtml(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        return await ctx.OpenAsync(req => req.Content(html));
    }

    private static async Task<CssStylesheet> ParseSheet(string css)
    {
        var ctx = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = ctx.GetService<AngleSharp.Css.Parser.ICssParser>()!;
        var sheet = parser.ParseStyleSheet(css);
        return CssParserAdapter.Adapt(sheet, href: null,
            origin: CssStylesheetOrigin.Author,
            ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);
    }

    private static async Task<Box> BuildAsync(string html, string? css = null)
    {
        var doc = await ParseHtml(html);
        var sheets = css is null
            ? ImmutableArray<CssStylesheet>.Empty
            : ImmutableArray.Create(await ParseSheet(css));
        var cascade = CascadeResolver.Resolve(doc, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, doc);
        return BoxBuilder.Build(doc, resolved);
    }

    private static IEnumerable<Box> Walk(Box root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in Walk(c))
                yield return d;
    }

    private static Box FirstTableWrapper(Box root) =>
        Walk(root).First(b => b.IsTableWrapper);

    private static Box FirstGrid(Box root) =>
        Walk(root).First(b => b.Kind == BoxKind.TableGrid);

    // ============================================================
    // Wrapper / grid split per Tables L3 §2.1
    // ============================================================

    [Fact]
    public async Task Table_wrapper_owns_exactly_one_anonymous_TableGrid()
    {
        var root = await BuildAsync("<table><tr><td>x</td></tr></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Single(wrapper.Children);
        var grid = wrapper.Children[0];
        Assert.Equal(BoxKind.TableGrid, grid.Kind);
        Assert.True(grid.IsAnonymous);
        Assert.Null(grid.SourceElement);
    }

    [Fact]
    public async Task TableGrid_holds_the_row_group_chain_not_the_wrapper()
    {
        var root = await BuildAsync("<table><tr><td>x</td></tr></table>");
        var wrapper = FirstTableWrapper(root);
        var grid = wrapper.Children[0];
        // The HTML5-auto-inserted <tbody> becomes a TableRowGroup, which
        // must live UNDER the grid, not directly under the wrapper.
        Assert.Contains(grid.Children, c => c.Kind == BoxKind.TableRowGroup);
        Assert.DoesNotContain(wrapper.Children, c => c.Kind == BoxKind.TableRowGroup);
    }

    [Fact]
    public async Task Inline_table_wrapper_also_gets_TableGrid()
    {
        var root = await BuildAsync(
            "<span class='it'><span>cell</span></span>",
            ".it { display: inline-table } .it > span { display: table-cell }");
        var wrapper = Walk(root).First(b => b.Kind == BoxKind.InlineTable);
        Assert.Single(wrapper.Children);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[0].Kind);
    }

    // ============================================================
    // Captions stay direct children of the wrapper, NOT inside the grid
    // ============================================================

    [Fact]
    public async Task Caption_is_direct_wrapper_child_not_inside_grid()
    {
        var root = await BuildAsync(
            "<table><caption>Title</caption><tr><td>x</td></tr></table>");
        var wrapper = FirstTableWrapper(root);
        // Wrapper children: [caption, grid].
        Assert.Equal(2, wrapper.Children.Count);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[0].Kind);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[1].Kind);
        // Caption is NOT inside the grid.
        var grid = wrapper.Children[1];
        Assert.DoesNotContain(grid.Children, c => c.Kind == BoxKind.TableCaption);
    }

    [Fact]
    public async Task Multiple_captions_all_become_direct_wrapper_children()
    {
        var root = await BuildAsync(
            "<table><caption>A</caption><caption>B</caption><tr><td>x</td></tr></table>");
        var wrapper = FirstTableWrapper(root);
        var captionCount = wrapper.Children.Count(c => c.Kind == BoxKind.TableCaption);
        Assert.Equal(2, captionCount);
        Assert.Single(wrapper.Children, c => c.Kind == BoxKind.TableGrid);
    }

    // ============================================================
    // Whitespace stripping per Tables L3 §3.1
    // ============================================================

    [Fact]
    public async Task Whitespace_between_table_rows_does_not_become_anon_cell()
    {
        // The HTML5 parser preserves whitespace between <tr> tags as text
        // nodes inside <tbody>. Without §3.1 stripping, those would become
        // anonymous cells full of whitespace inside an anonymous row.
        var html = """
            <table>
              <tr><td>a</td></tr>
              <tr><td>b</td></tr>
            </table>
            """;
        var root = await BuildAsync(html);
        // No anonymous TableCell carrying whitespace text.
        var anonCells = Walk(root)
            .Where(b => b.Kind == BoxKind.TableCell && b.IsAnonymous)
            .ToList();
        Assert.Empty(anonCells);
    }

    [Fact]
    public async Task Whitespace_between_cells_in_a_row_does_not_generate_boxes()
    {
        var html = "<table><tr>  <td>a</td>  <td>b</td>  </tr></table>";
        var root = await BuildAsync(html);
        var row = Walk(root).First(b => b.Kind == BoxKind.TableRow);
        // Row should have exactly the two real cells — no anonymous cells.
        Assert.Equal(2, row.Children.Count);
        foreach (var cell in row.Children)
        {
            Assert.Equal(BoxKind.TableCell, cell.Kind);
            Assert.False(cell.IsAnonymous);
        }
    }

    // ============================================================
    // Anonymous row-group / row / cell synthesis (§3.1.1 + §3.1.2)
    // ============================================================

    [Fact]
    public async Task Bare_row_inside_table_gets_wrapped_in_anon_row_group()
    {
        // CSS-only path: <div display:table> with <div display:table-row>
        // children. No row-group around them — fixup must synthesize one.
        var root = await BuildAsync(
            "<div class='t'><div class='r'><div class='c'>x</div></div></div>",
            ".t { display: table } .r { display: table-row } .c { display: table-cell }");
        var grid = FirstGrid(root);
        // Grid's child is an anonymous TableRowGroup wrapping the bare row.
        Assert.Single(grid.Children);
        var group = grid.Children[0];
        Assert.Equal(BoxKind.TableRowGroup, group.Kind);
        Assert.True(group.IsAnonymous);
        Assert.Single(group.Children);
        Assert.Equal(BoxKind.TableRow, group.Children[0].Kind);
        Assert.False(group.Children[0].IsAnonymous);
    }

    [Fact]
    public async Task Bare_cell_inside_row_group_gets_wrapped_in_anon_row()
    {
        // CSS-only path — HTML5 would foster-parent a <span> outside the table.
        // Here a <div display:table-row-group> directly contains a
        // <div display:table-cell> with no row in between.
        var root = await BuildAsync(
            "<div class='t'><div class='g'><div class='c'>x</div></div></div>",
            ".t { display: table } .g { display: table-row-group } .c { display: table-cell }");
        var rowGroup = Walk(root).First(b => b.Kind == BoxKind.TableRowGroup
            && !b.IsAnonymous);
        Assert.Single(rowGroup.Children);
        var anonRow = rowGroup.Children[0];
        Assert.Equal(BoxKind.TableRow, anonRow.Kind);
        Assert.True(anonRow.IsAnonymous);
        Assert.Single(anonRow.Children);
        Assert.Equal(BoxKind.TableCell, anonRow.Children[0].Kind);
        Assert.False(anonRow.Children[0].IsAnonymous);
    }

    [Fact]
    public async Task Non_cell_inside_row_gets_wrapped_in_anon_cell()
    {
        // <tr> with a bare <span> child. HTML5 normally foster-parents this
        // outside the table, but the box-fixup contract still has to handle
        // it for CSS-driven cases.
        var root = await BuildAsync(
            "<div class='t'><div class='r'><span>loose</span></div></div>",
            ".t { display: table } .r { display: table-row }");
        var row = Walk(root).First(b => b.Kind == BoxKind.TableRow);
        Assert.Single(row.Children);
        var anonCell = row.Children[0];
        Assert.Equal(BoxKind.TableCell, anonCell.Kind);
        Assert.True(anonCell.IsAnonymous);
        // Loose content lives inside the synthesized cell.
        Assert.Contains(anonCell.Children, c => c.Kind == BoxKind.InlineBox);
    }

    [Fact]
    public async Task Bare_cell_at_grid_level_gets_anon_row_and_anon_row_group()
    {
        var root = await BuildAsync(
            "<div class='t'><div class='c'>x</div></div>",
            ".t { display: table } .c { display: table-cell }");
        var grid = FirstGrid(root);
        Assert.Single(grid.Children);
        var anonGroup = grid.Children[0];
        Assert.Equal(BoxKind.TableRowGroup, anonGroup.Kind);
        Assert.True(anonGroup.IsAnonymous);
        Assert.Single(anonGroup.Children);
        var anonRow = anonGroup.Children[0];
        Assert.Equal(BoxKind.TableRow, anonRow.Kind);
        Assert.True(anonRow.IsAnonymous);
        Assert.Single(anonRow.Children);
        Assert.Equal(BoxKind.TableCell, anonRow.Children[0].Kind);
        Assert.False(anonRow.Children[0].IsAnonymous);
    }

    [Fact]
    public async Task Consecutive_bare_rows_share_one_anon_row_group()
    {
        var root = await BuildAsync(
            "<div class='t'><div class='r'><div class='c'>a</div></div><div class='r'><div class='c'>b</div></div></div>",
            ".t { display: table } .r { display: table-row } .c { display: table-cell }");
        var grid = FirstGrid(root);
        Assert.Single(grid.Children);
        var anonGroup = grid.Children[0];
        Assert.Equal(BoxKind.TableRowGroup, anonGroup.Kind);
        Assert.True(anonGroup.IsAnonymous);
        Assert.Equal(2, anonGroup.Children.Count);
        Assert.All(anonGroup.Children, c => Assert.Equal(BoxKind.TableRow, c.Kind));
    }

    // ============================================================
    // thead / tbody / tfoot pass through unchanged
    // ============================================================

    [Fact]
    public async Task Explicit_thead_tbody_tfoot_all_pass_through_under_grid()
    {
        var html = """
            <table>
              <thead><tr><th>h</th></tr></thead>
              <tbody><tr><td>b</td></tr></tbody>
              <tfoot><tr><td>f</td></tr></tfoot>
            </table>
            """;
        var root = await BuildAsync(html);
        var grid = FirstGrid(root);
        var groupKinds = grid.Children.Select(c => c.Kind).ToList();
        Assert.Contains(BoxKind.TableHeaderGroup, groupKinds);
        Assert.Contains(BoxKind.TableRowGroup, groupKinds);
        Assert.Contains(BoxKind.TableFooterGroup, groupKinds);
        // None of the row-groups should be anonymous (they came from explicit elements).
        foreach (var c in grid.Children)
        {
            Assert.False(c.IsAnonymous);
        }
    }

    // ============================================================
    // Nested table — both wrappers fixed independently
    // ============================================================

    [Fact]
    public async Task Nested_tables_each_get_their_own_TableGrid()
    {
        var html = """
            <table>
              <tr><td>
                <table><tr><td>inner</td></tr></table>
              </td></tr>
            </table>
            """;
        var root = await BuildAsync(html);
        var wrappers = Walk(root).Where(b => b.IsTableWrapper).ToList();
        Assert.Equal(2, wrappers.Count);
        foreach (var w in wrappers)
        {
            Assert.Single(w.Children);
            Assert.Equal(BoxKind.TableGrid, w.Children[0].Kind);
        }
    }

    // ============================================================
    // Empty table — wrapper still gets a (possibly empty) grid
    // ============================================================

    [Fact]
    public async Task Empty_table_still_owns_a_TableGrid()
    {
        var root = await BuildAsync("<table></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Single(wrapper.Children);
        var grid = wrapper.Children[0];
        Assert.Equal(BoxKind.TableGrid, grid.Kind);
        Assert.Empty(grid.Children);
    }

    [Fact]
    public async Task Caption_only_table_keeps_caption_direct_grid_empty()
    {
        var root = await BuildAsync("<table><caption>only</caption></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Equal(2, wrapper.Children.Count);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[0].Kind);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[1].Kind);
        Assert.Empty(wrapper.Children[1].Children);
    }

    // ============================================================
    // Mixed bare row + bare cells coalesce reasonably
    // ============================================================

    // ============================================================
    // Rec 1 — Tree-wide orphan fixup (Tables L3 §3.1 missing-parents walk)
    // ============================================================

    [Fact]
    public async Task Loose_table_cell_outside_table_gets_synthesized_table_wrapper()
    {
        // <body><div display:table-cell>x</div></body> — the cell has no table
        // ancestor. Tables L3 §3.1 requires synthesizing the missing
        // table+grid+row-group+row scaffolding around it.
        var root = await BuildAsync(
            "<div class='c'>x</div>",
            ".c { display: table-cell }");
        // Find the synthesized anon Table wrapper.
        var anonTable = Walk(root)
            .First(b => b.Kind == BoxKind.Table && b.IsAnonymous);
        Assert.True(anonTable.IsTableWrapper);
        // Its grid must contain anon row-group → anon row → real cell.
        var grid = anonTable.Children.First(c => c.Kind == BoxKind.TableGrid);
        var anonGroup = grid.Children.First(c => c.Kind == BoxKind.TableRowGroup);
        Assert.True(anonGroup.IsAnonymous);
        var anonRow = anonGroup.Children.First(c => c.Kind == BoxKind.TableRow);
        Assert.True(anonRow.IsAnonymous);
        var realCell = anonRow.Children.First(c => c.Kind == BoxKind.TableCell);
        Assert.False(realCell.IsAnonymous);
        Assert.NotNull(realCell.SourceElement);
    }

    [Fact]
    public async Task Loose_table_row_outside_table_gets_synthesized_table_wrapper()
    {
        var root = await BuildAsync(
            "<div class='r'><div class='c'>x</div></div>",
            ".r { display: table-row } .c { display: table-cell }");
        var anonTable = Walk(root)
            .First(b => b.Kind == BoxKind.Table && b.IsAnonymous);
        var grid = anonTable.Children.First(c => c.Kind == BoxKind.TableGrid);
        // Bare row at the synthesized grid level → anon row-group wraps the
        // explicit (real) row.
        var anonGroup = grid.Children.First();
        Assert.Equal(BoxKind.TableRowGroup, anonGroup.Kind);
        Assert.True(anonGroup.IsAnonymous);
        var explicitRow = anonGroup.Children.First();
        Assert.Equal(BoxKind.TableRow, explicitRow.Kind);
        Assert.False(explicitRow.IsAnonymous);
    }

    [Fact]
    public async Task Loose_row_group_outside_table_gets_synthesized_table_wrapper()
    {
        var root = await BuildAsync(
            "<div class='g'><div class='r'><div class='c'>x</div></div></div>",
            ".g { display: table-row-group } .r { display: table-row } .c { display: table-cell }");
        var anonTable = Walk(root)
            .First(b => b.Kind == BoxKind.Table && b.IsAnonymous);
        var grid = anonTable.Children.First(c => c.Kind == BoxKind.TableGrid);
        // Explicit row-group passes through under grid.
        var explicitGroup = grid.Children.First();
        Assert.Equal(BoxKind.TableRowGroup, explicitGroup.Kind);
        Assert.False(explicitGroup.IsAnonymous);
    }

    [Fact]
    public async Task Consecutive_loose_orphans_share_one_synthesized_table()
    {
        // Two loose cells in a row should share one anon table+grid+row-group
        // and a single anon row containing both.
        var root = await BuildAsync(
            "<div class='c'>a</div><div class='c'>b</div>",
            ".c { display: table-cell }");
        var anonTables = Walk(root)
            .Where(b => b.Kind == BoxKind.Table && b.IsAnonymous)
            .ToList();
        Assert.Single(anonTables);
        var anonRow = Walk(anonTables[0])
            .First(b => b.Kind == BoxKind.TableRow && b.IsAnonymous);
        // Both real cells live in this anon row.
        var realCells = anonRow.Children
            .Where(c => c.Kind == BoxKind.TableCell && !c.IsAnonymous)
            .ToList();
        Assert.Equal(2, realCells.Count);
    }

    [Fact]
    public async Task Loose_orphans_separated_by_normal_block_get_separate_tables()
    {
        // A normal block between two loose cells should break the run — each
        // gets its own anon table.
        var root = await BuildAsync(
            "<div class='c'>a</div><p>between</p><div class='c'>b</div>",
            ".c { display: table-cell }");
        var anonTables = Walk(root)
            .Where(b => b.Kind == BoxKind.Table && b.IsAnonymous)
            .ToList();
        Assert.Equal(2, anonTables.Count);
    }

    [Fact]
    public async Task Loose_caption_outside_table_gets_synthesized_table_wrapper()
    {
        var root = await BuildAsync(
            "<div class='cap'>orphaned caption</div>",
            ".cap { display: table-caption }");
        var anonTable = Walk(root)
            .First(b => b.Kind == BoxKind.Table && b.IsAnonymous);
        // Caption stays as a direct wrapper child (Rec 5 + Tables L3 §2.1).
        Assert.Contains(anonTable.Children, c => c.Kind == BoxKind.TableCaption);
    }

    // ============================================================
    // Rec 2 — Anonymous-box style isolation (CSS 2.1 §17.5.1 / Tables L3 §3.2)
    // ============================================================

    [Fact]
    public async Task Anon_TableGrid_does_not_inherit_wrapper_padding()
    {
        // padding-top is non-inheritable. The wrapper has explicit padding;
        // the synthesized anon grid must take the initial value (0), NOT
        // inherit the wrapper's padding.
        var root = await BuildAsync(
            "<table class='t'><tr><td>x</td></tr></table>",
            ".t { padding-top: 12px }");
        var wrapper = FirstTableWrapper(root);
        var grid = wrapper.Children.First(c => c.Kind == BoxKind.TableGrid);
        // Wrapper has padding-top set (12px from the rule).
        Assert.True(wrapper.Style.IsSet(PropertyId.PaddingTop));
        // Grid's padding-top is the registry initial (0), not 12.
        Assert.True(grid.Style.IsSet(PropertyId.PaddingTop));
        Assert.Equal(0.0, grid.Style.Get(PropertyId.PaddingTop).AsLengthPx());
    }

    [Fact]
    public async Task Anon_synthesized_row_does_not_inherit_wrapper_padding()
    {
        // Padding on the wrapper must not leak to anon row-group / row / cell.
        var root = await BuildAsync(
            "<div class='t'><div class='c'>x</div></div>",
            ".t { display: table; padding-left: 25px } .c { display: table-cell }");
        var anonRow = Walk(root)
            .First(b => b.Kind == BoxKind.TableRow && b.IsAnonymous);
        Assert.Equal(0.0, anonRow.Style.Get(PropertyId.PaddingLeft).AsLengthPx());
    }

    [Fact]
    public async Task Anon_grid_DOES_inherit_inheritable_color_from_wrapper()
    {
        // Color is inheritable. The grid should pick up the wrapper's color.
        var root = await BuildAsync(
            "<table class='t'><tr><td>x</td></tr></table>",
            ".t { color: rgb(10, 20, 30) }");
        var wrapper = FirstTableWrapper(root);
        var grid = wrapper.Children.First(c => c.Kind == BoxKind.TableGrid);
        Assert.True(grid.Style.IsSet(PropertyId.Color));
        Assert.Equal(wrapper.Style.Get(PropertyId.Color), grid.Style.Get(PropertyId.Color));
    }

    // ============================================================
    // Rec 3 — Replaced element with table-internal display value (Tables L3 §2)
    // ============================================================

    [Fact]
    public async Task Replaced_element_with_display_table_cell_becomes_inline_replaced()
    {
        // <img display:table-cell> must NOT become a TableCell — replaced
        // elements are atomic and cannot host the structural roles a
        // table-internal display value implies. They become inline-level
        // replaced boxes per Tables L3 §2.
        var root = await BuildAsync(
            "<img class='c' src='x.png'>",
            ".c { display: table-cell }");
        var imgBox = Walk(root)
            .First(b => b.SourceElement?.LocalName == "img");
        Assert.Equal(BoxKind.InlineReplacedElement, imgBox.Kind);
    }

    [Fact]
    public async Task Replaced_element_with_display_table_row_becomes_inline_replaced()
    {
        var root = await BuildAsync(
            "<img class='r' src='x.png'>",
            ".r { display: table-row }");
        var imgBox = Walk(root).First(b => b.SourceElement?.LocalName == "img");
        Assert.Equal(BoxKind.InlineReplacedElement, imgBox.Kind);
    }

    [Fact]
    public async Task Replaced_element_with_display_table_caption_becomes_inline_replaced()
    {
        var root = await BuildAsync(
            "<img class='cap' src='x.png'>",
            ".cap { display: table-caption }");
        var imgBox = Walk(root).First(b => b.SourceElement?.LocalName == "img");
        Assert.Equal(BoxKind.InlineReplacedElement, imgBox.Kind);
    }

    [Fact]
    public async Task Non_replaced_with_display_table_cell_still_becomes_TableCell()
    {
        // The replaced-element exception must NOT fire for non-replaced
        // elements — a div with display:table-cell is a real TableCell.
        var root = await BuildAsync(
            "<div class='c'>x</div>",
            ".c { display: table-cell }");
        var div = Walk(root).First(b => b.SourceElement?.LocalName == "div");
        Assert.Equal(BoxKind.TableCell, div.Kind);
    }

    // ============================================================
    // Rec 4 — Drop irrelevant children of TableColumn / TableColumnGroup
    // ============================================================

    [Fact]
    public async Task TableColumn_drops_all_children()
    {
        // <col> is normally void in HTML, but CSS-driven cases (display:
        // table-column on a non-col element) can have content. Per Tables L3
        // §3.1.4 the children must be dropped.
        var root = await BuildAsync(
            "<div class='col'><span>oops</span></div>",
            ".col { display: table-column }");
        var col = Walk(root).First(b => b.Kind == BoxKind.TableColumn);
        Assert.Empty(col.Children);
    }

    [Fact]
    public async Task TableColumnGroup_keeps_only_TableColumn_children()
    {
        var root = await BuildAsync(
            "<div class='cg'><div class='col'></div><span>nope</span><p>also-nope</p></div>",
            ".cg { display: table-column-group } .col { display: table-column }");
        var colGroup = Walk(root).First(b => b.Kind == BoxKind.TableColumnGroup);
        // Only the <div class='col'> should survive.
        Assert.Single(colGroup.Children);
        Assert.Equal(BoxKind.TableColumn, colGroup.Children[0].Kind);
    }

    // ============================================================
    // Rec 5 — Caption source order preserved relative to the grid
    // ============================================================

    [Fact]
    public async Task Caption_before_tbody_lands_before_grid_in_source_order()
    {
        var root = await BuildAsync(
            "<table><caption>top</caption><tr><td>x</td></tr></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Equal(2, wrapper.Children.Count);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[0].Kind);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[1].Kind);
    }

    [Fact]
    public async Task Caption_after_tbody_lands_after_grid_in_source_order()
    {
        // Source order: tbody first (HTML5 auto-inserts it for <tr>), then
        // caption. Caption must appear AFTER the grid in the box tree.
        var root = await BuildAsync(
            "<table><tr><td>x</td></tr><caption>bottom</caption></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Equal(2, wrapper.Children.Count);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[0].Kind);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[1].Kind);
    }

    [Fact]
    public async Task Captions_on_both_sides_split_correctly_around_grid()
    {
        var root = await BuildAsync(
            "<table><caption>top</caption><tr><td>x</td></tr><caption>bottom</caption></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Equal(3, wrapper.Children.Count);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[0].Kind);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[1].Kind);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[2].Kind);
    }

    [Fact]
    public async Task Multiple_captions_before_tbody_all_land_before_grid()
    {
        var root = await BuildAsync(
            "<table><caption>a</caption><caption>b</caption><tr><td>x</td></tr></table>");
        var wrapper = FirstTableWrapper(root);
        Assert.Equal(3, wrapper.Children.Count);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[0].Kind);
        Assert.Equal(BoxKind.TableCaption, wrapper.Children[1].Kind);
        Assert.Equal(BoxKind.TableGrid, wrapper.Children[2].Kind);
    }

    [Fact]
    public async Task Bare_cell_followed_by_bare_row_creates_two_anon_groups()
    {
        // Cell first, then row. Per Tables L3 §3.1.1 the cell needs anon row
        // + anon row-group; the row needs only anon row-group. They must not
        // be merged into a single row-group since that would re-order content.
        var root = await BuildAsync(
            "<div class='t'><div class='c'>cell</div><div class='r'><div class='c'>row-cell</div></div></div>",
            ".t { display: table } .r { display: table-row } .c { display: table-cell }");
        var grid = FirstGrid(root);
        Assert.Equal(2, grid.Children.Count);
        // First child: anon row-group containing anon row + bare cell.
        Assert.Equal(BoxKind.TableRowGroup, grid.Children[0].Kind);
        Assert.True(grid.Children[0].IsAnonymous);
        // Second child: anon row-group containing the explicit row.
        Assert.Equal(BoxKind.TableRowGroup, grid.Children[1].Kind);
        Assert.True(grid.Children[1].IsAnonymous);
        var explicitRow = grid.Children[1].Children[0];
        Assert.Equal(BoxKind.TableRow, explicitRow.Kind);
        Assert.False(explicitRow.IsAnonymous);
    }
}
