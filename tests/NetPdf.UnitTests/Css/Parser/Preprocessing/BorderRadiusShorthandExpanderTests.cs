// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="BorderRadiusShorthandExpander"/> — margin-box-border-radius cycle,
/// expanding the <c>border-radius</c> 1–4-value shorthand for <c>@page</c> margin-box bodies into the
/// four corner longhands. The slash (elliptical) form is deferred.
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
    public void One_value_applies_to_all_four_corners()
    {
        var d = Expand("8px");
        Assert.Equal(4, d.Count);
        foreach (var corner in new[] { "top-left", "top-right", "bottom-right", "bottom-left" })
            Assert.Equal("8px", d[$"border-{corner}-radius"]);
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
    public void IsDeferredElliptical_tells_a_top_level_slash_from_calc_or_malformed()
    {
        // PR #174 review P2 — only a TOP-LEVEL `/` is the deferred elliptical form; a `/` inside calc()
        // (a division) and a malformed value are NOT — so MarginBoxStyle diagnoses the latter two.
        Assert.True(BorderRadiusShorthandExpander.IsDeferredElliptical("8px / 4px"));
        Assert.False(BorderRadiusShorthandExpander.IsDeferredElliptical("calc(10px / 2)"));
        Assert.False(BorderRadiusShorthandExpander.IsDeferredElliptical("8px bogus"));
        Assert.False(BorderRadiusShorthandExpander.IsDeferredElliptical("8px"));
    }

    [Fact]
    public void Percentage_is_a_valid_corner_value()
    {
        var d = Expand("50%");
        Assert.Equal("50%", d["border-top-left-radius"]);
    }

    [Fact]
    public void Css_wide_keyword_maps_to_every_corner()
    {
        var d = Expand("inherit");
        Assert.Equal(4, d.Count);
        foreach (var v in d.Values) Assert.Equal("inherit", v);
    }

    [Fact]
    public void Slash_elliptical_form_is_deferred()
    {
        // `Rx / Ry` needs per-corner (rx, ry) pairs the single-value longhands can't store → deferred.
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-radius", "8px / 4px", out _));
    }

    [Fact]
    public void Slash_inside_calc_is_a_division_not_the_elliptical_separator()
    {
        // A `/` INSIDE a function is a division the corner-longhand resolver evaluates — only a
        // TOP-LEVEL `/` is the elliptical separator (post-PR-#174 review P3). `calc(10px / 2)` must
        // EXPAND (to the calc value on all four corners), not defer to square.
        Assert.True(BorderRadiusShorthandExpander.TryExpand("border-radius", "calc(10px / 2)", out var longhands));
        var map = new Dictionary<string, string>();
        foreach (var (prop, val) in longhands) map[prop] = val;
        Assert.Equal("calc(10px / 2)", map["border-top-left-radius"]);
        // ... but a real top-level slash inside an otherwise calc-bearing value still defers.
        Assert.False(BorderRadiusShorthandExpander.TryExpand("border-radius", "calc(10px) / 4px", out _));
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
