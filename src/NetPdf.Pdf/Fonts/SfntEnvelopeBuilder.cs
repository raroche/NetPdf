// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Assembles the SFNT envelope embedded as <c>FontFile2</c> inside a CIDFontType2 PDF
/// font. Takes a tag → bytes table set, lays out a fresh SFNT (header + sorted directory
/// + 4-byte-aligned tables), computes per-table checksums, and patches
/// <c>head.checkSumAdjustment</c> with the canonical <c>0xB1B0AFBA - file_checksum</c>
/// value.
/// </summary>
/// <remarks>
/// <para>
/// Tables are written in canonical (sorted by tag) on-disk order. The historical
/// <c>searchRange</c> / <c>entrySelector</c> / <c>rangeShift</c> SFNT-header fields are
/// computed correctly even though modern PDF readers ignore them — some legacy validators
/// still inspect them.
/// </para>
/// <para>
/// <b>Output is byte-deterministic</b> for byte-equal inputs. Caller is responsible for
/// supplying byte-equal subset table content; this builder adds no nondeterminism.
/// </para>
/// <para>
/// <b>Phase 1 scope.</b> TTF only. CFF embedding (<c>FontFile3</c> with subtype
/// <c>CIDFontType0C</c>) is the deferred CFF half of Tasks 8 / 10.
/// </para>
/// </remarks>
internal static class SfntEnvelopeBuilder
{
    public const uint TtfScalerType = 0x00010000u;
    public const uint CheckSumAdjustmentMagic = 0xB1B0AFBAu;
    public const int SfntHeaderSize = 12;
    public const int DirectoryRecordSize = 16;

    /// <summary>
    /// Build a TTF SFNT byte stream from <paramref name="tables"/>. The set must include
    /// <c>head</c> (so <c>checkSumAdjustment</c> can be patched) and at least one other
    /// table; missing <c>head</c> throws.
    /// </summary>
    public static byte[] BuildTtf(IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0)
        {
            throw new ArgumentException("SfntEnvelopeBuilder requires at least one table.", nameof(tables));
        }
        if (!tables.ContainsKey(OpenTypeTags.Head))
        {
            throw new ArgumentException(
                "SfntEnvelopeBuilder.BuildTtf requires a 'head' table — checkSumAdjustment is patched there.",
                nameof(tables));
        }

        // Sort tags ascending for canonical directory order. Avoid LINQ in this hot-ish path.
        var sortedTags = new uint[tables.Count];
        var idx = 0;
        foreach (var tag in tables.Keys)
        {
            sortedTags[idx++] = tag;
        }
        Array.Sort(sortedTags);

        // Compute table offsets (4-byte aligned).
        var directorySize = DirectoryRecordSize * sortedTags.Length;
        var firstTableOffset = SfntHeaderSize + directorySize;
        var offsets = new uint[sortedTags.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < sortedTags.Length; i++)
        {
            offsets[i] = (uint)cursor;
            cursor += AlignTo4(tables[sortedTags[i]].Length);
        }
        var totalSize = cursor;
        var output = new byte[totalSize];
        var span = output.AsSpan();

        // SFNT header.
        var (searchRange, entrySelector, rangeShift) = BinarySearchHeader(sortedTags.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], TtfScalerType);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)sortedTags.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], rangeShift);

        // Copy table bytes to their offsets, then compute per-table checksums and write
        // the directory. Padding bytes between tables are already zero (new byte[]).
        var directoryPos = SfntHeaderSize;
        for (var i = 0; i < sortedTags.Length; i++)
        {
            var tag = sortedTags[i];
            var tableBytes = tables[tag];
            var tableOffset = (int)offsets[i];
            tableBytes.Span.CopyTo(span[tableOffset..]);

            var paddedLength = AlignTo4(tableBytes.Length);
            var checksum = ComputeTableChecksum(span.Slice(tableOffset, paddedLength));

            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos, 4), tag);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 4, 4), checksum);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 8, 4), offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 12, 4), (uint)tableBytes.Length);
            directoryPos += DirectoryRecordSize;
        }

        // Patch head.checkSumAdjustment. The OpenType spec defines this as
        // 0xB1B0AFBA - file_checksum (computed with checkSumAdjustment temporarily zero).
        // Our subset emitter (Task 8) already zeroes the field, so the computed file
        // checksum already reflects the "checkSumAdjustment = 0" state.
        var fileChecksum = ComputeTableChecksum(span);
        var headIndex = Array.IndexOf(sortedTags, OpenTypeTags.Head);
        var headOffset = (int)offsets[headIndex];
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(headOffset + 8, 4), CheckSumAdjustmentMagic - fileChecksum);

        return output;
    }

    /// <summary>
    /// SFNT checksum: sum of big-endian 4-byte uints over the table bytes (with wraparound).
    /// Trailing bytes that don't form a complete 4-byte chunk are zero-padded — our caller
    /// hands us already-4-byte-aligned spans, so the loop is straightforward.
    /// </summary>
    private static uint ComputeTableChecksum(ReadOnlySpan<byte> tablePadded)
    {
        var sum = 0u;
        for (var i = 0; i + 4 <= tablePadded.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(tablePadded.Slice(i, 4));
        }
        // Handle any sub-4-byte tail: pad with zeros (right-align as if the missing bytes are 0).
        var remainder = tablePadded.Length & 3;
        if (remainder != 0)
        {
            Span<byte> tail = stackalloc byte[4];
            tablePadded[(tablePadded.Length - remainder)..].CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32BigEndian(tail);
        }
        return sum;
    }

    /// <summary>Binary-search header field calculation — historical fields per OpenType §"Organization of an OpenType font".</summary>
    private static (ushort SearchRange, ushort EntrySelector, ushort RangeShift) BinarySearchHeader(int numTables)
    {
        // largestPowerOfTwo ≤ numTables, then × 16.
        var pow2 = 1;
        var entrySelector = 0;
        while ((pow2 * 2) <= numTables)
        {
            pow2 *= 2;
            entrySelector++;
        }
        var searchRange = (ushort)(pow2 * 16);
        var rangeShift = (ushort)((numTables * 16) - searchRange);
        return (searchRange, (ushort)entrySelector, rangeShift);
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
