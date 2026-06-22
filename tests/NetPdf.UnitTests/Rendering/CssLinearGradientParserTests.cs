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
    [InlineData("linear-gradient(red 20px, blue)")]      // length-positioned stop (deferred)
    [InlineData("repeating-linear-gradient(red, blue)")] // repeating (deferred)
    public void Unsupported_forms_return_null(string value)
    {
        Assert.Null(CssLinearGradient_Parser.TryParse(value));
    }
}
