// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser;

/// <summary>
/// Integration tests for the <see cref="CssParserAdapter.Adapt(ICssStyleSheet, CssPreprocessResult, string?, CssStylesheetOrigin, CssStylesheetOwnerKind, string?, bool, int)"/>
/// overload — runs the full Phase 2 pipeline (raw CSS → preprocessor + AngleSharp → adapter
/// → AST) against the four Task 3 blockers from review cycle 1 and asserts the recovered
/// information is merged correctly into the emitted AST.
/// </summary>
public sealed class CssParserAdapterPreprocessTests
{
    [Fact]
    public async Task Adapt_with_preprocess_recovers_page_first_pseudo_into_prelude()
    {
        // The Task 2 review-cycle 1 blocker pin: AngleSharp drops `:first`. With preprocess
        // data merged, the `@page` rule's Prelude now carries the recovered selector.
        var (sheet, preprocess) = await ParseAndPreprocess("@page :first { margin-top: 0 }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("page", page.Name);
        Assert.Equal(":first", page.Prelude);    // Recovered from preprocess.
        Assert.NotEmpty(page.Declarations);       // AngleSharp still gives us declarations.
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_named_page_into_prelude()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("@page chapter { margin-top: 5cm }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("chapter", page.Prelude);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_page_margin_boxes_as_child_rules()
    {
        // Second Task 3 blocker: AngleSharp drops margin-boxes entirely. Preprocess
        // recovers them and the adapter re-parents them as ChildRules under @page.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page {
                margin: 1in;
                @top-center { content: "Header"; font-size: 10pt }
                @bottom-right { content: counter(page) }
            }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal(2, page.ChildRules.Length);
        var topCenter = Assert.IsType<CssAtRule>(page.ChildRules[0]);
        Assert.Equal("top-center", topCenter.Name);
        Assert.Equal(2, topCenter.Declarations.Length);
        Assert.Equal("content", topCenter.Declarations[0].Property);
        Assert.Contains("Header", topCenter.Declarations[0].Value.RawText);
        Assert.Equal("font-size", topCenter.Declarations[1].Property);
        Assert.Equal("10pt", topCenter.Declarations[1].Value.RawText);

        var bottomRight = Assert.IsType<CssAtRule>(page.ChildRules[1]);
        Assert.Equal("bottom-right", bottomRight.Name);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_import_layer_name()
    {
        // Third Task 3 blocker: AngleSharp folds `layer(name)` into a malformed media query.
        // With preprocess data, CssImportRule.LayerName is populated correctly.
        var (sheet, preprocess) = await ParseAndPreprocess("@import url(\"theme.css\") layer(framework);");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var import = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("theme.css", import.Url);
        Assert.Equal("framework", import.LayerName);
        // Authored had no media query — recovered MediaQuery should be empty (NOT AngleSharp's "not all").
        Assert.Equal(string.Empty, import.MediaQuery);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_import_supports_condition()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("@import url(\"grid.css\") supports(display: grid);");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var import = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("display: grid", import.SupportsCondition);
    }

    [Fact]
    public async Task Adapt_with_preprocess_recovers_import_layer_supports_and_media_together()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(
            "@import url(\"all.css\") layer(theme) supports(display: grid) screen;");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var import = Assert.IsType<CssImportRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("all.css", import.Url);
        Assert.Equal("theme", import.LayerName);
        Assert.Equal("display: grid", import.SupportsCondition);
        Assert.Contains("screen", import.MediaQuery);
    }

    [Fact]
    public async Task Adapt_with_preprocess_backfills_source_locations_on_rules()
    {
        // Fourth Task 3 blocker: Location was Unknown for every rule until preprocess
        // tracked positions. After merge, each rule's Location reflects its line in source.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            .a { color: red }
            .b { color: blue }
            @page { margin: 1in }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(3, stylesheet.Rules.Length);
        // Each rule should have a populated Location (line > 0).
        foreach (var rule in stylesheet.Rules)
        {
            var location = ExtractLocation(rule);
            Assert.True(location.Line > 0,
                $"rule {rule.GetType().Name} expected populated Location but got Line={location.Line}");
        }
    }

    [Fact]
    public async Task Adapt_with_empty_preprocess_falls_back_to_unrecovered_data()
    {
        // Backwards compat: passing empty preprocess (or omitting it) gives Task 2 behavior.
        var (sheet, _) = await ParseAndPreprocess("@page :first { margin-top: 0 }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, CssPreprocessResult.Empty, null, CssStylesheetOrigin.Author,
            CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Empty(page.Prelude); // No preprocess → no recovery, AngleSharp's loss visible.
    }

    [Fact]
    public async Task Adapt_existing_two_arg_overload_still_works()
    {
        // The original Task 2 entry point continues to work without preprocess.
        var (sheet, _) = await ParseAndPreprocess(".a { color: red }");
        var stylesheet = CssParserAdapter.Adapt(sheet);
        Assert.NotEmpty(stylesheet.Rules);
    }

    // ------------------------------------------------------------
    // Modern at-rule recovery via opaque slots (Rec 1 + Rec 2)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_with_preprocess_emits_opaque_at_rule_for_dropped_container()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(
            "@container (min-width: 800px) { .a { color: red } }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var opaque = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("container", opaque.Name);
        Assert.Contains("min-width", opaque.Prelude);
        // Body isn't decomposed in v1 — the at-rule is opaque from the cascade's perspective.
        Assert.Empty(opaque.ChildRules);
        Assert.Empty(opaque.Declarations);
    }

    [Fact]
    public async Task Adapt_with_preprocess_emits_opaque_at_rule_for_dropped_layer_block()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(
            "@layer framework { .a { color: red } }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var opaque = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("layer", opaque.Name);
        Assert.Equal("framework", opaque.Prelude);
    }

    [Fact]
    public async Task Adapt_with_preprocess_modern_at_rule_in_middle_does_not_drift_locations()
    {
        // The critical regression fix: AngleSharp drops the @container, leaving 2 emitted
        // rules (.a, .z) for 3 source rules. With slots-based merge, .z still gets line 3
        // (its real position), not line 2 (which was @container).
        var (sheet, preprocess) = await ParseAndPreprocess("""
            .a { color: red }
            @container (min-width: 800px) { .x { color: red } }
            .z { color: blue }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(3, stylesheet.Rules.Length);
        var first = Assert.IsType<CssStyleRule>(stylesheet.Rules[0]);
        var middle = Assert.IsType<CssAtRule>(stylesheet.Rules[1]);
        var last = Assert.IsType<CssStyleRule>(stylesheet.Rules[2]);

        Assert.Equal(1, first.Location.Line);
        Assert.Equal("container", middle.Name);
        Assert.Equal(2, middle.Location.Line);
        Assert.Equal(3, last.Location.Line);  // <-- the bug-fix lock-in: NOT 2.
        Assert.Equal(".z", last.Selector.RawText);
    }

    // ------------------------------------------------------------
    // Token-aware !important parsing (Rec 5)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_margin_box_value_with_quoted_important_string_is_not_stripped()
    {
        // Without token-aware parsing, the trailing chars `"` ahead of the value's literal
        // !important match a naive EndsWith check and corrupt the value. Pin the fix.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page { @top-center { content: "!important" } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var marginBox = Assert.IsType<CssAtRule>(Assert.Single(page.ChildRules));
        var declaration = Assert.Single(marginBox.Declarations);
        Assert.Equal("content", declaration.Property);
        Assert.Equal("\"!important\"", declaration.Value.RawText);
        Assert.False(declaration.IsImportant);
    }

    [Fact]
    public async Task Adapt_margin_box_value_with_real_important_marker_is_recognized()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page { @top-center { content: "Header" !important } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var marginBox = Assert.IsType<CssAtRule>(Assert.Single(page.ChildRules));
        var declaration = Assert.Single(marginBox.Declarations);
        Assert.Equal("\"Header\"", declaration.Value.RawText);
        Assert.True(declaration.IsImportant);
    }

    // ------------------------------------------------------------
    // Margin-box name validation (Rec 6)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_unknown_margin_box_name_does_not_appear_in_child_rules()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page {
                margin: 1in;
                @top-bogus { content: "ignored" }
                @top-center { content: "kept" }
            }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var marginBox = Assert.IsType<CssAtRule>(Assert.Single(page.ChildRules));
        Assert.Equal("top-center", marginBox.Name);
    }

    // ------------------------------------------------------------
    // Nested rule locations are Unknown (Rec 3)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_nested_rule_locations_are_populated_via_grouping_recursion()
    {
        // Review-cycle 2 improvement: the preprocessor now recurses into @media/@supports/
        // @keyframes bodies and emits nested CssRuleSlot lists. The adapter consumes those
        // nested slots so child rules get their real source positions instead of Unknown.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            .a { color: red }
            @media print { .child { color: blue } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(2, stylesheet.Rules.Length);
        Assert.Equal(1, ((CssStyleRule)stylesheet.Rules[0]).Location.Line);

        var media = Assert.IsType<CssAtRule>(stylesheet.Rules[1]);
        Assert.Equal(2, media.Location.Line);

        // Nested child rule's location now reflects its real source line (line 2 — the
        // child sits on the same line as the @media in the test input).
        var child = Assert.IsType<CssStyleRule>(Assert.Single(media.ChildRules));
        Assert.Equal(2, child.Location.Line);
        Assert.True(child.Location.Column > 0);
    }

    [Fact]
    public async Task Adapt_with_preprocess_handles_mixed_rule_set_correctly()
    {
        // Style rules + at-rules + page rules + imports all preserve order and recover
        // the bits AngleSharp loses in one pass.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @import url("a.css") layer(framework);
            .a { color: red }
            @media print { .b { color: black } }
            @page :first { @top-left { content: "Cover" } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(4, stylesheet.Rules.Length);
        var import = Assert.IsType<CssImportRule>(stylesheet.Rules[0]);
        Assert.Equal("framework", import.LayerName);

        Assert.IsType<CssStyleRule>(stylesheet.Rules[1]);

        var media = Assert.IsType<CssAtRule>(stylesheet.Rules[2]);
        Assert.Equal("media", media.Name);

        var page = Assert.IsType<CssAtRule>(stylesheet.Rules[3]);
        Assert.Equal("page", page.Name);
        Assert.Equal(":first", page.Prelude);
        Assert.Single(page.ChildRules);
        Assert.Equal("top-left", Assert.IsType<CssAtRule>(page.ChildRules[0]).Name);
    }

    // ------------------------------------------------------------
    // Raw-value capture for modern functions (Rec 1)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_oklch_value_is_replaced_with_authored_text_not_anglesharp_corruption()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(".a { color: oklch(0.5 0.2 30) }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var color = Assert.Single(rule.Declarations);
        Assert.Equal("color", color.Property);
        // AngleSharp's bogus rgba(9, 0, 0, 1) is replaced by the authored oklch(...) text.
        Assert.Contains("oklch", color.Value.RawText);
        Assert.DoesNotContain("rgba", color.Value.RawText);
    }

    [Fact]
    public async Task Adapt_color_mix_dropped_by_anglesharp_is_restored_from_recovery()
    {
        // AngleSharp drops the entire declaration when it can't parse `color-mix(...)`.
        // The recovery adds it back — without recovery the rule body would be empty.
        var (sheet, preprocess) = await ParseAndPreprocess(".a { color: color-mix(in oklch, red, blue) }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var color = Assert.Single(rule.Declarations);
        Assert.Equal("color", color.Property);
        Assert.Contains("color-mix", color.Value.RawText);
    }

    [Fact]
    public async Task Adapt_light_dark_dropped_by_anglesharp_is_restored_from_recovery()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(".a { color: light-dark(white, black) }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var color = Assert.Single(rule.Declarations);
        Assert.Contains("light-dark", color.Value.RawText);
    }

    [Fact]
    public async Task Adapt_oklch_with_important_preserves_important_flag()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(".a { color: oklch(0.5 0.2 30) !important }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var color = Assert.Single(rule.Declarations);
        Assert.True(color.IsImportant);
        Assert.Contains("oklch", color.Value.RawText);
        Assert.DoesNotContain("!important", color.Value.RawText);
    }

    [Fact]
    public async Task Adapt_normal_color_unaffected_by_recovery_path()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(".a { color: red }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(stylesheet.Rules));
        var color = Assert.Single(rule.Declarations);
        // No modern function present → AngleSharp's normalized rgba flows through.
        Assert.Equal("rgba(255, 0, 0, 1)", color.Value.RawText);
    }

    [Fact]
    public async Task Adapt_modern_value_recovery_in_nested_at_rule_works()
    {
        // The recovery should also apply to declarations inside @media-nested rules.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @media print {
                .a { color: oklch(0.5 0.2 30) }
            }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var media = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var inner = Assert.IsType<CssStyleRule>(Assert.Single(media.ChildRules));
        var color = Assert.Single(inner.Declarations);
        Assert.Contains("oklch", color.Value.RawText);
    }

    // ------------------------------------------------------------
    // RawBody preservation on opaque modern at-rules (Rec 2)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_opaque_container_carries_raw_body_for_downstream()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(
            "@container (min-width: 800px) { .a { color: red } .b { color: blue } }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var container = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("container", container.Name);
        Assert.NotEmpty(container.RawBody);
        Assert.Contains(".a", container.RawBody);
        Assert.Contains(".b", container.RawBody);
    }

    [Fact]
    public async Task Adapt_opaque_layer_block_carries_raw_body_for_downstream()
    {
        var (sheet, preprocess) = await ParseAndPreprocess(
            "@layer framework { .x { color: red } }");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var layer = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("layer", layer.Name);
        Assert.NotEmpty(layer.RawBody);
        Assert.Contains(".x", layer.RawBody);
    }

    [Fact]
    public async Task Adapt_layer_statement_form_has_empty_raw_body()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("@layer one, two, three;");
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var layer = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal("layer", layer.Name);
        Assert.Empty(layer.RawBody); // Statement-form has no body to preserve.
    }

    // ------------------------------------------------------------
    // Nested modern at-rule recovery (Rec 3)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_nested_container_in_media_appears_in_child_rules_via_opaque_slot()
    {
        // AngleSharp drops the @container nested inside @media. The preprocessor's nested
        // slot list lets the adapter splice an opaque CssAtRule for it under the @media's
        // ChildRules. Combined with surrounding style rules, ordering is preserved.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @media print {
                .a { color: red }
                @container (min-width: 800px) { .x { color: red } }
                .z { color: green }
            }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var media = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        Assert.Equal(3, media.ChildRules.Length);
        Assert.IsType<CssStyleRule>(media.ChildRules[0]);
        var nestedContainer = Assert.IsType<CssAtRule>(media.ChildRules[1]);
        Assert.Equal("container", nestedContainer.Name);
        Assert.NotEmpty(nestedContainer.RawBody);
        Assert.IsType<CssStyleRule>(media.ChildRules[2]);
    }

    // ------------------------------------------------------------
    // Comment-aware !important (Rec 6)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_margin_box_value_with_comment_before_important_is_recognized()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page { @top-center { content: "Header" /* note */ !important } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var marginBox = Assert.IsType<CssAtRule>(Assert.Single(page.ChildRules));
        var declaration = Assert.Single(marginBox.Declarations);
        Assert.True(declaration.IsImportant);
    }

    [Fact]
    public async Task Adapt_margin_box_value_with_comment_after_important_is_recognized()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page { @top-center { content: "Header" !important /* trailing */ } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var marginBox = Assert.IsType<CssAtRule>(Assert.Single(page.ChildRules));
        var declaration = Assert.Single(marginBox.Declarations);
        Assert.True(declaration.IsImportant);
    }

    [Fact]
    public async Task Adapt_margin_box_value_with_comment_inside_bang_marker_is_recognized()
    {
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @page { @top-center { content: "Header" ! /* split */ important } }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        var page = Assert.IsType<CssAtRule>(Assert.Single(stylesheet.Rules));
        var marginBox = Assert.IsType<CssAtRule>(Assert.Single(page.ChildRules));
        var declaration = Assert.Single(marginBox.Declarations);
        Assert.True(declaration.IsImportant);
    }

    // ------------------------------------------------------------
    // Robust slot mismatch (Rec 4)
    // ------------------------------------------------------------

    [Fact]
    public async Task Adapt_when_anglesharp_drops_unknown_at_rule_slot_demotes_to_opaque()
    {
        // The robust merge: even if AngleSharp drops a rule type the preprocessor doesn't
        // explicitly recognize as "modern", the slot's metadata still lets the adapter
        // emit an opaque CssAtRule rather than letting subsequent rules drift.
        // Note: AngleSharp 1.0.0-beta.144 actually DOES emit @scope as an unknown rule,
        // so this test exercises the "AngleSharp surprises us by dropping a normal-looking
        // at-rule" defensive code path. We use a synthetic mismatch instead.
        var (sheet, preprocess) = await ParseAndPreprocess("""
            @container (min-width: 800px) { .a { color: red } }
            .later { color: blue }
            """);
        var stylesheet = CssParserAdapter.Adapt(
            sheet, preprocess, null, CssStylesheetOrigin.Author, CssStylesheetOwnerKind.StyleElement, null, false, 0);

        Assert.Equal(2, stylesheet.Rules.Length);
        var container = Assert.IsType<CssAtRule>(stylesheet.Rules[0]);
        Assert.Equal("container", container.Name);
        var later = Assert.IsType<CssStyleRule>(stylesheet.Rules[1]);
        Assert.Equal(".later", later.Selector.RawText);
        // Crucially, `.later` got line 2, not @container's line 1.
        Assert.Equal(2, later.Location.Line);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    private static async Task<(ICssStyleSheet sheet, CssPreprocessResult preprocess)> ParseAndPreprocess(string css)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false, IsKeepingSourceReferences = true });
        var config = Configuration.Default
            .WithCss()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = false })
            .With(parser);
        var ctx = BrowsingContext.New(config);

        var html = $"<html><head><style>{css}</style></head><body></body></html>";
        var document = await ctx.OpenAsync(req => req.Content(html).Address("about:blank"));
        var sheet = document.StyleSheets.OfType<ICssStyleSheet>().Single();
        var preprocess = CssPreprocessor.Process(css);
        return (sheet, preprocess);
    }

    private static CssSourceLocation ExtractLocation(CssRule rule) => rule switch
    {
        CssStyleRule s => s.Location,
        CssAtRule a => a.Location,
        CssImportRule i => i.Location,
        _ => CssSourceLocation.Unknown,
    };

    [Fact]
    public void Adapt_synthesizes_a_wholly_dropped_rule_from_its_recovery()
    {
        // Content-pseudo cycle hardening — when AngleSharp drops a RULE entirely (its only
        // declaration carried a dropped function), the slot previously demoted to opaque,
        // silently losing the recovery AND desyncing the style-rule ordinal counter (a later
        // rule could steal this rule's recovery). Simulated directly: a preprocess result with
        // two style slots adapted against an AngleSharp sheet that only kept the second rule.
        var css = "h1 { string-set: title content(before) } p { color: red }";
        var preprocess = CssPreprocessor.Process(css);
        var parser = new AngleSharp.Css.Parser.CssParser();
        var sheet = parser.ParseStyleSheet("p { color: red }"); // rule 0 dropped by "AngleSharp"

        var adapted = CssParserAdapter.Adapt(sheet, preprocess, href: null,
            origin: CssStylesheetOrigin.Author, ownerKind: CssStylesheetOwnerKind.StyleElement,
            mediaQuery: null, isDisabled: false, order: 0);

        var first = Assert.IsType<CssStyleRule>(adapted.Rules[0]);
        var decl = Assert.Single(first.Declarations);
        Assert.Equal("string-set", decl.Property);
        Assert.Contains("content(before)", decl.Value.RawText);
        var second = Assert.IsType<CssStyleRule>(adapted.Rules[1]);
        Assert.Single(second.Declarations);   // the p rule adapted normally after the synthesis
    }
}
