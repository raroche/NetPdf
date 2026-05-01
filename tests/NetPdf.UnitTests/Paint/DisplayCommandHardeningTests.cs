// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using NetPdf.Paint;
using Xunit;

namespace NetPdf.UnitTests.Paint;

/// <summary>
/// Post-Task-5 hardening: tagged-union invariant, finite-geometry rejection at the IR
/// boundary, and side-table buffer isolation for <see cref="TextRun"/>.
/// </summary>
public sealed class DisplayCommandHardeningTests
{
    // ───── P1: Kind cannot be retagged after construction ─────────────────────

    [Fact]
    public void Kind_property_has_no_public_setter()
    {
        // Reflection-level guarantee that no internal or external consumer can mutate
        // Kind on a constructed DisplayCommand. The factories own the Kind+payload
        // invariant; retagging would leave the overlaid payload misinterpreted.
        var prop = typeof(DisplayCommand).GetProperty(
            "Kind",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.True(prop.CanRead);
        Assert.False(prop.CanWrite);
        Assert.Null(prop.SetMethod);
    }

    // ───── P2: Geometry factories reject NaN / Infinity ───────────────────────

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RectFill_rejects_non_finite_x(double bad)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.RectFill(bad, 0, 1, 1, RgbaColor.Black));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RectFill_rejects_non_finite_y(double bad)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.RectFill(0, bad, 1, 1, RgbaColor.Black));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RectFill_rejects_non_finite_width(double bad)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.RectFill(0, 0, bad, 1, RgbaColor.Black));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RectFill_rejects_non_finite_height(double bad)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.RectFill(0, 0, 1, bad, RgbaColor.Black));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TextRun_factory_rejects_non_finite_position(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TextRun(0, bad, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TextRun(0, 0, bad));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ImageDraw_rejects_non_finite_geometry(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.ImageDraw(0, bad, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.ImageDraw(0, 0, bad, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.ImageDraw(0, 0, 0, bad, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.ImageDraw(0, 0, 0, 1, bad));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TransformPush_rejects_non_finite_in_any_matrix_term(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TransformPush(bad, 0, 0, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TransformPush(1, bad, 0, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TransformPush(1, 0, bad, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TransformPush(1, 0, 0, bad, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TransformPush(1, 0, 0, 1, bad, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TransformPush(1, 0, 0, 1, 0, bad));
    }

    [Fact]
    public void Negative_geometry_values_are_still_accepted()
    {
        // Negative widths/heights are allowed by PDF's `re` operator (degenerate rectangle);
        // negative coordinates are routine. Only NaN/Infinity is rejected.
        var cmd = DisplayCommand.RectFill(-10, -20, -30, -40, RgbaColor.Black);
        Assert.Equal(DisplayCommandKind.RectFill, cmd.Kind);
    }
}
