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
    // A multi-layer list must not mis-terminate on a later layer's `)` (PR #209 Copilot).
    [InlineData("radial-gradient(red, blue), url(bg.png)")]
    [InlineData("radial-gradient(red, blue), radial-gradient(lime, yellow)")]
    public void Unsupported_forms_return_null(string value)
    {
        Assert.Null(CssRadialGradient_Parser.TryParse(value));
    }

    [Theory]
    // Two unambiguous keywords may appear in EITHER order (CSS position grammar) — both spellings
    // land on the same center (PR #209 review [P2]).
    [InlineData("at top left", 0.0, 0.0)]
    [InlineData("at left top", 0.0, 0.0)]
    [InlineData("at bottom right", 1.0, 1.0)]
    [InlineData("at right bottom", 1.0, 1.0)]
    [InlineData("at center right", 1.0, 0.5)]
    [InlineData("at right center", 1.0, 0.5)]
    [InlineData("at bottom center", 0.5, 1.0)]
    // A percentage fixes the order: first = horizontal, second = vertical.
    [InlineData("at 25% 75%", 0.25, 0.75)]
    [InlineData("at 30% top", 0.30, 0.0)]
    [InlineData("at left 80%", 0.0, 0.80)]
    public void At_position_keyword_pairs_are_order_independent(string prelude, double expectCx, double expectCy)
    {
        var g = CssRadialGradient_Parser.TryParse($"radial-gradient({prelude}, red, blue)");
        Assert.NotNull(g);
        Assert.Equal(expectCx, g!.CenterXFraction, precision: 4);
        Assert.Equal(expectCy, g.CenterYFraction, precision: 4);
    }

    [Theory]
    // A duplicate-axis pair or a misordered keyword/percentage is REJECTED rather than silently
    // painting the wrong center (PR #209 review [P2] — the whole value falls back to the bg-color).
    [InlineData("at left right")]   // two horizontal edges
    [InlineData("at top bottom")]   // two vertical edges
    [InlineData("at 25% left")]     // percentage horizontal, then a horizontal keyword in the vertical slot
    [InlineData("at top 25%")]      // a vertical keyword in the horizontal slot
    [InlineData("at left left")]    // duplicate same edge
    [InlineData("at 10% 20% 30%")]  // more than two position tokens
    public void At_position_invalid_or_misordered_axes_reject(string prelude)
    {
        Assert.Null(CssRadialGradient_Parser.TryParse($"radial-gradient({prelude}, red, blue)"));
    }
}
