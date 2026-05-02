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

    /// <summary>
    /// Auxiliary table tag <c>"wxyz"</c> — sorts after every real OpenType tag so the
    /// directory remains tag-ascending after insertion.
    /// </summary>
    public const uint AuxiliaryTableTag = 0x7778797Au;

    /// <summary>
    /// Builds a WOFF byte stream whose largest table is guaranteed to be zlib-compressed
    /// (not stored), used by tests that need to exercise the decompressor / corruption
    /// paths deterministically. Achieved by appending an auxiliary
    /// <see cref="AuxiliaryTableTag"/>-tagged table of <paramref name="auxiliarySize"/>
    /// zero bytes to the synthetic font — zlib shrinks all-zero input by ~99.5%, so
    /// the table is always stored compressed.
    /// </summary>
    /// <remarks>
    /// <c>OpenTypeFont.Parse</c> ignores unknown tags, so the SFNT produced by
    /// <c>WoffDecoder.Decode</c> still round-trips through the OpenType parser when a
    /// test wants the full pipeline.
    /// </remarks>
    public static byte[] BuildWithLargeCompressibleTable(int auxiliarySize = 4096)
    {
        if (auxiliarySize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(auxiliarySize), auxiliarySize, "Auxiliary table size must be positive.");
        }
        var sfnt = AppendAuxiliaryTable(SyntheticFont.Build(), AuxiliaryTableTag, new byte[auxiliarySize]);
        return Build(sfnt);
    }

    /// <summary>
    /// Build a conforming WOFF byte stream where the directory is in canonical
    /// tag-ascending order but the on-disk payload is laid out in reverse offset order
    /// — exercising the case W3C WOFF 1.0 §3 explicitly allows ("the order in which the
    /// tables appear in the WOFF file is not specified") that the layout validator must
    /// accept.
    /// </summary>
    /// <remarks>
    /// Method: build a normal synthetic WOFF, then re-lay the payload tables in reverse
    /// offset order while keeping the directory bytes themselves in tag-sorted order
    /// (only the offset fields change to point at the new positions). Padding between
    /// tables stays zeroed; total length is preserved (re-derived as a multiple of 4).
    /// </remarks>
    public static byte[] BuildWithReversedPayloadOrder()
    {
        var standard = Build();
        return ReorderPayloadDescendingOffset(standard);
    }

    private static byte[] ReorderPayloadDescendingOffset(byte[] standard)
    {
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(standard.AsSpan(12, 2));

        // Read all entry fields (tag, offset, compLength, origLength, origChecksum).
        var tag = new uint[numTables];
        var origOffset = new uint[numTables];
        var compLen = new uint[numTables];
        var origLen = new uint[numTables];
        var checksum = new uint[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var rec = WoffHeaderSize + (i * WoffDirectoryRecordSize);
            tag[i] = BinaryPrimitives.ReadUInt32BigEndian(standard.AsSpan(rec, 4));
            origOffset[i] = BinaryPrimitives.ReadUInt32BigEndian(standard.AsSpan(rec + 4, 4));
            compLen[i] = BinaryPrimitives.ReadUInt32BigEndian(standard.AsSpan(rec + 8, 4));
            origLen[i] = BinaryPrimitives.ReadUInt32BigEndian(standard.AsSpan(rec + 12, 4));
            checksum[i] = BinaryPrimitives.ReadUInt32BigEndian(standard.AsSpan(rec + 16, 4));
        }

        // Snapshot table bytes from their current positions.
        var bytes = new byte[numTables][];
        for (var i = 0; i < numTables; i++)
        {
            bytes[i] = standard.AsSpan((int)origOffset[i], (int)compLen[i]).ToArray();
        }

        // New placement order: original-offset-descending. The first slot in physical
        // layout receives the table that originally had the LARGEST offset.
        var placementOrder = new int[numTables];
        for (var i = 0; i < numTables; i++) placementOrder[i] = i;
        Array.Sort(placementOrder, (a, b) => origOffset[b].CompareTo(origOffset[a]));

        // Compute new offsets: contiguous after the directory with up-to-3-byte alignment
        // padding between tables (no padding after the last on-disk table).
        var directorySize = numTables * WoffDirectoryRecordSize;
        var firstTableOffset = WoffHeaderSize + directorySize;
        var newOffsetByEntry = new uint[numTables];
        var cursor = firstTableOffset;
        for (var slot = 0; slot < placementOrder.Length; slot++)
        {
            var entryIndex = placementOrder[slot];
            newOffsetByEntry[entryIndex] = (uint)cursor;
            cursor += bytes[entryIndex].Length;
            if (slot < placementOrder.Length - 1)
            {
                cursor = AlignTo4(cursor);
            }
        }
        var newLength = cursor;

        var output = new byte[newLength];
        // Copy header verbatim then patch length.
        standard.AsSpan(0, WoffHeaderSize).CopyTo(output);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8, 4), (uint)newLength);

        // Directory in tag-sorted order (preserved) with new offsets.
        for (var i = 0; i < numTables; i++)
        {
            var rec = WoffHeaderSize + (i * WoffDirectoryRecordSize);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(rec, 4), tag[i]);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(rec + 4, 4), newOffsetByEntry[i]);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(rec + 8, 4), compLen[i]);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(rec + 12, 4), origLen[i]);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(rec + 16, 4), checksum[i]);
        }

        // Copy each table's bytes to its new physical position.
        for (var i = 0; i < numTables; i++)
        {
            bytes[i].CopyTo(output, (int)newOffsetByEntry[i]);
        }
        return output;
    }

    /// <summary>
    /// Find the directory index of <paramref name="tag"/> in a WOFF byte stream. Returns
    /// -1 if the tag is absent. Useful for tests that mutate a specific table's record
    /// or payload after the synthetic builder runs.
    /// </summary>
    public static int FindDirectoryIndex(ReadOnlySpan<byte> woffBytes, uint tag)
    {
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(woffBytes.Slice(12, 2));
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = WoffHeaderSize + (i * WoffDirectoryRecordSize);
            var entryTag = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.Slice(recordOffset, 4));
            if (entryTag == tag) return i;
        }
        return -1;
    }

    /// <summary>
    /// Append a new table to an SFNT byte stream by extending the directory and
    /// concatenating the payload in tag-sorted order. Recomputes
    /// <c>head.checkSumAdjustment</c> so the resulting SFNT remains internally consistent.
    /// </summary>
    private static byte[] AppendAuxiliaryTable(byte[] sfntBytes, uint tag, byte[] payload)
    {
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        const uint checkSumAdjustmentMagic = 0xB1B0AFBAu;

        var oldNumTables = BinaryPrimitives.ReadUInt16BigEndian(sfntBytes.AsSpan(4, 2));
        var newNumTables = oldNumTables + 1;
        var oldDirectorySize = oldNumTables * recordSize;
        var newDirectorySize = newNumTables * recordSize;
        var oldDirectory = sfntBytes.AsSpan(sfntHeaderSize, oldDirectorySize).ToArray();

        var paddedPayloadLength = AlignTo4(payload.Length);
        var insertIndex = (int)oldNumTables;
        for (var i = 0; i < oldNumTables; i++)
        {
            var entryTag = BinaryPrimitives.ReadUInt32BigEndian(oldDirectory.AsSpan(i * recordSize, 4));
            if (tag < entryTag)
            {
                insertIndex = i;
                break;
            }
        }

        long oldPayloadTotal = 0;
        for (var i = 0; i < oldNumTables; i++)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(oldDirectory.AsSpan((i * recordSize) + 12, 4));
            oldPayloadTotal += AlignTo4((int)len);
        }

        var newSize = sfntHeaderSize + newDirectorySize + (int)oldPayloadTotal + paddedPayloadLength;
        var output = new byte[newSize];
        var span = output.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span[..4], BinaryPrimitives.ReadUInt32BigEndian(sfntBytes.AsSpan(0, 4)));
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)newNumTables);
        var (searchRange, entrySelector, rangeShift) = BinarySearchHeaderFields(newNumTables);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], rangeShift);

        var firstPayloadOffset = sfntHeaderSize + newDirectorySize;
        var directoryPos = sfntHeaderSize;
        var payloadCursor = firstPayloadOffset;

        for (var i = 0; i < newNumTables; i++)
        {
            uint entryTag, oldChecksum, oldLength;
            byte[] tablePayload;
            int paddedLen;
            if (i == insertIndex)
            {
                entryTag = tag;
                oldChecksum = ComputeTableChecksum(payload);
                oldLength = (uint)payload.Length;
                tablePayload = payload;
                paddedLen = paddedPayloadLength;
            }
            else
            {
                var oldRecordIdx = i < insertIndex ? i : i - 1;
                entryTag = BinaryPrimitives.ReadUInt32BigEndian(oldDirectory.AsSpan(oldRecordIdx * recordSize, 4));
                oldChecksum = BinaryPrimitives.ReadUInt32BigEndian(oldDirectory.AsSpan((oldRecordIdx * recordSize) + 4, 4));
                var oldOffset = BinaryPrimitives.ReadUInt32BigEndian(oldDirectory.AsSpan((oldRecordIdx * recordSize) + 8, 4));
                oldLength = BinaryPrimitives.ReadUInt32BigEndian(oldDirectory.AsSpan((oldRecordIdx * recordSize) + 12, 4));
                tablePayload = sfntBytes.AsSpan((int)oldOffset, (int)oldLength).ToArray();
                paddedLen = AlignTo4((int)oldLength);
            }

            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos, 4), entryTag);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 4, 4), oldChecksum);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 8, 4), (uint)payloadCursor);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 12, 4), oldLength);
            directoryPos += recordSize;

            tablePayload.AsSpan().CopyTo(span[payloadCursor..]);
            payloadCursor += paddedLen;
        }

        for (var i = 0; i < newNumTables; i++)
        {
            var entryTag = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(sfntHeaderSize + (i * recordSize), 4));
            if (entryTag == 0x68656164u)
            {
                var headOffset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(sfntHeaderSize + (i * recordSize) + 8, 4));
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice((int)headOffset + 8, 4), 0u);
                var fileChecksum = ComputeTableChecksum(output);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice((int)headOffset + 8, 4), checkSumAdjustmentMagic - fileChecksum);
                break;
            }
        }
        return output;
    }

    private static uint ComputeTableChecksum(byte[] tablePadded)
    {
        var sum = 0u;
        var span = tablePadded.AsSpan();
        for (var i = 0; i + 4 <= span.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i, 4));
        }
        var remainder = span.Length & 3;
        if (remainder != 0)
        {
            Span<byte> tail = stackalloc byte[4];
            span[(span.Length - remainder)..].CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32BigEndian(tail);
        }
        return sum;
    }

    private static (ushort SearchRange, ushort EntrySelector, ushort RangeShift) BinarySearchHeaderFields(int numTables)
    {
        var pow2 = 1;
        var entrySelector = 0;
        while (pow2 * 2 <= numTables)
        {
            pow2 *= 2;
            entrySelector++;
        }
        var searchRange = (ushort)(pow2 * 16);
        var rangeShift = (ushort)((numTables * 16) - searchRange);
        return (searchRange, (ushort)entrySelector, rangeShift);
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

        // Layout: 44-byte header + numTables × 20-byte directory + concatenated tables
        // with up-to-3 byte zero-padding BETWEEN tables for 4-byte alignment of the next
        // table. The spec forbids extraneous data after the last block, so the file ends
        // exactly at lastTable.Offset + lastTable.CompLength when there is no metadata
        // or private block (per WOFF 1.0 §3 and the strict layout validator).
        var directorySize = numTables * WoffDirectoryRecordSize;
        var firstTableOffset = WoffHeaderSize + directorySize;
        var cursor = firstTableOffset;
        var woffOffsets = new uint[numTables];
        for (var i = 0; i < numTables; i++)
        {
            woffOffsets[i] = (uint)cursor;
            cursor += sfntTables[i].CompressedBytes.Length;
            // Pad-to-4 between tables only — never after the final table.
            if (i < numTables - 1)
            {
                cursor = AlignTo4(cursor);
            }
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
