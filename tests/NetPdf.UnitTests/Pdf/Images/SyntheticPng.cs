// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Builds valid PNG byte streams (signature + IHDR + optional PLTE + IDAT + IEND) for
/// tests. Uses an inline IEEE 802.3 CRC-32 implementation (PNG §5.5) for chunk CRCs and
/// <see cref="ZLibStream"/> for the IDAT zlib compression so the produced files
/// round-trip through any conformant PNG decoder, not just our own.
/// </summary>
internal static class SyntheticPng
{
    public static byte[] BuildOpaqueGrayscale8(int width, int height, byte fillValue = 0xCC)
    {
        var raw = new byte[height * (1 + width)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (1 + width)] = 0; // filter None
            for (var x = 0; x < width; x++) raw[y * (1 + width) + 1 + x] = fillValue;
        }
        return BuildPng(width, height, bitDepth: 8, colorType: 0, palette: null, decompressedScanlines: raw);
    }

    public static byte[] BuildOpaqueRgb8(int width, int height, byte r = 0xFF, byte g = 0x80, byte b = 0x40)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width * 3);
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[rowStart + 1 + x * 3 + 0] = r;
                raw[rowStart + 1 + x * 3 + 1] = g;
                raw[rowStart + 1 + x * 3 + 2] = b;
            }
        }
        return BuildPng(width, height, bitDepth: 8, colorType: 2, palette: null, decompressedScanlines: raw);
    }

    public static byte[] BuildIndexed8(int width, int height, byte[] palette, byte fillIndex = 0)
    {
        var raw = new byte[height * (1 + width)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (1 + width)] = 0;
            for (var x = 0; x < width; x++) raw[y * (1 + width) + 1 + x] = fillIndex;
        }
        return BuildPng(width, height, bitDepth: 8, colorType: 3, palette: palette, decompressedScanlines: raw);
    }

    public static byte[] BuildRgba8(int width, int height, byte r = 0xFF, byte g = 0x80, byte b = 0x40, byte a = 0xC0)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width * 4);
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[rowStart + 1 + x * 4 + 0] = r;
                raw[rowStart + 1 + x * 4 + 1] = g;
                raw[rowStart + 1 + x * 4 + 2] = b;
                raw[rowStart + 1 + x * 4 + 3] = a;
            }
        }
        return BuildPng(width, height, bitDepth: 8, colorType: 6, palette: null, decompressedScanlines: raw);
    }

    public static byte[] BuildGrayscaleAlpha8(int width, int height, byte gray = 0x80, byte alpha = 0xC0)
    {
        var raw = new byte[height * (1 + width * 2)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width * 2);
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[rowStart + 1 + x * 2 + 0] = gray;
                raw[rowStart + 1 + x * 2 + 1] = alpha;
            }
        }
        return BuildPng(width, height, bitDepth: 8, colorType: 4, palette: null, decompressedScanlines: raw);
    }

    public static byte[] BuildOpaqueGrayscale8WithTrns(int width, int height, byte transparentGray)
    {
        var raw = new byte[height * (1 + width)];
        for (var y = 0; y < height; y++) raw[y * (1 + width)] = 0;
        var trns = new byte[2];
        trns[0] = 0;
        trns[1] = transparentGray;
        return BuildPng(width, height, 8, 0, palette: null, decompressedScanlines: raw, transparency: trns);
    }

    public static byte[] BuildOpaqueRgb8WithTrns(int width, int height, byte tr, byte tg, byte tb)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++) raw[y * (1 + width * 3)] = 0;
        var trns = new byte[6];
        trns[0] = 0; trns[1] = tr;
        trns[2] = 0; trns[3] = tg;
        trns[4] = 0; trns[5] = tb;
        return BuildPng(width, height, 8, 2, palette: null, decompressedScanlines: raw, transparency: trns);
    }

    public static byte[] BuildIndexed8WithTrns(int width, int height, byte[] palette, byte[] trns, byte fillIndex = 0)
    {
        var raw = new byte[height * (1 + width)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (1 + width)] = 0;
            for (var x = 0; x < width; x++) raw[y * (1 + width) + 1 + x] = fillIndex;
        }
        return BuildPng(width, height, 8, 3, palette, raw, transparency: trns);
    }

    public static byte[] BuildIndexedCustomBitDepth(int width, int height, int bitDepth, byte[] palette)
    {
        // Bytes-per-scanline depends on bitDepth × width / 8 (rounded up).
        var bytesPerScanline = (width * bitDepth + 7) / 8;
        var raw = new byte[height * (1 + bytesPerScanline)];
        for (var y = 0; y < height; y++) raw[y * (1 + bytesPerScanline)] = 0;
        return BuildPng(width, height, bitDepth, 3, palette, raw);
    }

    public static byte[] BuildInterlaced(int width, int height)
    {
        // Build an opaque RGB PNG with the IHDR interlace flag set to 1 (Adam7). The IDAT
        // bytes don't represent a valid Adam7 layout — Phase 1 rejects interlaced PNGs at
        // the IHDR level so the test only needs the flag set.
        var raw = new byte[height * (1 + width * 3)];
        return BuildPng(width, height, bitDepth: 8, colorType: 2, palette: null, decompressedScanlines: raw, interlace: 1);
    }

    private static byte[] BuildPng(int width, int height, int bitDepth, int colorType, byte[]? palette, byte[] decompressedScanlines, int interlace = 0, byte[]? transparency = null)
    {
        using var ms = new MemoryStream();
        // Signature.
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        // IHDR.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = (byte)interlace;
        WriteChunk(ms, "IHDR", ihdr);

        // PLTE (if indexed).
        if (palette is not null)
        {
            WriteChunk(ms, "PLTE", palette);
        }

        // tRNS (if requested).
        if (transparency is not null)
        {
            WriteChunk(ms, "tRNS", transparency);
        }

        // IDAT — zlib-compressed scanlines.
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(decompressedScanlines);
            }
            compressed = output.ToArray();
        }
        WriteChunk(ms, "IDAT", compressed);

        // IEND.
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];

        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        s.Write(lenBytes);
        s.Write(typeBytes);
        s.Write(data);

        // CRC over type + data (PNG §5.5: IEEE 802.3 CRC-32, polynomial 0xEDB88320).
        var crc = ComputeCrc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> typeBytes, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        foreach (var b in data) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            }
            t[n] = c;
        }
        return t;
    }
}
