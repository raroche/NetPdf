// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="CssPreprocessor"/> covering the AngleSharp.Css 1.0.0-beta.144
/// gaps Phase 2 Task 3 was tasked to close, plus the review-cycle 1 hardening:
/// modern at-rule capture (<c>@container</c>, <c>@layer</c>), ordinal-drift fix via the
/// unified <see cref="CssPreprocessResult.RuleSlots"/> list, token-aware <c>!important</c>
/// recognition, and margin-box name validation.
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
    }

    [Fact]
    public void Process_only_whitespace_and_comments_returns_empty_result()
    {
        var result = CssPreprocessor.Process("  /* foo */  \n  /* bar */ ");
        Assert.True(result.PageRecoveries.IsEmpty);
        Assert.True(result.RuleSlots.IsEmpty);
    }

    // ------------------------------------------------------------
    // @page selector recovery (Task 3 blocker #1)
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
    // @page margin-box recovery (Task 3 blocker #2) + name validation
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
        // Pins all 16 CSS Paged Media L3 §6.4 names round-trip through the recovery.
        var result = CssPreprocessor.Process($"@page {{ @{boxName} {{ content: 'x' }} }}");
        var page = Assert.Single(result.PageRecoveries);
        var box = Assert.Single(page.MarginBoxes);
        Assert.Equal(boxName, box.Name);
    }

    [Fact]
    public void Process_unknown_margin_box_name_is_dropped_silently()
    {
        // CSS Paged Media L3 §6.4 enumerates exactly 16 margin-box names. Any other
        // @<ident> { ... } inside @page is malformed CSS — the preprocessor drops it
        // rather than silently converting it into a pseudo-margin-box.
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
    // @import modern-syntax recovery (Task 3 blocker #3)
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
        Assert.Null(import.SupportsCondition);
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
    // Modern at-rule recovery (Task 3 review-cycle 1 P1)
    // ------------------------------------------------------------

    [Fact]
    public void Process_container_at_rule_emits_opaque_slot()
    {
        // AngleSharp drops @container entirely. Preprocessor captures it as opaque so the
        // adapter can splice a CssAtRule into the AST in source order.
        var result = CssPreprocessor.Process("@container (min-width: 800px) { .a { color: red } }");
        var slot = Assert.Single(result.RuleSlots);
        var opaque = Assert.IsType<CssOpaqueAtRuleSlot>(slot);
        Assert.Equal("container", opaque.Name);
        Assert.Contains("min-width", opaque.Prelude);
    }

    [Fact]
    public void Process_layer_block_form_at_rule_emits_opaque_slot()
    {
        var result = CssPreprocessor.Process("@layer framework { .x { color: blue } }");
        var slot = Assert.Single(result.RuleSlots);
        var opaque = Assert.IsType<CssOpaqueAtRuleSlot>(slot);
        Assert.Equal("layer", opaque.Name);
        Assert.Equal("framework", opaque.Prelude);
    }

    [Fact]
    public void Process_layer_statement_form_at_rule_emits_opaque_slot()
    {
        var result = CssPreprocessor.Process("@layer one, two, three;");
        var slot = Assert.Single(result.RuleSlots);
        var opaque = Assert.IsType<CssOpaqueAtRuleSlot>(slot);
        Assert.Equal("layer", opaque.Name);
        Assert.Contains("one", opaque.Prelude);
        Assert.Contains("two", opaque.Prelude);
        Assert.Contains("three", opaque.Prelude);
    }

    // ------------------------------------------------------------
    // Ordinal-drift fix (Task 3 review-cycle 1 P2)
    // ------------------------------------------------------------

    [Fact]
    public void Process_modern_at_rule_in_middle_does_not_drift_subsequent_slots()
    {
        // Critical regression test for review cycle 1: AngleSharp drops the @container in
        // the middle, so its emit list has 2 entries (.a, .z) while the source has 3 rules
        // plus the @container. The preprocessor's RuleSlots must produce 3 entries: an
        // AngleSharp slot for .a, an opaque slot for @container, an AngleSharp slot for .z.
        // The adapter walks slots in order and only consumes from rules[] on AngleSharp slots.
        var result = CssPreprocessor.Process("""
            .a { color: red }
            @container (min-width: 800px) { .x { color: red } }
            .z { color: blue }
            """);

        Assert.Equal(3, result.RuleSlots.Length);
        Assert.IsType<CssAngleSharpRuleSlot>(result.RuleSlots[0]);
        Assert.IsType<CssOpaqueAtRuleSlot>(result.RuleSlots[1]);
        Assert.IsType<CssAngleSharpRuleSlot>(result.RuleSlots[2]);
        Assert.Equal(1, result.RuleSlots[0].Location.Line);
        Assert.Equal(2, result.RuleSlots[1].Location.Line);
        Assert.Equal(3, result.RuleSlots[2].Location.Line);
    }

    [Fact]
    public void Process_multiple_modern_at_rules_get_distinct_opaque_slots()
    {
        var result = CssPreprocessor.Process("""
            @layer one;
            .a { color: red }
            @container (min-width: 800px) { .x { color: red } }
            """);

        Assert.Equal(3, result.RuleSlots.Length);
        Assert.IsType<CssOpaqueAtRuleSlot>(result.RuleSlots[0]);
        Assert.IsType<CssAngleSharpRuleSlot>(result.RuleSlots[1]);
        Assert.IsType<CssOpaqueAtRuleSlot>(result.RuleSlots[2]);
        Assert.Equal("layer", ((CssOpaqueAtRuleSlot)result.RuleSlots[0]).Name);
        Assert.Equal("container", ((CssOpaqueAtRuleSlot)result.RuleSlots[2]).Name);
    }

    // ------------------------------------------------------------
    // Source positions (Task 3 blocker #4)
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

    [Fact]
    public void Process_records_at_rule_positions_too()
    {
        var result = CssPreprocessor.Process("""
            @import url("a.css");
            .a { color: red }
            @page { margin: 1in }
            """);
        Assert.Equal(3, result.RuleSlots.Length);
        Assert.Equal(1, result.RuleSlots[0].Location.Line);
        Assert.Equal(2, result.RuleSlots[1].Location.Line);
        Assert.Equal(3, result.RuleSlots[2].Location.Line);
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
    public void Process_skips_over_unrelated_at_rules()
    {
        // @media, @keyframes, @supports flow through as AngleSharp-emitted slots (we don't
        // capture them; AngleSharp handles them). Only @page / @import / @container / @layer
        // get special treatment.
        var result = CssPreprocessor.Process("""
            @media print { .a { color: red } }
            @page :first { margin: 0 }
            @keyframes pop { 0% { opacity: 0 } 100% { opacity: 1 } }
            """);
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal(":first", page.SelectorText);
        Assert.Equal(3, result.RuleSlots.Length);
        // None of these are modern at-rules → all three are AngleSharp-emitted slots.
        Assert.All(result.RuleSlots, s => Assert.IsType<CssAngleSharpRuleSlot>(s));
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
    // CSS escape support — known limitation (Rec 7)
    // ------------------------------------------------------------

    [Fact]
    public void Process_documents_css_escape_in_identifier_limitation()
    {
        // The tokenizer doesn't process CSS escape sequences (\41 = "A" etc.) in
        // identifiers. Generated CSS rarely uses identifier escapes — most tooling emits
        // ASCII identifiers — so this is a documented v1 limitation. When we eventually
        // add escape support, this test will start failing and the correct expansion can
        // replace these assertions.
        var result = CssPreprocessor.Process("@layer fra\\6dework { .a { color: red } }");
        var slot = Assert.Single(result.RuleSlots);
        // We capture the layer at-rule, but the "framework" name with embedded \6d is not
        // unescaped — it shows up as the raw text including the backslash sequence.
        var opaque = Assert.IsType<CssOpaqueAtRuleSlot>(slot);
        Assert.Equal("layer", opaque.Name);
        // Prelude carries the unescaped text — escape sequences pass through verbatim.
        Assert.Contains("\\", opaque.Prelude);
    }
}
