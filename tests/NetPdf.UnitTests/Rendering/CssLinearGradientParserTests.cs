// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients — unit tests for <see cref="CssLinearGradient_Parser"/>: the
/// direction grammar (default / <c>to &lt;side&gt;</c> / <c>to &lt;corner&gt;</c> /
/// <c>&lt;angle&gt;</c>), the color-stop list, and rejection of unsupported forms.</summary>
public sealed class CssLinearGradientParserTests
{
    [Fact]
    public void Default_direction_is_to_bottom_180deg_with_two_stops()
    {
        var g = CssLinearGradient_Parser.TryParse("linear-gradient(red, blue)");
        Assert.NotNull(g);
        Assert.Equal(180.0, g!.AngleDeg, precision: 3); // "to bottom"
        Assert.Equal(2, g.Stops.Count);
        Assert.Equal("red", g.Stops[0].ColorRaw);
        Assert.Null(g.Stops[0].Position);
    }

    [Theory]
    [InlineData("to top", 0.0)]
    [InlineData("to right", 90.0)]
    [InlineData("to bottom", 180.0)]
    [InlineData("to left", 270.0)]
    [InlineData("to top right", 45.0)]
    [InlineData("to bottom right", 135.0)]
    [InlineData("to bottom left", 225.0)]
    [InlineData("to top left", 315.0)]
    public void Side_and_corner_directions_map_to_css_angles(string direction, double expectedDeg)
    {
        var g = CssLinearGradient_Parser.TryParse($"linear-gradient({direction}, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(expectedDeg, g!.AngleDeg, precision: 3);
    }

    [Theory]
    [InlineData("45deg", 45.0)]
    [InlineData("0.5turn", 180.0)]
    [InlineData("200grad", 180.0)]
    [InlineData("-90deg", 270.0)] // normalized into [0, 360)
    public void Angle_units_parse_and_normalize(string angle, double expectedDeg)
    {
        var g = CssLinearGradient_Parser.TryParse($"linear-gradient({angle}, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(expectedDeg, g!.AngleDeg, precision: 3);
    }

    [Fact]
    public void Corner_directions_record_the_corner_and_keep_a_square_box_angle_fallback()
    {
        // A `to <corner>` direction records the corner symbolically (the painter derives the true,
        // aspect-ratio-correct angle) while AngleDeg keeps the square-box fallback (PR #209 review [P2]).
        AssertCorner("to top right", LinearGradientCorner.TopRight, 45.0);
        AssertCorner("to bottom right", LinearGradientCorner.BottomRight, 135.0);
        AssertCorner("to bottom left", LinearGradientCorner.BottomLeft, 225.0);
        AssertCorner("to top left", LinearGradientCorner.TopLeft, 315.0);

        static void AssertCorner(string direction, LinearGradientCorner expectedCorner, double expectedFallbackDeg)
        {
            var g = CssLinearGradient_Parser.TryParse($"linear-gradient({direction}, red, blue)");
            Assert.NotNull(g);
            Assert.Equal(expectedCorner, g!.Corner);
            Assert.Equal(expectedFallbackDeg, g.AngleDeg, precision: 3);
        }
    }

    [Theory]
    // Sides and explicit angles leave Corner null (the painter uses AngleDeg directly).
    [InlineData("to top")]
    [InlineData("to right")]
    [InlineData("45deg")]
    public void Side_and_angle_directions_leave_corner_null(string direction)
    {
        var g = CssLinearGradient_Parser.TryParse($"linear-gradient({direction}, red, blue)");
        Assert.NotNull(g);
        Assert.Null(g!.Corner);
    }

    [Fact]
    public void Percentage_stop_positions_parse()
    {
        var g = CssLinearGradient_Parser.TryParse("linear-gradient(to right, #ff0000 10%, lime 50%, blue 90%)");
        Assert.NotNull(g);
        Assert.Equal(3, g!.Stops.Count);
        Assert.Equal(0.10, g.Stops[0].Position!.Value, precision: 4);
        Assert.Equal(0.50, g.Stops[1].Position!.Value, precision: 4);
        Assert.Equal(0.90, g.Stops[2].Position!.Value, precision: 4);
    }

    [Fact]
    public void Function_colors_with_internal_commas_stay_one_stop()
    {
        var g = CssLinearGradient_Parser.TryParse("linear-gradient(to right, rgb(255, 0, 0), rgb(0, 0, 255))");
        Assert.NotNull(g);
        Assert.Equal(2, g!.Stops.Count);
        Assert.Equal("rgb(255, 0, 0)", g.Stops[0].ColorRaw);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("url(bg.png)")]
    [InlineData("radial-gradient(red, blue)")]
    [InlineData("linear-gradient(red)")]                 // < 2 stops
    [InlineData("linear-gradient(red 2em, blue)")]       // font-relative stop (no context here)
    [InlineData("linear-gradient(red 50vw, blue)")]      // viewport-relative stop (no context here)
    public void Unsupported_forms_return_null(string value)
    {
        Assert.Null(CssLinearGradient_Parser.TryParse(value));
    }

    [Fact]
    public void Repeating_linear_gradient_sets_the_repeating_flag()
    {
        var plain = CssLinearGradient_Parser.TryParse("linear-gradient(to right, red, blue 20px)");
        Assert.NotNull(plain);
        Assert.False(plain!.Repeating);

        var rep = CssLinearGradient_Parser.TryParse("repeating-linear-gradient(to right, red, blue 20px)");
        Assert.NotNull(rep);
        Assert.True(rep!.Repeating);
        Assert.Equal(2, rep.Stops.Count);
        Assert.Equal(20.0, rep.Stops[1].PositionPx!.Value, precision: 4);
    }

    [Theory]
    [InlineData("linear-gradient(red 20px, blue)", 20.0)] // px
    [InlineData("linear-gradient(red 1in, blue)", 96.0)]  // absolute unit → CSS px
    [InlineData("linear-gradient(red -50px, blue 150px)", -50.0)] // negative (out of range) — kept raw
    public void Length_positioned_stops_carry_a_px_position(string value, double expectedPx)
    {
        var g = CssLinearGradient_Parser.TryParse(value);
        Assert.NotNull(g);
        Assert.Null(g!.Stops[0].Position);                 // not a fraction
        Assert.Equal(expectedPx, g.Stops[0].PositionPx!.Value, precision: 4);
    }

    [Theory]
    // A multi-layer background-image list must NOT mis-terminate on a later layer's `)` and parse
    // as one gradient — it falls through to the unsupported-form path (PR #209 Copilot).
    [InlineData("linear-gradient(red, blue), url(bg.png)")]
    [InlineData("linear-gradient(red, blue), linear-gradient(lime, yellow)")]
    [InlineData("url(bg.png), linear-gradient(red, blue)")]
    [InlineData("linear-gradient(red, blue) extra")]     // trailing junk after the function
    public void Multi_layer_lists_are_rejected(string value)
    {
        Assert.Null(CssLinearGradient_Parser.TryParse(value));
    }
}
