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
    public void Polygon_nonzero_is_the_default_fill_rule()
    {
        var c = CssClipPath_Parser.TryParse("polygon(0 0, 10px 0, 5px 10px)");
        Assert.False(c!.EvenOdd);
    }

    [Fact]
    public void Polygon_evenodd_fill_rule_is_honored()
    {
        var c = CssClipPath_Parser.TryParse("polygon(evenodd, 0 0, 10px 0, 5px 10px)");
        Assert.Equal(ClipShapeKind.Polygon, c!.Kind);
        Assert.True(c.EvenOdd);
        Assert.Equal(3, c.Points!.Length);
    }

    [Fact]
    public void Circle_closest_side_keyword_radius_parses()
    {
        var c = CssClipPath_Parser.TryParse("circle(closest-side at center)");
        Assert.Equal(ClipShapeKind.Circle, c!.Kind);
        Assert.Equal(ClipRadiusExtent.ClosestSide, c.RadiusExtent);
    }

    [Fact]
    public void Circle_farthest_side_keyword_radius_parses()
    {
        var c = CssClipPath_Parser.TryParse("circle(farthest-side at center)");
        Assert.Equal(ClipRadiusExtent.FarthestSide, c!.RadiusExtent);
    }

    [Fact]
    public void Ellipse_per_axis_side_keywords_parse()
    {
        var c = CssClipPath_Parser.TryParse("ellipse(closest-side farthest-side)");
        Assert.Equal(ClipShapeKind.Ellipse, c!.Kind);
        Assert.Equal(ClipRadiusExtent.ClosestSide, c.RxExtent);
        Assert.Equal(ClipRadiusExtent.FarthestSide, c.RyExtent);
    }

    [Fact]
    public void Omitted_circle_radius_defaults_to_closest_side()
    {
        var c = CssClipPath_Parser.TryParse("circle()");
        Assert.Equal(ClipRadiusExtent.ClosestSide, c!.RadiusExtent);
    }

    [Theory]
    [InlineData("circle(closest-corner)")]   // corner keywords are radial-gradient-only, invalid for basic shapes
    [InlineData("ellipse(farthest-corner closest-side)")]
    public void Corner_radius_keywords_are_rejected(string value)
    {
        Assert.Null(CssClipPath_Parser.TryParse(value));
    }

    [Fact]
    public void Path_keeps_the_svg_data()
    {
        var c = CssClipPath_Parser.TryParse("path(\"M0 0 L10 10 Z\")");
        Assert.Equal(ClipShapeKind.Path, c!.Kind);
        Assert.Equal("M0 0 L10 10 Z", c.PathData);
        Assert.False(c.EvenOdd);                    // default fill-rule is nonzero
    }

    [Fact]
    public void Path_parses_the_optional_fill_rule()
    {
        var eo = CssClipPath_Parser.TryParse("path(evenodd, \"M0 0 L10 10 Z\")");
        Assert.Equal("M0 0 L10 10 Z", eo!.PathData);
        Assert.True(eo.EvenOdd);

        var nz = CssClipPath_Parser.TryParse("path(nonzero, 'M0 0 L10 10 Z')");
        Assert.False(nz!.EvenOdd);

        Assert.Null(CssClipPath_Parser.TryParse("path(junk, 'M0 0 Z')"));   // unknown fill-rule → invalid
    }

    [Fact]
    public void Path_rejects_a_missing_comma_and_trailing_garbage()
    {
        // PR-234 review [P3] — a fill-rule REQUIRES the comma; nothing may follow the closing quote.
        Assert.Null(CssClipPath_Parser.TryParse("path(evenodd \"M0 0 Z\")"));   // no comma after the fill-rule
        Assert.Null(CssClipPath_Parser.TryParse("path('M0 0 Z' extra)"));      // trailing garbage
        Assert.NotNull(CssClipPath_Parser.TryParse("path('M0 0 Z'  )"));       // trailing whitespace is OK
    }

    [Fact]
    public void Inset_round_parses_the_slash_form_into_separate_x_and_y_radii()
    {
        // PR-234 review [P2] — inset(... round <x>{1,4} / <y>{1,4}) gives separate corner X / Y radii.
        var c = CssClipPath_Parser.TryParse("inset(10px round 20px / 40px)");
        Assert.NotNull(c);
        Assert.Equal(ClipShapeKind.Inset, c!.Kind);
        Assert.NotNull(c.Radii);
        Assert.NotNull(c.RadiiY);
        Assert.Equal(20.0, c.Radii![0].Px, precision: 3);   // X radius
        Assert.Equal(40.0, c.RadiiY![0].Px, precision: 3);  // Y radius
        Assert.Equal(20.0, c.Radii![2].Px, precision: 3);   // 1-value shorthand → all corners
        Assert.Equal(40.0, c.RadiiY![2].Px, precision: 3);
    }

    [Fact]
    public void Inset_round_without_slash_leaves_radii_y_null()
    {
        var c = CssClipPath_Parser.TryParse("inset(10px round 8px)");
        Assert.NotNull(c!.Radii);
        Assert.Null(c.RadiiY);                              // X used for both axes
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
