// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using SkiaSharp;

namespace NetPdf.TestKit;

/// <summary>
/// Single source of truth for the small image fixtures used by both the AOT smoke
/// (<c>NetPdf.AotSmoke.SmokeDocumentFactory</c>) and the benchmarks
/// (<c>NetPdf.Benchmarks</c>). Replaces three formerly-duplicate copies that lived
/// in their respective projects.
/// <para>
/// Hand-crafted fixtures (<see cref="MinimalBaselineJpeg"/>,
/// <see cref="MinimalTransparentGif"/>) are inline byte literals — byte-stable
/// across platforms and runtime versions. Skia-encoded fixtures
/// (<see cref="EncodeOpaqueWebp"/>) are produced at runtime; their byte content is
/// platform-dependent (SkiaSharp version + native build) and therefore unsuitable
/// for byte-determinism pinning, but they are perfectly fine for measuring the
/// timing of the <see cref="NetPdf.Pdf.Images.RasterImageXObject"/> embed pipeline.
/// </para>
/// <para>
/// All bytes are <b>tiny synthetic</b> — they exist to drive the parser/wrapper
/// path, not to characterize realistic image-embedding throughput. A 1×1 JPEG
/// embed cost is dominated by the dictionary build + SHA-256 dedup hash; for
/// representative real-world image throughput, point benchmarks at a curated
/// corpus of full-resolution photos / screenshots. That corpus is post-Phase-1
/// scope (lives in <c>NetPdf.RenderingCorpus</c> when it's wired).
/// </para>
/// </summary>
public static class MinimalImageFixtures
{
    /// <summary>
    /// Hand-crafted minimal valid baseline JPEG: SOI / APP0 JFIF / SOF0 (1×1, 3
    /// components) / SOS / single-byte ECS / EOI. Syntactically valid enough for
    /// <c>JpegHeaderParser</c> to accept (size + component count) and for
    /// <c>JpegImageXObject</c> to wrap as a passthrough Image XObject. Pixel data
    /// does not decode to a meaningful image — the wrapper path is the
    /// measurement target.
    /// </summary>
    public static byte[] MinimalBaselineJpeg() =>
    [
        0xFF, 0xD8,                                         // SOI
        0xFF, 0xE0, 0x00, 0x10,                             // APP0 length 16
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
        0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        // SOF0: precision=8, height=1, width=1, components=3 (Y'CbCr)
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03,
        0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        // SOS: 3 components scanning Y/Cb/Cr, Ss/Se/Ah/Al = 0/63/0/0
        0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
        0x00, // entropy-coded segment placeholder
        0xFF, 0xD9, // EOI
    ];

    /// <summary>
    /// Hand-crafted minimal GIF89a (1×1) with a Graphic Control Extension marking
    /// palette index 0 as transparent. Drives the <c>RasterImageDecoder</c> →
    /// <c>RasterImageXObject</c> alpha-split path through the
    /// <c>PdfDocument.RegisterImage(ImageXObjectResult)</c> SMask indirect-ref
    /// branch.
    /// </summary>
    public static byte[] MinimalTransparentGif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,                 // "GIF89a"
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,           // LSD: 1×1, gct flag, 2-color table
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,                 // GCT: white + black
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,     // GCE: transparency index = 0
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        0x3B,                                               // Trailer
    ];

    /// <summary>
    /// Hand-crafted minimal opaque GIF89a (1×1, single white pixel). Used where a
    /// caller wants the raster decode path without exercising the alpha-split
    /// SMask branch.
    /// </summary>
    public static byte[] MinimalOpaqueGif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        0x3B,
    ];

    /// <summary>
    /// Encode a small opaque WebP via SkiaSharp at the given <paramref name="width"/>
    /// × <paramref name="height"/>. Output bytes vary across SkiaSharp versions and
    /// native builds (NOT byte-stable), so the result is suitable for benchmark
    /// timing but NOT for byte-determinism pinning. The caller is expected to feed
    /// it directly to <c>RasterImageXObject.Build</c>.
    /// </summary>
    public static byte[] EncodeOpaqueWebp(int width = 8, int height = 8, byte r = 0xFF, byte g = 0x80, byte b = 0x40)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(r, g, b, 0xFF));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality: 95)
            ?? throw new InvalidOperationException("SkiaSharp WebP encoder returned null on this host.");
        return data.ToArray();
    }

    /// <summary>
    /// Encode a small RGBA WebP (semi-transparent solid fill) via SkiaSharp.
    /// Drives the alpha-bearing raster path under timing measurements.
    /// </summary>
    public static byte[] EncodeRgbaWebp(int width = 8, int height = 8, byte alpha = 0x80)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(0xFF, 0x80, 0x40, alpha));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality: 95)
            ?? throw new InvalidOperationException("SkiaSharp WebP encoder returned null on this host.");
        return data.ToArray();
    }
}
