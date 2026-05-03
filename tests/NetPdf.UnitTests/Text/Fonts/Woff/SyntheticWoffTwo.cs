// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;
using NetPdf.Text.Fonts.Woff;
using NetPdf.UnitTests.Text.Fonts.OpenType;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Builds a minimal-but-valid WOFF 2.0 byte stream wrapping <see cref="SyntheticFont"/>
/// using the null transform on every table (including <c>glyf</c> / <c>loca</c> via
/// transform version 3). Tests that exercise <see cref="WoffTwoDecoder"/> feed the
/// produced bytes through the decoder and assert round-trip equivalence.
/// </summary>
internal static class SyntheticWoffTwo
{
    /// <summary>
    /// Produce a WOFF 2.0 byte stream over the SyntheticFont with all-null transforms
    /// and glyf/loca placed adjacently in the directory.
    /// </summary>
    public static byte[] BuildNullTransform() => Build(forceGlyfLocaAdjacent: true);

    /// <summary>
    /// Produce a WOFF 2.0 byte stream over the SyntheticFont with all-null transforms
    /// and glyf/loca <i>NOT</i> adjacent in the directory (interleaved with head/hhea/etc.).
    /// Per WOFF 2.0 §5.1 adjacency is required only for the transformed pair, so this
    /// layout is conformant with version-3 (null) transforms — the decoder must accept it.
    /// </summary>
    public static byte[] BuildNullTransformNonAdjacentGlyfLoca() => Build(forceGlyfLocaAdjacent: false);

    private static byte[] Build(bool forceGlyfLocaAdjacent)
    {
        var sfntBytes = SyntheticFont.Build();
        var sfntSpan = sfntBytes.AsSpan();
        // Walk SFNT directory.
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfntSpan[4..6]);
        const int sfntHeaderSize = 12;
        const int recordSize = 16;

        var tables = new (uint Tag, byte[] Bytes)[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var rec = sfntHeaderSize + i * recordSize;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(sfntSpan[rec..(rec + 4)]);
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(sfntSpan[(rec + 8)..(rec + 12)]);
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(sfntSpan[(rec + 12)..(rec + 16)]);
            tables[i] = (tag, sfntBytes[off..(off + len)]);
        }

        // Sort tables by tag for the WOFF 2.0 directory base ordering.
        Array.Sort(tables, static (a, b) => a.Tag.CompareTo(b.Tag));

        // glyf (0x676C7966) and loca (0x6C6F6361) are NOT naturally adjacent after sort —
        // head/hhea/hmtx/maxp interleave between them. With the actual transform (version 0)
        // WOFF 2.0 §5.1 mandates adjacency; with null transforms (version 3) the directory
        // may interleave other tables freely. Callers control the layout via
        // <c>forceGlyfLocaAdjacent</c>.
        if (forceGlyfLocaAdjacent)
        {
            ReorderForGlyfLocaPairing(tables);
        }

        // Build directory + raw concatenated data simultaneously.
        using var dirStream = new MemoryStream();
        using var dataStream = new MemoryStream();

        foreach (var (tag, bytes) in tables)
        {
            byte transformVersion = (tag is WoffTwoTags.Glyf or WoffTwoTags.Loca or WoffTwoTags.Hmtx)
                ? (byte)3   // explicit null transform for special tables
                : (byte)0;  // version 0 = null transform for ordinary tables

            byte tagIndex = TagToKnownIndex(tag);
            byte flags = (byte)(((transformVersion & 0x03) << 6) | (tagIndex & 0x3F));
            dirStream.WriteByte(flags);
            if (tagIndex == WoffTwoConstants.CustomTagSentinel)
            {
                dirStream.WriteByte((byte)((tag >> 24) & 0xFF));
                dirStream.WriteByte((byte)((tag >> 16) & 0xFF));
                dirStream.WriteByte((byte)((tag >> 8) & 0xFF));
                dirStream.WriteByte((byte)(tag & 0xFF));
            }
            WriteUIntBase128(dirStream, (uint)bytes.Length);
            // Null transform: no transformLength.

            dataStream.Write(bytes);
        }

        var directoryBytes = dirStream.ToArray();
        var dataBytes = dataStream.ToArray();

        // Brotli-compress the data stream.
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var brotli = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                brotli.Write(dataBytes);
            }
            compressed = ms.ToArray();
        }

        // Lay out the WOFF2 file: header + directory + compressed-data, 4-byte aligned.
        var compressedAligned = AlignTo4(compressed.Length);
        var headerSize = WoffTwoConstants.HeaderSize;
        var totalSize = headerSize + directoryBytes.Length + compressedAligned;
        var output = new byte[totalSize];
        var writer = output.AsSpan();

        // Header.
        BinaryPrimitives.WriteUInt32BigEndian(writer[0..4], WoffTwoConstants.Signature);
        BinaryPrimitives.WriteUInt32BigEndian(writer[4..8], 0x00010000u); // TrueType flavor
        BinaryPrimitives.WriteUInt32BigEndian(writer[8..12], (uint)totalSize);
        BinaryPrimitives.WriteUInt16BigEndian(writer[12..14], (ushort)tables.Length);
        BinaryPrimitives.WriteUInt16BigEndian(writer[14..16], 0); // reserved
        BinaryPrimitives.WriteUInt32BigEndian(writer[16..20], (uint)sfntBytes.Length); // totalSfntSize
        BinaryPrimitives.WriteUInt32BigEndian(writer[20..24], (uint)compressed.Length); // totalCompressedSize
        BinaryPrimitives.WriteUInt16BigEndian(writer[24..26], 1); // majorVersion
        BinaryPrimitives.WriteUInt16BigEndian(writer[26..28], 0); // minorVersion
        // metaOffset/length/origLength + privOffset/length all zero.

        // Directory.
        directoryBytes.CopyTo(writer[headerSize..]);

        // Compressed data.
        var dataOffset = headerSize + directoryBytes.Length;
        compressed.CopyTo(writer[dataOffset..(dataOffset + compressed.Length)]);

        return output;
    }

    private static void ReorderForGlyfLocaPairing((uint Tag, byte[] Bytes)[] tables)
    {
        // After sort, find glyf and loca; if they are not adjacent (loca immediately after
        // glyf), swap loca into position glyf+1 and shift the displaced entry to where
        // loca was. This preserves overall tag-ascending ordering only when the swap is
        // a no-op (the typical case for SyntheticFont). For SyntheticFont's known set,
        // glyf (0x676C7966) and loca (0x6C6F6361) are consecutive after sort.
        int glyfIdx = -1, locaIdx = -1;
        for (var i = 0; i < tables.Length; i++)
        {
            if (tables[i].Tag == WoffTwoTags.Glyf) glyfIdx = i;
            else if (tables[i].Tag == WoffTwoTags.Loca) locaIdx = i;
        }
        if (glyfIdx >= 0 && locaIdx >= 0 && locaIdx != glyfIdx + 1)
        {
            (tables[glyfIdx + 1], tables[locaIdx]) = (tables[locaIdx], tables[glyfIdx + 1]);
        }
    }

    private static byte TagToKnownIndex(uint tag)
    {
        for (byte i = 0; i < WoffTwoConstants.KnownTags.Length; i++)
        {
            if (WoffTwoConstants.KnownTags[i] == tag) return i;
        }
        return WoffTwoConstants.CustomTagSentinel;
    }

    private static void WriteUIntBase128(Stream stream, uint value)
    {
        // Big-endian 7-bit groups, MSB set on continuation bytes.
        Span<byte> buf = stackalloc byte[5];
        var size = 0;
        if (value == 0)
        {
            stream.WriteByte(0);
            return;
        }
        // Compute the number of 7-bit digits required.
        var v = value;
        while (v != 0)
        {
            v >>= 7;
            size++;
        }
        for (var i = 0; i < size; i++)
        {
            var shift = 7 * (size - 1 - i);
            buf[i] = (byte)((value >> shift) & 0x7F);
            if (i < size - 1) buf[i] |= 0x80;
        }
        stream.Write(buf[..size]);
    }

    private static int AlignTo4(int n) => (n + 3) & ~3;
}
