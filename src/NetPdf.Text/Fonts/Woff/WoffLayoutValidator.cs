// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Strict file-layout validator for WOFF 1.0. Runs after <see cref="WoffHeader.Parse"/>
/// + <see cref="WoffTableEntry.ParseDirectory"/> and before any decompression so that
/// structurally-malformed inputs reject before any table-sized buffer is allocated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec basis (clean-room).</b> W3C "WOFF File Format 1.0" Recommendation
/// (<c>https://www.w3.org/TR/WOFF/</c>), §3 "WOFF File" + §4 "Conformance". Per §4 a
/// conformant decoder MUST reject any input that does not conform — every check below
/// maps to a §3 layout constraint.
/// </para>
/// <para>
/// <b>Security boundary.</b> The single most important check is <c>totalSfntSize</c>
/// matching the cumulative declared <c>origLength</c> values, capped by
/// <see cref="MaxSfntSize"/>. Without it a malicious WOFF could declare gigabyte-scale
/// per-table <c>origLength</c> and force unbounded allocations in
/// <see cref="WoffDecoder"/>; with it, total uncompressed output size is bounded before
/// any table buffer is touched.
/// </para>
/// <para>
/// <b>Single source of truth.</b> Per-entry validity (compLength ≤ origLength, offset
/// in-range, strict tag ordering) is in <see cref="WoffTableEntry.ParseDirectory"/>.
/// Per-header validity (signature, flavor, length equality, totalSfntSize multiple-of-4,
/// metadata/private offset-length consistency) is in <see cref="WoffHeader.Parse"/>.
/// Cross-entry layout is here.
/// </para>
/// </remarks>
internal static class WoffLayoutValidator
{
    /// <summary>SFNT header size: 12 bytes (sfntVersion + numTables + 3× search-helper fields).</summary>
    private const int SfntHeaderSize = 12;

    /// <summary>SFNT directory record size: 16 bytes (tag + checksum + offset + length).</summary>
    private const int SfntDirectoryRecordSize = 16;

    /// <summary>Maximum gap (in bytes) between consecutive blocks for 4-byte alignment padding.</summary>
    private const int MaxAlignmentPadding = 3;

    /// <summary>
    /// Hard upper bound on the reconstructed SFNT size — 256 MiB. Real-world fonts
    /// (full-coverage Noto CJK families, etc.) fit comfortably in &lt; 100 MiB; 256 MiB
    /// is generous for legitimate inputs while preventing memory-exhaustion attacks
    /// from a malicious WOFF that declares huge <c>origLength</c> values.
    /// </summary>
    public const long MaxSfntSize = 256L * 1024 * 1024;

    /// <summary>
    /// Validate the full WOFF file layout. Throws <see cref="InvalidDataException"/> on
    /// any non-conformance. The caller (typically <see cref="WoffDecoder.Decode"/>)
    /// invokes this immediately after directory parsing and before any decompression.
    /// </summary>
    public static void Validate(WoffHeader header, WoffTableEntry[] entries, ReadOnlySpan<byte> woffBytes)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(entries);

        // (1) Cumulative SFNT size cap + match against header.totalSfntSize.
        //     This is the security boundary — if a malicious encoder declares huge
        //     origLength values, the cumulative sum exceeds MaxSfntSize and we throw
        //     before any per-table allocation happens.
        var expectedSfntSize = (long)SfntHeaderSize + ((long)entries.Length * SfntDirectoryRecordSize);
        for (var i = 0; i < entries.Length; i++)
        {
            expectedSfntSize += AlignTo4(entries[i].OrigLength);
            if (expectedSfntSize > MaxSfntSize)
            {
                throw new InvalidDataException(
                    $"WOFF: cumulative SFNT size exceeds {MaxSfntSize:N0}-byte safety cap " +
                    $"after table {i} ('{OpenTypeTags.ToAsciiString(entries[i].Tag)}').");
            }
        }
        if ((uint)expectedSfntSize != header.TotalSfntSize)
        {
            throw new InvalidDataException(
                $"WOFF: header.totalSfntSize {header.TotalSfntSize} does not match the value derived " +
                $"from the directory's origLengths ({expectedSfntSize}). Spec §3 requires exact match.");
        }

        // (2) Per-entry offset alignment — §3 "Tables MUST be aligned to a four-byte boundary."
        for (var i = 0; i < entries.Length; i++)
        {
            if ((entries[i].Offset & 3u) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF: table '{OpenTypeTags.ToAsciiString(entries[i].Tag)}' offset {entries[i].Offset} " +
                    "is not 4-byte aligned.");
            }
        }

        // (3) Tables contiguous in tag-ascending order with up to 3 bytes of zero-padding for
        //     alignment. Combined with the strict tag-ascending order from
        //     WoffTableEntry.ParseDirectory and §3 "Tables MUST be stored in ascending order
        //     of their tag values", offsets must be ascending too. Padding bytes between
        //     tables MUST be zero.
        var directoryEnd = WoffHeader.HeaderSize + (entries.Length * WoffTableEntry.RecordSize);
        long expectedNext = directoryEnd;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            EnsureNoOverlapAndPadIsZero(woffBytes, expectedNext, entry.Offset, $"table '{OpenTypeTags.ToAsciiString(entry.Tag)}'");
            expectedNext = (long)entry.Offset + entry.CompLength;
        }

        // (4) Metadata block (if present) immediately follows the table data after up to 3
        //     bytes of zero-padding. §3 "The metadata block, if present, MUST immediately
        //     follow the table data, with up to 3 bytes of zero-padding."
        if (header.MetaOffset != 0)
        {
            if ((header.MetaOffset & 3u) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF: metaOffset {header.MetaOffset} is not 4-byte aligned.");
            }
            EnsureNoOverlapAndPadIsZero(woffBytes, expectedNext, header.MetaOffset, "metadata block");
            expectedNext = (long)header.MetaOffset + header.MetaLength;
        }

        // (5) Private block (if present) MUST be the last block. §3 "The private data block,
        //     if present, MUST be the last block in the file." When metadata is present,
        //     no padding is required between metadata and private (gap = 0). When metadata
        //     is absent, the private block is aligned-to-4 from the table data (gap = 0..3).
        if (header.PrivOffset != 0)
        {
            if (header.MetaOffset != 0)
            {
                if ((long)header.PrivOffset != expectedNext)
                {
                    throw new InvalidDataException(
                        $"WOFF: privOffset {header.PrivOffset} does not immediately follow metadata block " +
                        $"(expected {expectedNext}); spec disallows padding between metadata and private.");
                }
            }
            else
            {
                EnsureNoOverlapAndPadIsZero(woffBytes, expectedNext, header.PrivOffset, "private block");
            }
            expectedNext = (long)header.PrivOffset + header.PrivLength;
        }

        // (6) No extraneous trailing bytes — §3 "There MUST be no extraneous data after the
        //     last data block." We've already required header.Length == buffer.Length, so
        //     this also rejects a truncated final block.
        if (expectedNext != header.Length)
        {
            throw new InvalidDataException(
                $"WOFF: {(header.Length - expectedNext)} extraneous trailing byte(s) — " +
                $"final block ends at {expectedNext} but file length is {header.Length}.");
        }
    }

    /// <summary>
    /// Verify that the next block starts at <paramref name="actualStart"/>, where
    /// <paramref name="expectedMinStart"/> is the previous block's end. Allows up to
    /// <see cref="MaxAlignmentPadding"/> bytes of zero-padding between them; rejects
    /// overlap, larger gaps, and non-zero padding bytes.
    /// </summary>
    private static void EnsureNoOverlapAndPadIsZero(
        ReadOnlySpan<byte> woffBytes,
        long expectedMinStart,
        uint actualStart,
        string blockLabel)
    {
        if (actualStart < expectedMinStart)
        {
            throw new InvalidDataException(
                $"WOFF: {blockLabel} starts at {actualStart} which overlaps the prior region " +
                $"(expected ≥ {expectedMinStart}).");
        }
        var gap = actualStart - expectedMinStart;
        if (gap > MaxAlignmentPadding)
        {
            throw new InvalidDataException(
                $"WOFF: {gap} extraneous byte(s) before {blockLabel} (expected at offset " +
                $"{expectedMinStart}, found at {actualStart}); spec permits at most {MaxAlignmentPadding} alignment-padding bytes.");
        }
        // Padding bytes MUST be zero per §3.
        for (var p = expectedMinStart; p < actualStart; p++)
        {
            if (woffBytes[(int)p] != 0)
            {
                throw new InvalidDataException(
                    $"WOFF: alignment padding byte at offset {p} before {blockLabel} is " +
                    $"0x{woffBytes[(int)p]:X2}, expected 0.");
            }
        }
    }

    private static long AlignTo4(uint length) => (length + 3L) & ~3L;
}
