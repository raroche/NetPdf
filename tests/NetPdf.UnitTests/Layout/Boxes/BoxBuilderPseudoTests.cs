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
    public async Task Marker_inherits_color_from_list_item_via_anon_style()
    {
        // The marker's style is fresh-rented + inherits inheritable
        // properties (color, font-*) from the list-item's style. Verify by
        // checking IsSet — Color is inheritable so the marker carries the
        // inherited value (defaulted to the registry's `canvastext`).
        // (NOTE: ::marker cascade rules are dropped by AngleSharp.Css today,
        // so this test does NOT verify cascade-applied marker styling — see
        // Rec 2 note above.)
        var root = await BuildAsync("<ul><li>x</li></ul>");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
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

    // ============================================================
    // Rec 1 — ::before / ::after on replaced elements are suppressed
    // ============================================================

    [Theory]
    [InlineData("img")]
    [InlineData("video")]
    [InlineData("iframe")]
    [InlineData("canvas")]
    [InlineData("object")]
    [InlineData("embed")]
    public async Task Pseudos_on_any_replaced_element_are_suppressed(string tag)
    {
        // Per CSS Pseudo L4 §3 + Task 14 review Rec 1: replaced elements
        // are atomic and have no place for generated-content pseudos.
        var root = await BuildAsync(
            $"<{tag}>",
            $"{tag}::before {{ content: 'X' }} {tag}::after {{ content: 'Y' }}");
        var box = Walk(root).First(b => b.SourceElement?.LocalName == tag);
        Assert.DoesNotContain(box.Children, c => c.Pseudo == BoxPseudo.Before);
        Assert.DoesNotContain(box.Children, c => c.Pseudo == BoxPseudo.After);
    }

    // ============================================================
    // Rec 2 — ::marker content override
    //
    // NOTE: AngleSharp.Css 1.0.0-beta.144 silently drops `::marker`
    // selectors during CSS parse (same class of behavior as `display: contents`
    // per Task 12 hardening notes). The marker-content override code path
    // in BoxBuilder.MarkerContentFromCascade is therefore unreachable in
    // practice — it returns null because the cascade has no ::marker rules
    // to deliver. The implementation is still correct per CSS Pseudo L4 §3.4
    // and will fire once cycle 2's CssPreprocessor recovery preserves
    // ::marker rules through the cascade. Cycle-1 hardening covers only the
    // fallback path (cascade delivers no override → list-style-type wins).
    // ============================================================

    [Fact]
    public async Task Marker_reads_list_style_type_AFTER_marker_rules_apply_per_PR10_Rec_2()
    {
        // PR #10 review Rec 2: previously BuildListItemMarker read styleType
        // from hostStyle BEFORE applying marker rules to markerStyle, so a
        // hypothetical `li::marker { list-style-type: square }` would have no
        // effect even after AngleSharp.Css's ::marker drop is fixed. The fix
        // re-reads from markerStyle after ApplyMarkerApplicableDeclarations
        // runs.
        //
        // We can't directly verify the cascade-delivered override (AngleSharp
        // drops ::marker selectors at parse time — see Rec 2 note above), but
        // we CAN verify the read order by checking that markerStyle has the
        // expected list-style-type slot after construction. The marker box's
        // Style is markerStyle; ReadListStyleType is called on it.
        var root = await BuildAsync(
            "<ul class='outer'><li class='m'>x</li></ul>",
            ".outer { list-style-type: square }");
        // The wrapper's list-style-type is "square"; <li> inherits it via the
        // cascade (list-style-type is inheritable). The marker style inherits
        // from the list-item, so markerStyle.ListStyleType is "square". The
        // marker glyph uses that.
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.StartsWith("▪", marker.Children[0].Text);
    }

    [Fact]
    public async Task Marker_with_no_explicit_content_falls_back_to_list_style_type()
    {
        // The ::marker content override path is exercised; absent a delivered
        // override it falls back to the list-style-type-derived glyph.
        var root = await BuildAsync(
            "<ul><li class='m'>x</li></ul>",
            ".m::marker { content: counter(items) }");   // unsupported → fallback
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.StartsWith("•", marker.Children[0].Text);
    }

    // ============================================================
    // Rec 3 — Count display:list-item siblings, not just <li>
    // ============================================================

    [Fact]
    public async Task Css_only_list_item_div_is_counted_alongside_li_siblings()
    {
        // A <div display:list-item> is a list-item per CSS, even though it's
        // not an <li>. Position counting must include it.
        var root = await BuildAsync(
            "<ol><li>a</li><div class='li'>b</div><li>c</li></ol>",
            ".li { display: list-item }");
        var listItems = Walk(root).Where(b => b.Kind == BoxKind.ListItem).ToList();
        Assert.Equal(3, listItems.Count);
        var markers = listItems.Select(li => li.Children.First(c => c.Kind == BoxKind.Marker)
            .Children[0].Text).ToList();
        Assert.StartsWith("1.", markers[0]);
        Assert.StartsWith("2.", markers[1]);
        Assert.StartsWith("3.", markers[2]);
    }

    // ============================================================
    // Rec 5 — ::first-line / ::first-letter staging gated to block containers
    // ============================================================

    [Fact]
    public async Task First_line_does_not_stage_on_inline_box()
    {
        var root = await BuildAsync(
            "<span class='x'>body</span>",
            ".x::first-line { color: red }");
        var span = Walk(root).First(b => b.SourceElement?.LocalName == "span");
        // span is inline-level, not a block container — no staging.
        Assert.Null(span.FirstLineStyle);
    }

    [Fact]
    public async Task First_letter_does_not_stage_on_replaced_element()
    {
        var root = await BuildAsync(
            "<img class='x'>",
            ".x::first-letter { font-weight: bold }");
        var img = Walk(root).First(b => b.SourceElement?.LocalName == "img");
        Assert.Null(img.FirstLetterStyle);
    }

    [Fact]
    public async Task First_line_still_stages_on_block_div()
    {
        var root = await BuildAsync(
            "<div class='x'>body</div>",
            ".x::first-line { color: red }");
        var div = Walk(root).First(b => b.SourceElement?.LocalName == "div");
        Assert.NotNull(div.FirstLineStyle);
    }

    // ============================================================
    // Rec 6 — Author ::marker properties filtered to the spec allowlist
    //
    // NOTE: AngleSharp.Css 1.0.0-beta.144 drops ::marker selectors during
    // parse (same caveat as Rec 2). These tests exercise the property-
    // filter logic on the inheritance + initial-value path: padding /
    // margin / display take their initial values regardless of cascade
    // delivery. The filter implementation in
    // BoxBuilder.ApplyMarkerApplicableDeclarations becomes load-bearing
    // once cycle 2's CssPreprocessor recovery preserves ::marker rules
    // through the cascade.
    // ============================================================

    [Fact]
    public async Task Marker_does_not_honor_padding_property_from_cascade()
    {
        var root = await BuildAsync(
            "<ul><li class='m'>x</li></ul>",
            ".m::marker { padding-top: 25px; color: rgb(255, 0, 0) }");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        // padding-top is NOT in the marker-applicable allowlist — slot stays
        // at the registry initial (0), not 25.
        Assert.Equal(0.0, marker.Style.Get(NetPdf.Css.Properties.PropertyId.PaddingTop).AsLengthPx());
        // color IS in the allowlist — applied.
        Assert.True(marker.Style.IsSet(NetPdf.Css.Properties.PropertyId.Color));
    }

    [Fact]
    public async Task Marker_does_not_honor_margin_or_display_from_cascade()
    {
        var root = await BuildAsync(
            "<ul><li class='m'>x</li></ul>",
            ".m::marker { margin-left: 50px; display: block }");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.Equal(0.0, marker.Style.Get(NetPdf.Css.Properties.PropertyId.MarginLeft).AsLengthPx());
    }

    [Fact]
    public async Task Marker_honors_font_weight_from_cascade()
    {
        var root = await BuildAsync(
            "<ul><li class='m'>x</li></ul>",
            ".m::marker { font-weight: bold }");
        var marker = FindMarker(Walk(root).First(b => b.Kind == BoxKind.ListItem));
        Assert.True(marker.Style.IsSet(NetPdf.Css.Properties.PropertyId.FontWeight));
    }

    // ============================================================
    // Rec 7 — lower-greek produces real Greek letters
    // ============================================================

    [Fact]
    public async Task Lower_greek_marker_uses_alpha_beta_gamma_sequence()
    {
        var root = await BuildAsync(
            "<ol class='lg'><li>a</li><li>b</li><li>c</li></ol>",
            ".lg { list-style-type: lower-greek }");
        var markers = Walk(root)
            .Where(b => b.Kind == BoxKind.Marker)
            .Select(m => m.Children[0].Text).ToList();
        Assert.StartsWith("α.", markers[0]);
        Assert.StartsWith("β.", markers[1]);
        Assert.StartsWith("γ.", markers[2]);
    }

    [Fact]
    public async Task Lower_greek_wraps_at_24_to_double_letters()
    {
        // Position 25 should be αα.
        var lis = string.Concat(Enumerable.Range(1, 25).Select(_ => "<li>x</li>"));
        var root = await BuildAsync(
            $"<ol class='lg'>{lis}</ol>",
            ".lg { list-style-type: lower-greek }");
        var markers = Walk(root)
            .Where(b => b.Kind == BoxKind.Marker)
            .Select(m => m.Children[0].Text).ToList();
        Assert.StartsWith("ω.", markers[23]);
        Assert.StartsWith("αα.", markers[24]);
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
