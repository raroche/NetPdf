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
using NetPdf.Css.Parser;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Task 14 — Pseudo-element materialization: ::marker for list-items,
/// extended ::before/::after content (multi-string concatenation + attr()),
/// ::first-line / ::first-letter cascade staging.
/// </summary>
public sealed class BoxBuilderPseudoTests
{
    // ============================================================
    // Test infrastructure
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

    private static Box FindMarker(Box parent) =>
        parent.Children.First(c => c.Kind == BoxKind.Marker);

    // ============================================================
    // ::marker for list-items — default disc / decimal
    // ============================================================

    [Fact]
    public async Task Unordered_list_item_gets_disc_marker_by_default()
    {
        var root = await BuildAsync("<ul><li>x</li></ul>");
        var li = Walk(root).First(b => b.Kind == BoxKind.ListItem);
        var marker = FindMarker(li);
        Assert.Equal(BoxPseudo.Marker, marker.Pseudo);
        Assert.Single(marker.Children);
        // Disc bullet U+2022 + trailing space.
        Assert.StartsWith("•", marker.Children[0].Text);
    }

    [Fact]
    public async Task Ordered_list_items_get_decimal_markers_in_sequence()
    {
        var root = await BuildAsync("<ol><li>a</li><li>b</li><li>c</li></ol>");
        var lis = Walk(root).Where(b => b.Kind == BoxKind.ListItem).ToList();
        Assert.Equal(3, lis.Count);
        var markers = lis.Select(li => li.Children.First(c => c.Kind == BoxKind.Marker))
            .Select(m => m.Children[0].Text)
            .ToList();
        Assert.StartsWith("1.", markers[0]);
        Assert.StartsWith("2.", markers[1]);
        Assert.StartsWith("3.", markers[2]);
    }

    [Fact]
    public async Task List_style_type_none_suppresses_marker()
    {
        var root = await BuildAsync(
            "<ol class='nm'><li>x</li></ol>",
            ".nm { list-style-type: none }");
        var li = Walk(root).First(b => b.Kind == BoxKind.ListItem);
        Assert.DoesNotContain(li.Children, c => c.Kind == BoxKind.Marker);
    }

    [Fact]
    public async Task List_style_type_circle_emits_circle_glyph()
    {
        var root = await BuildAsync(
            "<ul class='c'><li>x</li></ul>",
            ".c { list-style-type: circle }");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.StartsWith("◦", marker.Children[0].Text);
    }

    [Fact]
    public async Task List_style_type_square_emits_square_glyph()
    {
        var root = await BuildAsync(
            "<ul class='c'><li>x</li></ul>",
            ".c { list-style-type: square }");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.StartsWith("▪", marker.Children[0].Text);
    }

    [Fact]
    public async Task Decimal_leading_zero_pads_single_digit_positions()
    {
        var root = await BuildAsync(
            "<ol class='lz'><li>a</li><li>b</li></ol>",
            ".lz { list-style-type: decimal-leading-zero }");
        var markers = Walk(root)
            .Where(b => b.Kind == BoxKind.Marker)
            .Select(m => m.Children[0].Text).ToList();
        Assert.StartsWith("01.", markers[0]);
        Assert.StartsWith("02.", markers[1]);
    }

    [Fact]
    public async Task Lower_alpha_marker_uses_a_b_c_sequence()
    {
        var root = await BuildAsync(
            "<ol class='la'><li>a</li><li>b</li><li>c</li></ol>",
            ".la { list-style-type: lower-alpha }");
        var markers = Walk(root)
            .Where(b => b.Kind == BoxKind.Marker)
            .Select(m => m.Children[0].Text).ToList();
        Assert.StartsWith("a.", markers[0]);
        Assert.StartsWith("b.", markers[1]);
        Assert.StartsWith("c.", markers[2]);
    }

    [Fact]
    public async Task Upper_roman_marker_handles_complex_numerals()
    {
        // Position 4 → IV, position 9 → IX. Generate an ol with 9 items.
        var lis = string.Concat(Enumerable.Range(1, 9).Select(_ => "<li>x</li>"));
        var root = await BuildAsync(
            $"<ol class='ur'>{lis}</ol>",
            ".ur { list-style-type: upper-roman }");
        var markers = Walk(root)
            .Where(b => b.Kind == BoxKind.Marker)
            .Select(m => m.Children[0].Text).ToList();
        Assert.StartsWith("IV.", markers[3]);
        Assert.StartsWith("IX.", markers[8]);
    }

    [Fact]
    public async Task Marker_pseudo_carries_BoxKind_Marker_and_BoxPseudo_Marker()
    {
        var root = await BuildAsync("<ul><li>x</li></ul>");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.Equal(BoxKind.Marker, marker.Kind);
        Assert.Equal(BoxPseudo.Marker, marker.Pseudo);
        Assert.NotNull(marker.SourceElement);
        Assert.Equal("li", marker.SourceElement!.LocalName);
    }

    [Fact]
    public async Task Author_marker_rule_styles_apply_to_marker_box()
    {
        var root = await BuildAsync(
            "<ul><li class='m'>x</li></ul>",
            ".m::marker { color: rgb(255, 0, 0) }");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        // Cascade applied → the marker's color slot is set (not the inherited
        // default). Verify by checking the slot is present.
        Assert.True(marker.Style.IsSet(NetPdf.Css.Properties.PropertyId.Color));
    }

    // ============================================================
    // Multi-string content concatenation
    // ============================================================

    [Fact]
    public async Task Multi_string_content_concatenates_in_source_order()
    {
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::before { content: 'A' 'B' 'C' }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("ABC", pseudo.Children[0].Text);
    }

    [Fact]
    public async Task Multi_string_with_whitespace_between_works()
    {
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::before { content: 'Hello, ' 'World!' }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("Hello, World!", pseudo.Children[0].Text);
    }

    // ============================================================
    // attr() in content
    // ============================================================

    [Fact]
    public async Task Attr_function_substitutes_attribute_value()
    {
        var root = await BuildAsync(
            "<p class='x' data-label='Hello'>body</p>",
            ".x::before { content: attr(data-label) }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("Hello", pseudo.Children[0].Text);
    }

    [Fact]
    public async Task Attr_with_missing_attribute_substitutes_empty_string()
    {
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::before { content: 'pre-' attr(missing-attr) '-post' }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("pre--post", pseudo.Children[0].Text);
    }

    [Fact]
    public async Task Strings_and_attr_can_mix_in_content_value()
    {
        var root = await BuildAsync(
            "<p class='x' data-key='widget'>body</p>",
            ".x::before { content: '[' attr(data-key) '] ' }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("[widget] ", pseudo.Children[0].Text);
    }

    // Note: modern multi-arg attr() syntax (`attr(name type, fallback)`) is
    // deferred to cycle 2 — AngleSharp.Css 1.0.0-beta.144 normalizes the
    // value before reaching ResolverResult, dropping the type/fallback args.
    // Cycle 1's `attr(name)` form covers >95% of in-the-wild usage.

    // ============================================================
    // Single-string still works (regression)
    // ============================================================

    [Fact]
    public async Task Single_string_content_continues_to_work()
    {
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::before { content: 'just one' }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        var pseudo = p.Children.First(c => c.Pseudo == BoxPseudo.Before);
        Assert.Equal("just one", pseudo.Children[0].Text);
    }

    // ============================================================
    // ::first-line / ::first-letter cascade staging
    // ============================================================

    [Fact]
    public async Task First_line_cascade_styles_stage_on_box_for_phase3()
    {
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::first-line { color: rgb(10, 20, 30) }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        // Box generation can't materialize ::first-line as a box (line-extent
        // depends on layout), so it stages the style for Phase 3.
        Assert.NotNull(p.FirstLineStyle);
        Assert.True(p.FirstLineStyle!.IsSet(NetPdf.Css.Properties.PropertyId.Color));
    }

    [Fact]
    public async Task First_letter_cascade_styles_stage_on_box_for_phase3()
    {
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::first-letter { font-weight: bold }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        Assert.NotNull(p.FirstLetterStyle);
    }

    [Fact]
    public async Task No_first_line_or_first_letter_rule_means_null_staging()
    {
        var root = await BuildAsync("<p>body</p>");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        Assert.Null(p.FirstLineStyle);
        Assert.Null(p.FirstLetterStyle);
    }

    [Fact]
    public async Task First_line_style_does_not_become_a_box()
    {
        // Sanity: ::first-line / ::first-letter must NOT add boxes to the tree.
        var root = await BuildAsync(
            "<p class='x'>body</p>",
            ".x::first-line { color: red } .x::first-letter { color: blue }");
        var p = Walk(root).First(b => b.SourceElement?.LocalName == "p");
        // The pseudos do not appear as Pseudo-tagged child boxes.
        Assert.DoesNotContain(p.Children, c =>
            c.Pseudo is BoxPseudo.Before or BoxPseudo.After or BoxPseudo.Marker);
        // No "first-line" or "first-letter" pseudo box exists in the tree at all.
        // (BoxPseudo doesn't even define values for them — they're layout-time.)
    }
}
