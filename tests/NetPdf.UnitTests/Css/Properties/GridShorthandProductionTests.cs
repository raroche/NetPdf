// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Phase 3 Task 17 cycle 0c — production-pipeline tests for the grid
/// shorthand expansions (<c>grid-row</c> / <c>grid-column</c> /
/// <c>grid-area</c>). Exercises the FULL pipeline (HTML →
/// <c>CssPreprocessor</c> →
/// <c>CssParserAdapter</c> → <c>CascadeResolver</c> →
/// <c>BoxBuilder</c> → <c>box.Style.ReadGridXxx()</c>) to verify the
/// shorthand expansion lands the right longhand values at the cascade.
/// </summary>
public sealed class GridShorthandProductionTests
{
    // =====================================================================
    //  grid-row shorthand
    // =====================================================================

    [Fact]
    public async Task Grid_row_with_two_integers_lands_both_longhands()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2 / 4; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridRowStart();
        Assert.Equal(GridLineKind.LineNumber, start.Kind);
        Assert.Equal(2, start.LineNumber);

        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.LineNumber, end.Kind);
        Assert.Equal(4, end.LineNumber);
    }

    [Fact]
    public async Task Grid_row_with_single_integer_pairs_to_auto()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
    }

    [Fact]
    public async Task Grid_row_with_single_custom_ident_duplicates_to_end()
    {
        // Per §8.4 — a bare custom-ident in the shorthand duplicates to
        // the omitted end longhand.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: header; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridRowStart();
        Assert.Equal(GridLineKind.NamedLine, start.Kind);
        Assert.Equal("header", start.NamedLine);

        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.NamedLine, end.Kind);
        Assert.Equal("header", end.NamedLine);
    }

    [Fact]
    public async Task Grid_row_with_span_in_end_position()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2 / span 3; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);

        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.Span, end.Kind);
        Assert.Equal(3, end.LineNumber);
    }

    // =====================================================================
    //  grid-column shorthand (= same grammar as grid-row)
    // =====================================================================

    [Fact]
    public async Task Grid_column_with_two_integers_lands_both_longhands()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-column: 1 / 3; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(1, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnEnd().LineNumber);
    }

    [Fact]
    public async Task Grid_column_single_value_with_omitted_pair()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-column: span 2; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridColumnStart();
        Assert.Equal(GridLineKind.Span, start.Kind);
        Assert.Equal(2, start.LineNumber);
        // span is reserved; omitted end falls back to auto.
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    // =====================================================================
    //  grid-area shorthand
    // =====================================================================

    [Fact]
    public async Task Grid_area_four_values_lands_all_four_longhands()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3 / 4 / 5; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
        Assert.Equal(5, item.Style.ReadGridColumnEnd().LineNumber);
    }

    [Fact]
    public async Task Grid_area_single_ident_duplicates_to_all_four()
    {
        // Per §8.4 fallback — a bare custom-ident replicates to all 4
        // longhands. Named-area resolution (= matching against
        // grid-template-areas) is cycle 7's scope; cycle 0c just gets
        // the ident to all four longhands so the named-line resolver
        // sees it.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: header; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal("header", item.Style.ReadGridRowStart().NamedLine);
        Assert.Equal("header", item.Style.ReadGridColumnStart().NamedLine);
        Assert.Equal("header", item.Style.ReadGridRowEnd().NamedLine);
        Assert.Equal("header", item.Style.ReadGridColumnEnd().NamedLine);
    }

    [Fact]
    public async Task Grid_area_two_values_omit_per_spec()
    {
        // 2 / 3 → row-start: 2; column-start: 3; row-end: <2> = auto;
        // column-end: <3> = auto (= integers don't duplicate).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    [Fact]
    public async Task Grid_area_two_idents_duplicate_per_spec()
    {
        // foo / bar → row-start: foo; column-start: bar; row-end: foo;
        // column-end: bar (= idents duplicate).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: foo / bar; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal("foo", item.Style.ReadGridRowStart().NamedLine);
        Assert.Equal("bar", item.Style.ReadGridColumnStart().NamedLine);
        Assert.Equal("foo", item.Style.ReadGridRowEnd().NamedLine);
        Assert.Equal("bar", item.Style.ReadGridColumnEnd().NamedLine);
    }

    [Fact]
    public async Task Grid_area_three_values_omit_column_end_per_spec()
    {
        // 2 / 3 / 4 → column-end omitted, falls back to <3> = auto.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3 / 4; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(3, item.Style.ReadGridColumnStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    // =====================================================================
    //  Cascade interaction — shorthand vs explicit longhand source order
    // =====================================================================

    [Fact]
    public async Task Explicit_longhand_after_shorthand_wins_per_cascade_order()
    {
        // Per CSS Cascade §7.4 — later declaration wins at the same
        // specificity / origin. Shorthand expands first, then explicit
        // longhand overrides. Mirrors L17's pattern (= ExplicitLonghandRef
        // tracking in the preprocessor).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row: 2 / 4;
                    grid-row-end: 6;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Shorthand set start to 2 (still valid).
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        // Explicit grid-row-end: 6 should override the shorthand's 4.
        Assert.Equal(6, item.Style.ReadGridRowEnd().LineNumber);
    }

    [Fact]
    public async Task Shorthand_after_explicit_longhand_wins()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row-end: 6;
                    grid-row: 2 / 4;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Shorthand wins both longhands since it came last.
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
    }

    // =====================================================================
    //  PR-#91 review F1 — atomic shorthand application
    // =====================================================================

    [Fact]
    public async Task Invalid_grid_row_shorthand_does_not_partially_apply()
    {
        // Per F1 + CSS Cascade L4 §4.2 — an invalid shorthand contributes
        // none of its longhands. `grid-row: 2 / 0` has invalid end (= 0).
        // The whole shorthand must drop atomically; both longhands fall
        // back to the cascade default (= auto).
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: 2 / 0; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowStart().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
    }

    [Fact]
    public async Task Invalid_shorthand_within_rule_known_gap_drops_to_initial()
    {
        // KNOWN-GAP per PR-#91 review F1 multi-decl edge case. Per CSS
        // Cascade L4 §4.2, an invalid declaration should drop + the
        // cascade should fall back to any EARLIER valid declaration in
        // the same rule (= start=3 should win). However: AngleSharp.Css's
        // per-rule property dedup discards `grid-row-start: 3` BEFORE
        // our recovery layer runs (= keeping only the later
        // `grid-row: 2 / 0` shorthand expansion in its emit). The
        // recovery layer can override AngleSharp's emit with invalid-
        // sentinel longhand records (cycle-0c F1 fix), which the
        // resolver rejects, dropping both longhands to property initial
        // (auto). Preserving the earlier in-rule explicit longhand
        // value would require extending ExplicitLonghandRef to carry
        // the raw value and rewriting the merge — tracked as a
        // cycle-0c+ deferral. Pin the current behavior so a future
        // fix doesn't go unnoticed.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row-start: 3;
                    grid-row: 2 / 0;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Cycle-0c behavior: both longhands drop to initial (auto).
        // Spec-correct: start=3, end=auto. Flip when the deferral lands.
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowStart().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
    }

    [Fact]
    public async Task Invalid_grid_area_does_not_partially_apply()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: 2 / 3 / 0 / 5; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // All four longhands must fall back to auto since the shorthand
        // contributes nothing.
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowStart().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnStart().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridColumnEnd().Kind);
    }

    // =====================================================================
    //  PR-#91 review F2 — var() in grid shorthand silently drops
    // =====================================================================

    [Fact]
    public async Task Grid_row_with_var_function_silently_drops_to_default()
    {
        // Per F2 — the preprocessor can't know the post-substitution
        // shape, so the shorthand expander returns false; the declaration
        // silently drops at the cascade (= grid-row isn't a registered
        // property). The author's intent doesn't take effect. Pinned as a
        // known limitation tracked for post-substitution re-expansion in
        // a future cycle. NB: NO partial application — that's the win.
        const string html = """
            <!DOCTYPE html><html><head><style>
                :root { --placement: 2 / 4; }
                .grid { display: grid; }
                .item { grid-row: var(--placement); }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowStart().Kind);
        Assert.Equal(GridLineKind.Auto, item.Style.ReadGridRowEnd().Kind);
    }

    // =====================================================================
    //  PR-#91 review F3 — !important precedence across shorthand/longhand
    // =====================================================================

    [Fact]
    public async Task Important_longhand_before_shorthand_within_rule_known_gap()
    {
        // KNOWN-GAP per PR-#91 review F3 — within-rule edge case.
        // Per CSS Cascade §5 + §7.4 an !important longhand should beat
        // a later normal shorthand regardless of source order. My F3 fix
        // (unconditional explicit-longhand tracking) correctly identifies
        // that the explicit longhand wins the cascade comparison — BUT
        // AngleSharp.Css's per-rule dedup has already discarded the
        // important `grid-row-end: 6` from its emit (= replaced by the
        // later normal value 4). The merge layer correctly detects
        // "explicit wins" but has no way to recover the explicit's
        // value (= ExplicitLonghandRef stores only ordinal+importance,
        // not the value). Extending ExplicitLonghandRef to carry values
        // + rewriting the merge to use them when the explicit wins
        // would fix this; tracked as a cycle-0c+ deferral.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row-end: 6 !important;
                    grid-row: 2 / 4;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Shorthand sets start=2.
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        // Cycle-0c behavior: end=4 (= AngleSharp's dedup discarded the
        // important 6). Spec-correct: end=6. Flip when the deferral lands.
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
    }

    [Fact]
    public async Task Important_shorthand_before_normal_longhand_wins()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row: 2 / 4 !important;
                    grid-row-end: 6;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Important shorthand beats the later normal longhand.
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
    }

    [Fact]
    public async Task Both_important_later_wins_per_cascade_order()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item {
                    grid-row-end: 6 !important;
                    grid-row: 2 / 4 !important;
                }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        // Both !important — later source order wins (= shorthand at end=4).
        Assert.Equal(2, item.Style.ReadGridRowStart().LineNumber);
        Assert.Equal(4, item.Style.ReadGridRowEnd().LineNumber);
    }

    // =====================================================================
    //  PR-#91 review F4 — inherit deferred (known limitation pin)
    // =====================================================================

    [Fact]
    public async Task Grid_row_inherit_currently_resolves_to_auto_known_gap()
    {
        // Per F4 — the cycle-0c expander passes `inherit` to both
        // longhands, but the GridLineResolver's PR-#90 F3 defense
        // rejects CSS-wide keywords (= they fall back to auto rather
        // than inheriting the parent's value). Central cascade
        // interception is a separate cycle's scope. Pin the current
        // behavior so a future fix flipping to true-inherit semantics
        // doesn't go unnoticed.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .parent { grid-row: 2 / 4; }
                .child { grid-row: inherit; }
            </style></head><body>
            <div class="grid">
                <div class="parent">
                    <div class="child"></div>
                </div>
            </div>
            </body></html>
            """;

        var child = await FindBoxByClassAsync(html, "child");
        // KNOWN-GAP per F4 — `inherit` should pull parent's (2, 4) but
        // currently resolves to auto/auto. Flip this assertion when the
        // central cascade fix lands.
        Assert.Equal(GridLineKind.Auto, child.Style.ReadGridRowStart().Kind);
        Assert.Equal(GridLineKind.Auto, child.Style.ReadGridRowEnd().Kind);
    }

    // =====================================================================
    //  PR-#91 review F5 — `none` as named line
    // =====================================================================

    [Fact]
    public async Task Grid_row_with_single_none_duplicates_to_end_as_named_line()
    {
        // Per F5 — `none` is a valid <custom-ident> in <grid-line>
        // position (§8.3 excludes only `auto` and `span`). The
        // omitted-pair rule duplicates it.
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-row: none; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        var start = item.Style.ReadGridRowStart();
        Assert.Equal(GridLineKind.NamedLine, start.Kind);
        Assert.Equal("none", start.NamedLine);
        var end = item.Style.ReadGridRowEnd();
        Assert.Equal(GridLineKind.NamedLine, end.Kind);
        Assert.Equal("none", end.NamedLine);
    }

    [Fact]
    public async Task Grid_area_with_single_none_duplicates_to_all_four()
    {
        const string html = """
            <!DOCTYPE html><html><head><style>
                .grid { display: grid; }
                .item { grid-area: none; }
            </style></head><body>
            <div class="grid"><div class="item"></div></div>
            </body></html>
            """;

        var item = await FindBoxByClassAsync(html, "item");
        Assert.Equal("none", item.Style.ReadGridRowStart().NamedLine);
        Assert.Equal("none", item.Style.ReadGridColumnStart().NamedLine);
        Assert.Equal("none", item.Style.ReadGridRowEnd().NamedLine);
        Assert.Equal("none", item.Style.ReadGridColumnEnd().NamedLine);
    }

    // ================================================================
    //  Pipeline driver — mirrors GridParserProductionTests.
    // ================================================================

    private static async Task<Box> FindBoxByClassAsync(string html, string className)
    {
        var root = await BuildBoxTreeAsync(html);
        return FindBoxByClass(root, className)
            ?? throw new System.InvalidOperationException(
                $"no box with class '{className}' found in tree");
    }

    private static async Task<Box> BuildBoxTreeAsync(string html)
    {
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());
        var sheets = AdaptAllSheetsViaPreprocessor(document);
        var cascade = CascadeResolver.Resolve(document, sheets, CssMediaContext.DefaultPrint);
        var resolved = VarResolver.Resolve(cascade, document);
        return BoxBuilder.Build(document, resolved);
    }

    private static Box? FindBoxByClass(Box root, string className)
    {
        var el = root.SourceElement;
        if (el is not null)
        {
            var classAttr = el.GetAttribute("class");
            if (classAttr is not null
                && System.Array.IndexOf(classAttr.Split(' '), className) >= 0)
            {
                return root;
            }
        }
        foreach (var child in root.Children)
        {
            var found = FindBoxByClass(child, className);
            if (found is not null) return found;
        }
        return null;
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
}
