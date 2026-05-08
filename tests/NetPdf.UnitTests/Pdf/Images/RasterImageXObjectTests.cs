// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Tests for <see cref="RasterImageXObject"/> — the Skia-decoded image → PDF Image
/// XObject builder. Verifies the produced <see cref="PdfStream"/> dictionary keys
/// match ISO 32000-2:2020 §8.9.5 for both the opaque and alpha-split paths.
/// </summary>
public sealed class RasterImageXObjectTests
{
    private static PdfName? GetName(PdfDictionary dict, PdfName key) => dict.Get(key) as PdfName;
    private static long GetInt(PdfDictionary dict, PdfName key) => ((PdfInteger)dict.Get(key)!).Value;

    [Fact]
    public void Opaque_WebP_emits_RGB_image_without_SMask()
    {
        var bytes = SyntheticRasterImage.BuildOpaqueWebp(width: 16, height: 8);
        var result = RasterImageXObject.Build(bytes);
        Assert.Null(result.SMask);
        var d = result.Image.Dictionary;
        Assert.Equal("XObject", GetName(d, PdfNames.Type)!.Value);
        Assert.Equal("Image", GetName(d, PdfNames.Subtype)!.Value);
        Assert.Equal(16, GetInt(d, PdfNames.Width));
        Assert.Equal(8, GetInt(d, PdfNames.Height));
        Assert.Equal("DeviceRGB", GetName(d, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(d, PdfNames.BitsPerComponent));
        Assert.Equal("FlateDecode", GetName(d, PdfNames.Filter)!.Value);
    }

    [Fact]
    public void RGBA_WebP_emits_RGB_image_plus_DeviceGray_SMask()
    {
        var bytes = SyntheticRasterImage.BuildRgbaWebp(width: 12, height: 12, a: 0x80);
        var result = RasterImageXObject.Build(bytes);
        Assert.NotNull(result.SMask);
        var imgDict = result.Image.Dictionary;
        Assert.Equal("DeviceRGB", GetName(imgDict, PdfNames.ColorSpace)!.Value);
        // /SMask wiring deferred to PdfDocument.RegisterImage(ImageXObjectResult).
        Assert.Null(imgDict.Get(PdfNames.SMask));

        var sm = result.SMask!.Dictionary;
        Assert.Equal("DeviceGray", GetName(sm, PdfNames.ColorSpace)!.Value);
        Assert.Equal(8, GetInt(sm, PdfNames.BitsPerComponent));
        Assert.Equal(GetInt(imgDict, PdfNames.Width), GetInt(sm, PdfNames.Width));
        Assert.Equal(GetInt(imgDict, PdfNames.Height), GetInt(sm, PdfNames.Height));
    }

    [Fact]
    public void GIF_decode_round_trips_through_RasterImageXObject()
    {
        var bytes = SyntheticRasterImage.BuildMinimalGif();
        var result = RasterImageXObject.Build(bytes);
        Assert.Equal(1, GetInt(result.Image.Dictionary, PdfNames.Width));
        Assert.Equal(1, GetInt(result.Image.Dictionary, PdfNames.Height));
    }

    [Fact]
    public void Build_passes_decoded_pixel_count_into_color_stream()
    {
        // Compressed FlateDecode size is bounded by 3 × width × height (RGB) plus zlib
        // overhead. A 16×16 opaque WebP decodes to 768 raw bytes; the compressed stream
        // should be substantially smaller (solid color compresses very well).
        var bytes = SyntheticRasterImage.BuildOpaqueWebp(width: 16, height: 16);
        var result = RasterImageXObject.Build(bytes);
        Assert.True(result.Image.Data.Length > 0);
        Assert.True(result.Image.Data.Length < 16 * 16 * 3,
            "solid-color RGB should compress smaller than raw byte count");
    }

    [Fact]
    public void Build_throws_on_null_args()
    {
        Assert.Throws<ArgumentNullException>(() => RasterImageXObject.Build((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => RasterImageXObject.Build((RasterImageInfo)null!));
    }

    // ───── Build(RasterImageInfo) contract validation (review follow-up #3) ──

    [Fact]
    public void Build_rejects_RasterImageInfo_with_invalid_dimensions()
    {
        var info = new RasterImageInfo
        {
            Width = 0,
            Height = 16,
            HasAlpha = false,
            PixelBytes = new byte[0],
        };
        Assert.Throws<InvalidDataException>(() => RasterImageXObject.Build(info));
    }

    [Fact]
    public void Build_rejects_RasterImageInfo_with_dimension_pixel_byte_mismatch()
    {
        // Width × height × 4 = 16 × 16 × 4 = 1024 bytes; pass a buffer half that size.
        var info = new RasterImageInfo
        {
            Width = 16,
            Height = 16,
            HasAlpha = false,
            PixelBytes = new byte[512],
        };
        var ex = Assert.Throws<InvalidDataException>(() => RasterImageXObject.Build(info));
        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_rejects_RasterImageInfo_exceeding_pixel_cap()
    {
        // 20,000 × 20,000 = 400 megapixels — well over the 25 megapixel cap.
        var info = new RasterImageInfo
        {
            Width = 20_000,
            Height = 20_000,
            HasAlpha = false,
            PixelBytes = new byte[1], // any length; the dimension check fires first
        };
        Assert.Throws<InvalidDataException>(() => RasterImageXObject.Build(info));
    }

    // ───── End-to-end embed tests (review follow-up #4) ──────────────────────

    [Fact]
    public void Transparent_GIF_emits_DeviceRGB_image_plus_DeviceGray_SMask()
    {
        var bytes = SyntheticRasterImage.BuildTransparentGif();
        var result = RasterImageXObject.Build(bytes);
        // The 1×1 GIF marks palette index 0 transparent; decoded RGBA8888 will have
        // α < 255 so the alpha-split path emits an SMask.
        Assert.NotNull(result.SMask);
        Assert.Equal("DeviceRGB", GetName(result.Image.Dictionary, PdfNames.ColorSpace)!.Value);
        Assert.Equal("DeviceGray", GetName(result.SMask!.Dictionary, PdfNames.ColorSpace)!.Value);
    }

    [Fact]
    public void Opaque_AVIF_now_rejected_by_phase_C_safety_gate()
    {
        // Per PR #17 Phase C C-1 follow-up — AVIF input is now explicitly
        // rejected by ImageSafetyValidator before reaching
        // RasterImageDecoder. The previous "decode if libavif present, skip
        // otherwise" semantics changed: NetPdf v1 doesn't decode AVIF on
        // any host (the Phase C threat model considers AVIF in scope but
        // support is post-v1, and macOS SkiaSharp lacks libavif anyway).
        // The bundled fixture stays in the resource set for the future
        // post-v1 wireup.
        using var stream = typeof(RasterImageXObjectTests).Assembly
            .GetManifestResourceStream("NetPdf.UnitTests.Resources.Images.white_1x1.avif")
            ?? throw new InvalidOperationException("Test resource white_1x1.avif missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        var ex = Assert.Throws<InvalidOperationException>(
            () => RasterImageXObject.Build(ms.ToArray()));
        Assert.Contains("AVIF", ex.Message, StringComparison.Ordinal);
    }

    // ───── HasAlpha contract validation (review follow-up #2) ────────────────

    [Fact]
    public void Build_rejects_RasterImageInfo_with_HasAlpha_false_but_translucent_bytes()
    {
        // 4×1 RGBA buffer — 1 fully-opaque pixel + 3 with α=128.
        var pixels = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            pixels[i * 4 + 0] = 0xFF;
            pixels[i * 4 + 1] = 0xFF;
            pixels[i * 4 + 2] = 0xFF;
            pixels[i * 4 + 3] = i == 0 ? (byte)0xFF : (byte)0x80; // first opaque, rest translucent
        }
        var info = new RasterImageInfo
        {
            Width = 4,
            Height = 1,
            HasAlpha = false, // wrong — bytes have translucent alpha
            PixelBytes = pixels,
        };
        var ex = Assert.Throws<InvalidDataException>(() => RasterImageXObject.Build(info));
        Assert.Contains("HasAlpha=false", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_accepts_RasterImageInfo_with_HasAlpha_false_and_all_opaque_bytes()
    {
        // Same shape, all alpha = 255 — must succeed.
        var pixels = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            pixels[i * 4 + 0] = 0xFF;
            pixels[i * 4 + 1] = 0xFF;
            pixels[i * 4 + 2] = 0xFF;
            pixels[i * 4 + 3] = 0xFF;
        }
        var info = new RasterImageInfo
        {
            Width = 4,
            Height = 1,
            HasAlpha = false,
            PixelBytes = pixels,
        };
        var result = RasterImageXObject.Build(info);
        Assert.Null(result.SMask);
    }
}
