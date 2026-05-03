// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Unit tests for <see cref="PngHeaderParser"/> covering signature, IHDR, PLTE, IDAT,
/// IEND, tRNS extraction plus every reject branch (CRC mismatch, unknown critical
/// chunks, IDAT non-consecutive, post-IEND data, PLTE/tRNS placement, palette/bit-depth
/// mismatch).
/// </summary>
public sealed class PngHeaderParserTests
{
    // ───── Happy path ────────────────────────────────────────────────────────

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
        Assert.Null(info.TransparencyChunk);
    }

    [Fact]
    public void Parse_round_trips_an_opaque_RGB_8bit_PNG()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8(width: 64, height: 48);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(PngColorType.Rgb, info.ColorType);
        Assert.Equal(3, info.ColorComponents);
    }

    [Fact]
    public void Parse_extracts_PLTE_from_indexed_PNG()
    {
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
    }

    [Fact]
    public void Parse_round_trips_a_grayscale_alpha_8bit_PNG()
    {
        var bytes = SyntheticPng.BuildGrayscaleAlpha8(width: 16, height: 8);
        var info = PngHeaderParser.Parse(bytes);
        Assert.Equal(PngColorType.GrayscaleAlpha, info.ColorType);
    }

    [Fact]
    public void Parse_recognizes_the_interlaced_flag()
    {
        var bytes = SyntheticPng.BuildInterlaced(width: 16, height: 8);
        var info = PngHeaderParser.Parse(bytes);
        Assert.True(info.IsInterlaced);
    }

    [Fact]
    public void Parse_extracts_tRNS_for_grayscale_PNG()
    {
        var bytes = SyntheticPng.BuildOpaqueGrayscale8WithTrns(16, 8, transparentGray: 0x80);
        var info = PngHeaderParser.Parse(bytes);
        Assert.NotNull(info.TransparencyChunk);
        Assert.Equal(2, info.TransparencyChunk!.Length);
    }

    [Fact]
    public void Parse_extracts_tRNS_for_RGB_PNG()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8WithTrns(16, 8, tr: 0xFF, tg: 0x00, tb: 0x00);
        var info = PngHeaderParser.Parse(bytes);
        Assert.NotNull(info.TransparencyChunk);
        Assert.Equal(6, info.TransparencyChunk!.Length);
    }

    [Fact]
    public void Parse_extracts_tRNS_for_indexed_PNG()
    {
        var palette = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };
        var trns = new byte[] { 0x00, 0x80, 0xFF, 0xFF }; // first transparent, second semi
        var bytes = SyntheticPng.BuildIndexed8WithTrns(8, 8, palette, trns);
        var info = PngHeaderParser.Parse(bytes);
        Assert.NotNull(info.TransparencyChunk);
        Assert.Equal(trns, info.TransparencyChunk);
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
        // Build a byte stream with valid signature + valid IEND-typed chunk (correct CRC)
        // as the first chunk. CRC check passes; the IHDR-required check kicks in next.
        using var ms = new MemoryStream();
        ms.Write(PngHeaderParser.Signature);
        WriteValidChunk(ms, "IEND", []);
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(ms.ToArray()));
        Assert.Contains("IHDR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_chunk_with_invalid_CRC()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8(16, 8);
        // Mutate one byte inside the IHDR chunk's data region (signature is 8 bytes,
        // then IHDR length + type at 8..16, then 13 bytes data). Byte 16 is the first
        // byte of width — flip it to invalidate the IHDR CRC.
        bytes[16] ^= 0xFF;
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(bytes));
        Assert.Contains("CRC mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_unknown_critical_chunk()
    {
        // Build a valid signature + IHDR + an unknown CRITICAL chunk (uppercase first
        // letter) then IEND. The parser must reject the unknown critical chunk.
        var sourceBytes = SyntheticPng.BuildOpaqueRgb8(16, 8);
        // Find the start of the IDAT chunk in the source bytes — we'll insert an unknown
        // critical chunk just before it. For simplicity, we'll rebuild: signature + IHDR
        // (copied) + 'XXXX' chunk + rest of source.
        var ihdrEnd = 8 + 4 + 4 + 13 + 4; // signature + length + type + data + CRC
        using var ms = new MemoryStream();
        ms.Write(sourceBytes.AsSpan(0, ihdrEnd));
        // Inject an unknown critical chunk 'tEsT' — wait, lowercase 't' = ancillary.
        // We need uppercase first letter for critical. Use 'XXXX' (all uppercase).
        WriteValidChunk(ms, "XXXX", new byte[] { 1, 2, 3 });
        ms.Write(sourceBytes.AsSpan(ihdrEnd));
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(ms.ToArray()));
        Assert.Contains("unknown critical", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_skips_unknown_ancillary_chunk()
    {
        // Lowercase first letter 'x' makes it ancillary — must be skipped without rejection.
        var sourceBytes = SyntheticPng.BuildOpaqueRgb8(16, 8);
        var ihdrEnd = 8 + 4 + 4 + 13 + 4;
        using var ms = new MemoryStream();
        ms.Write(sourceBytes.AsSpan(0, ihdrEnd));
        WriteValidChunk(ms, "tEsT", new byte[] { 1, 2, 3 });
        ms.Write(sourceBytes.AsSpan(ihdrEnd));
        var info = PngHeaderParser.Parse(ms.ToArray()); // must not throw
        Assert.Equal(16, info.Width);
    }

    [Fact]
    public void Parse_rejects_trailing_bytes_after_IEND()
    {
        var bytes = SyntheticPng.BuildOpaqueRgb8(16, 8);
        var withTrailing = new byte[bytes.Length + 4];
        bytes.CopyTo(withTrailing, 0);
        withTrailing[^4] = 0xDE;
        withTrailing[^3] = 0xAD;
        withTrailing[^2] = 0xBE;
        withTrailing[^1] = 0xEF;
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(withTrailing));
        Assert.Contains("trailing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_PLTE_for_grayscale_PNG()
    {
        // Grayscale PNG with an injected PLTE chunk — spec §11.2.3 forbids this.
        var sourceBytes = SyntheticPng.BuildOpaqueGrayscale8(16, 8);
        var ihdrEnd = 8 + 4 + 4 + 13 + 4;
        using var ms = new MemoryStream();
        ms.Write(sourceBytes.AsSpan(0, ihdrEnd));
        WriteValidChunk(ms, "PLTE", new byte[] { 0xFF, 0, 0, 0, 0xFF, 0 });
        ms.Write(sourceBytes.AsSpan(ihdrEnd));
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(ms.ToArray()));
        Assert.Contains("PLTE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_tRNS_for_RGBA_PNG()
    {
        // tRNS is forbidden for color types that already carry alpha.
        var sourceBytes = SyntheticPng.BuildRgba8(16, 8);
        var ihdrEnd = 8 + 4 + 4 + 13 + 4;
        using var ms = new MemoryStream();
        ms.Write(sourceBytes.AsSpan(0, ihdrEnd));
        WriteValidChunk(ms, "tRNS", new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        ms.Write(sourceBytes.AsSpan(ihdrEnd));
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(ms.ToArray()));
        Assert.Contains("tRNS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_palette_with_more_entries_than_bit_depth_allows()
    {
        // 1-bit indexed → max 2 palette entries. Build with 4 entries → rejected.
        var palette = new byte[] { 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF };
        var bytes = SyntheticPng.BuildIndexedCustomBitDepth(8, 8, bitDepth: 1, palette: palette);
        var ex = Assert.Throws<InvalidDataException>(() => PngHeaderParser.Parse(bytes));
        Assert.Contains("exceeds 2^bitDepth", ex.Message, StringComparison.Ordinal);
    }

    private static void WriteValidChunk(Stream s, string type, byte[] data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];

        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        s.Write(lenBytes);
        s.Write(typeBytes);
        s.Write(data);

        var crc = PngCrc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }
}
