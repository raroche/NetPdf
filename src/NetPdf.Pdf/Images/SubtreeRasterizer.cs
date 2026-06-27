// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>Phase 4 subtree renderer (PR 5) — the reusable Skia raster TARGET: render an arbitrary draw
/// callback onto a transparent RGBA <see cref="SKCanvas"/> of a given pixel size and return it as a
/// <see cref="RasterImageInfo"/> (RGBA, ready for <see cref="RasterImageXObject"/> → an image XObject + an
/// alpha <c>/SMask</c>). This is the foundation the deferred "Skia subtree renderer" needs — anything PDF
/// can't draw natively (an SVG document, a general element's filtered / masked subtree, an isolated
/// blend-group) can be rasterized through this single bridge instead of each feature reinventing the
/// SKSurface plumbing (cf. <see cref="ShadowRasterizer"/> / <see cref="ImageFilterApplier"/> /
/// <see cref="ImageMaskApplier"/>). The same raster caps apply.</summary>
internal static class SubtreeRasterizer
{
    /// <summary>Render <paramref name="draw"/> onto a transparent <paramref name="width"/> ×
    /// <paramref name="height"/> RGBA canvas. Returns <see langword="null"/> when the size is non-positive
    /// or exceeds the raster caps. The canvas Y axis is top-down (CSS / device space).</summary>
    public static RasterImageInfo? Render(int width, int height, Action<SKCanvas> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (width <= 0 || height <= 0) return null;
        if (width > ShadowRasterizer.MaxDeviceDimension || height > ShadowRasterizer.MaxDeviceDimension) return null;
        if ((long)width * height > ShadowRasterizer.MaxDevicePixels) return null;

        var imageInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        try { draw(canvas); }
        catch (Exception) { return null; } // a malformed input must not crash the render
        canvas.Flush();

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var width4 = width * 4;
        var pixels = new byte[width4 * height];
        var source = pixmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            source.Slice(y * rowBytes, width4).CopyTo(pixels.AsSpan(y * width4));

        return new RasterImageInfo { Width = width, Height = height, HasAlpha = true, PixelBytes = pixels };
    }

    /// <summary>Encode a rasterized <see cref="RasterImageInfo"/> (RGBA unpremul) to PNG bytes. Used so a
    /// non-codec source (an SVG render) can store DECODABLE bytes as its <c>Entry.SourceBytes</c> — the
    /// CSS <c>filter</c> / <c>mask</c> appliers re-decode those bytes via <see cref="SKCodec"/>, which can't
    /// read SVG XML but can read this PNG (PR-230 review [P2]). Returns <see langword="null"/> on failure.</summary>
    public static byte[]? EncodePng(RasterImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var imageInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(imageInfo, info.PixelBytes);
        if (image is null) return null;
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
