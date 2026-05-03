// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using SkiaSharp;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Test fixture builder that encodes synthetic images into WebP / JPEG / PNG via
/// SkiaSharp. The opaque + RGBA flavors exercise both the "no alpha" and "alpha-split
/// SMask" paths in <c>RasterImageXObject</c>. GIF encoding is not bundled with
/// SkiaSharp by default — for a minimal real GIF we emit a hand-crafted byte stream.
/// </summary>
internal static class SyntheticRasterImage
{
    public static byte[] BuildOpaqueWebp(int width, int height, byte r = 0xFF, byte g = 0x80, byte b = 0x40)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        FillSolid(bitmap, r, g, b, 0xFF);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality: 95);
        return data.ToArray();
    }

    public static byte[] BuildRgbaWebp(int width, int height, byte r = 0xFF, byte g = 0x80, byte b = 0x40, byte a = 0x80)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        FillSolid(bitmap, r, g, b, a);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality: 95);
        return data.ToArray();
    }

    public static byte[] BuildOpaquePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        FillSolid(bitmap, 0x40, 0x80, 0xFF, 0xFF);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        return data.ToArray();
    }

    /// <summary>
    /// Hand-crafted minimal valid GIF (1×1 white pixel, GIF89a). Used when SkiaSharp's
    /// GIF encoder is not available — every conformant GIF decoder accepts this.
    /// </summary>
    public static byte[] BuildMinimalGif() =>
    [
        // Header: "GIF89a"
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        // Logical Screen Descriptor: 1×1, global color table flag, 1 byte, no sort, 2-color table
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        // Global color table: 2 entries, white + black
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        // Image Descriptor: 0x2C, position (0,0), size 1×1, no local color table
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        // LZW Minimum Code Size + image data (1×1 white pixel)
        0x02, 0x02, 0x44, 0x01, 0x00,
        // Trailer
        0x3B,
    ];

    /// <summary>
    /// Hand-crafted minimal GIF89a (1×1) with a Graphic Control Extension marking
    /// palette index 0 as transparent. Validates the parser's transparent-GIF path.
    /// </summary>
    public static byte[] BuildTransparentGif() =>
    [
        // Header
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        // LSD: 1×1, gct flag, 2-color table
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        // GCT: white + black
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        // Graphic Control Extension: 0x21 0xF9 0x04 (block size) + flags + delay (2B) + transparent index + 0x00 (terminator)
        // flags = 0x01 (transparency flag = 1)
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,
        // Image Descriptor + 1×1 image data
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        // Trailer
        0x3B,
    ];

    /// <summary>
    /// Hand-crafted GIF89a with TWO frames (1×1 each). The decoder must collapse to
    /// the first frame for static PDF embedding. Frame 0 = white, frame 1 = black.
    /// </summary>
    public static byte[] BuildAnimatedGif() =>
    [
        // Header
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        // LSD: 1×1, gct flag, 2-color table
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        // GCT: white + black
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        // NETSCAPE 2.0 Application Extension (loop forever) — marks the file as animated.
        0x21, 0xFF, 0x0B, 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30,
        0x03, 0x01, 0x00, 0x00, 0x00,
        // Frame 1: GCE + Image Descriptor + image data (white)
        0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        // Frame 2: GCE + Image Descriptor + image data (black)
        0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x4C, 0x01, 0x00,
        // Trailer
        0x3B,
    ];

    /// <summary>
    /// Encode a real AVIF byte stream via SkiaSharp. Returns null when the bundled
    /// libavif build does not include the encoder for this platform — the test then
    /// skips its assertion gracefully rather than failing on environment mismatch.
    /// </summary>
    public static byte[]? TryBuildOpaqueAvif(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        FillSolid(bitmap, 0x40, 0x80, 0xFF, 0xFF);
        using var image = SKImage.FromBitmap(bitmap);
        try
        {
            using var data = image.Encode(SKEncodedImageFormat.Avif, quality: 75);
            return data is null ? null : data.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Truncate a byte stream to the first <paramref name="keepBytes"/> bytes.</summary>
    public static byte[] Truncate(byte[] source, int keepBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keepBytes, source.Length);
        var result = new byte[keepBytes];
        Array.Copy(source, result, keepBytes);
        return result;
    }

    private static void FillSolid(SKBitmap bitmap, byte r, byte g, byte b, byte a)
    {
        // SKBitmap.Pixels returns SKColor[] in raster order; setting via per-pixel SetPixel
        // is fine for tiny test images.
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(r, g, b, a));
            }
        }
    }
}
