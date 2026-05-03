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
}
