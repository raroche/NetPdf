// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>
/// Phase 4 shadows — the Skia raster fallback for a BLURRED <c>text-shadow</c> (PDF has no native
/// Gaussian-blur primitive, so a blurred shadow can't be a glyph-show in the shadow color). Builds an
/// <see cref="SKTypeface"/> from the SAME font-program bytes the PDF embeds (so the glyph ids match
/// HarfBuzz's shaping), unions the run's glyph OUTLINES at their natural advances, applies a Gaussian
/// blur, and wraps the unpremultiplied RGBA8888 result as a PDF Image XObject (RGB plane + alpha
/// <c>/SMask</c>) via <see cref="RasterImageXObject"/>. The caller places it UNDER the text at the
/// shadow offset.
/// </summary>
/// <remarks>Deterministic, like <see cref="ShadowRasterizer"/>: a CPU raster surface (no GPU context)
/// + a pure Gaussian blur of the same glyph outlines yields the same bytes on a given platform
/// (CLAUDE.md #4). The bitmap shares <see cref="ShadowRasterizer"/>'s per-side + total-pixel caps; an
/// oversize run returns <see langword="null"/> so the caller falls back to a SHARP offset shadow rather
/// than emit a huge stream.</remarks>
internal static class TextShadowRasterizer
{
    /// <summary>Rasterize a blurred text-shadow for the glyph run <paramref name="glyphIds"/> of the
    /// font <paramref name="fontBytes"/> at <paramref name="fontSizePx"/> (CSS px). The geometry is
    /// rastered at <paramref name="scale"/>× device resolution; <paramref name="blurPx"/> is the CSS
    /// blur radius (σ = blur/2, the same convention as <see cref="ShadowRasterizer"/>). Color channels
    /// are [0, 1]. On success returns the image and, via the out params, the top-left of the raster
    /// RELATIVE to the glyph run's baseline origin, plus its size — all in CSS px (the caller adds the
    /// shadow offset + the run origin). Returns <see langword="null"/> (and leaves the out params 0) for
    /// an empty/whitespace run, an unreadable font, or an over-cap bitmap.</summary>
    public static ImageXObjectResult? TryRasterizeGlyphRun(
        ReadOnlyMemory<byte> fontBytes, ReadOnlySpan<ushort> glyphIds,
        float fontSizePx, float blurPx, double r, double g, double b, double a, double scale,
        out double destOffsetXPx, out double destOffsetYPx, out double destWidthPx, out double destHeightPx)
    {
        destOffsetXPx = destOffsetYPx = destWidthPx = destHeightPx = 0;
        if (glyphIds.Length == 0 || fontSizePx <= 0 || scale <= 0 || a <= 0) return null;

        using var data = SKData.CreateCopy(fontBytes.ToArray());
        using var typeface = SKTypeface.FromData(data);
        if (typeface is null) return null;

        var deviceFontSize = (float)(fontSizePx * scale);
        using var font = new SKFont(typeface, deviceFontSize);

        // Glyph advances (device px) come from the same hmtx the PDF embeds, so the rastered run lines
        // up with the sharp ShowGlyphs text.
        var glyphArray = glyphIds.ToArray();
        var widths = font.GetGlyphWidths(glyphArray);

        // Union the glyph outlines at their cumulative advances. SKFont.GetGlyphPath returns the
        // outline at the font size with the baseline at y = 0 (Skia y-down, so ink above the baseline
        // is negative-y), the glyph at x = 0.
        using var fullPath = new SKPath();
        var penX = 0f;
        for (var i = 0; i < glyphArray.Length; i++)
        {
            using var glyphPath = font.GetGlyphPath(glyphArray[i]);
            if (glyphPath is not null && !glyphPath.IsEmpty)
            {
                using var placed = new SKPath();
                glyphPath.Transform(SKMatrix.CreateTranslation(penX, 0), placed);
                fullPath.AddPath(placed);
            }
            if (i < widths.Length) penX += widths[i];
        }
        if (fullPath.IsEmpty) return null; // an all-whitespace run paints no shadow.

        var ink = fullPath.TightBounds;
        var sigmaDevice = (float)(blurPx / 2.0 * scale);
        var marginDevice = (float)Math.Ceiling(3.0 * sigmaDevice);

        var deviceWidth = (int)Math.Ceiling(ink.Width + 2 * marginDevice);
        var deviceHeight = (int)Math.Ceiling(ink.Height + 2 * marginDevice);
        if (deviceWidth <= 0 || deviceHeight <= 0) return null;
        if (deviceWidth > ShadowRasterizer.MaxDeviceDimension
            || deviceHeight > ShadowRasterizer.MaxDeviceDimension) return null;
        if ((long)deviceWidth * deviceHeight > ShadowRasterizer.MaxDevicePixels) return null;

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
        if (sigmaDevice > 0)
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigmaDevice);

        // Translate so the ink bounds' top-left sits at (margin, margin) inside the bitmap.
        canvas.Translate(marginDevice - ink.Left, marginDevice - ink.Top);
        canvas.DrawPath(fullPath, paint);

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
        var result = RasterImageXObject.Build(info);
        if (result is null) return null;

        // The bitmap's top-left in baseline space (device px) is (ink.Left - margin, ink.Top - margin);
        // report it (and the size) in CSS px for the caller to place.
        destOffsetXPx = (ink.Left - marginDevice) / scale;
        destOffsetYPx = (ink.Top - marginDevice) / scale;
        destWidthPx = deviceWidth / scale;
        destHeightPx = deviceHeight / scale;
        return result;
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp(Math.Round(channel * 255.0), 0, 255);
}
