// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Pdf.Images;
using SkiaSharp;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>Phase 4 filters (PR 2) — verifies the <see cref="ImageFilterApplier"/> color matrices
/// (CSS Filter Effects L1 §C) and the Skia color-matrix [0,1] offset convention on a known solid
/// image (channels sampled from the raw filtered RGBA, with a small round-trip tolerance).</summary>
public sealed class ImageFilterApplierTests
{
    // A solid opaque image of a known color, encoded as PNG.
    private static byte[] SolidPng(byte r, byte g, byte b)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                bitmap.SetPixel(x, y, new SKColor(r, g, b, 0xFF));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (byte R, byte G, byte B, byte A) FirstPixel(byte[] png, params ImageFilterStep[] steps)
    {
        var raster = ImageFilterApplier.TryFilterToRaster(png, new List<ImageFilterStep>(steps));
        Assert.NotNull(raster);
        var p = raster!.PixelBytes;
        return (p[0], p[1], p[2], p[3]);
    }

    private static void Near(byte expected, byte actual, int tol = 3) =>
        Assert.True(System.Math.Abs(expected - actual) <= tol, $"expected ~{expected}, got {actual}");

    [Fact]
    public void Invert_full_maps_each_channel_to_its_complement()
    {
        // (64, 128, 255) → (191, 127, 0). Proves the matrix offset column is [0,1] (not ×255).
        var (r, g, b, a) = FirstPixel(SolidPng(64, 128, 255), new ImageFilterStep(ImageFilterKind.Invert, 1.0));
        Near(191, r); Near(127, g); Near(0, b); Near(255, a);
    }

    [Fact]
    public void Grayscale_full_equalizes_the_channels_to_luminance()
    {
        // luminance(64,128,255) = 0.213·64 + 0.715·128 + 0.072·255 ≈ 124.
        var (r, g, b, _) = FirstPixel(SolidPng(64, 128, 255), new ImageFilterStep(ImageFilterKind.Grayscale, 1.0));
        Near(124, r, 4); Near(124, g, 4); Near(124, b, 4);
        Assert.True(System.Math.Abs(r - g) <= 2 && System.Math.Abs(g - b) <= 2);
    }

    [Fact]
    public void Brightness_zero_is_black_and_identity_is_unchanged()
    {
        var black = FirstPixel(SolidPng(200, 100, 50), new ImageFilterStep(ImageFilterKind.Brightness, 0.0));
        Near(0, black.R); Near(0, black.G); Near(0, black.B);
        var same = FirstPixel(SolidPng(200, 100, 50), new ImageFilterStep(ImageFilterKind.Brightness, 1.0));
        Near(200, same.R); Near(100, same.G); Near(50, same.B);
    }

    [Fact]
    public void Opacity_scales_the_alpha_channel()
    {
        var (_, _, _, a) = FirstPixel(SolidPng(255, 0, 0), new ImageFilterStep(ImageFilterKind.Opacity, 0.5));
        Near(128, a, 3);
    }

    [Fact]
    public void Chained_filters_compose_in_order()
    {
        // grayscale then invert: gray ≈ 124 → invert → ≈ 131, equal across channels.
        var (r, g, b, _) = FirstPixel(SolidPng(64, 128, 255),
            new ImageFilterStep(ImageFilterKind.Grayscale, 1.0), new ImageFilterStep(ImageFilterKind.Invert, 1.0));
        Near(131, r, 4); Near(131, g, 4); Near(131, b, 4);
    }

    // A 32×32 image, left half (255,0,0), right half (0,0,255) — a sharp vertical seam at x=16.
    private static byte[] HalfRedHalfBluePng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                bitmap.SetPixel(x, y, x < 16 ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Blur_blends_a_sharp_color_seam()
    {
        var raster = ImageFilterApplier.TryFilterToRaster(HalfRedHalfBluePng(),
            new List<ImageFilterStep> { new(ImageFilterKind.Blur, 4.0) });
        Assert.NotNull(raster);
        // Sample the seam column (x=16, y=16): the blur mixes red + blue → both channels mid-range.
        var idx = (16 * 32 + 16) * 4;
        var p = raster!.PixelBytes;
        Assert.InRange(p[idx], 40, 215);     // R partially present (was 0 just right of the seam)
        Assert.InRange(p[idx + 2], 40, 215); // B partially present
    }

    [Fact]
    public void Undecodable_bytes_return_null()
    {
        Assert.Null(ImageFilterApplier.TryFilterToRaster([0, 1, 2, 3],
            new List<ImageFilterStep> { new(ImageFilterKind.Invert, 1.0) }));
    }
}
