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
    public void Drop_shadow_pads_the_raster_and_casts_the_shadow_outside_the_image()
    {
        // A 4×4 opaque blue image + drop-shadow(6px 6px, no blur, red): σ=0 → pads right/bottom by 6,
        // output 10×10. The red shadow (alpha-following, offset 6,6) shows at the bottom-right
        // OUTSIDE the image; the image interior stays blue.
        var step = new ImageFilterStep(ImageFilterKind.DropShadow, 0,
            ShadowDx: 6, ShadowDy: 6, ShadowBlur: 0, ShadowR: 1, ShadowG: 0, ShadowB: 0, ShadowA: 1);
        var raster = ImageFilterApplier.TryFilterToRaster(SolidPng(0, 0, 255), new List<ImageFilterStep> { step });
        Assert.NotNull(raster);
        Assert.Equal(10, raster!.Width);
        Assert.Equal(10, raster.Height);

        var shadow = Pixel(raster, 8, 8);   // inside the offset shadow rect, outside the image
        Near(255, shadow.R); Near(0, shadow.G); Near(0, shadow.B);
        var inside = Pixel(raster, 2, 2);   // the image interior
        Near(0, inside.R); Near(0, inside.G); Near(255, inside.B);
    }

    private static (byte R, byte G, byte B, byte A) Pixel(RasterImageInfo raster, int x, int y)
    {
        var idx = (y * raster.Width + x) * 4;
        var p = raster.PixelBytes;
        return (p[idx], p[idx + 1], p[idx + 2], p[idx + 3]);
    }

    [Fact]
    public void Drop_shadow_blur_uses_the_length_as_sigma_directly()
    {
        // CSS Filter Effects §2.5 — the drop-shadow blur length IS σ (not 2σ like box-shadow). A
        // 4×4 image + drop-shadow(0 0 blur=4): pad = 3σ = 12 each side → 28×28 (would be 16×16 if σ
        // were halved). PR 227 review [P1].
        var step = new ImageFilterStep(ImageFilterKind.DropShadow, 0,
            ShadowDx: 0, ShadowDy: 0, ShadowBlur: 4, ShadowR: 0, ShadowG: 0, ShadowB: 0, ShadowA: 1);
        var raster = ImageFilterApplier.TryFilterToRaster(SolidPng(255, 255, 255), new List<ImageFilterStep> { step });
        Assert.NotNull(raster);
        Assert.Equal(28, raster!.Width);
        Assert.Equal(28, raster.Height);
    }

    [Fact]
    public void Oversized_source_is_rejected_at_the_header_before_a_full_decode()
    {
        // A 5000-px-wide image exceeds the 4096-px cap; the SKCodec preflight rejects it from the
        // header (no full pixel decode) — PR 227 review [P1].
        using var bitmap = new SKBitmap(new SKImageInfo(5000, 8, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Null(ImageFilterApplier.TryFilterToRaster(data.ToArray(),
            new List<ImageFilterStep> { new(ImageFilterKind.Invert, 1.0) }));
    }

    [Fact]
    public void Filtered_raster_is_at_the_source_resolution_documented_residual()
    {
        // PR 227 review [P2] — filter lengths are applied in the image's INTRINSIC pixel space: the
        // raster is the source size regardless of display size (the painter scales it). This pins the
        // documented residual (blur/drop-shadow are exact at ~intrinsic display size).
        var raster = ImageFilterApplier.TryFilterToRaster(SolidPng(10, 20, 30),
            new List<ImageFilterStep> { new(ImageFilterKind.Blur, 1.0) });
        Assert.NotNull(raster);
        Assert.Equal(4, raster!.Width);   // the 4×4 SOURCE size — not a display size
        Assert.Equal(4, raster.Height);
    }

    [Fact]
    public void Undecodable_bytes_return_null()
    {
        Assert.Null(ImageFilterApplier.TryFilterToRaster([0, 1, 2, 3],
            new List<ImageFilterStep> { new(ImageFilterKind.Invert, 1.0) }));
    }
}
