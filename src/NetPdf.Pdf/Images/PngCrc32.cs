// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Images;

/// <summary>
/// IEEE 802.3 CRC-32 used by PNG chunks per W3C PNG (Third Edition) §5.5. The
/// polynomial is <c>0xEDB88320</c> (reflected form of <c>0x04C11DB7</c>); initial
/// value <c>0xFFFFFFFF</c>; final XOR <c>0xFFFFFFFF</c>. Inline implementation —
/// roughly 30 lines of trivial data-handling code, avoids adding a runtime dependency
/// on <c>System.IO.Hashing</c>.
/// </summary>
internal static class PngCrc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Compute CRC-32 over the concatenation of <paramref name="typeBytes"/> + <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> typeBytes, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        foreach (var b in data) crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
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
