// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Phase 4 shadows — the Skia raster fallback for a BLURRED shadow shape (PDF has no native
/// Gaussian-blur primitive). Draws a rounded rectangle in the shadow color into a transparent
/// raster surface, applies a Gaussian blur, and wraps the unpremultiplied RGBA8888 result as a
/// PDF Image XObject (RGB plane + alpha <c>/SMask</c>) via <see cref="RasterImageXObject"/>.
/// </summary>
/// <remarks>Deterministic: a CPU raster surface (no GPU context) + Skia's blur are pure functions
/// of the inputs, so the same shadow produces the same bytes on a given platform (CLAUDE.md #4 —
/// like the existing <see cref="RasterImageDecoder"/> path). The bitmap is capped at
/// <see cref="MaxDeviceDimension"/> px per side; an oversize request returns <see langword="null"/>
/// so the caller can fall back to a sharp native shadow rather than emit a huge stream.</remarks>
internal static class ShadowRasterizer
{
    /// <summary>Per the phase-4 plan: cap the raster at 4096 px on the longest side.</summary>
    internal const int MaxDeviceDimension = 4096;

    /// <summary>Total-pixel cap (PR #223 review [P1]) — reject a raster whose AREA would be large even when
    /// each side is under <see cref="MaxDeviceDimension"/> (e.g. 4000 × 4000 ≈ 16 M px × 4 bytes = 64 MB),
    /// so untrusted HTML can't drive a huge allocation under the per-dimension cap. 4096 × 1024 = 4 Mpx.</summary>
    internal const long MaxDevicePixels = 4096L * 1024L;

    /// <summary>Rasterize a blurred rounded-rect shadow into an Image XObject. All geometry is in
    /// DEVICE pixels (the caller multiplies CSS px by its raster scale): the bitmap is
    /// <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/>; the shadow shape sits at
    /// (<paramref name="shapeLeft"/>, <paramref name="shapeTop"/>) with size
    /// (<paramref name="shapeWidth"/>, <paramref name="shapeHeight"/>) and corner radius
    /// <paramref name="radius"/>; the Gaussian <paramref name="blurSigma"/> is the device-px
    /// standard deviation. Color channels are [0, 1]. Returns <see langword="null"/> for a
    /// non-positive or over-cap bitmap.</summary>
    public static ImageXObjectResult? TryRasterize(
        int deviceWidth, int deviceHeight,
        float shapeLeft, float shapeTop, float shapeWidth, float shapeHeight, float radius,
        float blurSigma, double r, double g, double b, double a)
    {
        if (deviceWidth <= 0 || deviceHeight <= 0) return null;
        if (deviceWidth > MaxDeviceDimension || deviceHeight > MaxDeviceDimension) return null;
        if ((long)deviceWidth * deviceHeight > MaxDevicePixels) return null;   // total-pixel cap (PR #223 [P1])

        var imageInfo = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(ToByte(r), ToByte(g), ToByte(b), ToByte(a)),
        };
        if (blurSigma > 0)
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma);

        var rect = new SKRect(shapeLeft, shapeTop, shapeLeft + shapeWidth, shapeTop + shapeHeight);
        if (radius > 0)
            canvas.DrawRoundRect(rect, radius, radius, paint);
        else
            canvas.DrawRect(rect, paint);

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var width4 = deviceWidth * 4;
        var pixels = new byte[width4 * deviceHeight];
        var source = pixmap.GetPixelSpan();
        for (var y = 0; y < deviceHeight; y++)
            source.Slice(y * rowBytes, width4).CopyTo(pixels.AsSpan(y * width4));

        var info = new RasterImageInfo
        {
            Width = deviceWidth,
            Height = deviceHeight,
            HasAlpha = true,
            PixelBytes = pixels,
        };
        return RasterImageXObject.Build(info);
    }

    /// <summary>Phase 4 shadows (PR 1 refinements) — rasterize a blurred INSET shadow into an Image
    /// XObject. The bitmap is the PADDING box (<paramref name="deviceWidth"/> ×
    /// <paramref name="deviceHeight"/> device px, corner radius <paramref name="paddingRadius"/>),
    /// FILLED with the shadow color, then the inner lit HOLE (offset + spread-contracted, at
    /// (<paramref name="holeLeft"/>, <paramref name="holeTop"/>) size
    /// (<paramref name="holeWidth"/>, <paramref name="holeHeight"/>) radius
    /// <paramref name="holeRadius"/>) is punched out with a Gaussian-blurred <c>DstOut</c> — so the
    /// shadow color forms a soft band around the inside edges (CSS B&amp;B §7.2 inset). Same caps /
    /// determinism contract as <see cref="TryRasterize"/>. A non-positive hole fills the whole padding
    /// box (the shadow swallowed the lit area).</summary>
    public static ImageXObjectResult? TryRasterizeInset(
        int deviceWidth, int deviceHeight, float paddingRadius,
        float holeLeft, float holeTop, float holeWidth, float holeHeight, float holeRadius,
        float blurSigma, double r, double g, double b, double a)
    {
        if (deviceWidth <= 0 || deviceHeight <= 0) return null;
        if (deviceWidth > MaxDeviceDimension || deviceHeight > MaxDeviceDimension) return null;
        if ((long)deviceWidth * deviceHeight > MaxDevicePixels) return null;

        var imageInfo = new SKImageInfo(deviceWidth, deviceHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var paddingRect = new SKRect(0, 0, deviceWidth, deviceHeight);
        var color = new SKColor(ToByte(r), ToByte(g), ToByte(b), ToByte(a));

        // 1) Fill the whole padding box (rounded) with the shadow color.
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color })
        {
            if (paddingRadius > 0) canvas.DrawRoundRect(paddingRect, paddingRadius, paddingRadius, fill);
            else canvas.DrawRect(paddingRect, fill);
        }

        // 2) Punch the lit hole out with a blurred DstOut (dst α ×= 1 − src α): the band stays, the
        // hole clears, and the blurred edge gives the soft inset falloff.
        if (holeWidth > 0 && holeHeight > 0)
        {
            using var cut = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = SKColors.Black,           // any opaque color — only its alpha matters for DstOut
                BlendMode = SKBlendMode.DstOut,
            };
            if (blurSigma > 0) cut.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma);
            var holeRect = new SKRect(holeLeft, holeTop, holeLeft + holeWidth, holeTop + holeHeight);
            if (holeRadius > 0) canvas.DrawRoundRect(holeRect, holeRadius, holeRadius, cut);
            else canvas.DrawRect(holeRect, cut);
        }

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var width4 = deviceWidth * 4;
        var pixels = new byte[width4 * deviceHeight];
        var source = pixmap.GetPixelSpan();
        for (var y = 0; y < deviceHeight; y++)
            source.Slice(y * rowBytes, width4).CopyTo(pixels.AsSpan(y * width4));

        var info = new RasterImageInfo
        {
            Width = deviceWidth,
            Height = deviceHeight,
            HasAlpha = true,
            PixelBytes = pixels,
        };
        return RasterImageXObject.Build(info);
    }

    private static byte ToByte(double channel) =>
        (byte)System.Math.Clamp((int)System.Math.Round(channel * 255.0), 0, 255);
}
