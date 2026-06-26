// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 clip-path (PR 3) — unit tests for <see cref="CssClipPath_Parser"/>: the basic
/// shapes inset / circle / ellipse / polygon / path and rejection of unsupported values.</summary>
public sealed class CssClipPathParserTests
{
    [Fact]
    public void Inset_one_value_expands_to_four_edges()
    {
        var c = CssClipPath_Parser.TryParse("inset(10px)");
        Assert.NotNull(c);
        Assert.Equal(ClipShapeKind.Inset, c!.Kind);
        Assert.Equal(4, c.Edges!.Length);
        Assert.All(c.Edges, e => Assert.Equal(10.0, e.Px, precision: 4));
        Assert.Null(c.Radii);
    }

    [Fact]
    public void Inset_four_values_plus_round_radii()
    {
        var c = CssClipPath_Parser.TryParse("inset(1px 2px 3px 4px round 5px)");
        Assert.Equal(2.0, c!.Edges![1].Px, precision: 4); // right
        Assert.Equal(4.0, c.Edges![3].Px, precision: 4);  // left
        Assert.NotNull(c.Radii);
        Assert.Equal(5.0, c.Radii![0].Px, precision: 4);
    }

    [Fact]
    public void Circle_with_radius_and_position()
    {
        var c = CssClipPath_Parser.TryParse("circle(40px at 30% 70%)");
        Assert.Equal(ClipShapeKind.Circle, c!.Kind);
        Assert.Equal(40.0, c.Radius.Px, precision: 4);
        Assert.Equal(0.30, c.Cx.Frac, precision: 4);
        Assert.Equal(0.70, c.Cy.Frac, precision: 4);
    }

    [Fact]
    public void Bare_circle_radius_is_omitted_sentinel()
    {
        var c = CssClipPath_Parser.TryParse("circle()");
        Assert.Equal(ClipShapeKind.Circle, c!.Kind);
        Assert.True(double.IsNaN(c.Radius.Frac)); // closest-side
    }

    [Fact]
    public void Ellipse_two_radii()
    {
        var c = CssClipPath_Parser.TryParse("ellipse(30px 50% at center)");
        Assert.Equal(ClipShapeKind.Ellipse, c!.Kind);
        Assert.Equal(30.0, c.Rx.Px, precision: 4);
        Assert.Equal(0.5, c.Ry.Frac, precision: 4);
    }

    [Fact]
    public void Polygon_vertices_parse()
    {
        var c = CssClipPath_Parser.TryParse("polygon(0% 0%, 100% 0%, 50% 100%)");
        Assert.Equal(ClipShapeKind.Polygon, c!.Kind);
        Assert.Equal(3, c.Points!.Length);
        Assert.Equal(1.0, c.Points[1].X.Frac, precision: 4);
        Assert.Equal(1.0, c.Points[2].Y.Frac, precision: 4);
    }

    [Fact]
    public void Path_keeps_the_svg_data()
    {
        var c = CssClipPath_Parser.TryParse("path(\"M0 0 L10 10 Z\")");
        Assert.Equal(ClipShapeKind.Path, c!.Kind);
        Assert.Equal("M0 0 L10 10 Z", c.PathData);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("url(#clip)")]            // SVG reference (out of scope)
    [InlineData("inset(2em)")]            // font-relative (no context)
    [InlineData("polygon(0% 0%)")]        // < 3 vertices
    [InlineData("circle(40px 50px)")]     // two radii on a circle
    public void Unsupported_values_return_null(string value)
    {
        Assert.Null(CssClipPath_Parser.TryParse(value));
    }
}
