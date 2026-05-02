// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;
using NetPdf.UnitTests.Text.Fonts.OpenType;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Builds a valid WOFF byte stream from <see cref="SyntheticFont"/>'s SFNT output. Used
/// by the WOFF decoder tests to drive the full SFNT → WOFF → SFNT round-trip end-to-end
/// without depending on a real shipped font.
/// </summary>
/// <remarks>
/// <para>
/// Each table is independently zlib-compressed via <see cref="ZLibStream"/>; if the
/// compressed bytes are not strictly shorter than the original, the table is stored
/// uncompressed (matching real WOFF encoder behavior). The 4-byte alignment between
/// tables is honored. The 44-byte header is filled in with consistent length /
/// totalSfntSize / numTables values; metadata and private blocks are absent.
/// </para>
/// <para>
/// The builder is parametric on the input SFNT bytes so tests that need an OTF/CFF
/// flavor or a hand-mutated SFNT can pass those in too.
/// </para>
/// </remarks>
internal static class SyntheticWoff
{
    private const uint WoffSignature = 0x774F4646u; // "wOFF"
    private const int WoffHeaderSize = 44;
    private const int WoffDirectoryRecordSize = 20;
    private const int SfntHeaderSize = 12;
    private const int SfntDirectoryRecordSize = 16;

    /// <summary>Wraps the synthetic TTF from <see cref="SyntheticFont.Build"/> as a WOFF byte stream.</summary>
    public static byte[] Build()
    {
        return Build(SyntheticFont.Build());
    }

    /// <summary>Wraps an arbitrary SFNT byte stream as a WOFF byte stream.</summary>
    public static byte[] Build(byte[] sfntBytes)
    {
        ArgumentNullException.ThrowIfNull(sfntBytes);
        var sfntSpan = sfntBytes.AsSpan();

        // Parse the SFNT header to get sfntVersion + numTables. We don't need a full parser
        // here — the directory records sit at offset 12 and each table's tag/offset/length
        // are uint32 fields at well-known positions.
        if (sfntBytes.Length < SfntHeaderSize)
        {
            throw new ArgumentException("SFNT bytes too short for an SFNT header.", nameof(sfntBytes));
        }
        var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(sfntSpan[..4]);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfntSpan[4..6]);

        // Read each SFNT directory record. Then compress each table independently.
        var sfntTables = new SfntTable[numTables];
        var directoryBase = SfntHeaderSize;
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = directoryBase + (i * SfntDirectoryRecordSize);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(sfntSpan.Slice(recordOffset, 4));
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(sfntSpan.Slice(recordOffset + 4, 4));
            var offset = BinaryPrimitives.ReadUInt32BigEndian(sfntSpan.Slice(recordOffset + 8, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(sfntSpan.Slice(recordOffset + 12, 4));

            var original = sfntBytes.AsSpan((int)offset, (int)length).ToArray();
            var compressed = TryCompress(original);
            // WOFF spec: store uncompressed when zlib does not shrink the bytes.
            var stored = compressed is null || compressed.Length >= original.Length;
            sfntTables[i] = new SfntTable
            {
                Tag = tag,
                Checksum = checksum,
                OrigLength = (uint)original.Length,
                CompressedBytes = stored ? original : compressed!,
                IsStored = stored,
            };
        }

        // Layout: 44-byte header + numTables × 20-byte directory + concatenated 4-byte-aligned
        // compressed tables. Track per-table WOFF offsets while we lay out.
        var directorySize = numTables * WoffDirectoryRecordSize;
        var firstTableOffset = WoffHeaderSize + directorySize;
        var cursor = firstTableOffset;
        var woffOffsets = new uint[numTables];
        for (var i = 0; i < numTables; i++)
        {
            woffOffsets[i] = (uint)cursor;
            cursor += AlignTo4(sfntTables[i].CompressedBytes.Length);
        }
        var woffLength = cursor;

        // totalSfntSize: header + 16-byte-per-record directory + 4-byte-aligned uncompressed
        // tables. Matches the layout WoffDecoder.AssembleSfnt produces.
        var sfntDirectorySize = numTables * SfntDirectoryRecordSize;
        var totalSfntSize = SfntHeaderSize + sfntDirectorySize;
        for (var i = 0; i < numTables; i++)
        {
            totalSfntSize += AlignTo4((int)sfntTables[i].OrigLength);
        }

        var output = new byte[woffLength];
        var span = output.AsSpan();

        // WOFF header.
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], WoffSignature);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], sfntVersion);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)woffLength);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], numTables);
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], 0);                       // reserved
        BinaryPrimitives.WriteUInt32BigEndian(span[16..20], (uint)totalSfntSize);
        BinaryPrimitives.WriteUInt16BigEndian(span[20..22], 1);                       // majorVersion (cosmetic)
        BinaryPrimitives.WriteUInt16BigEndian(span[22..24], 0);                       // minorVersion
        BinaryPrimitives.WriteUInt32BigEndian(span[24..28], 0);                       // metaOffset
        BinaryPrimitives.WriteUInt32BigEndian(span[28..32], 0);                       // metaLength
        BinaryPrimitives.WriteUInt32BigEndian(span[32..36], 0);                       // metaOrigLength
        BinaryPrimitives.WriteUInt32BigEndian(span[36..40], 0);                       // privOffset
        BinaryPrimitives.WriteUInt32BigEndian(span[40..44], 0);                       // privLength

        // Directory + table data.
        var directoryPos = WoffHeaderSize;
        for (var i = 0; i < numTables; i++)
        {
            var t = sfntTables[i];
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos, 4), t.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 4, 4), woffOffsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 8, 4), (uint)t.CompressedBytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 12, 4), t.OrigLength);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 16, 4), t.Checksum);
            directoryPos += WoffDirectoryRecordSize;

            t.CompressedBytes.AsSpan().CopyTo(span.Slice((int)woffOffsets[i]));
        }

        return output;
    }

    private static byte[]? TryCompress(byte[] original)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(original, 0, original.Length);
        }
        return ms.ToArray();
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;

    private sealed class SfntTable
    {
        public required uint Tag { get; init; }
        public required uint Checksum { get; init; }
        public required uint OrigLength { get; init; }
        public required byte[] CompressedBytes { get; init; }
        public required bool IsStored { get; init; }
    }
}
