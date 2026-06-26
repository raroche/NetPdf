// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>Phase 4 mask (PR 4) — composite a raster image with a <c>mask-image</c> via Skia and re-embed
/// it as an RGBA XObject + alpha <c>/SMask</c>. The mask's ALPHA channel scales the base image's alpha
/// (CSS Masking L1 §6.1: <c>mask-mode: match-source</c> on an <c>&lt;image&gt;</c> source uses its alpha —
/// luminance masks, which apply to SVG <c>&lt;mask&gt;</c> references, are a documented follow-up). The
/// mask is scaled to the base image's pixel size. Mirrors <see cref="ImageFilterApplier"/>'s raster path.
/// A drawn-on-the-image-box mask means no bounds expansion (unlike a drop-shadow filter).</summary>
internal static class ImageMaskApplier
{
    /// <summary>Build the masked XObject from the base + mask source bytes. Returns <see langword="null"/>
    /// when either image fails the raster caps / decode (the caller then paints the image unmasked).</summary>
    public static ImageXObjectResult? TryApply(byte[] baseBytes, byte[] maskBytes)
    {
        var raster = TryRender(baseBytes, maskBytes);
        return raster is null ? null : RasterImageXObject.Build(raster);
    }

    private static RasterImageInfo? TryRender(byte[] baseBytes, byte[] maskBytes)
    {
        ArgumentNullException.ThrowIfNull(baseBytes);
        ArgumentNullException.ThrowIfNull(maskBytes);

        using var baseDecoded = DecodeWithinCaps(baseBytes);
        if (baseDecoded is null) return null;
        using var maskDecoded = DecodeWithinCaps(maskBytes);
        if (maskDecoded is null) return null;

        var w = baseDecoded.Width;
        var h = baseDecoded.Height;
        var imageInfo = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Draw the base, then multiply its alpha by the mask's alpha (DstIn) — the mask scaled to fill
        // the base's pixel box. Result alpha = baseAlpha × maskAlpha; result RGB = base RGB.
        canvas.DrawBitmap(baseDecoded, 0, 0);
        using (var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.DstIn })
        {
            canvas.DrawBitmap(maskDecoded, new SKRect(0, 0, w, h), paint);
        }

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var width4 = w * 4;
        var pixels = new byte[width4 * h];
        var source = pixmap.GetPixelSpan();
        for (var y = 0; y < h; y++)
            source.Slice(y * rowBytes, width4).CopyTo(pixels.AsSpan(y * width4));

        return new RasterImageInfo { Width = w, Height = h, HasAlpha = true, PixelBytes = pixels };
    }

    /// <summary>Header-preflight the caps (no pixel decode) then decode (PR 227 review [P1] pattern).</summary>
    private static SKBitmap? DecodeWithinCaps(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        SKCodec? codec;
        try { codec = SKCodec.Create(data); }
        catch (Exception) { return null; }
        if (codec is null) return null;
        using var keepCodec = codec;
        var w = codec.Info.Width;
        var h = codec.Info.Height;
        if (w <= 0 || h <= 0) return null;
        if (w > ShadowRasterizer.MaxDeviceDimension || h > ShadowRasterizer.MaxDeviceDimension) return null;
        if ((long)w * h > ShadowRasterizer.MaxDevicePixels) return null;

        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(codec); }
        catch (Exception) { return null; }
        if (decoded is null) return null;
        if (decoded.Width != w || decoded.Height != h) { decoded.Dispose(); return null; }
        return decoded;
    }
}
