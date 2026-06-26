// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 filters (PR 2) — unit tests for <see cref="CssFilter_Parser"/>: the ten filter
/// functions, number/percentage/angle/length arguments, defaults, chaining order, and rejection.</summary>
public sealed class CssFilterParserTests
{
    [Fact]
    public void Grayscale_percentage_parses_to_a_fraction()
    {
        var f = CssFilter_Parser.TryParse("grayscale(50%)");
        Assert.NotNull(f);
        var op = Assert.Single(f!.Ops);
        Assert.Equal(FilterKind.Grayscale, op.Kind);
        Assert.Equal(0.5, op.Amount, precision: 4);
    }

    [Fact]
    public void Number_and_percentage_amounts_are_equivalent()
    {
        Assert.Equal(
            CssFilter_Parser.TryParse("brightness(1.5)")!.Ops[0].Amount,
            CssFilter_Parser.TryParse("brightness(150%)")!.Ops[0].Amount, precision: 6);
    }

    [Fact]
    public void Blur_length_is_css_px()
    {
        var f = CssFilter_Parser.TryParse("blur(4px)");
        Assert.Equal(FilterKind.Blur, f!.Ops[0].Kind);
        Assert.Equal(4.0, f.Ops[0].Amount, precision: 4);
    }

    [Fact]
    public void Hue_rotate_angle_units_convert_to_degrees()
    {
        Assert.Equal(90.0, CssFilter_Parser.TryParse("hue-rotate(90deg)")!.Ops[0].Amount, precision: 4);
        Assert.Equal(180.0, CssFilter_Parser.TryParse("hue-rotate(0.5turn)")!.Ops[0].Amount, precision: 4);
    }

    [Fact]
    public void Bare_functions_default_to_one()
    {
        // CSS Filter Effects §2 — an OMITTED amount defaults to 1 for every proportional function
        // (`grayscale()` ≡ `grayscale(1)` = full grayscale, NOT a no-op).
        Assert.Equal(1.0, CssFilter_Parser.TryParse("grayscale()")!.Ops[0].Amount, precision: 4);
        Assert.Equal(1.0, CssFilter_Parser.TryParse("invert()")!.Ops[0].Amount, precision: 4);
        Assert.Equal(1.0, CssFilter_Parser.TryParse("sepia()")!.Ops[0].Amount, precision: 4);
        Assert.Equal(1.0, CssFilter_Parser.TryParse("brightness()")!.Ops[0].Amount, precision: 4);
        Assert.Equal(1.0, CssFilter_Parser.TryParse("opacity()")!.Ops[0].Amount, precision: 4);
    }

    [Fact]
    public void Grayscale_invert_sepia_opacity_clamp_above_one_others_do_not()
    {
        Assert.Equal(1.0, CssFilter_Parser.TryParse("grayscale(150%)")!.Ops[0].Amount, precision: 4); // clamped
        Assert.Equal(1.0, CssFilter_Parser.TryParse("invert(2)")!.Ops[0].Amount, precision: 4);        // clamped
        Assert.Equal(1.0, CssFilter_Parser.TryParse("opacity(300%)")!.Ops[0].Amount, precision: 4);    // clamped
        Assert.Equal(2.5, CssFilter_Parser.TryParse("brightness(250%)")!.Ops[0].Amount, precision: 4); // NOT clamped
        Assert.Equal(3.0, CssFilter_Parser.TryParse("saturate(3)")!.Ops[0].Amount, precision: 4);      // NOT clamped
    }

    [Fact]
    public void Drop_shadow_parses_offsets_blur_and_color()
    {
        var f = CssFilter_Parser.TryParse("drop-shadow(4px 6px 8px red)");
        var op = Assert.Single(f!.Ops);
        Assert.Equal(FilterKind.DropShadow, op.Kind);
        var s = op.Shadow!.Value;
        Assert.Equal(4.0, s.OffsetXPx, precision: 4);
        Assert.Equal(6.0, s.OffsetYPx, precision: 4);
        Assert.Equal(8.0, s.BlurPx, precision: 4);
        Assert.Equal("red", s.ColorRaw);
    }

    [Fact]
    public void Drop_shadow_without_blur_or_color_defaults()
    {
        var s = CssFilter_Parser.TryParse("drop-shadow(2px 2px)")!.Ops[0].Shadow!.Value;
        Assert.Equal(0.0, s.BlurPx, precision: 4);
        Assert.Null(s.ColorRaw);
    }

    [Fact]
    public void Chained_filters_keep_their_declared_order()
    {
        var f = CssFilter_Parser.TryParse("grayscale(100%) blur(2px) brightness(1.2)");
        Assert.NotNull(f);
        Assert.Equal(3, f!.Ops.Count);
        Assert.Equal(FilterKind.Grayscale, f.Ops[0].Kind);
        Assert.Equal(FilterKind.Blur, f.Ops[1].Kind);
        Assert.Equal(FilterKind.Brightness, f.Ops[2].Kind);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("blur(-2px)")]            // negative blur
    [InlineData("grayscale(-1)")]         // negative amount
    [InlineData("url(#myfilter)")]        // SVG filter ref (out of scope)
    [InlineData("frobnicate(1)")]         // unknown function
    [InlineData("drop-shadow(2px)")]      // only one length
    [InlineData("blur(2px) bogus(1)")]    // one bad function rejects the whole value
    public void Unsupported_or_invalid_values_return_null(string value)
    {
        Assert.Null(CssFilter_Parser.TryParse(value));
    }
}
