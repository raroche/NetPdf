// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Unit tests for <see cref="PngHeaderParser"/> covering signature, IHDR, PLTE, IDAT,
/// IEND walks plus every reject branch. Uses <see cref="SyntheticPng"/> to build valid
/// PNG byte streams (CRC-checked, zlib-compressed IDAT) so the parser sees real PNG
/// structure, not just header stubs.
/// </summary>
public sealed class PngHeaderParserTests
{
    [Fact]
    public void Parse_round_trips_an_opaque_grayscale_8bit_PNG()
    {
        var bytes = SyntheticPng.BuildOpaqueGrayscale8(width: 32, height: 16);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(32, info.Width);
        Assert.Equal(16, info.Height);
        Assert.Equal(8, info.BitDepth);
        Assert.Equal(PngColorType.Grayscale, info.ColorType);
        Assert.False(info.HasAlpha);
        Assert.False(info.IsIndexed);
        Assert.False(info.IsInterlaced);
        Assert.Null(info.Palette);
        Assert.True(info.CompressedIdatBytes.Length > 0);
    }

    [Fact]
    public void Parse_round_trips_an_opaque_RGB_8bit_PNG()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8(width: 64, height: 48);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(PngColorType.Rgb, info.ColorType);
        Assert.Equal(3, info.ColorComponents);
    }

    [Fact]
    public void Parse_extracts_PLTE_from_indexed_PNG()
    {
        // 4-entry palette: red, green, blue, white.
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        var bytes = SyntheticPng.BuildIndexed8(width: 8, height: 8, palette: palette);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(PngColorType.Indexed, info.ColorType);
        Assert.NotNull(info.Palette);
        Assert.Equal(palette, info.Palette);
    }

    [Fact]
    public void Parse_round_trips_an_RGBA_8bit_PNG()
    {
        var bytes = SyntheticPng.BuildRgba8(width: 16, height: 8);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(PngColorType.Rgba, info.ColorType);
        Assert.True(info.HasAlpha);
        Assert.Equal(3, info.ColorComponents);
        Assert.Equal(4, info.Channels);
    }

    [Fact]
    public void Parse_round_trips_a_grayscale_alpha_8bit_PNG()
    {
        var bytes = SyntheticPng.BuildGrayscaleAlpha8(width: 16, height: 8);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(PngColorType.GrayscaleAlpha, info.ColorType);
        Assert.True(info.HasAlpha);
        Assert.Equal(2, info.Channels);
    }

    [Fact]
    public void Parse_recognizes_the_interlaced_flag()
    {
        var bytes = SyntheticPng.BuildInterlaced(width: 16, height: 8);
        var info = PngHeaderParser.Parse(bytes);
        Assert.True(info.IsInterlaced);
    }

    // ───── Reject branches ───────────────────────────────────────────────────

    [Fact]
    public void Parse_rejects_too_short_input()
    {
        Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(new byte[7]));
    }

    [Fact]
    public void Parse_rejects_missing_signature()
    {
        var bytes = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(bytes));
    }

    [Fact]
    public void Parse_rejects_first_chunk_not_IHDR()
    {
        // Build a "PNG" with a zero-length non-IHDR first chunk. Use IEND as the marker
        // so the parser sees a valid 4-byte type but rejects the wrong type.
        var bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
            0, 0, 0, 0,                                       // chunk length 0
            (byte)'I', (byte)'E', (byte)'N', (byte)'D',       // type IEND
            0, 0, 0, 0,                                       // CRC (unchecked)
        };
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(bytes));
        Assert.Contains("IHDR", ex.Message, StringComparison.Ordinal);
    }
}
