// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using SkiaSharp;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>Phase 4 subtree renderer (PR 5) — unit tests for <see cref="SubtreeRasterizer"/>: the reusable
/// SKCanvas → RGBA <see cref="RasterImageInfo"/> bridge (the foundation for SVG + the deferred general
/// element subtree filters / masks / blend groups).</summary>
public sealed class SubtreeRasterizerTests
{
    [Fact]
    public void Renders_a_draw_callback_to_rgba_pixels()
    {
        var info = SubtreeRasterizer.Render(10, 10, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(0, 0, 10, 10), paint);
        });
        Assert.NotNull(info);
        Assert.Equal(10, info!.Width);
        Assert.Equal(10, info.Height);
        Assert.True(info.HasAlpha);
        var idx = (5 * 10 + 5) * 4;
        Assert.True(info.PixelBytes[idx] > 200);     // R
        Assert.True(info.PixelBytes[idx + 3] > 200); // A (opaque where drawn)
    }

    [Fact]
    public void Transparent_where_nothing_is_drawn()
    {
        var info = SubtreeRasterizer.Render(8, 8, _ => { /* draw nothing */ });
        Assert.NotNull(info);
        Assert.Equal(0, info!.PixelBytes[3]); // alpha 0 at (0,0)
    }

    [Fact]
    public void Rejects_nonpositive_and_over_cap_sizes()
    {
        Assert.Null(SubtreeRasterizer.Render(0, 10, _ => { }));
        Assert.Null(SubtreeRasterizer.Render(10, -1, _ => { }));
        Assert.Null(SubtreeRasterizer.Render(ShadowRasterizer.MaxDeviceDimension + 1, 10, _ => { }));
    }

    [Fact]
    public void A_throwing_draw_callback_yields_null_not_a_crash()
    {
        Assert.Null(SubtreeRasterizer.Render(10, 10, _ => throw new System.InvalidOperationException()));
    }
}
