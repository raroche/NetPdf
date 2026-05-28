// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Phase 3 Task 18 cycle 7a — CSS parser/resolver tests for
/// <see cref="GridTemplateAreasResolver"/>. Covers the multi-string
/// syntax + name → rectangle map derivation + validation invariants
/// per CSS Grid L1 §7.3.
/// </summary>
public sealed class GridTemplateAreasResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static ComputedStyle Materialize(string value, out CapturingSink sink)
    {
        sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(
            PropertyId.GridTemplateAreas, value, sink);
        var style = ComputedStyle.Rent();
        result.MaterializeInto(style, PropertyId.GridTemplateAreas);
        return style;
    }

    [Fact]
    public void None_keyword_resolves_to_default_without_side_table()
    {
        using var style = Materialize("none", out var sink);
        Assert.Empty(sink.Diagnostics);

        var slot = style.Get(PropertyId.GridTemplateAreas);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
        Assert.Equal(GridTemplateAreasResolver.KeywordIdNone, slot.AsKeyword());
        Assert.Equal(GridTemplateAreas.None, style.ReadGridTemplateAreas());
    }

    [Fact]
    public void Single_row_single_name_builds_one_area()
    {
        using var style = Materialize("\"head\"", out var sink);
        Assert.Empty(sink.Diagnostics);

        var areas = style.ReadGridTemplateAreas();
        Assert.Equal(1, areas.RowCount);
        Assert.Equal(1, areas.ColumnCount);
        Assert.Single(areas.NameToRect);
        Assert.Equal(new GridAreaRect(1, 2, 1, 2), areas.NameToRect["head"]);
        Assert.Equal("head", areas[0, 0]);
    }

    [Fact]
    public void Multi_row_invoice_template_builds_three_areas()
    {
        // Classic invoice template: header row spans both cols; main
        // + sidebar split row 2; footer spans both cols in row 3.
        using var style = Materialize(
            "\"head head\" \"main side\" \"foot foot\"",
            out var sink);
        Assert.Empty(sink.Diagnostics);

        var areas = style.ReadGridTemplateAreas();
        Assert.Equal(3, areas.RowCount);
        Assert.Equal(2, areas.ColumnCount);
        Assert.Equal(4, areas.NameToRect.Count);

        // head: rows 1-2 (= 1-based line 1 to 2), cols 1-3 (= span 2).
        Assert.Equal(new GridAreaRect(1, 2, 1, 3), areas.NameToRect["head"]);
        Assert.Equal(new GridAreaRect(2, 3, 1, 2), areas.NameToRect["main"]);
        Assert.Equal(new GridAreaRect(2, 3, 2, 3), areas.NameToRect["side"]);
        Assert.Equal(new GridAreaRect(3, 4, 1, 3), areas.NameToRect["foot"]);
    }

    [Fact]
    public void Period_token_is_null_cell()
    {
        using var style = Materialize(
            "\"head . head\" \"main main main\"", out var sink);
        // "head . head" is invalid: head appears in non-rectangular
        // positions (col 1 and col 3, but not col 2). Expect Invalid.
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void Period_token_excluded_from_name_map()
    {
        using var style = Materialize("\"head .\" \"foot foot\"", out var sink);
        Assert.Empty(sink.Diagnostics);

        var areas = style.ReadGridTemplateAreas();
        Assert.Equal(2, areas.RowCount);
        Assert.Equal(2, areas.ColumnCount);
        Assert.Null(areas[0, 1]);
        Assert.Equal("head", areas[0, 0]);
        Assert.Equal(2, areas.NameToRect.Count);
        Assert.Equal(new GridAreaRect(1, 2, 1, 2), areas.NameToRect["head"]);
        Assert.Equal(new GridAreaRect(2, 3, 1, 3), areas.NameToRect["foot"]);
    }

    [Fact]
    public void Multiple_periods_count_as_single_null_cell()
    {
        // Per §7.3 — "a sequence of one or more . characters" is one
        // null cell.
        using var style = Materialize(
            "\"head ...\" \"main main\"", out var sink);
        Assert.Empty(sink.Diagnostics);

        var areas = style.ReadGridTemplateAreas();
        Assert.Equal(2, areas.ColumnCount);
        Assert.Null(areas[0, 1]);
    }

    [Fact]
    public void Ragged_rows_rejected()
    {
        using var style = Materialize(
            "\"head head\" \"main\"", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        var d = sink.Diagnostics[0];
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, d.Code);
        Assert.Contains("ragged rows", d.Message);
    }

    [Fact]
    public void Non_rectangular_named_area_rejected()
    {
        // L-shape: head occupies (0,0), (0,1), (1,0) — not a rectangle.
        using var style = Materialize(
            "\"head head\" \"head .\"", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001,
            sink.Diagnostics[0].Code);
    }

    [Fact]
    public void Empty_value_rejected()
    {
        using var style = Materialize("\"\"", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void Unquoted_value_rejected()
    {
        using var style = Materialize("head head", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void Unclosed_string_rejected()
    {
        using var style = Materialize("\"head", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Fact]
    public void Css_wide_keyword_rejected_defense_in_depth()
    {
        using var style = Materialize("inherit", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    // =================================================================
    //  PR-#105 review F1 — DoS guard caps.
    // =================================================================

    [Fact]
    public void F1_source_exceeding_max_length_rejected()
    {
        var huge = new string('a', GridTemplateAreasResolver.MaxSourceLength + 1);
        using var style = Materialize($"\"{huge}\"", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains("source length", sink.Diagnostics[0].Message);
    }

    [Fact]
    public void F1_too_many_rows_rejected()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < GridTemplateAreasResolver.MaxRows + 1; i++)
        {
            sb.Append("\"a\" ");
        }
        using var style = Materialize(sb.ToString(), out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains("MaxRows", sink.Diagnostics[0].Message);
    }

    [Fact]
    public void F1_too_many_columns_rejected()
    {
        var sb = new System.Text.StringBuilder("\"");
        for (var i = 0; i < GridTemplateAreasResolver.MaxColumns + 1; i++)
        {
            sb.Append("a ");
        }
        sb.Append("\"");
        using var style = Materialize(sb.ToString(), out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains("MaxColumns", sink.Diagnostics[0].Message);
    }

    [Fact]
    public void F1_long_ident_rejected()
    {
        var longIdent = new string('a', GridTemplateAreasResolver.MaxIdentLength + 1);
        using var style = Materialize($"\"{longIdent}\"", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains("MaxIdentLength", sink.Diagnostics[0].Message);
    }

    // =================================================================
    //  PR-#105 review F2 — TryValidate shares rectangle validation.
    // =================================================================

    [Fact]
    public void F2_TryValidate_rejects_non_rectangular_template()
    {
        // Pre-fix TryValidate skipped the rectangle pass + would have
        // admitted L-shaped templates.
        Assert.False(GridTemplateAreasResolver.TryValidate(
            "\"head head\" \"head .\""));
    }

    [Fact]
    public void F2_TryValidate_accepts_valid_template()
    {
        Assert.True(GridTemplateAreasResolver.TryValidate(
            "\"head head\" \"main side\" \"foot foot\""));
    }

    [Fact]
    public void F2_TryValidate_accepts_none_and_empty()
    {
        Assert.True(GridTemplateAreasResolver.TryValidate("none"));
        Assert.True(GridTemplateAreasResolver.TryValidate(""));
        Assert.True(GridTemplateAreasResolver.TryValidate("   "));
    }

    [Fact]
    public void F2_TryValidate_rejects_oversized_source()
    {
        var huge = new string('a', GridTemplateAreasResolver.MaxSourceLength + 1);
        Assert.False(GridTemplateAreasResolver.TryValidate($"\"{huge}\""));
    }

    // =================================================================
    //  PR-#105 review F3 — CSS string escape handling.
    // =================================================================

    [Fact]
    public void F3_hex_escape_in_area_name_decodes()
    {
        // \61 = 'a'. So "\\61bc" decodes to "abc".
        using var style = Materialize("\"\\61 bc\"", out var sink);
        Assert.Empty(sink.Diagnostics);
        var areas = style.ReadGridTemplateAreas();
        Assert.Single(areas.NameToRect);
        Assert.Contains("abc", areas.NameToRect.Keys);
    }

    [Fact]
    public void F3_literal_escape_in_area_name()
    {
        // "\\zhead" → "zhead" (= backslash + next-char escape for
        // non-hex characters appends the literal next char).
        using var style = Materialize("\"\\zhead\"", out var sink);
        Assert.Empty(sink.Diagnostics);
        var areas = style.ReadGridTemplateAreas();
        Assert.Contains("zhead", areas.NameToRect.Keys);
    }

    [Fact]
    public void F3_non_ascii_ident_accepted()
    {
        // Per CSS Syntax §4.4 — non-ASCII identifier code points are
        // allowed.
        using var style = Materialize("\"α α\" \"β γ\"", out var sink);
        Assert.Empty(sink.Diagnostics);
        var areas = style.ReadGridTemplateAreas();
        Assert.Equal(3, areas.NameToRect.Count);
        Assert.Contains("α", areas.NameToRect.Keys);
    }

    [Fact]
    public void F3_dangling_backslash_rejected()
    {
        using var style = Materialize("\"a\\", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
    }

    // =================================================================
    //  PR-#105 review F10 — invalid cell character reports actual reason.
    // =================================================================

    [Fact]
    public void F10_invalid_cell_character_reports_position()
    {
        using var style = Materialize("\"head @ side\"", out var sink);
        Assert.NotEmpty(sink.Diagnostics);
        var msg = sink.Diagnostics[0].Message;
        Assert.Contains("@", msg);
        Assert.Contains("row 1", msg);
    }
}
