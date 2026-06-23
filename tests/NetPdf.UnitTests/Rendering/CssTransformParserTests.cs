// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 transforms — unit tests for <see cref="CssTransform_Parser"/> (function → 2D
/// matrix, composition, 3D flattening, rejection), <see cref="CssTransformOrigin_Parser"/>, and the
/// CSS→PDF <c>cm</c> math (<see cref="CssTransform_Parser.ToPdfMatrix"/>).</summary>
public sealed class CssTransformParserTests
{
    [Fact]
    public void Translate_scale_skew_matrix_reduce_to_the_expected_2d_matrix()
    {
        var tr = CssTransform_Parser.TryParse("translate(10px, 20px)")!;
        Assert.Equal((1.0, 0.0, 0.0, 1.0, 10.0, 20.0), (tr.A, tr.B, tr.C, tr.D, tr.E, tr.F));
        Assert.False(tr.Had3D);

        var sc = CssTransform_Parser.TryParse("scale(2, 3)")!;
        Assert.Equal((2.0, 0.0, 0.0, 3.0, 0.0, 0.0), (sc.A, sc.B, sc.C, sc.D, sc.E, sc.F));

        var skx = CssTransform_Parser.TryParse("skewX(45deg)")!;
        Assert.Equal(1.0, Math.Tan(Math.PI / 4), 6);
        Assert.Equal(Math.Tan(Math.PI / 4), skx.C, 6); // skewX → C = tan(angle)

        var mx = CssTransform_Parser.TryParse("matrix(1, 2, 3, 4, 5, 6)")!;
        Assert.Equal((1.0, 2.0, 3.0, 4.0, 5.0, 6.0), (mx.A, mx.B, mx.C, mx.D, mx.E, mx.F));
    }

    [Fact]
    public void Rotate_is_the_cos_sin_matrix()
    {
        var r = CssTransform_Parser.TryParse("rotate(90deg)")!;
        Assert.Equal(0.0, r.A, 6);
        Assert.Equal(1.0, r.B, 6);
        Assert.Equal(-1.0, r.C, 6);
        Assert.Equal(0.0, r.D, 6);
    }

    [Fact]
    public void Function_list_composes_left_to_right()
    {
        // translate(10px,0) scale(2): scale applied first, then translate ⇒ [2 0 0 2 10 0].
        var t = CssTransform_Parser.TryParse("translate(10px, 0) scale(2)")!;
        Assert.Equal((2.0, 0.0, 0.0, 2.0, 10.0, 0.0), (t.A, t.B, t.C, t.D, t.E, t.F));
    }

    [Fact]
    public void Three_d_functions_flatten_and_flag_had3d()
    {
        var rx = CssTransform_Parser.TryParse("rotateX(45deg)")!;
        Assert.True(rx.Had3D);
        Assert.True(rx.IsIdentity); // genuinely-3D → flattened to identity

        var t3 = CssTransform_Parser.TryParse("translate3d(10px, 20px, 30px)")!;
        Assert.True(t3.Had3D);
        Assert.Equal((1.0, 0.0, 0.0, 1.0, 10.0, 20.0), (t3.A, t3.B, t3.C, t3.D, t3.E, t3.F)); // z dropped
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("wobble(1)")]        // unknown function
    [InlineData("translate(2em)")]   // unresolvable unit
    [InlineData("rotate(2px)")]      // a length where an angle is required
    [InlineData("matrix(1, 2, 3)")]  // wrong arg count
    public void Unsupported_or_empty_forms_return_null(string value)
    {
        Assert.Null(CssTransform_Parser.TryParse(value));
    }

    [Theory]
    // Non-finite numbers must REJECT (not reach PDF emission and throw) — PR #210 review [P2].
    [InlineData("translate(NaNpx)")]
    [InlineData("translate(Infinitypx, 0)")]
    [InlineData("rotate(Infinitydeg)")]
    [InlineData("scale(NaN)")]
    [InlineData("matrix(1, 0, 0, 1, 1e400, 0)")] // overflowing exponent → +Infinity
    public void Non_finite_numbers_reject(string value)
    {
        Assert.Null(CssTransform_Parser.TryParse(value));
    }

    [Theory]
    // Every CSS zero form is valid (not just exact "0") — PR #210 review [P3].
    [InlineData("translate(0, 0)")]
    [InlineData("translate(0.0, +0)")]
    [InlineData("translate(-0, 0px)")]
    [InlineData("rotate(0)")]   // unitless zero angle
    public void Unitless_zero_forms_parse(string value)
    {
        Assert.NotNull(CssTransform_Parser.TryParse(value));
    }

    [Theory]
    // A duplicate-axis or misordered transform-origin defaults to center (PR #210 review [P2]).
    [InlineData("left right")]
    [InlineData("top bottom")]
    [InlineData("25% left")]
    [InlineData("top 25%")]
    [InlineData("left top center")] // invalid 3rd token (not a z-length)
    public void Transform_origin_invalid_axes_default_to_center(string value)
    {
        Assert.Equal(TransformOrigin.Center, CssTransformOrigin_Parser.Parse(value));
    }

    [Fact]
    public void Transform_origin_valid_z_length_is_ignored()
    {
        var o = CssTransformOrigin_Parser.Parse("left top 10px"); // z = 10px, ignored
        Assert.Equal(0.0, o.XFraction, 4);
        Assert.Equal(0.0, o.YFraction, 4);
    }

    [Theory]
    [InlineData("left top", 0.0, 0.0, 0.0, 0.0)]
    [InlineData("top left", 0.0, 0.0, 0.0, 0.0)]   // keyword order-independent
    [InlineData("center", 0.5, 0.0, 0.5, 0.0)]
    [InlineData("25% 75%", 0.25, 0.0, 0.75, 0.0)]
    [InlineData("10px 20px", 0.0, 10.0, 0.0, 20.0)]
    [InlineData("right bottom", 1.0, 0.0, 1.0, 0.0)]
    public void Transform_origin_parses_to_fraction_plus_px(
        string value, double xf, double xp, double yf, double yp)
    {
        var o = CssTransformOrigin_Parser.Parse(value);
        Assert.Equal(xf, o.XFraction, 4);
        Assert.Equal(xp, o.XPx, 4);
        Assert.Equal(yf, o.YFraction, 4);
        Assert.Equal(yp, o.YPx, 4);
    }

    [Fact]
    public void Transform_origin_default_is_center()
    {
        var o = CssTransformOrigin_Parser.Parse(null);
        Assert.Equal(TransformOrigin.Center, o);
    }

    [Fact]
    public void ToPdfMatrix_translate_is_position_independent_and_y_flips()
    {
        // translate(20px, 30px): E = +PxToPt(20) = 15; F = -PxToPt(30) = -22.5 (CSS down → PDF down).
        var t = new CssTransform(1, 0, 0, 1, 20, 30, false);
        var cm = CssTransform_Parser.ToPdfMatrix(t, TransformOrigin.Center, 0, 0, 100, 100, 800);
        Assert.Equal((1.0, 0.0, 0.0, 1.0, 15.0, -22.5), cm);
    }

    [Fact]
    public void ToPdfMatrix_keeps_the_transform_origin_fixed_for_scale_and_rotate()
    {
        // A 100×100 box at the page top-left, origin = center (50, 50 px), page 800 pt tall.
        // The origin point in PDF space is (PxToPt(50), 800 - PxToPt(50)) = (37.5, 762.5).
        const double ox = 37.5, oy = 762.5;

        var scale = CssTransform_Parser.ToPdfMatrix(new CssTransform(2, 0, 0, 2, 0, 0, false),
            TransformOrigin.Center, 0, 0, 100, 100, 800);
        AssertFixedPoint(scale, ox, oy);

        var rotate = CssTransform_Parser.ToPdfMatrix(new CssTransform(0, 1, -1, 0, 0, 0, false),
            TransformOrigin.Center, 0, 0, 100, 100, 800);
        AssertFixedPoint(rotate, ox, oy);

        static void AssertFixedPoint(
            (double A, double B, double C, double D, double E, double F) cm, double x, double y)
        {
            var x2 = cm.A * x + cm.C * y + cm.E;
            var y2 = cm.B * x + cm.D * y + cm.F;
            Assert.Equal(x, x2, 4);
            Assert.Equal(y, y2, 4);
        }
    }
}
