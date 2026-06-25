// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients (PR 1 refinements) — unit tests for
/// <see cref="CssConicGradient_Parser"/>: the optional <c>from &lt;angle&gt;</c> / <c>at
/// &lt;position&gt;</c> prelude, angular color stops (deg/grad/rad/turn/%), and the
/// <c>repeating-</c> prefix.</summary>
public sealed class CssConicGradientParserTests
{
    [Fact]
    public void Bare_stops_default_from_zero_centered_not_repeating()
    {
        var g = CssConicGradient_Parser.TryParse("conic-gradient(red, blue)");
        Assert.NotNull(g);
        Assert.Equal(0.0, g!.FromAngleDeg, precision: 4);
        Assert.Equal(0.5, g.CenterXFraction, precision: 4);
        Assert.Equal(0.5, g.CenterYFraction, precision: 4);
        Assert.False(g.Repeating);
        Assert.Equal(2, g.Stops.Count);
    }

    [Fact]
    public void From_angle_and_at_position_prelude_parse()
    {
        var g = CssConicGradient_Parser.TryParse("conic-gradient(from 45deg at 30% 70%, red, lime, blue)");
        Assert.NotNull(g);
        Assert.Equal(45.0, g!.FromAngleDeg, precision: 4);
        Assert.Equal(0.30, g.CenterXFraction, precision: 4);
        Assert.Equal(0.70, g.CenterYFraction, precision: 4);
        Assert.Equal(3, g.Stops.Count);
    }

    [Fact]
    public void Lone_at_position_without_from_parses()
    {
        var g = CssConicGradient_Parser.TryParse("conic-gradient(at top left, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(0.0, g!.FromAngleDeg, precision: 4);
        Assert.Equal(0.0, g.CenterXFraction, precision: 4);
        Assert.Equal(0.0, g.CenterYFraction, precision: 4);
    }

    [Fact]
    public void Angular_stop_positions_normalize_to_turn_fractions()
    {
        var g = CssConicGradient_Parser.TryParse("conic-gradient(red 0deg, lime 90deg, blue 0.75turn)");
        Assert.NotNull(g);
        Assert.Equal(3, g!.Stops.Count);
        Assert.Equal(0.0, g.Stops[0].Position!.Value, precision: 4);
        Assert.Equal(0.25, g.Stops[1].Position!.Value, precision: 4); // 90deg / 360
        Assert.Equal(0.75, g.Stops[2].Position!.Value, precision: 4); // 0.75turn
    }

    [Fact]
    public void Percentage_stop_positions_parse()
    {
        var g = CssConicGradient_Parser.TryParse("conic-gradient(red 0%, blue 50%)");
        Assert.NotNull(g);
        Assert.Equal(0.0, g!.Stops[0].Position!.Value, precision: 4);
        Assert.Equal(0.5, g.Stops[1].Position!.Value, precision: 4);
    }

    [Fact]
    public void Repeating_prefix_is_recognized()
    {
        var g = CssConicGradient_Parser.TryParse("repeating-conic-gradient(red 0deg, blue 30deg)");
        Assert.NotNull(g);
        Assert.True(g!.Repeating);
        Assert.Equal(2, g.Stops.Count);
        Assert.Equal(30.0 / 360.0, g.Stops[1].Position!.Value, precision: 5);
    }

    [Fact]
    public void Function_colors_keep_inner_commas()
    {
        var g = CssConicGradient_Parser.TryParse("conic-gradient(rgb(255, 0, 0) 0deg, rgb(0, 0, 255) 180deg)");
        Assert.NotNull(g);
        Assert.Equal(2, g!.Stops.Count);
        Assert.Equal("rgb(255, 0, 0)", g.Stops[0].ColorRaw);
        Assert.Equal(0.5, g.Stops[1].Position!.Value, precision: 4); // 180deg
    }

    [Theory]
    [InlineData("linear-gradient(red, blue)")]        // not a conic
    [InlineData("conic-gradient(red)")]               // need ≥ 2 stops
    [InlineData("conic-gradient(from bogus, red, blue)")] // malformed from-angle
    [InlineData("conic-gradient(at nonsense pos, red, blue)")] // malformed position
    [InlineData("conic-gradient(red, blue), url(x.png)")] // multi-layer
    [InlineData("none")]
    public void Unsupported_or_non_conic_values_return_null(string value)
    {
        Assert.Null(CssConicGradient_Parser.TryParse(value));
    }
}
