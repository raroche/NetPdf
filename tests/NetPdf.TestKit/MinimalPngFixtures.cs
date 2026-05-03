// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace NetPdf.TestKit;

/// <summary>
/// Hand-crafted minimal PNG fixtures for benchmarks. Each fixture is a syntactically
/// valid PNG containing the smallest possible IHDR + IDAT + IEND chunk sequence with
/// correct IEEE 802.3 CRC-32 checksums. Pixel data is placeholder (all-zero or
/// minimal-fill); the goal is to exercise <c>PngHeaderParser</c> +
/// <c>PngImageXObject</c> byte-emit paths under timing.
/// <para>
/// Output bytes are <b>byte-stable</b> across platforms and runtime versions because
/// CRC-32 is deterministic and zlib/Deflate output for tiny inputs is uniform across
/// .NET 10 BCL implementations. Suitable for both timing and byte-determinism use.
/// </para>
/// </summary>
public static class MinimalPngFixtures
{
    private const uint PngSignatureMagic1 = 0x89504E47;
    private const uint PngSignatureMagic2 = 0x0D0A1A0A;

    /// <summary>
    /// Minimal opaque RGB8 PNG (1×1, color type 2). Drives
    /// <c>PngImageXObject</c>'s passthrough branch (no SMask, no /Mask).
    /// </summary>
    public static byte[] MinimalOpaqueRgb8()
    {
        // IHDR: 1×1, bitDepth=8, colorType=2 (RGB), compression=0, filter=0, interlace=0
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 1);   // width
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 1);   // height
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type RGB
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        // 1 row of 3 bytes prefixed with filter byte (0 = None) = 4 bytes.
        var raw = new byte[] { 0x00, 0xFF, 0x80, 0x40 };
        return BuildPng(ihdr, ZlibCompress(raw));
    }

    /// <summary>
    /// Minimal RGBA8 PNG (1×1, color type 6). Drives the alpha-split SMask branch
    /// in <c>PngImageXObject</c> through <c>PdfDocument.RegisterImage(ImageXObjectResult)</c>.
    /// </summary>
    public static byte[] MinimalRgba8()
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 1);
        ihdr[8] = 8;
        ihdr[9] = 6;  // color type RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        // 1 row × 4 bytes (RGBA) prefixed with filter byte = 5 bytes.
        var raw = new byte[] { 0x00, 0xFF, 0x80, 0x40, 0xC0 };
        return BuildPng(ihdr, ZlibCompress(raw));
    }

    /// <summary>
    /// Minimal indexed8 PNG (1×1, color type 3) with a binary tRNS chunk marking
    /// palette index 0 as transparent. Drives <c>PngImageXObject</c>'s color-key
    /// /Mask branch.
    /// </summary>
    public static byte[] MinimalIndexed8WithBinaryTrns()
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 1);
        ihdr[8] = 8;
        ihdr[9] = 3;  // color type indexed
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;

        // PLTE chunk: 3 entries × RGB = 9 bytes
        var plte = new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF };
        // tRNS chunk: 3 entries (binary alpha — 0 transparent, 0xFF opaque)
        var trns = new byte[] { 0x00, 0xFF, 0xFF };
        // 1 row × 1 byte (index 0) prefixed with filter byte = 2 bytes.
        var raw = new byte[] { 0x00, 0x00 };
        return BuildPngIndexed(ihdr, plte, trns, ZlibCompress(raw));
    }

    private static byte[] BuildPng(byte[] ihdrData, byte[] idatData)
    {
        var output = new List<byte>(64 + idatData.Length);
        WriteSignature(output);
        WriteChunk(output, "IHDR"u8.ToArray(), ihdrData);
        WriteChunk(output, "IDAT"u8.ToArray(), idatData);
        WriteChunk(output, "IEND"u8.ToArray(), []);
        return output.ToArray();
    }

    private static byte[] BuildPngIndexed(byte[] ihdrData, byte[] plteData, byte[] trnsData, byte[] idatData)
    {
        var output = new List<byte>(96 + idatData.Length);
        WriteSignature(output);
        WriteChunk(output, "IHDR"u8.ToArray(), ihdrData);
        WriteChunk(output, "PLTE"u8.ToArray(), plteData);
        WriteChunk(output, "tRNS"u8.ToArray(), trnsData);
        WriteChunk(output, "IDAT"u8.ToArray(), idatData);
        WriteChunk(output, "IEND"u8.ToArray(), []);
        return output.ToArray();
    }

    private static void WriteSignature(List<byte> output)
    {
        Span<byte> sig = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(sig[..4], PngSignatureMagic1);
        BinaryPrimitives.WriteUInt32BigEndian(sig[4..], PngSignatureMagic2);
        for (var i = 0; i < 8; i++) output.Add(sig[i]);
    }

    private static void WriteChunk(List<byte> output, byte[] type, byte[] data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        for (var i = 0; i < 4; i++) output.Add(lenBytes[i]);
        output.AddRange(type);
        output.AddRange(data);
        // CRC covers type + data, NOT length.
        var crcInput = new byte[type.Length + data.Length];
        Buffer.BlockCopy(type, 0, crcInput, 0, type.Length);
        Buffer.BlockCopy(data, 0, crcInput, type.Length, data.Length);
        var crc = Crc32(crcInput);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        for (var i = 0; i < 4; i++) output.Add(crcBytes[i]);
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        return output.ToArray();
    }

    /// <summary>
    /// IEEE 802.3 CRC-32 — the polynomial used by PNG chunk CRCs. Inline so TestKit
    /// has no dependency on System.IO.Hashing.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
