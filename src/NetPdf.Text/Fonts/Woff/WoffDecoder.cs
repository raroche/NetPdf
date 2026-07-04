// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Decodes a W3C WOFF File Format 1.0 byte stream back into the SFNT byte stream that
/// <see cref="OpenTypeFont.Parse"/> consumes directly. WOFF is a small wrapper around an
/// SFNT font (TTF or OTF/CFF) where each table is independently zlib-compressed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> W3C "WOFF File Format 1.0" Recommendation
/// (<c>https://www.w3.org/TR/WOFF/</c>), §3 "WOFF File Format". No code transliterated
/// from any third-party implementation.
/// </para>
/// <para>
/// <b>Output shape.</b> The reconstructed SFNT has its directory in tag-sorted (canonical)
/// order, all tables 4-byte-aligned, the WOFF-preserved <c>origChecksum</c> values written
/// into each directory record, and <c>head.checkSumAdjustment</c> repatched to
/// <c>0xB1B0AFBA - file_checksum</c> per OpenType §"head". Bytes are deterministic for
/// byte-equal input.
/// </para>
/// <para>
/// <b>Scope.</b> Decodes the wrapped font tables. The optional XML metadata block and
/// private-data block are validated for consistency in the header but not extracted —
/// neither is required for rendering. WOFF2 (Brotli + glyf/loca transform) is Task 18.
/// </para>
/// <para>
/// <b>Trust boundary.</b> Every length, offset, and checksum is bounded against the
/// source buffer before allocation or decompression. Malformed inputs reject with
/// <see cref="InvalidDataException"/>; downstream <see cref="OpenTypeFont.Parse"/> applies
/// its own cross-table validation on the reconstructed bytes.
/// </para>
/// </remarks>
internal static class WoffDecoder
{
    /// <summary>SFNT header size: 12 bytes (sfntVersion + numTables + 3× search-helper fields).</summary>
    private const int SfntHeaderSize = 12;

    /// <summary>SFNT directory record size: 16 bytes (tag + checksum + offset + length).</summary>
    private const int SfntDirectoryRecordSize = 16;

    /// <summary>OpenType <c>head.checkSumAdjustment</c> magic — §"head" defines the field as <c>0xB1B0AFBA - sum(file)</c>.</summary>
    private const uint CheckSumAdjustmentMagic = 0xB1B0AFBAu;

    /// <summary>Byte offset of the <c>checkSumAdjustment</c> field inside the <c>head</c> table.</summary>
    private const int HeadCheckSumAdjustmentOffset = 8;

    /// <summary>
    /// Decode <paramref name="woffBytes"/> into a fresh SFNT byte stream. The output
    /// can be fed directly to <see cref="OpenTypeFont.Parse"/>.
    /// </summary>
    /// <remarks>
    /// Validation runs in three passes before any allocation that depends on declared
    /// lengths: header parse → directory parse → full layout validation
    /// (<see cref="WoffLayoutValidator.Validate"/>). Only after all three pass do we
    /// allocate per-table decompression buffers, so a malicious WOFF cannot drive
    /// unbounded memory use through fabricated lengths.
    /// </remarks>
    public static byte[] Decode(ReadOnlySpan<byte> woffBytes)
    {
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        WoffLayoutValidator.Validate(header, entries, woffBytes);

        // Decompress each table into its own buffer. Index-aligned with `entries` so the
        // sort below can carry decompressed bytes alongside the directory entry.
        var decompressed = new byte[entries.Length][];
        for (var i = 0; i < entries.Length; i++)
        {
            decompressed[i] = DecompressTable(woffBytes, entries[i]);
        }

        var sfnt = AssembleSfnt(header, entries, decompressed);

        // SEC-7 — re-validate the RECONSTRUCTED sfnt before any shaper sees it. The WOFF wrapper checks
        // above cannot see inside the compressed tables (danger-class tables, table count, per-table
        // bounds), so the decompressed sfnt is re-run through the same pre-decode gate a bare TTF/OTF
        // passes. This is the invariant SEC-7 requires of the (currently dormant) WOFF render wiring.
        var revalidation = FontSafetyValidator.Validate(sfnt);
        if (!revalidation.IsSafe)
        {
            throw new InvalidDataException(
                $"WOFF: reconstructed sfnt failed font-safety re-validation: {revalidation.Reason}");
        }

        return sfnt;
    }

    /// <summary>
    /// Decompresses (or copies, when stored) one WOFF table into a fresh
    /// <see cref="byte"/>[] sized to <see cref="WoffTableEntry.OrigLength"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stored vs compressed.</b> When <c>compLength == origLength</c> the table bytes
    /// are stored verbatim — no zlib wrapping. When <c>compLength &lt; origLength</c> the
    /// bytes are zlib-framed (RFC 1950 wrapper around RFC 1951 deflate); we decompress
    /// via <see cref="ZLibStream"/> which handles the zlib header + Adler-32 trailer.
    /// </para>
    /// <para>
    /// <b>Length verification.</b> We assert the decompressor produced exactly
    /// <c>origLength</c> bytes. Producing fewer means the compressed payload was
    /// truncated; producing more would mean the encoder lied about <c>origLength</c>.
    /// Both cases reject so a downstream parser never sees mismatched expectations.
    /// </para>
    /// </remarks>
    private static byte[] DecompressTable(ReadOnlySpan<byte> woffBytes, WoffTableEntry entry)
    {
        var compressedSlice = woffBytes.Slice((int)entry.Offset, (int)entry.CompLength);
        if (entry.IsStored)
        {
            return compressedSlice.ToArray();
        }

        var output = new byte[entry.OrigLength];
        // The MemoryStream wrapper is needed because ZLibStream consumes a Stream. The
        // ToArray() copy is a known perf cost — switching to ReadOnlySpan-backed input
        // requires either unsafe code or a public-API change to ReadOnlyMemory<byte>;
        // deferred per the Task 17 review until the strict-validation path is benchmarked.
        using var input = new MemoryStream(compressedSlice.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);

        try
        {
            zlib.ReadExactly(output);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                $"WOFF: table '{OpenTypeTags.ToAsciiString(entry.Tag)}' decompression produced fewer " +
                $"than the declared origLength {entry.OrigLength} bytes.", ex);
        }

        // Drain any trailing byte — origLength must exactly match the decompressed size.
        if (zlib.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"WOFF: table '{OpenTypeTags.ToAsciiString(entry.Tag)}' decompression produced more " +
                $"than the declared origLength {entry.OrigLength} bytes.");
        }
        return output;
    }

    /// <summary>
    /// Build a complete SFNT byte stream from the decompressed tables: 12-byte header,
    /// tag-sorted 16-byte-per-entry directory, 4-byte-aligned table data, and
    /// <c>head.checkSumAdjustment</c> patched to the canonical value.
    /// </summary>
    private static byte[] AssembleSfnt(WoffHeader header, WoffTableEntry[] entries, byte[][] decompressed)
    {
        // Sort entries + their decompressed bytes together by tag (canonical SFNT order).
        var sortedIndices = new int[entries.Length];
        for (var i = 0; i < sortedIndices.Length; i++)
        {
            sortedIndices[i] = i;
        }
        Array.Sort(sortedIndices, (a, b) => entries[a].Tag.CompareTo(entries[b].Tag));

        // Compute per-table SFNT offsets (4-byte aligned). Tracking total size up-front
        // lets the output be a single byte[] — no MemoryStream growth, deterministic.
        var directorySize = entries.Length * SfntDirectoryRecordSize;
        var firstTableOffset = SfntHeaderSize + directorySize;
        var sfntOffsets = new int[entries.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < entries.Length; i++)
        {
            sfntOffsets[i] = cursor;
            cursor += AlignTo4(decompressed[sortedIndices[i]].Length);
        }
        var totalSize = cursor;

        // Sanity: warn-level only — totalSfntSize is encoder-supplied. We don't trust
        // it as ground truth, but a wildly different value hints at malformed input.
        // We tolerate small differences (the spec lets encoders round it) and do not throw.

        var output = new byte[totalSize];
        var span = output.AsSpan();

        // SFNT header. The flavor (sfntVersion) carries directly from the WOFF.
        var (searchRange, entrySelector, rangeShift) = BinarySearchHeader(entries.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], header.Flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], rangeShift);

        // Copy each table's decompressed bytes to its offset; padding bytes between tables
        // are already zero (new byte[]). Then write the directory record using the
        // WOFF-preserved origChecksum so downstream consumers see the original table
        // checksums without us recomputing.
        var directoryPos = SfntHeaderSize;
        var headSfntOffset = -1;
        for (var i = 0; i < entries.Length; i++)
        {
            var srcIndex = sortedIndices[i];
            var entry = entries[srcIndex];
            var tableBytes = decompressed[srcIndex];
            var tableOffset = sfntOffsets[i];

            tableBytes.AsSpan().CopyTo(span[tableOffset..]);

            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos, 4), entry.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 4, 4), entry.OrigChecksum);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 8, 4), (uint)tableOffset);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(directoryPos + 12, 4), entry.OrigLength);
            directoryPos += SfntDirectoryRecordSize;

            if (entry.Tag == OpenTypeTags.Head)
            {
                headSfntOffset = tableOffset;
            }
        }

        if (headSfntOffset < 0)
        {
            throw new InvalidDataException("WOFF: wrapped font is missing required 'head' table.");
        }

        // Patch head.checkSumAdjustment per OpenType §"head". Zero the field first so the
        // file-level checksum reflects the canonical "checkSumAdjustment = 0" state, then
        // write 0xB1B0AFBA - file_checksum back. The original WOFF-preserved value is
        // discarded — the directory-order shuffle would have invalidated it anyway.
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(headSfntOffset + HeadCheckSumAdjustmentOffset, 4), 0u);
        var fileChecksum = ComputeFileChecksum(span);
        BinaryPrimitives.WriteUInt32BigEndian(
            span.Slice(headSfntOffset + HeadCheckSumAdjustmentOffset, 4),
            CheckSumAdjustmentMagic - fileChecksum);

        return output;
    }

    /// <summary>
    /// SFNT file checksum: sum of big-endian 4-byte uints over the entire file (with
    /// wraparound). The spec requires the file to be a multiple of 4 bytes long, which
    /// our 4-byte-aligned table layout guarantees.
    /// </summary>
    private static uint ComputeFileChecksum(ReadOnlySpan<byte> file)
    {
        var sum = 0u;
        for (var i = 0; i + 4 <= file.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(file.Slice(i, 4));
        }
        // Tail handling: caller guarantees 4-byte alignment, so the loop above covers
        // every byte. The standalone branch survives as a safety net for non-aligned
        // inputs (would only trigger on corruption above).
        var remainder = file.Length & 3;
        if (remainder != 0)
        {
            Span<byte> tail = stackalloc byte[4];
            file[(file.Length - remainder)..].CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32BigEndian(tail);
        }
        return sum;
    }

    /// <summary>
    /// SFNT search-helper fields per OpenType §"Organization of an OpenType font". Modern
    /// readers ignore them, but legacy validators inspect them — emit the spec-correct values.
    /// </summary>
    private static (ushort SearchRange, ushort EntrySelector, ushort RangeShift) BinarySearchHeader(int numTables)
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

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
