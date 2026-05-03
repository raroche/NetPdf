// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Tests for <see cref="RasterImageDecoder"/> (Skia-based decode of WebP / AVIF / GIF
/// formats NetPdf doesn't natively parse). Uses <see cref="SyntheticRasterImage"/> to
/// build real WebP fixtures via Skia at test time and a hand-crafted minimal GIF for
/// GIF coverage.
/// </summary>
public sealed class RasterImageDecoderTests
{
    [Fact]
    public void Decode_extracts_dimensions_from_opaque_WebP()
    {
        var bytes = SyntheticRasterImage.BuildOpaqueWebp(width: 32, height: 16);
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(32, info.Width);
        Assert.Equal(16, info.Height);
        Assert.False(info.HasAlpha, "solid-color opaque WebP should report HasAlpha=false");
        Assert.Equal(32 * 16 * 4, info.PixelBytes.Length);
    }

    [Fact]
    public void Decode_extracts_dimensions_from_RGBA_WebP_and_detects_alpha()
    {
        var bytes = SyntheticRasterImage.BuildRgbaWebp(width: 24, height: 24, a: 0x80);
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(24, info.Width);
        Assert.Equal(24, info.Height);
        Assert.True(info.HasAlpha, "RGBA with α=128 must report HasAlpha=true");
    }

    [Fact]
    public void Decode_handles_a_minimal_GIF()
    {
        var bytes = SyntheticRasterImage.BuildMinimalGif();
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
    }

    [Fact]
    public void Decode_works_when_handed_a_PNG_too()
    {
        // The Raster path is not the optimal path for PNG (use PngImageXObject) but
        // SkiaSharp will happily decode it — verify so the fallback works for code that
        // doesn't pre-classify the input format.
        var bytes = SyntheticRasterImage.BuildOpaquePng(width: 8, height: 8);
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(8, info.Width);
        Assert.Equal(8, info.Height);
    }

    [Fact]
    public void Decode_rejects_empty_input()
    {
        Assert.Throws<InvalidDataException>(() => RasterImageDecoder.Decode([]));
    }

    [Fact]
    public void Decode_rejects_unrecognized_byte_garbage()
    {
        var garbage = new byte[256];
        for (var i = 0; i < garbage.Length; i++) garbage[i] = (byte)i;
        Assert.Throws<InvalidDataException>(() => RasterImageDecoder.Decode(garbage));
    }

    [Fact]
    public void Decode_throws_on_null_args()
    {
        Assert.Throws<ArgumentNullException>(() => RasterImageDecoder.Decode(null!));
    }

    // ───── Trust-boundary + fixture-coverage tests (review follow-up) ────────

    [Fact]
    public void Decode_handles_a_committed_AVIF_fixture_when_host_libavif_is_present()
    {
        // 305-byte 1×1 white opaque AVIF from the libavif test corpus (BSD-2-Clause).
        // Removes dependence on the host having a libavif ENCODER. AVIF decode itself
        // still requires libavif to be linked into the host's SkiaSharp build —
        // SkiaSharp 3.119's Linux/Windows assets generally include it, the macOS
        // asset does not. When the host can't decode AVIF the test no-ops; the
        // committed fixture means CI on AVIF-capable platforms always exercises the
        // path deterministically.
        var avifBytes = LoadEmbedded("NetPdf.UnitTests.Resources.Images.white_1x1.avif");
        RasterImageInfo info;
        try
        {
            info = RasterImageDecoder.Decode(avifBytes);
        }
        catch (InvalidDataException)
        {
            // libavif not linked into this host's SkiaSharp build — skip with a clear
            // contract: when AVIF *is* decodable here, the rest of this test enforces
            // the fixture's white-pixel result.
            return;
        }
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
        Assert.False(info.HasAlpha);
        Assert.Equal(0xFF, info.PixelBytes[0]); // R
        Assert.Equal(0xFF, info.PixelBytes[1]); // G
        Assert.Equal(0xFF, info.PixelBytes[2]); // B
        Assert.Equal(0xFF, info.PixelBytes[3]); // A
    }

    private static byte[] LoadEmbedded(string resourceName)
    {
        using var stream = typeof(RasterImageDecoderTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Test resource '{resourceName}' missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Decode_decodes_a_Skia_encoded_AVIF_when_the_host_encoder_is_available()
    {
        var bytes = SyntheticRasterImage.TryBuildOpaqueAvif(width: 16, height: 16);
        if (bytes is null)
        {
            // Optional extra coverage — only runs on hosts with libavif encoder.
            // The committed white_1x1.avif test (above) is the deterministic AVIF
            // contract gate.
            return;
        }
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(16, info.Width);
        Assert.Equal(16, info.Height);
    }

    [Fact]
    public void Decode_recognizes_a_transparent_GIF()
    {
        var bytes = SyntheticRasterImage.BuildTransparentGif();
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
        // Transparent palette index 0 was used; HasAlpha must be true.
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public void Decode_collapses_an_animated_GIF_to_its_first_frame()
    {
        // The animated GIF has 2 frames (white, then black). Decoder must yield the
        // first frame's pixels — for our 1×1 fixture, the white frame.
        var bytes = SyntheticRasterImage.BuildAnimatedGif();
        var info = RasterImageDecoder.Decode(bytes);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
        // Pixel 0 of the first frame: white.
        Assert.Equal(0xFF, info.PixelBytes[0]); // R
        Assert.Equal(0xFF, info.PixelBytes[1]); // G
        Assert.Equal(0xFF, info.PixelBytes[2]); // B
    }

    [Fact]
    public void Decode_rejects_truncated_WebP_with_partial_decode()
    {
        // A valid WebP truncated mid-stream produces a partial decode (Skia would
        // return IncompleteInput). Our hardened decoder must reject this rather than
        // silently embed corrupted pixels.
        var full = SyntheticRasterImage.BuildOpaqueWebp(width: 32, height: 32);
        var truncated = SyntheticRasterImage.Truncate(full, full.Length / 2);
        Assert.Throws<InvalidDataException>(() => RasterImageDecoder.Decode(truncated));
    }

    [Fact]
    public void Decode_rejects_dimensions_exceeding_the_pixel_cap()
    {
        // We can't easily fabricate a real WebP claiming 100M+ pixels through Skia's
        // encoder, but the bounds-check logic is exposed via ValidateDecodedDimensions
        // and runs identically on any input. Drive it directly here.
        Assert.Throws<InvalidDataException>(() =>
            RasterImageDecoder.ValidateDecodedDimensions(20_000, 20_000)); // 400 megapixels
    }

    [Fact]
    public void Decode_rejects_negative_or_zero_dimensions()
    {
        Assert.Throws<InvalidDataException>(() =>
            RasterImageDecoder.ValidateDecodedDimensions(0, 100));
        Assert.Throws<InvalidDataException>(() =>
            RasterImageDecoder.ValidateDecodedDimensions(100, 0));
        Assert.Throws<InvalidDataException>(() =>
            RasterImageDecoder.ValidateDecodedDimensions(-1, 100));
    }
}
