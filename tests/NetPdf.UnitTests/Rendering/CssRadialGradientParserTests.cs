// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients — unit tests for <see cref="CssRadialGradient_Parser"/>: the
/// optional shape / extent / position prelude and the color-stop list.</summary>
public sealed class CssRadialGradientParserTests
{
    [Fact]
    public void Bare_stops_default_to_ellipse_farthest_corner_centered()
    {
        var g = CssRadialGradient_Parser.TryParse("radial-gradient(red, blue)");
        Assert.NotNull(g);
        Assert.False(g!.IsCircle);                       // default ellipse
        Assert.Equal(RadialExtent.FarthestCorner, g.Extent);
        Assert.Equal(0.5, g.CenterXFraction, precision: 4);
        Assert.Equal(0.5, g.CenterYFraction, precision: 4);
        Assert.Equal(2, g.Stops.Count);
    }

    [Fact]
    public void Circle_shape_and_extent_keyword_parse()
    {
        var g = CssRadialGradient_Parser.TryParse("radial-gradient(circle closest-side, red, lime, blue)");
        Assert.NotNull(g);
        Assert.True(g!.IsCircle);
        Assert.Equal(RadialExtent.ClosestSide, g.Extent);
        Assert.Equal(3, g.Stops.Count);
    }

    [Fact]
    public void At_position_keywords_and_percentages_parse()
    {
        var g = CssRadialGradient_Parser.TryParse("radial-gradient(circle at top left, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(0.0, g!.CenterXFraction, precision: 4);
        Assert.Equal(0.0, g.CenterYFraction, precision: 4);

        var p = CssRadialGradient_Parser.TryParse("radial-gradient(at 25% 75%, red, blue)");
        Assert.NotNull(p);
        Assert.Equal(0.25, p!.CenterXFraction, precision: 4);
        Assert.Equal(0.75, p.CenterYFraction, precision: 4);
    }

    [Theory]
    [InlineData("linear-gradient(red, blue)")]
    [InlineData("radial-gradient(red)")]                  // < 2 stops
    [InlineData("repeating-radial-gradient(red, blue)")]  // repeating (deferred)
    [InlineData("url(bg.png)")]
    public void Unsupported_forms_return_null(string value)
    {
        Assert.Null(CssRadialGradient_Parser.TryParse(value));
    }
}
