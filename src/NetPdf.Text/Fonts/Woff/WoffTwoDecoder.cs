// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// WOFF 2.0 → SFNT (TTF / OTF) byte-stream decoder. Spec basis: W3C "WOFF File Format 2.0"
/// Recommendation, 1 March 2018. Clean-room implementation from the spec; no third-party
/// implementation source consulted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 scope.</b> The decoder handles every WOFF 2.0 file whose tables either use
/// the null transform (verbatim copy) or are <c>glyf</c> / <c>loca</c> with the standard
/// transform (version 0). The optional <c>hmtx</c> transform (version 1) is rare in
/// practice and is rejected with a clear diagnostic for now — Phase 1 hardening adds it
/// later. Font collections (<c>'ttcf'</c> flavor) are also rejected here; collection
/// support lands with Task 18.5.
/// </para>
/// <para>
/// <b>Pipeline.</b> (1) Parse the 48-byte header. (2) Walk the variable-length table
/// directory. (3) Brotli-decompress the single concatenated table-data stream. (4) For
/// each entry, slice <c>transformLength</c> bytes from the decompressed stream and either
/// pass them through (null transform) or hand to the appropriate transform-reverser. (5)
/// Lay out the resulting tables into a fresh SFNT byte stream with a sorted directory,
/// 4-byte alignment, and patch <c>head.checkSumAdjustment</c> per OpenType spec.
/// </para>
/// </remarks>
internal static class WoffTwoDecoder
{
    /// <summary>
    /// Decode a WOFF 2.0 byte stream to SFNT. Throws <see cref="InvalidDataException"/>
    /// for malformed input or unsupported features (font collections, hmtx transform).
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> woff2Bytes)
    {
        // (1) Header.
        var header = WoffTwoHeader.Parse(woff2Bytes);
        if (header.IsCollection)
        {
            throw new InvalidDataException(
                "WOFF2: TrueType collection ('ttcf') decoding not supported in Phase 1.");
        }
        if ((long)header.Length != woff2Bytes.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: header.length ({header.Length}) does not equal actual buffer size ({woff2Bytes.Length}).");
        }

        // (2) Table directory.
        var cursor = WoffTwoConstants.HeaderSize;
        var entries = WoffTwoTableEntry.ParseDirectory(woff2Bytes, ref cursor, header.NumTables);

        // Spec invariant (§5.1): when glyf and loca are both present AND at least one uses
        // the actual transform (version 0), they MUST be located adjacently in the directory
        // (loca immediately after glyf). With null transforms (version 3) on both, the
        // directory follows no special adjacency rule.
        ValidateGlyfLocaAdjacencyForTransform(entries);

        // (3) Validate the wrapper layout (block ordering, alignment, no overlaps, no
        // trailing bytes). Returns the start of the compressed-data block.
        var compressedStart = cursor;
        var expectedDecompressedSize = ComputeExpectedDecompressedSize(entries);
        WoffTwoLayoutValidator.Validate(header, woff2Bytes.Length, compressedStart);

        var compressedEnd = compressedStart + (int)header.TotalCompressedSize;
        var compressed = woff2Bytes[compressedStart..compressedEnd];

        // (4) Brotli-decompress with a hard cap from the directory's declared sizes —
        // protects against decompression-bomb inputs.
        var decompressed = BrotliDecompressBounded(compressed, expectedDecompressedSize);

        // (4) Reverse transforms.
        var tableSegments = new TableSegment[entries.Length];
        var streamCursor = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (streamCursor + entry.TransformLength > decompressed.Length)
            {
                throw new InvalidDataException(
                    $"WOFF2: table '{TagToString(entry.Tag)}' (transformLength={entry.TransformLength}) extends past decompressed stream end ({decompressed.Length}).");
            }
            var transformed = decompressed.AsSpan(streamCursor, (int)entry.TransformLength);

            if (entry.IsNullTransform)
            {
                // Null transform: bytes are the table verbatim.
                if (entry.TransformLength != entry.OrigLength)
                {
                    throw new InvalidDataException(
                        $"WOFF2: null-transform '{TagToString(entry.Tag)}' must have transformLength == origLength; got {entry.TransformLength} vs {entry.OrigLength}.");
                }
                tableSegments[i] = new TableSegment(entry.Tag, transformed.ToArray());
            }
            else if (entry.Tag == WoffTwoTags.Glyf && entry.TransformVersion == 0)
            {
                // Phase B: glyf transform reversal. Not yet implemented.
                throw new NotSupportedException(
                    "WOFF2: glyf transform (version 0) reversal is implemented in a follow-up phase. " +
                    "This decoder currently handles WOFF2 files whose tables all use the null transform.");
            }
            else if (entry.Tag == WoffTwoTags.Loca && entry.TransformVersion == 0)
            {
                // Loca with transform version 0 is reconstructed alongside glyf; same dependency.
                throw new NotSupportedException(
                    "WOFF2: loca transform (version 0) reconstruction is paired with glyf reversal — implemented in a follow-up phase.");
            }
            else if (entry.Tag == WoffTwoTags.Hmtx && entry.TransformVersion == 1)
            {
                throw new NotSupportedException(
                    "WOFF2: hmtx transform (version 1) reversal is post-Phase-1 work.");
            }
            else
            {
                throw new InvalidDataException(
                    $"WOFF2: table '{TagToString(entry.Tag)}' uses unrecognized transform version {entry.TransformVersion}.");
            }

            streamCursor += (int)entry.TransformLength;
        }

        if (streamCursor != decompressed.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: decompressed stream has {decompressed.Length - streamCursor} trailing byte(s) after all tables consumed — file is malformed.");
        }

        // (5) Re-assemble into SFNT.
        return AssembleSfnt(header.Flavor, tableSegments);
    }

    private static void ValidateGlyfLocaAdjacencyForTransform(WoffTwoTableEntry[] entries)
    {
        int glyfIndex = -1, locaIndex = -1;
        bool glyfTransformed = false, locaTransformed = false;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Tag == WoffTwoTags.Glyf)
            {
                glyfIndex = i;
                glyfTransformed = entries[i].TransformVersion == 0;
            }
            else if (entries[i].Tag == WoffTwoTags.Loca)
            {
                locaIndex = i;
                locaTransformed = entries[i].TransformVersion == 0;
            }
        }
        if (glyfIndex == -1 && locaIndex == -1) return;
        if (glyfIndex == -1 || locaIndex == -1)
        {
            throw new InvalidDataException("WOFF2: 'glyf' and 'loca' must both be present or both absent.");
        }
        // Per §5.1: only the transformed pair must be located adjacently. With version 3
        // (null) on both, the directory may interleave other tables between them.
        var transformInPlay = glyfTransformed || locaTransformed;
        if (transformInPlay && locaIndex != glyfIndex + 1)
        {
            throw new InvalidDataException(
                $"WOFF2: transformed 'loca' must immediately follow transformed 'glyf' in the directory; got glyf@{glyfIndex} loca@{locaIndex}.");
        }
    }

    private static long ComputeExpectedDecompressedSize(WoffTwoTableEntry[] entries)
    {
        // The Brotli stream contains the concatenation of every table's transformLength.
        // Compute once so the decompressor can fail fast on a Brotli bomb.
        long sum = 0;
        foreach (var e in entries)
        {
            sum += e.TransformLength;
        }
        return sum;
    }

    private static byte[] BrotliDecompressBounded(ReadOnlySpan<byte> compressed, long expectedSize)
    {
        if (expectedSize < 0 || expectedSize > int.MaxValue)
        {
            throw new InvalidDataException(
                $"WOFF2: declared decompressed size ({expectedSize}) is out of range.");
        }
        try
        {
            // BrotliStream is a forward-only decoder over an underlying Stream; we wrap
            // the span in a MemoryStream to avoid native allocation and keep AOT-clean.
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);

            // Bound the output by expectedSize and probe for overflow with one extra byte
            // — protects against decompression-bomb inputs without materializing an
            // unbounded stream.
            var cap = (int)expectedSize;
            var buffer = new byte[cap];
            var read = 0;
            while (read < cap)
            {
                var n = brotli.Read(buffer, read, cap - read);
                if (n == 0) break;
                read += n;
            }
            Span<byte> overflowProbe = stackalloc byte[1];
            if (brotli.Read(overflowProbe) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: decompressed stream exceeds declared total ({expectedSize} bytes) — possible decompression-bomb input.");
            }
            if (read != cap)
            {
                throw new InvalidDataException(
                    $"WOFF2: decompressed stream is short — got {read} bytes, expected {cap} (sum of transformLength).");
            }
            return buffer;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surface every decompression-side exception (BrotliStream may throw
            // InvalidOperationException, IOException, etc. on malformed framing) as a
            // single InvalidDataException so the decoder's failure contract is uniform.
            throw new InvalidDataException(
                $"WOFF2: Brotli decompression failed: {ex.Message}", ex);
        }
    }

    private static byte[] AssembleSfnt(uint flavor, TableSegment[] tables)
    {
        // Sort by tag — SFNT directory is required to be tag-ascending.
        Array.Sort(tables, static (a, b) => a.Tag.CompareTo(b.Tag));

        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var directorySize = recordSize * tables.Length;
        var firstTableOffset = sfntHeaderSize + directorySize;

        // Compute table offsets (4-byte aligned).
        var offsets = new int[tables.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignTo4(tables[i].Bytes.Length);
        }
        var totalSize = cursor;

        var output = new byte[totalSize];
        var writer = output.AsSpan();

        // SFNT header.
        BinaryPrimitives.WriteUInt32BigEndian(writer[..4], flavor);
        BinaryPrimitives.WriteUInt16BigEndian(writer[4..6], (ushort)tables.Length);
        // searchRange, entrySelector, rangeShift per OpenType spec.
        var pow2 = HighestPow2LessOrEqual(tables.Length);
        BinaryPrimitives.WriteUInt16BigEndian(writer[6..8], (ushort)(pow2 * 16));
        BinaryPrimitives.WriteUInt16BigEndian(writer[8..10], (ushort)Log2(pow2));
        BinaryPrimitives.WriteUInt16BigEndian(writer[10..12], (ushort)((tables.Length * 16) - (pow2 * 16)));

        // Directory + per-table checksums.
        var dirCursor = sfntHeaderSize;
        var headOffset = -1;
        for (var i = 0; i < tables.Length; i++)
        {
            var checksum = ComputeTableChecksum(tables[i].Bytes);
            BinaryPrimitives.WriteUInt32BigEndian(writer[dirCursor..(dirCursor + 4)], tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(dirCursor + 4)..(dirCursor + 8)], checksum);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(dirCursor + 8)..(dirCursor + 12)], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(dirCursor + 12)..(dirCursor + 16)], (uint)tables[i].Bytes.Length);
            dirCursor += recordSize;

            tables[i].Bytes.CopyTo(writer[offsets[i]..(offsets[i] + tables[i].Bytes.Length)]);

            if (tables[i].Tag == WoffTwoTags.Head) headOffset = offsets[i];
        }

        // Patch head.checkSumAdjustment per OpenType spec: 0xB1B0AFBA - whole-file checksum.
        if (headOffset >= 0 && tables[FindByTag(tables, WoffTwoTags.Head)].Bytes.Length >= 12)
        {
            // Zero out checkSumAdjustment first so it doesn't contribute to the file checksum.
            BinaryPrimitives.WriteUInt32BigEndian(writer[(headOffset + 8)..(headOffset + 12)], 0u);
            var fileChecksum = ComputeTableChecksum(output);
            var checkSumAdjustment = unchecked(0xB1B0AFBAu - fileChecksum);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(headOffset + 8)..(headOffset + 12)], checkSumAdjustment);
        }

        return output;
    }

    private static int FindByTag(TableSegment[] tables, uint tag)
    {
        for (var i = 0; i < tables.Length; i++)
        {
            if (tables[i].Tag == tag) return i;
        }
        return -1;
    }

    private static uint ComputeTableChecksum(ReadOnlySpan<byte> bytes)
    {
        // OpenType checksum: sum of UInt32 big-endian words; padded with zero bytes.
        uint sum = 0;
        var i = 0;
        while (i + 4 <= bytes.Length)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(bytes[i..(i + 4)]));
            i += 4;
        }
        // Padding: zero-extend to 4 bytes for any tail.
        if (i < bytes.Length)
        {
            uint tail = 0;
            for (var k = 0; k < 4; k++)
            {
                tail <<= 8;
                if (i + k < bytes.Length) tail |= bytes[i + k];
            }
            sum = unchecked(sum + tail);
        }
        return sum;
    }

    private static int AlignTo4(int n) => (n + 3) & ~3;

    private static int HighestPow2LessOrEqual(int n)
    {
        var p = 1;
        while (p * 2 <= n) p *= 2;
        return p;
    }

    private static int Log2(int n)
    {
        var k = 0;
        while ((1 << (k + 1)) <= n) k++;
        return k;
    }

    private static string TagToString(uint tag)
    {
        Span<char> buf = stackalloc char[4];
        buf[0] = (char)((tag >> 24) & 0xFF);
        buf[1] = (char)((tag >> 16) & 0xFF);
        buf[2] = (char)((tag >> 8) & 0xFF);
        buf[3] = (char)(tag & 0xFF);
        return new string(buf);
    }

    private readonly record struct TableSegment(uint Tag, byte[] Bytes);
}
