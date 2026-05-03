// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Builds JPEG byte streams for parser tests. The "Build" entry produces a stream that
/// passes the parser's full trust-boundary contract (SOI → optional APP14/APP2 → SOFn →
/// SOS scan-header + minimal entropy data → EOI) so tests can drive the happy path.
/// Specialized "BuildHeaderOnly" / "BuildWithoutSos" / "BuildSofnLengthMismatch" entries
/// produce intentionally malformed streams to drive each reject branch.
/// </summary>
internal static class SyntheticJpeg
{
    /// <summary>
    /// Build a JPEG byte stream with a complete SOI→...→SOFn→SOS+scan→EOI structure.
    /// Defaults to SOF0 (baseline) RGB with 8-bit precision.
    /// </summary>
    public static byte[] BuildBaseline(
        ushort width,
        ushort height,
        byte componentCount,
        byte precision = 8,
        byte sofMarker = 0xC0,
        int? adobeColorTransform = null,
        int? adobeColorTransformAfterSofn = null,
        bool includeIccProfile = false)
    {
        using var ms = new MemoryStream();
        WriteSoi(ms);
        if (adobeColorTransform is { } ctBefore)
        {
            WriteApp14(ms, ctBefore);
        }
        if (includeIccProfile)
        {
            WriteApp2IccProfile(ms);
        }
        WriteSofn(ms, sofMarker, precision, height, width, componentCount);
        if (adobeColorTransformAfterSofn is { } ctAfter)
        {
            WriteApp14(ms, ctAfter);
        }
        WriteSosWithMinimalScan(ms, componentCount);
        WriteEoi(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a JPEG byte stream that has SOI + DQT + COM segments before the SOFn frame
    /// header (then continues SOS + EOI). Drives the "skip arbitrary-length pre-SOFn
    /// segments" parser path.
    /// </summary>
    public static byte[] BuildWithPreSofnSegments(ushort width, ushort height, byte componentCount)
    {
        using var ms = new MemoryStream();
        WriteSoi(ms);
        // COM (comment) — variable length.
        ms.WriteByte(0xFF); ms.WriteByte(0xFE);
        WriteUInt16BE(ms, 6); ms.Write([(byte)'h', (byte)'i', 0, 0]);
        // DQT — quantization table.
        ms.WriteByte(0xFF); ms.WriteByte(0xDB);
        WriteUInt16BE(ms, 67);
        ms.WriteByte(0x00);
        for (var k = 0; k < 64; k++) ms.WriteByte(1);
        // SOF0
        WriteSofn(ms, 0xC0, 8, height, width, componentCount);
        WriteSosWithMinimalScan(ms, componentCount);
        WriteEoi(ms);
        return ms.ToArray();
    }

    /// <summary>Build a JPEG that ends after SOFn — no SOS, no EOI. Should be rejected.</summary>
    public static byte[] BuildHeaderOnly(ushort width, ushort height, byte componentCount)
    {
        using var ms = new MemoryStream();
        WriteSoi(ms);
        WriteSofn(ms, 0xC0, 8, height, width, componentCount);
        return ms.ToArray();
    }

    /// <summary>Build a JPEG with SOI + SOFn + EOI — no SOS at all. Should be rejected.</summary>
    public static byte[] BuildWithoutSos(ushort width, ushort height, byte componentCount)
    {
        using var ms = new MemoryStream();
        WriteSoi(ms);
        WriteSofn(ms, 0xC0, 8, height, width, componentCount);
        WriteEoi(ms);
        return ms.ToArray();
    }

    /// <summary>Build a JPEG with SOFn whose declared segment length is too short for Nf.</summary>
    public static byte[] BuildSofnLengthMismatch(ushort width, ushort height, byte componentCount)
    {
        using var ms = new MemoryStream();
        WriteSoi(ms);
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        // Declared segment length: legitimate prefix + per-component records would need
        // 8 + 3 * componentCount; we deliberately write 8 (not enough room for components).
        WriteUInt16BE(ms, 8);
        ms.WriteByte(8); WriteUInt16BE(ms, height); WriteUInt16BE(ms, width);
        ms.WriteByte(componentCount);
        // No per-component records — total segment is short.
        WriteEoi(ms);
        return ms.ToArray();
    }

    /// <summary>Build a JPEG with SOI + SOS but truncated entropy-coded data (no EOI).</summary>
    public static byte[] BuildTruncatedScan(ushort width, ushort height, byte componentCount)
    {
        using var ms = new MemoryStream();
        WriteSoi(ms);
        WriteSofn(ms, 0xC0, 8, height, width, componentCount);
        // SOS header but only a single scan byte then truncate (no EOI).
        WriteSosWithMinimalScan(ms, componentCount);
        // Strip the EOI from the final stream.
        var bytes = ms.ToArray();
        // Last 2 bytes are 0xFF 0xD9 from WriteSosWithMinimalScan? No — WriteSosWithMinimalScan
        // doesn't write EOI. So bytes already lacks EOI. Good.
        return bytes;
    }

    // ───── Marker writers ────────────────────────────────────────────────────

    private static void WriteSoi(Stream s) { s.WriteByte(0xFF); s.WriteByte(0xD8); }
    private static void WriteEoi(Stream s) { s.WriteByte(0xFF); s.WriteByte(0xD9); }

    private static void WriteApp14(Stream s, int colorTransform)
    {
        s.WriteByte(0xFF); s.WriteByte(0xEE);
        WriteUInt16BE(s, 14);
        s.Write([(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e']);
        WriteUInt16BE(s, 100); // version
        WriteUInt16BE(s, 0);   // flags0
        WriteUInt16BE(s, 0);   // flags1
        s.WriteByte((byte)colorTransform);
    }

    private static void WriteApp2IccProfile(Stream s)
    {
        // Minimal APP2 with "ICC_PROFILE\0" tag + 2-byte chunk header + 4 bytes of data.
        s.WriteByte(0xFF); s.WriteByte(0xE2);
        var tag = "ICC_PROFILE\0"u8;
        var payload = new byte[tag.Length + 2 + 4];
        tag.CopyTo(payload);
        payload[tag.Length] = 1;     // chunk number
        payload[tag.Length + 1] = 1; // chunk count
        // 4 bytes of dummy ICC data
        WriteUInt16BE(s, (ushort)(2 + payload.Length));
        s.Write(payload);
    }

    private static void WriteSofn(Stream s, byte sofMarker, byte precision, ushort height, ushort width, byte componentCount)
    {
        s.WriteByte(0xFF); s.WriteByte(sofMarker);
        WriteUInt16BE(s, (ushort)(8 + 3 * componentCount));
        s.WriteByte(precision);
        WriteUInt16BE(s, height);
        WriteUInt16BE(s, width);
        s.WriteByte(componentCount);
        for (var i = 0; i < componentCount; i++)
        {
            s.WriteByte((byte)(i + 1));  // component id
            s.WriteByte(0x11);           // sampling factors
            s.WriteByte(0);              // quant table id
        }
    }

    private static void WriteSosWithMinimalScan(Stream s, byte componentCount)
    {
        // SOS segment header: marker + length + Ns + Ns × 2 bytes per component selector + 3 bytes Ss/Se/AhAl.
        s.WriteByte(0xFF); s.WriteByte(0xDA);
        WriteUInt16BE(s, (ushort)(6 + 2 * componentCount));
        s.WriteByte(componentCount);
        for (var i = 0; i < componentCount; i++)
        {
            s.WriteByte((byte)(i + 1));  // component selector
            s.WriteByte(0x00);           // table specifier
        }
        s.WriteByte(0x00); s.WriteByte(0x3F); s.WriteByte(0x00); // Ss=0, Se=63, AhAl=0
        // Minimal entropy-coded data: a single non-0xFF byte. Real decoders can't decode
        // this back into pixels but the parser only needs structural validity.
        s.WriteByte(0x00);
    }

    private static void WriteUInt16BE(Stream s, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        s.Write(buf);
    }
}
