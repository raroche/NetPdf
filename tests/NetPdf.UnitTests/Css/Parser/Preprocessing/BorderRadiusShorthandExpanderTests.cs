// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="BorderRadiusShorthandExpander"/> — margin-box-border-radius cycle,
/// expanding the <c>border-radius</c> 1–4-value shorthand for <c>@page</c> margin-box bodies into the
/// four corner longhands. The elliptical Rx / Ry slash form also expands the vertical radii onto the internal -netpdf-border-{corner}-radius-y longhands.
/// </summary>
public sealed class BorderRadiusShorthandExpanderTests
{
    private static Dictionary<string, string> Expand(string value)
    {
        Assert.True(BorderRadiusShorthandExpander.TryExpand("border-radius", value, out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        return map;
    }

    [Fact]
    public void One_value_applies_to_all_four_corners_both_axes()
    {
        // A circular value sets BOTH axes (post-PR-#186 review P1) — the 4 horizontal corner longhands
        // AND the 4 internal `-y` longhands, all 8px, so a later circular value resets a prior elliptical.
        var d = Expand("8px");
        Assert.Equal(8, d.Count);
        foreach (var corner in new[] { "top-left", "top-right", "bottom-right", "bottom-left" })
        {
            Assert.Equal("8px", d[$"border-{corner}-radius"]);
            Assert.Equal("8px", d[$"-netpdf-border-{corner}-radius-y"]);
        }
    }

    [Fact]
    public void Two_values_are_first_diagonal_then_second_diagonal()
    {
        // `a b` → TL=BR=a, TR=BL=b (the corner analogue of the edge box's vertical/horizontal pairing).
        var d = Expand("8px 24px");
        Assert.Equal("8px", d["border-top-left-radius"]);
        Assert.Equal("24px", d["border-top-right-radius"]);
        Assert.Equal("8px", d["border-bottom-right-radius"]);
        Assert.Equal("24px", d["border-bottom-left-radius"]);
    }

    [Fact]
    public void Three_values_are_tl_then_tr_bl_then_br()
    {
        // PR #174 review P3 — `a b c` → TL=a; TR=BL=b; BR=c (CSS Backgrounds & Borders L3 §6.1).
        var d = Expand("1px 2px 3px");
        Assert.Equal("1px", d["border-top-left-radius"]);
        Assert.Equal("2px", d["border-top-right-radius"]);
        Assert.Equal("3px", d["border-bottom-right-radius"]);
        Assert.Equal("2px", d["border-bottom-left-radius"]);
    }

    [Fact]
    public void Four_values_are_tl_tr_br_bl()
    {
        var d = Expand("1px 2px 3px 4px");
        Assert.Equal("1px", d["border-top-left-radius"]);
        Assert.Equal("2px", d["border-top-right-radius"]);
        Assert.Equal("3px", d["border-bottom-right-radius"]);
        Assert.Equal("4px", d["border-bottom-left-radius"]);
    }

    [Fact]
    public void Negative_radius_rejects_the_shorthand()
    {
        // PR #174 review P2 — a border-radius is non-negative (CSS B&B §6.1); a negative value fails
        // resolver validation → the shorthand is rejected (→ MarginBoxStyle diagnoses + renders square).
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-radius", "-5px", out _));
    }

    [Fact]
    public void HasTopLevelSlash_tells_a_top_level_slash_from_calc_or_no_slash()
    {
        // border-radius-elliptical cycle — only a well-formed TOP-LEVEL `/` is the elliptical separator
        // the body preprocessor recovers; a `/` inside calc() (a division) and a value with no slash
        // are NOT.
        Assert.True(BorderRadiusShorthandExpander.HasTopLevelSlash("8px / 4px"));
        Assert.False(BorderRadiusShorthandExpander.HasTopLevelSlash("calc(10px / 2)"));
        Assert.False(BorderRadiusShorthandExpander.HasTopLevelSlash("8px bogus"));
        Assert.False(BorderRadiusShorthandExpander.HasTopLevelSlash("8px"));
    }

    [Fact]
    public void Percentage_is_a_valid_corner_value()
    {
        var d = Expand("50%");
        Assert.Equal("50%", d["border-top-left-radius"]);
    }

    [Fact]
    public void Css_wide_keyword_maps_to_every_corner_both_axes()
    {
        // A CSS-wide keyword maps to every corner on BOTH axes (post-PR-#186 review P1).
        var d = Expand("inherit");
        Assert.Equal(8, d.Count);
        foreach (var v in d.Values) Assert.Equal("inherit", v);
    }

    [Fact]
    public void Slash_elliptical_form_expands_to_horizontal_and_vertical_longhands()
    {
        // border-radius-elliptical cycle — `Rx / Ry` expands the horizontal radii onto the corner
        // longhands + the vertical radii onto the internal `-netpdf-border-{corner}-radius-y` longhands.
        Assert.True(BorderRadiusShorthandExpander.TryExpand("border-radius", "8px / 4px", out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        Assert.Equal("8px", map["border-top-left-radius"]);
        Assert.Equal("8px", map["border-bottom-right-radius"]);
        Assert.Equal("4px", map["-netpdf-border-top-left-radius-y"]);
        Assert.Equal("4px", map["-netpdf-border-bottom-right-radius-y"]);
    }

    [Fact]
    public void Two_value_slash_form_distributes_each_side_per_the_box_rules()
    {
        // `border-radius: 10px 20px / 5px 15px` → TL/BR get h=10,v=5; TR/BL get h=20,v=15 (each side
        // expands by the 1–4-value box distribution independently).
        Assert.True(BorderRadiusShorthandExpander.TryExpand("border-radius", "10px 20px / 5px 15px", out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        Assert.Equal("10px", map["border-top-left-radius"]);
        Assert.Equal("20px", map["border-top-right-radius"]);
        Assert.Equal("5px", map["-netpdf-border-top-left-radius-y"]);
        Assert.Equal("15px", map["-netpdf-border-top-right-radius-y"]);
    }

    [Fact]
    public void Slash_inside_calc_is_a_division_not_the_elliptical_separator()
    {
        // A `/` INSIDE a function is a division the corner-longhand resolver evaluates — only a
        // TOP-LEVEL `/` is the elliptical separator. `calc(10px / 2)` expands (the calc value on all
        // four corners, horizontal only — no top-level slash, so no vertical longhands).
        Assert.True(BorderRadiusShorthandExpander.TryExpand("border-radius", "calc(10px / 2)", out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        Assert.Equal("calc(10px / 2)", map["border-top-left-radius"]);
        // Circular (no top-level slash) sets both axes — the `-y` equals the horizontal (post-PR-#186 P1).
        Assert.Equal("calc(10px / 2)", map["-netpdf-border-top-left-radius-y"]);
        // A real top-level slash with a calc on one side EXPANDS (h = calc(10px), v = 4px).
        Assert.True(BorderRadiusShorthandExpander.TryExpand("border-radius", "calc(10px) / 4px", out var sl));
        var slMap = new Dictionary<string, string>();
        foreach (var (prop, val) in sl) slMap[prop] = val;
        Assert.Equal("calc(10px)", slMap["border-top-left-radius"]);
        Assert.Equal("4px", slMap["-netpdf-border-top-left-radius-y"]);
    }

    [Fact]
    public void Corner_longhand_co_writes_the_vertical_for_lockstep_reset()
    {
        // post-PR-#186 review P1 — a 1-value / CSS-wide corner longhand co-writes its internal `-y` (so a
        // later corner longhand RESETS a prior elliptical's stale vertical); a 2-value elliptical corner
        // longhand and a non-corner property stay unsupported.
        Assert.True(BorderRadiusShorthandExpander.IsCornerRadiusLonghand("border-top-left-radius"));
        Assert.False(BorderRadiusShorthandExpander.IsCornerRadiusLonghand("border-radius"));

        Assert.True(BorderRadiusShorthandExpander.TryExpandCornerVertical(
            "border-top-left-radius", "5px", out var p1, out var v1));
        Assert.Equal("-netpdf-border-top-left-radius-y", p1);
        Assert.Equal("5px", v1);

        Assert.True(BorderRadiusShorthandExpander.TryExpandCornerVertical(
            "border-bottom-right-radius", "inherit", out _, out var v2));
        Assert.Equal("inherit", v2);

        Assert.False(BorderRadiusShorthandExpander.TryExpandCornerVertical(
            "border-top-left-radius", "5px 3px", out _, out _));   // 2-value elliptical longhand: unsupported
        Assert.False(BorderRadiusShorthandExpander.TryExpandCornerVertical(
            "border-width", "5px", out _, out _));                 // not a corner radius longhand
    }

    [Fact]
    public void Negative_vertical_radius_in_a_slash_rejects_the_whole_shorthand()
    {
        // post-PR-#186 Copilot review — a negative VERTICAL radius (`10px / -5px`) is invalid (CSS B&B
        // §6.1) exactly like a negative horizontal, so the whole shorthand is rejected (the raw is kept
        // → unrounds), NOT clamped to 0. Requires the internal `-y` properties in NonNegativeProperties.
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-radius", "10px / -5px", out _));
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-radius", "-5px / 10px", out _));
    }

    [Fact]
    public void Invalid_value_rejects_the_whole_shorthand()
    {
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-radius", "8px bogus", out _));
    }

    [Fact]
    public void Non_border_radius_property_is_not_handled()
    {
        Assert.False(BorderRadiusShorthandExpander.IsBorderRadiusShorthand("border-width"));
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-width", "2px", out _));
    }
}
