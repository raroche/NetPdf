// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="CssPreprocessor"/> with the unified <see cref="CssRuleSlot"/>
/// shape introduced in Task 3 review cycle 2. Covers AngleSharp.Css 1.0.0-beta.144 gaps —
/// page rules, modern at-rules, modern value functions in declarations, nested-grouping
/// recursion, robust slot merging, and the CSS Paged Media L3 §6.4 margin-box allowlist.
/// </summary>
public sealed class CssPreprocessorTests
{
    [Fact]
    public void Process_null_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => CssPreprocessor.Process(null!));
    }

    [Fact]
    public void Process_empty_returns_empty_result()
    {
        var result = CssPreprocessor.Process(string.Empty);
        Assert.True(result.PageRecoveries.IsEmpty);
        Assert.True(result.ImportRecoveries.IsEmpty);
        Assert.True(result.RuleSlots.IsEmpty);
        Assert.True(result.StyleRuleRecoveries.IsEmpty);
    }

    [Fact]
    public void Process_only_whitespace_and_comments_returns_empty_result()
    {
        var result = CssPreprocessor.Process("  /* foo */  \n  /* bar */ ");
        Assert.True(result.PageRecoveries.IsEmpty);
        Assert.True(result.RuleSlots.IsEmpty);
    }

    // ------------------------------------------------------------
    // Slot kinds
    // ------------------------------------------------------------

    [Fact]
    public void Process_style_rule_emits_style_rule_slot()
    {
        var result = CssPreprocessor.Process(".foo { color: red }");
        var slot = Assert.Single(result.RuleSlots);
        Assert.Equal(CssRuleSlotKind.StyleRule, slot.Kind);
        Assert.Equal(".foo", slot.Prelude);
        Assert.Empty(slot.AtKeyword);
        Assert.Contains("color", slot.RawBody);
    }

    [Fact]
    public void Process_at_rule_emits_at_rule_slot_with_keyword()
    {
        var result = CssPreprocessor.Process("@media print { p { color: red } }");
        var slot = Assert.Single(result.RuleSlots);
        Assert.Equal(CssRuleSlotKind.AtRule, slot.Kind);
        Assert.Equal("media", slot.AtKeyword);
    }

    // ------------------------------------------------------------
    // @page selector recovery
    // ------------------------------------------------------------

    [Fact]
    public void Process_unnamed_page_yields_empty_selector()
    {
        var result = CssPreprocessor.Process("@page { margin: 1in }");
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal(string.Empty, page.SelectorText);
        Assert.True(page.MarginBoxes.IsEmpty);
        Assert.Equal(0, page.OrdinalIndex);
    }

    [Fact]
    public void Process_first_page_recovers_first_pseudo()
    {
        var result = CssPreprocessor.Process("@page :first { margin-top: 0 }");
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal(":first", page.SelectorText);
    }

    [Fact]
    public void Process_left_right_pseudo_pages_recover_distinct_selectors()
    {
        var result = CssPreprocessor.Process("""
            @page :left { margin-left: 2cm }
            @page :right { margin-right: 2cm }
            """);
        Assert.Equal(2, result.PageRecoveries.Length);
        Assert.Equal(":left", result.PageRecoveries[0].SelectorText);
        Assert.Equal(":right", result.PageRecoveries[1].SelectorText);
    }

    [Fact]
    public void Process_named_page_recovers_name()
    {
        var result = CssPreprocessor.Process("@page chapter { margin-top: 5cm }");
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal("chapter", page.SelectorText);
    }

    // ------------------------------------------------------------
    // @page margin-box recovery + name validation
    // ------------------------------------------------------------

    [Fact]
    public void Process_page_with_top_center_margin_box_recovers_box()
    {
        var result = CssPreprocessor.Process("""
            @page { margin: 1in; @top-center { content: "Header" } }
            """);
        var page = Assert.Single(result.PageRecoveries);
        var box = Assert.Single(page.MarginBoxes);
        Assert.Equal("top-center", box.Name);
        Assert.Contains("Header", box.DeclarationsRawText);
    }

    [Fact]
    public void Process_page_with_multiple_margin_boxes_recovers_each_in_order()
    {
        var result = CssPreprocessor.Process("""
            @page {
                margin: 1in;
                @top-center { content: "Header" }
                @bottom-right { content: counter(page) }
            }
            """);
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal(2, page.MarginBoxes.Length);
        Assert.Equal("top-center", page.MarginBoxes[0].Name);
        Assert.Equal("bottom-right", page.MarginBoxes[1].Name);
    }

    [Fact]
    public void Process_first_page_with_margin_box_recovers_both()
    {
        var result = CssPreprocessor.Process("""
            @page :first { @top-left { content: "Cover" } }
            """);
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal(":first", page.SelectorText);
        var box = Assert.Single(page.MarginBoxes);
        Assert.Equal("top-left", box.Name);
    }

    [Theory]
    [InlineData("top-left-corner")]
    [InlineData("top-right-corner")]
    [InlineData("bottom-left-corner")]
    [InlineData("bottom-right-corner")]
    [InlineData("left-top")]
    [InlineData("left-middle")]
    [InlineData("left-bottom")]
    [InlineData("right-top")]
    [InlineData("right-middle")]
    [InlineData("right-bottom")]
    public void Process_recovers_all_spec_named_margin_boxes(string boxName)
    {
        var result = CssPreprocessor.Process($"@page {{ @{boxName} {{ content: 'x' }} }}");
        var page = Assert.Single(result.PageRecoveries);
        var box = Assert.Single(page.MarginBoxes);
        Assert.Equal(boxName, box.Name);
    }

    [Fact]
    public void Process_unknown_margin_box_name_is_dropped_silently()
    {
        var result = CssPreprocessor.Process("""
            @page {
                margin: 1in;
                @top-bogus { content: "ignored" }
                @top-center { content: "kept" }
            }
            """);
        var page = Assert.Single(result.PageRecoveries);
        var box = Assert.Single(page.MarginBoxes);
        Assert.Equal("top-center", box.Name);
    }

    // ------------------------------------------------------------
    // @import modern-syntax recovery
    // ------------------------------------------------------------

    [Fact]
    public void Process_plain_import_recovers_url_and_no_clauses()
    {
        var result = CssPreprocessor.Process("@import url(\"foo.css\");");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal("foo.css", import.Url);
        Assert.Equal(string.Empty, import.MediaQuery);
        Assert.Null(import.LayerName);
        Assert.Null(import.SupportsCondition);
    }

    [Fact]
    public void Process_import_with_media_query_recovers_url_and_media()
    {
        var result = CssPreprocessor.Process("@import url(\"print.css\") screen and (min-width: 800px);");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal("print.css", import.Url);
        Assert.Contains("min-width", import.MediaQuery);
        Assert.Null(import.LayerName);
    }

    [Fact]
    public void Process_import_with_named_layer_recovers_layer_name()
    {
        var result = CssPreprocessor.Process("@import url(\"theme.css\") layer(framework);");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal("theme.css", import.Url);
        Assert.Equal("framework", import.LayerName);
    }

    [Fact]
    public void Process_import_with_anonymous_layer_recovers_empty_layer_name()
    {
        var result = CssPreprocessor.Process("@import url(\"named.css\") layer;");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal(string.Empty, import.LayerName);
    }

    [Fact]
    public void Process_import_with_supports_clause_recovers_condition()
    {
        var result = CssPreprocessor.Process("@import url(\"grid.css\") supports(display: grid);");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal("display: grid", import.SupportsCondition);
    }

    [Fact]
    public void Process_import_with_layer_supports_and_media_recovers_all_three()
    {
        var result = CssPreprocessor.Process(
            "@import url(\"everything.css\") layer(theme) supports(display: grid) screen;");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal("everything.css", import.Url);
        Assert.Equal("theme", import.LayerName);
        Assert.Equal("display: grid", import.SupportsCondition);
        Assert.Contains("screen", import.MediaQuery);
    }

    [Fact]
    public void Process_import_with_quoted_url_strips_quotes()
    {
        var result = CssPreprocessor.Process("@import \"plain.css\";");
        var import = Assert.Single(result.ImportRecoveries);
        Assert.Equal("plain.css", import.Url);
    }

    // ------------------------------------------------------------
    // Modern at-rule capture as opaque slots with raw body (Rec 2)
    // ------------------------------------------------------------

    [Fact]
    public void Process_container_at_rule_emits_at_rule_slot_with_raw_body()
    {
        var result = CssPreprocessor.Process("@container (min-width: 800px) { .a { color: red } }");
        var slot = Assert.Single(result.RuleSlots);
        Assert.Equal(CssRuleSlotKind.AtRule, slot.Kind);
        Assert.Equal("container", slot.AtKeyword);
        Assert.Contains("min-width", slot.Prelude);
        Assert.Contains(".a", slot.RawBody);
        Assert.Contains("color: red", slot.RawBody);
    }

    [Fact]
    public void Process_layer_block_form_at_rule_emits_at_rule_slot_with_raw_body()
    {
        var result = CssPreprocessor.Process("@layer framework { .x { color: blue } }");
        var slot = Assert.Single(result.RuleSlots);
        Assert.Equal(CssRuleSlotKind.AtRule, slot.Kind);
        Assert.Equal("layer", slot.AtKeyword);
        Assert.Equal("framework", slot.Prelude);
        Assert.Contains(".x", slot.RawBody);
    }

    [Fact]
    public void Process_layer_statement_form_at_rule_emits_slot_with_no_body()
    {
        var result = CssPreprocessor.Process("@layer one, two, three;");
        var slot = Assert.Single(result.RuleSlots);
        Assert.Equal(CssRuleSlotKind.AtRule, slot.Kind);
        Assert.Equal("layer", slot.AtKeyword);
        Assert.Contains("one", slot.Prelude);
        Assert.Empty(slot.RawBody);
    }

    // ------------------------------------------------------------
    // Modern value function capture in style rules (Rec 1)
    // ------------------------------------------------------------

    [Fact]
    public void Process_oklch_in_color_emits_style_rule_recovery()
    {
        var result = CssPreprocessor.Process(".a { color: oklch(0.5 0.2 30) }");
        var rec = Assert.Single(result.StyleRuleRecoveries);
        Assert.Equal(0, rec.OrdinalIndex);
        var decl = Assert.Single(rec.Declarations);
        Assert.Equal("color", decl.Property);
        Assert.Contains("oklch", decl.RawValueText);
        Assert.False(decl.IsImportant);
    }

    [Fact]
    public void Process_oklab_in_color_emits_style_rule_recovery()
    {
        var result = CssPreprocessor.Process(".a { background-color: oklab(0.7 0.1 0.1) }");
        var rec = Assert.Single(result.StyleRuleRecoveries);
        var decl = Assert.Single(rec.Declarations);
        Assert.Equal("background-color", decl.Property);
        Assert.Contains("oklab", decl.RawValueText);
    }

    [Fact]
    public void Process_color_mix_in_color_emits_style_rule_recovery()
    {
        var result = CssPreprocessor.Process(".a { color: color-mix(in oklch, red, blue) }");
        var rec = Assert.Single(result.StyleRuleRecoveries);
        var decl = Assert.Single(rec.Declarations);
        Assert.Equal("color", decl.Property);
        Assert.Contains("color-mix", decl.RawValueText);
    }

    [Fact]
    public void Process_light_dark_emits_style_rule_recovery()
    {
        var result = CssPreprocessor.Process(".a { color: light-dark(white, black) }");
        var rec = Assert.Single(result.StyleRuleRecoveries);
        var decl = Assert.Single(rec.Declarations);
        Assert.Equal("color", decl.Property);
        Assert.Contains("light-dark", decl.RawValueText);
    }

    [Fact]
    public void Process_math_function_nested_in_unknown_function_emits_style_rule_recovery()
    {
        // Post-PR-#159 Copilot review — the math-function detector's contract is CONTAINS:
        // it scans INTO an unknown function's argument list, so calc() nested in a var()
        // fallback is recovered (pre-fix the block was skipped wholesale and missed it).
        var result = CssPreprocessor.Process(".a { width: var(--x, calc(1in - 24pt)) }");
        var rec = Assert.Single(result.StyleRuleRecoveries);
        var decl = Assert.Single(rec.Declarations);
        Assert.Equal("width", decl.Property);
        Assert.Contains("calc", decl.RawValueText);
    }

    [Fact]
    public void Process_math_function_name_in_a_string_inside_function_args_is_not_matched()
    {
        // The scan inside a function's argument list still skips quoted strings — a literal
        // "calc(1px)" in a var() fallback is NOT a math-function call.
        var result = CssPreprocessor.Process(".a { width: var(--x, \"calc(1px)\") }");
        Assert.True(result.StyleRuleRecoveries.IsEmpty);
    }

    [Fact]
    public void Process_oklch_with_important_recovers_important_flag()
    {
        var result = CssPreprocessor.Process(".a { color: oklch(0.5 0.2 30) !important }");
        var rec = Assert.Single(result.StyleRuleRecoveries);
        var decl = Assert.Single(rec.Declarations);
        Assert.True(decl.IsImportant);
        Assert.DoesNotContain("!important", decl.RawValueText);
    }

    [Fact]
    public void Process_normal_rgba_value_does_not_emit_recovery()
    {
        var result = CssPreprocessor.Process(".a { color: rgba(255, 0, 0, 1) }");
        Assert.True(result.StyleRuleRecoveries.IsEmpty);
    }

    [Fact]
    public void ScanForModernDeclarations_recovers_a_dropped_string_set_content()
    {
        // Phase 3 Task 22 (the content() form): AngleSharp.Css drops `string-set: name content()`
        // (content() — an unknown function in the unknown string-set property). The recovery re-adds the
        // declaration verbatim so the cascade + MarginContentCollector see it and resolve content().
        var rec = Assert.Single(CssPreprocessor.ScanForModernDeclarations("string-set: title content()"));
        Assert.Equal("string-set", rec.Property);
        Assert.Contains("content()", rec.RawValueText);
        Assert.False(rec.IsImportant);
    }

    [Fact]
    public void ScanForModernDeclarations_does_not_recover_a_string_set_without_content()
    {
        // The attr()/literal forms AngleSharp already keeps must NOT be duplicate-recovered — the trigger
        // is gated to string-set + content().
        Assert.Empty(CssPreprocessor.ScanForModernDeclarations("string-set: title attr(data-t)"));
        Assert.Empty(CssPreprocessor.ScanForModernDeclarations("string-set: title \"literal\""));
    }

    [Fact]
    public void Process_modern_function_inside_string_does_not_emit_recovery()
    {
        // Function-name match must be token-aware: "oklch(" inside a string is not a function call.
        var result = CssPreprocessor.Process(".a { content: \"oklch(red)\" }");
        Assert.True(result.StyleRuleRecoveries.IsEmpty);
    }

    [Fact]
    public void Process_multiple_style_rules_track_recovery_ordinal_correctly()
    {
        var result = CssPreprocessor.Process("""
            .a { color: red }
            .b { color: oklch(0.5 0.2 30) }
            .c { color: blue }
            """);
        var rec = Assert.Single(result.StyleRuleRecoveries);
        Assert.Equal(1, rec.OrdinalIndex);
    }

    // ------------------------------------------------------------
    // Robust slot merge — ordinal drift fix
    // ------------------------------------------------------------

    [Fact]
    public void Process_modern_at_rule_in_middle_does_not_drift_subsequent_slots()
    {
        var result = CssPreprocessor.Process("""
            .a { color: red }
            @container (min-width: 800px) { .x { color: red } }
            .z { color: blue }
            """);

        Assert.Equal(3, result.RuleSlots.Length);
        Assert.Equal(CssRuleSlotKind.StyleRule, result.RuleSlots[0].Kind);
        Assert.Equal(CssRuleSlotKind.AtRule, result.RuleSlots[1].Kind);
        Assert.Equal("container", result.RuleSlots[1].AtKeyword);
        Assert.Equal(CssRuleSlotKind.StyleRule, result.RuleSlots[2].Kind);
        Assert.Equal(1, result.RuleSlots[0].Location.Line);
        Assert.Equal(2, result.RuleSlots[1].Location.Line);
        Assert.Equal(3, result.RuleSlots[2].Location.Line);
    }

    [Fact]
    public void Process_multiple_modern_at_rules_get_distinct_at_rule_slots()
    {
        var result = CssPreprocessor.Process("""
            @layer one;
            .a { color: red }
            @container (min-width: 800px) { .x { color: red } }
            """);

        Assert.Equal(3, result.RuleSlots.Length);
        Assert.Equal("layer", result.RuleSlots[0].AtKeyword);
        Assert.Equal(CssRuleSlotKind.StyleRule, result.RuleSlots[1].Kind);
        Assert.Equal("container", result.RuleSlots[2].AtKeyword);
    }

    // ------------------------------------------------------------
    // Nested grouping recursion (Rec 3)
    // ------------------------------------------------------------

    [Fact]
    public void Process_nested_container_inside_media_emits_nested_slot()
    {
        var result = CssPreprocessor.Process("""
            @media print {
                .a { color: red }
                @container (min-width: 800px) { .x { color: red } }
                .z { color: green }
            }
            """);

        var media = Assert.Single(result.RuleSlots);
        Assert.Equal("media", media.AtKeyword);
        Assert.Equal(3, media.NestedSlots.Length);
        Assert.Equal(CssRuleSlotKind.StyleRule, media.NestedSlots[0].Kind);
        Assert.Equal(CssRuleSlotKind.AtRule, media.NestedSlots[1].Kind);
        Assert.Equal("container", media.NestedSlots[1].AtKeyword);
        Assert.Equal(CssRuleSlotKind.StyleRule, media.NestedSlots[2].Kind);
    }

    [Fact]
    public void Process_nested_layer_inside_supports_emits_nested_slot()
    {
        var result = CssPreprocessor.Process("""
            @supports (display: grid) {
                @layer fallback { .x { color: red } }
            }
            """);
        var supports = Assert.Single(result.RuleSlots);
        Assert.Equal("supports", supports.AtKeyword);
        var layer = Assert.Single(supports.NestedSlots);
        Assert.Equal("layer", layer.AtKeyword);
        Assert.Contains("fallback", layer.Prelude);
    }

    // ------------------------------------------------------------
    // Source positions
    // ------------------------------------------------------------

    [Fact]
    public void Process_records_position_for_each_top_level_rule_via_slots()
    {
        var result = CssPreprocessor.Process("""
            .a { color: red }
            .b { color: blue }
            """);
        Assert.Equal(2, result.RuleSlots.Length);
        Assert.Equal(1, result.RuleSlots[0].Location.Line);
        Assert.Equal(2, result.RuleSlots[1].Location.Line);
    }

    [Fact]
    public void Process_rule_slot_carries_source_label()
    {
        var result = CssPreprocessor.Process(".a { color: red }", source: "embedded.css");
        var pos = Assert.Single(result.RuleSlots);
        Assert.Equal("embedded.css", pos.Location.Source);
    }

    // ------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------

    [Fact]
    public void Process_does_not_throw_on_unterminated_block()
    {
        var result = CssPreprocessor.Process("@page { margin: 1in");
        Assert.NotNull(result);
    }

    [Fact]
    public void Process_does_not_throw_on_unterminated_string()
    {
        var result = CssPreprocessor.Process("@page { content: \"unterminated");
        Assert.NotNull(result);
    }

    [Fact]
    public void Process_handles_multiple_imports_followed_by_pages()
    {
        var result = CssPreprocessor.Process("""
            @import url("a.css") layer(framework);
            @import url("b.css") supports(display: grid);
            @page :first { @top-center { content: "Cover" } }
            @page :left { margin-left: 2cm }
            """);

        Assert.Equal(2, result.ImportRecoveries.Length);
        Assert.Equal("framework", result.ImportRecoveries[0].LayerName);
        Assert.Equal("display: grid", result.ImportRecoveries[1].SupportsCondition);

        Assert.Equal(2, result.PageRecoveries.Length);
        Assert.Equal(":first", result.PageRecoveries[0].SelectorText);
        Assert.Single(result.PageRecoveries[0].MarginBoxes);
        Assert.Equal(":left", result.PageRecoveries[1].SelectorText);
    }

    [Fact]
    public void Process_ordinal_indices_are_zero_indexed_per_kind()
    {
        var result = CssPreprocessor.Process("""
            @page :first { margin-top: 0 }
            @page :left { margin-left: 0 }
            @import url("x.css");
            """);
        Assert.Equal(0, result.PageRecoveries[0].OrdinalIndex);
        Assert.Equal(1, result.PageRecoveries[1].OrdinalIndex);
        Assert.Equal(0, result.ImportRecoveries[0].OrdinalIndex);
    }

    // ------------------------------------------------------------
    // CSS escape support — known limitation
    // ------------------------------------------------------------

    [Fact]
    public void Process_documents_css_escape_in_identifier_limitation()
    {
        var result = CssPreprocessor.Process("@layer fra\\6dework { .a { color: red } }");
        var slot = Assert.Single(result.RuleSlots);
        Assert.Equal(CssRuleSlotKind.AtRule, slot.Kind);
        Assert.Equal("layer", slot.AtKeyword);
        Assert.Contains("\\", slot.Prelude);
    }
}
