// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="CssPreprocessor"/> covering the four AngleSharp.Css
/// 1.0.0-beta.144 gaps Phase 2 Task 3 was tasked to close. Each blocker has at least
/// one test that pins the recovered shape end-to-end.
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
        Assert.True(result.RulePositions.IsEmpty);
    }

    [Fact]
    public void Process_only_whitespace_and_comments_returns_empty_result()
    {
        var result = CssPreprocessor.Process("  /* foo */  \n  /* bar */ ");
        Assert.True(result.PageRecoveries.IsEmpty);
        Assert.True(result.RulePositions.IsEmpty);
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
    // @page margin-box recovery (Task 3 blocker #2)
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
    // Source positions (Task 3 blocker #4)
    // ------------------------------------------------------------

    [Fact]
    public void Process_records_position_for_each_top_level_rule()
    {
        var result = CssPreprocessor.Process("""
            .a { color: red }
            .b { color: blue }
            """);
        Assert.Equal(2, result.RulePositions.Length);
        Assert.Equal(1, result.RulePositions[0].Location.Line);
        // Second rule starts on line 2 (0-indexed line ordinal aside, content starts after \n).
        Assert.Equal(2, result.RulePositions[1].Location.Line);
    }

    [Fact]
    public void Process_rule_position_carries_source_label()
    {
        var result = CssPreprocessor.Process(".a { color: red }", source: "embedded.css");
        var pos = Assert.Single(result.RulePositions);
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
        Assert.Equal(3, result.RulePositions.Length);
        Assert.Equal(1, result.RulePositions[0].Location.Line);
        Assert.Equal(2, result.RulePositions[1].Location.Line);
        Assert.Equal(3, result.RulePositions[2].Location.Line);
    }

    // ------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------

    [Fact]
    public void Process_does_not_throw_on_unterminated_block()
    {
        // Reaches end while still inside @page body. Preprocessor must not loop forever.
        var result = CssPreprocessor.Process("@page { margin: 1in");
        // Whatever it produced, the call returned — that's the contract.
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
        var result = CssPreprocessor.Process("""
            @media print { .a { color: red } }
            @page :first { margin: 0 }
            @keyframes pop { 0% { opacity: 0 } 100% { opacity: 1 } }
            """);
        // We don't recover @media or @keyframes — only @page (and @import).
        var page = Assert.Single(result.PageRecoveries);
        Assert.Equal(":first", page.SelectorText);
        Assert.Equal(3, result.RulePositions.Length);
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
}
