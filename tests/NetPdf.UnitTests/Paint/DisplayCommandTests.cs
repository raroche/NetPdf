// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using NetPdf.Paint;
using Xunit;

namespace NetPdf.UnitTests.Paint;

public sealed class DisplayCommandTests
{
    // ───── Layout ─────────────────────────────────────────────────────────────

    [Fact]
    public void DisplayCommand_size_is_64_bytes()
    {
        Assert.Equal(64, Marshal.SizeOf<DisplayCommand>());
    }

    [Fact]
    public void Default_DisplayCommand_has_kind_None()
    {
        var cmd = default(DisplayCommand);
        Assert.Equal(DisplayCommandKind.None, cmd.Kind);
    }

    // ───── RectFill ───────────────────────────────────────────────────────────

    [Fact]
    public void RectFill_round_trips_through_AsRectFill()
    {
        var color = new RgbaColor(10, 20, 30, 40);
        var cmd = DisplayCommand.RectFill(1.5, 2.5, 100, 50, color);

        Assert.Equal(DisplayCommandKind.RectFill, cmd.Kind);
        var p = cmd.AsRectFill();
        Assert.Equal(1.5, p.X);
        Assert.Equal(2.5, p.Y);
        Assert.Equal(100, p.Width);
        Assert.Equal(50, p.Height);
        Assert.Equal(color, p.Color);
    }

    [Fact]
    public void RectFill_AsRectFill_throws_when_kind_mismatched()
    {
        var cmd = DisplayCommand.OpacityPop();
        Assert.Throws<InvalidOperationException>(() => cmd.AsRectFill());
    }

    // ───── TextRun ────────────────────────────────────────────────────────────

    [Fact]
    public void TextRun_round_trips_through_AsTextRun()
    {
        var cmd = DisplayCommand.TextRun(textRunIndex: 7, x: 12.5, y: 36.25);

        Assert.Equal(DisplayCommandKind.TextRun, cmd.Kind);
        var p = cmd.AsTextRun();
        Assert.Equal(7, p.TextRunIndex);
        Assert.Equal(12.5, p.X);
        Assert.Equal(36.25, p.Y);
    }

    [Fact]
    public void TextRun_factory_rejects_negative_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.TextRun(-1, 0, 0));
    }

    // ───── ImageDraw ──────────────────────────────────────────────────────────

    [Fact]
    public void ImageDraw_round_trips_through_AsImageDraw()
    {
        var cmd = DisplayCommand.ImageDraw(imageIndex: 3, x: 10, y: 20, width: 200, height: 150);

        Assert.Equal(DisplayCommandKind.ImageDraw, cmd.Kind);
        var p = cmd.AsImageDraw();
        Assert.Equal(3, p.ImageIndex);
        Assert.Equal(10, p.X);
        Assert.Equal(20, p.Y);
        Assert.Equal(200, p.Width);
        Assert.Equal(150, p.Height);
    }

    [Fact]
    public void ImageDraw_factory_rejects_negative_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.ImageDraw(-1, 0, 0, 1, 1));
    }

    // ───── TransformPush / TransformPop ───────────────────────────────────────

    [Fact]
    public void TransformPush_round_trips_through_AsTransformPush()
    {
        var cmd = DisplayCommand.TransformPush(1, 2, 3, 4, 5, 6);

        Assert.Equal(DisplayCommandKind.TransformPush, cmd.Kind);
        var p = cmd.AsTransformPush();
        Assert.Equal(1, p.A);
        Assert.Equal(2, p.B);
        Assert.Equal(3, p.C);
        Assert.Equal(4, p.D);
        Assert.Equal(5, p.E);
        Assert.Equal(6, p.F);
    }

    [Fact]
    public void TransformPop_has_no_payload()
    {
        var cmd = DisplayCommand.TransformPop();
        Assert.Equal(DisplayCommandKind.TransformPop, cmd.Kind);
    }

    // ───── OpacityPush / OpacityPop ───────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void OpacityPush_accepts_values_in_unit_interval(double alpha)
    {
        var cmd = DisplayCommand.OpacityPush(alpha);
        Assert.Equal(DisplayCommandKind.OpacityPush, cmd.Kind);
        Assert.Equal(alpha, cmd.AsOpacityPush().Alpha);
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(-1)]
    [InlineData(1.0001)]
    [InlineData(2)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void OpacityPush_rejects_out_of_range_alpha(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCommand.OpacityPush(bad));
    }

    [Fact]
    public void OpacityPop_has_no_payload()
    {
        var cmd = DisplayCommand.OpacityPop();
        Assert.Equal(DisplayCommandKind.OpacityPop, cmd.Kind);
    }

    // ───── Equality ───────────────────────────────────────────────────────────

    [Fact]
    public void Identical_RectFill_commands_are_equal()
    {
        var a = DisplayCommand.RectFill(1, 2, 3, 4, new RgbaColor(10, 20, 30));
        var b = DisplayCommand.RectFill(1, 2, 3, 4, new RgbaColor(10, 20, 30));
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_kinds_compare_unequal_even_with_overlapping_payload_bytes()
    {
        // OpacityPush(0) and TransformPop() both have Alpha == 0 / no-payload zero bytes.
        // They must still differ on Kind.
        var a = DisplayCommand.OpacityPush(0);
        var b = DisplayCommand.TransformPop();
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void Different_RectFill_payloads_compare_unequal()
    {
        var a = DisplayCommand.RectFill(1, 2, 3, 4, RgbaColor.Black);
        var b = DisplayCommand.RectFill(1, 2, 3, 4, RgbaColor.White);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Same_kind_clears_old_payload_bytes_on_default_init()
    {
        // After RectFill(1,2,3,4,Black), build a TransformPop. Its overlapping bytes must
        // be zero — i.e., a TransformPop never compares equal to a TransformPop just
        // because of leftover payload bits.
        var x = DisplayCommand.TransformPop();
        var y = DisplayCommand.TransformPop();
        Assert.Equal(x, y);
    }
}
