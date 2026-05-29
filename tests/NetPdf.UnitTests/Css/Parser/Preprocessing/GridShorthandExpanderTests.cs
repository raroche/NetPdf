// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 18 cycle 8 — unit tests for
/// <see cref="GridShorthandExpander"/>. Covers the §7.4 `grid`
/// shorthand grammar:
/// <list type="bullet">
///   <item><c>grid: none</c> (full reset)</item>
///   <item><c>grid: &lt;rows&gt; / &lt;columns&gt;</c></item>
///   <item><c>grid: &lt;rows&gt; / auto-flow [dense]? &lt;auto-cols&gt;?</c></item>
///   <item><c>grid: auto-flow [dense]? &lt;auto-rows&gt;? / &lt;cols&gt;</c></item>
///   <item>CSS-wide keywords (initial/inherit/unset/...)</item>
/// </list>
/// + invalid-input rejection so the cascade falls back to
/// initial values per CSS Cascade L4 §4.2.
/// </summary>
public sealed class GridShorthandExpanderTests
{
    private static void AssertExpands(
        string raw,
        string expectedRows, string expectedCols, string expectedAreas,
        string expectedAutoRows, string expectedAutoCols, string expectedAutoFlow)
    {
        Assert.True(GridShorthandExpander.TryExpand(raw,
            out var rows, out var cols, out var areas,
            out var autoRows, out var autoCols, out var autoFlow));
        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedAreas, areas);
        Assert.Equal(expectedAutoRows, autoRows);
        Assert.Equal(expectedAutoCols, autoCols);
        Assert.Equal(expectedAutoFlow, autoFlow);
    }

    private static void AssertRejects(string raw)
    {
        Assert.False(GridShorthandExpander.TryExpand(raw,
            out _, out _, out _, out _, out _, out _));
    }

    // ============================================================
    // grid: none — full reset to initial values per §7.4
    // ============================================================

    [Fact]
    public void None_resets_all_six_longhands_to_initial()
    {
        AssertExpands("none",
            expectedRows: "none",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void NONE_case_insensitive()
    {
        AssertExpands("NONE",
            expectedRows: "none",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    // ============================================================
    // grid: <rows> / <columns> — basic template form
    // ============================================================

    [Fact]
    public void Basic_rows_slash_columns()
    {
        AssertExpands("100px / 200px",
            expectedRows: "100px",
            expectedCols: "200px",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Multi_track_rows_and_columns()
    {
        AssertExpands("100px 100px / 200px 200px 200px",
            expectedRows: "100px 100px",
            expectedCols: "200px 200px 200px",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Fr_unit_in_rows_and_columns()
    {
        AssertExpands("1fr 2fr / 1fr 1fr 1fr",
            expectedRows: "1fr 2fr",
            expectedCols: "1fr 1fr 1fr",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Repeat_function_preserved_in_rows_and_columns()
    {
        AssertExpands("repeat(3, 100px) / repeat(2, 50%)",
            expectedRows: "repeat(3, 100px)",
            expectedCols: "repeat(2, 50%)",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Minmax_function_preserved()
    {
        AssertExpands("minmax(100px, 1fr) / minmax(50px, 200px)",
            expectedRows: "minmax(100px, 1fr)",
            expectedCols: "minmax(50px, 200px)",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Auto_keyword_in_track_values()
    {
        AssertExpands("auto auto / auto",
            expectedRows: "auto auto",
            expectedCols: "auto",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    // ============================================================
    // grid: <rows> / auto-flow [dense]? <auto-cols>? — column flow
    // ============================================================

    [Fact]
    public void Right_auto_flow_no_dense_sets_column_flow()
    {
        AssertExpands("100px / auto-flow 200px",
            expectedRows: "100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "200px",
            expectedAutoFlow: "column");
    }

    [Fact]
    public void Right_auto_flow_dense_sets_column_dense_flow()
    {
        AssertExpands("100px / auto-flow dense 200px",
            expectedRows: "100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "200px",
            expectedAutoFlow: "column dense");
    }

    [Fact]
    public void Right_dense_auto_flow_order_also_valid()
    {
        AssertExpands("100px / dense auto-flow 200px",
            expectedRows: "100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "200px",
            expectedAutoFlow: "column dense");
    }

    [Fact]
    public void Right_auto_flow_no_auto_tracks_defaults_to_auto()
    {
        AssertExpands("100px / auto-flow",
            expectedRows: "100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "column");
    }

    [Fact]
    public void Right_auto_flow_dense_no_auto_tracks()
    {
        AssertExpands("100px 100px / auto-flow dense",
            expectedRows: "100px 100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "column dense");
    }

    [Fact]
    public void Right_auto_flow_with_multi_track_auto_cols()
    {
        AssertExpands("1fr / auto-flow 100px 200px",
            expectedRows: "1fr",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "100px 200px",
            expectedAutoFlow: "column");
    }

    // ============================================================
    // grid: auto-flow [dense]? <auto-rows>? / <cols> — row flow
    // ============================================================

    [Fact]
    public void Left_auto_flow_sets_row_flow()
    {
        AssertExpands("auto-flow 200px / 100px",
            expectedRows: "none",
            expectedCols: "100px",
            expectedAreas: "none",
            expectedAutoRows: "200px",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Left_auto_flow_dense_sets_row_dense_flow()
    {
        AssertExpands("auto-flow dense 200px / 100px",
            expectedRows: "none",
            expectedCols: "100px",
            expectedAreas: "none",
            expectedAutoRows: "200px",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row dense");
    }

    [Fact]
    public void Left_dense_auto_flow_order_valid()
    {
        AssertExpands("dense auto-flow 200px / 100px",
            expectedRows: "none",
            expectedCols: "100px",
            expectedAreas: "none",
            expectedAutoRows: "200px",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row dense");
    }

    [Fact]
    public void Left_auto_flow_no_auto_tracks()
    {
        AssertExpands("auto-flow / 100px 100px",
            expectedRows: "none",
            expectedCols: "100px 100px",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    // ============================================================
    // CSS-wide keywords — passthrough to all six longhands
    // ============================================================

    [Fact]
    public void Inherit_passes_through_to_all_six()
    {
        AssertExpands("inherit",
            expectedRows: "inherit",
            expectedCols: "inherit",
            expectedAreas: "inherit",
            expectedAutoRows: "inherit",
            expectedAutoCols: "inherit",
            expectedAutoFlow: "inherit");
    }

    [Fact]
    public void Initial_passes_through()
    {
        AssertExpands("initial",
            expectedRows: "initial",
            expectedCols: "initial",
            expectedAreas: "initial",
            expectedAutoRows: "initial",
            expectedAutoCols: "initial",
            expectedAutoFlow: "initial");
    }

    [Fact]
    public void Unset_passes_through()
    {
        AssertExpands("unset",
            expectedRows: "unset",
            expectedCols: "unset",
            expectedAreas: "unset",
            expectedAutoRows: "unset",
            expectedAutoCols: "unset",
            expectedAutoFlow: "unset");
    }

    // ============================================================
    // Invalid input — atomic rejection
    // ============================================================

    [Fact]
    public void Empty_string_rejected()
    {
        AssertRejects("");
        AssertRejects("   ");
    }

    [Fact]
    public void No_slash_rejected()
    {
        // Bare track values without a slash aren't covered by cycle
        // 8 — the inline template-areas form (= `"a a" 50px`) would
        // need a follow-up cycle. Reject so the cascade falls back.
        AssertRejects("100px 100px");
        AssertRejects("1fr");
    }

    [Fact]
    public void Two_slashes_rejected()
    {
        // §7.4 has at most one slash per grid shorthand declaration.
        AssertRejects("100px / 200px / 300px");
    }

    [Fact]
    public void Empty_left_side_rejected()
    {
        AssertRejects(" / 100px");
    }

    [Fact]
    public void Empty_right_side_rejected()
    {
        AssertRejects("100px / ");
    }

    [Fact]
    public void Both_sides_auto_flow_rejected()
    {
        AssertRejects("auto-flow / auto-flow");
    }

    [Fact]
    public void Bare_dense_without_auto_flow_rejected()
    {
        // The §7.4 `[ auto-flow && dense? ]` grammar requires
        // auto-flow when dense is used in the shorthand. `dense
        // / 100px` is NOT valid.
        AssertRejects("dense / 100px");
        AssertRejects("100px / dense");
    }

    [Fact]
    public void Duplicate_auto_flow_token_rejected()
    {
        AssertRejects("100px / auto-flow auto-flow 200px");
    }

    [Fact]
    public void Duplicate_dense_token_rejected()
    {
        AssertRejects("100px / auto-flow dense dense");
    }

    [Fact]
    public void Var_expression_falls_through_to_atomic_invalidation()
    {
        // var() requires post-substitution re-expansion; the
        // preprocessor doesn't attempt mid-substitution expansion in
        // cycle 8. Falling through to atomic invalidation means the
        // cascade falls back to the property initial values, which
        // is the documented behavior of the other grid expanders.
        AssertRejects("var(--my-rows) / 100px");
        AssertRejects("100px / var(--my-cols)");
    }

    // ============================================================
    // Case-insensitivity of auto-flow / dense tokens
    // ============================================================

    [Fact]
    public void AUTO_FLOW_case_insensitive()
    {
        AssertExpands("100px / AUTO-FLOW 200px",
            expectedRows: "100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "200px",
            expectedAutoFlow: "column");
    }

    [Fact]
    public void DENSE_case_insensitive()
    {
        AssertExpands("100px / auto-flow DENSE 200px",
            expectedRows: "100px",
            expectedCols: "none",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "200px",
            expectedAutoFlow: "column dense");
    }

    // ============================================================
    // Whitespace handling
    // ============================================================

    [Fact]
    public void Extra_whitespace_around_slash()
    {
        AssertExpands("100px    /    200px",
            expectedRows: "100px",
            expectedCols: "200px",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }

    [Fact]
    public void Leading_trailing_whitespace_trimmed()
    {
        AssertExpands("   100px / 200px   ",
            expectedRows: "100px",
            expectedCols: "200px",
            expectedAreas: "none",
            expectedAutoRows: "auto",
            expectedAutoCols: "auto",
            expectedAutoFlow: "row");
    }
}
