// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Builds minimal-but-valid JPEG byte streams (SOI + optional APP14 + SOFn + EOI) for
/// header-parser tests. The streams contain no actual scan data — they exist only to
/// drive the parser through every recognized marker path.
/// </summary>
internal static class SyntheticJpeg
{
    /// <summary>
    /// Build a JPEG byte stream with a single SOFn frame header. <paramref name="sofMarker"/>
    /// is the second byte of the marker code (e.g., 0xC0 = SOF0 baseline, 0xC2 = SOF2
    /// progressive). No APP14 marker is included; the parser will treat 4-component output
    /// as standard CMYK (not Adobe-inverted).
    /// </summary>
    public static byte[] BuildBaseline(
        ushort width,
        ushort height,
        byte componentCount,
        byte precision = 8,
        byte sofMarker = 0xC0,
        int? adobeColorTransform = null)
    {
        using var ms = new MemoryStream();
        // SOI.
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD8);

        // Optional APP14 (Adobe) before SOFn.
        if (adobeColorTransform is { } ct)
        {
            ms.WriteByte(0xFF);
            ms.WriteByte(0xEE);                // APP14
            WriteUInt16BE(ms, 14);             // segment length (incl. length bytes)
            ms.Write([(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e']);
            WriteUInt16BE(ms, 100);            // version
            WriteUInt16BE(ms, 0);              // flags0
            WriteUInt16BE(ms, 0);              // flags1
            ms.WriteByte((byte)ct);            // ColorTransform
        }

        // SOFn segment.
        ms.WriteByte(0xFF);
        ms.WriteByte(sofMarker);
        // Segment length: 2 (length itself) + 1 precision + 2 height + 2 width + 1 nf +
        // 3 × componentCount per-component records.
        var sofSegmentLength = (ushort)(8 + 3 * componentCount);
        WriteUInt16BE(ms, sofSegmentLength);
        ms.WriteByte(precision);
        WriteUInt16BE(ms, height);
        WriteUInt16BE(ms, width);
        ms.WriteByte(componentCount);
        // Per-component info: 1 byte id, 1 byte sampling, 1 byte quant table id.
        for (var i = 0; i < componentCount; i++)
        {
            ms.WriteByte((byte)(i + 1));
            ms.WriteByte(0x11);
            ms.WriteByte(0);
        }

        // EOI (parser doesn't require it for SOFn-only headers, but real JPEGs end with it).
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD9);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a JPEG byte stream with several pre-SOFn segments (DQT, DHT, COM) so the
    /// parser exercises the "skip arbitrary-length segment" path before reaching the SOFn.
    /// </summary>
    public static byte[] BuildWithPreSofnSegments(ushort width, ushort height, byte componentCount)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8); // SOI
        // COM (comment).
        ms.WriteByte(0xFF); ms.WriteByte(0xFE);
        WriteUInt16BE(ms, 6); // length 6 = 2 + 4 bytes payload
        ms.Write([(byte)'h', (byte)'i', 0, 0]);
        // DQT.
        ms.WriteByte(0xFF); ms.WriteByte(0xDB);
        WriteUInt16BE(ms, 67); // length 67 = 2 + 1 + 64 bytes payload
        ms.WriteByte(0x00);
        for (var k = 0; k < 64; k++) ms.WriteByte(1);
        // SOF0.
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        WriteUInt16BE(ms, (ushort)(8 + 3 * componentCount));
        ms.WriteByte(8); // precision
        WriteUInt16BE(ms, height);
        WriteUInt16BE(ms, width);
        ms.WriteByte(componentCount);
        for (var i = 0; i < componentCount; i++)
        {
            ms.WriteByte((byte)(i + 1)); ms.WriteByte(0x11); ms.WriteByte(0);
        }
        ms.WriteByte(0xFF); ms.WriteByte(0xD9); // EOI
        return ms.ToArray();
    }

    private static void WriteUInt16BE(Stream s, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        s.Write(buf);
    }
}
