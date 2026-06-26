// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace NetPdf.Pdf.Images;

/// <summary>Phase 4 filters (PR 2) — the kind of a single image-filter step. The step's <c>Amount</c>
/// is kind-dependent (a fraction for the proportional kinds, CSS px for <see cref="Blur"/>, degrees
/// for <see cref="HueRotate"/>); the <c>Shadow*</c> fields apply only to <see cref="DropShadow"/>
/// (device-px offsets/blur + a [0,1] DeviceRGBA color).</summary>
internal enum ImageFilterKind { Grayscale, Sepia, Invert, Brightness, Contrast, Saturate, HueRotate, Opacity, Blur, DropShadow }

internal readonly record struct ImageFilterStep(
    ImageFilterKind Kind, double Amount,
    double ShadowDx = 0, double ShadowDy = 0, double ShadowBlur = 0,
    double ShadowR = 0, double ShadowG = 0, double ShadowB = 0, double ShadowA = 1);

/// <summary>Phase 4 filters — the per-side padding a filtered raster adds AROUND the source image
/// (for a drop-shadow's outward extent), as FRACTIONS of the source dimension so the painter can
/// scale them to whatever display size the image is placed at. <see cref="None"/> = no padding.</summary>
internal readonly record struct FilterPadding(float LeftFrac, float TopFrac, float RightFrac, float BottomFrac)
{
    public static readonly FilterPadding None = new(0, 0, 0, 0);

    public bool IsZero => LeftFrac == 0 && TopFrac == 0 && RightFrac == 0 && BottomFrac == 0;
}

/// <summary>Phase 4 filters — a filtered image XObject + its <see cref="FilterPadding"/> (the
/// drop-shadow frame), so the painter places the larger raster with the image CONTENT still aligned
/// to the element's box and the shadow extending outward.</summary>
internal readonly record struct FilteredImageResult(ImageXObjectResult Image, FilterPadding Padding);

/// <summary>
/// Phase 4 filters — applies a CSS <c>filter</c> chain to a decoded raster image via Skia (PDF has
/// no native filter primitive). The image is decoded, drawn through a composed
/// <see cref="SKImageFilter"/> chain (color matrices for the proportional functions, plus blur /
/// drop-shadow), and the unpremultiplied RGBA8888 result is wrapped as an Image XObject (RGB plane +
/// alpha <c>/SMask</c>) via <see cref="RasterImageXObject"/>.
/// </summary>
/// <remarks>Deterministic — a CPU surface + Skia's filters are pure functions of the inputs
/// (CLAUDE.md #4). The color-matrix coefficients are CSS Filter Effects L1 §C.</remarks>
internal static class ImageFilterApplier
{
    /// <summary>Apply <paramref name="steps"/> (in order — the first is applied first) to
    /// <paramref name="imageBytes"/>. Returns <see langword="null"/> for an undecodable / over-cap
    /// image or a step kind not yet implemented. The output keeps the source pixel dimensions
    /// (color filters don't change bounds; blur / drop-shadow render within the same frame for now).</summary>
    public static FilteredImageResult? TryApply(byte[] imageBytes, IReadOnlyList<ImageFilterStep> steps)
    {
        var raster = TryRender(imageBytes, steps, out var padding);
        return raster is null ? null : new FilteredImageResult(RasterImageXObject.Build(raster), padding);
    }

    /// <summary>Apply <paramref name="steps"/> and return the raw filtered RGBA8888 raster (the
    /// pre-XObject form — exercised directly by tests). The raster INCLUDES any drop-shadow padding
    /// frame. Returns <see langword="null"/> for an undecodable / over-cap image or an unimplemented
    /// step kind.</summary>
    public static RasterImageInfo? TryFilterToRaster(byte[] imageBytes, IReadOnlyList<ImageFilterStep> steps) =>
        TryRender(imageBytes, steps, out _);

    private static RasterImageInfo? TryRender(
        byte[] imageBytes, IReadOnlyList<ImageFilterStep> steps, out FilterPadding padding)
    {
        padding = FilterPadding.None;
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) return null;

        // SKBitmap.Decode throws (not returns null) on undecodable bytes — guard it so untrusted
        // image data can't surface as an unhandled exception.
        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(imageBytes); }
        catch (Exception) { return null; }
        if (decoded is null) return null;
        using var src = decoded;
        if (src.Width <= 0 || src.Height <= 0) return null;
        var srcW = src.Width;
        var srcH = src.Height;

        // A drop-shadow extends OUTSIDE the image box (offset + blur), so the output frame is padded
        // by the shadow extent and the source is drawn at (padL, padT). Colour matrices + blur add no
        // padding (blur's halo is clipped at the image box — a documented residual).
        ComputeShadowPadding(steps, out var padL, out var padT, out var padR, out var padB);
        var w = srcW + padL + padR;
        var h = srcH + padT + padB;
        if (w > ShadowRasterizer.MaxDeviceDimension || h > ShadowRasterizer.MaxDeviceDimension) return null;
        if ((long)w * h > ShadowRasterizer.MaxDevicePixels) return null;

        SKImageFilter? chain;
        try { chain = BuildChain(steps); }
        catch (NotSupportedException) { return null; } // a kind not implemented in this cut
        if (chain is null) return null;

        var imageInfo = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(imageInfo);
        if (surface is null) { chain.Dispose(); return null; }
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using (chain)
        using (var paint = new SKPaint { IsAntialias = true, ImageFilter = chain })
        {
            canvas.DrawBitmap(src, padL, padT, paint);
        }

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var width4 = w * 4;
        var pixels = new byte[width4 * h];
        var source = pixmap.GetPixelSpan();
        for (var y = 0; y < h; y++)
            source.Slice(y * rowBytes, width4).CopyTo(pixels.AsSpan(y * width4));

        padding = new FilterPadding(padL / (float)srcW, padT / (float)srcH, padR / (float)srcW, padB / (float)srcH);
        return new RasterImageInfo { Width = w, Height = h, HasAlpha = true, PixelBytes = pixels };
    }

    /// <summary>Per-side padding (source px) needed to fit the drop-shadow steps: a shadow offset by
    /// (dx, dy) with σ = blur/2 reaches the image box by (dx ± 3σ, dy ± 3σ). Colour matrices + blur
    /// contribute nothing (their output stays within the image box in this cut).</summary>
    private static void ComputeShadowPadding(
        IReadOnlyList<ImageFilterStep> steps, out int padL, out int padT, out int padR, out int padB)
    {
        double l = 0, t = 0, r = 0, b = 0;
        foreach (var step in steps)
        {
            if (step.Kind != ImageFilterKind.DropShadow) continue;
            var m = 3.0 * (step.ShadowBlur / 2.0); // 3σ
            l += Math.Max(0, m - step.ShadowDx);
            r += Math.Max(0, m + step.ShadowDx);
            t += Math.Max(0, m - step.ShadowDy);
            b += Math.Max(0, m + step.ShadowDy);
        }
        padL = (int)Math.Ceiling(l);
        padT = (int)Math.Ceiling(t);
        padR = (int)Math.Ceiling(r);
        padB = (int)Math.Ceiling(b);
    }

    /// <summary>Fold the steps into a composed <see cref="SKImageFilter"/> chain — each step wraps the
    /// previous as its input, so step 0 is applied first (CSS Filter Effects §2 declaration order).</summary>
    private static SKImageFilter? BuildChain(IReadOnlyList<ImageFilterStep> steps)
    {
        SKImageFilter? chain = null;
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case ImageFilterKind.Blur:
                    chain = SKImageFilter.CreateBlur((float)step.Amount, (float)step.Amount, chain);
                    break;
                case ImageFilterKind.DropShadow:
                    chain = SKImageFilter.CreateDropShadow(
                        (float)step.ShadowDx, (float)step.ShadowDy,
                        (float)(step.ShadowBlur / 2.0), (float)(step.ShadowBlur / 2.0),
                        new SKColor(ToByte(step.ShadowR), ToByte(step.ShadowG), ToByte(step.ShadowB), ToByte(step.ShadowA)),
                        chain);
                    break;
                default:
                    using (var cf = SKColorFilter.CreateColorMatrix(BuildColorMatrix(step)))
                        chain = SKImageFilter.CreateColorFilter(cf, chain);
                    break;
            }
        }
        return chain;
    }

    /// <summary>The 4×5 row-major color matrix for a proportional / hue-rotate filter step
    /// (CSS Filter Effects L1 §C). Channels + the translation column are in [0, 1].</summary>
    private static float[] BuildColorMatrix(ImageFilterStep step)
    {
        var a = (float)step.Amount;
        switch (step.Kind)
        {
            case ImageFilterKind.Saturate: return Saturate(a);
            case ImageFilterKind.Grayscale: return Saturate(1f - a);          // grayscale(x) ≡ saturate(1−x)
            case ImageFilterKind.Sepia: return Sepia(a);
            case ImageFilterKind.Invert:
                return [1 - 2 * a, 0, 0, 0, a, 0, 1 - 2 * a, 0, 0, a, 0, 0, 1 - 2 * a, 0, a, 0, 0, 0, 1, 0];
            case ImageFilterKind.Brightness:
                return [a, 0, 0, 0, 0, 0, a, 0, 0, 0, 0, 0, a, 0, 0, 0, 0, 0, 1, 0];
            case ImageFilterKind.Contrast:
                var t = 0.5f * (1f - a); // intercept
                return [a, 0, 0, 0, t, 0, a, 0, 0, t, 0, 0, a, 0, t, 0, 0, 0, 1, 0];
            case ImageFilterKind.Opacity:
                return [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, a, 0];
            case ImageFilterKind.HueRotate: return HueRotate(a);
            default: throw new NotSupportedException($"Color matrix for {step.Kind} is not implemented.");
        }
    }

    private static float[] Saturate(float s) =>
    [
        0.213f + 0.787f * s, 0.715f - 0.715f * s, 0.072f - 0.072f * s, 0, 0,
        0.213f - 0.213f * s, 0.715f + 0.285f * s, 0.072f - 0.072f * s, 0, 0,
        0.213f - 0.213f * s, 0.715f - 0.715f * s, 0.072f + 0.928f * s, 0, 0,
        0, 0, 0, 1, 0,
    ];

    private static float[] Sepia(float amount)
    {
        var a = 1f - amount; // sepia(1) → full sepia matrix; sepia(0) → identity
        return
        [
            0.393f + 0.607f * a, 0.769f - 0.769f * a, 0.189f - 0.189f * a, 0, 0,
            0.349f - 0.349f * a, 0.686f + 0.314f * a, 0.168f - 0.168f * a, 0, 0,
            0.272f - 0.272f * a, 0.534f - 0.534f * a, 0.131f + 0.869f * a, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] HueRotate(float degrees)
    {
        var rad = degrees * (float)(Math.PI / 180.0);
        var c = (float)Math.Cos(rad);
        var s = (float)Math.Sin(rad);
        return
        [
            0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f, 0, 0,
            0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f, 0, 0,
            0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static byte ToByte(double channel) =>
        (byte)Math.Clamp((int)Math.Round(channel * 255.0), 0, 255);
}
