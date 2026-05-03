// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Wrapper-structure validator for WOFF 2.0 files. Confirms that the on-disk layout —
/// header, table directory, compressed-data block, optional metadata block, optional
/// private-data block — follows the ordering, alignment, and "no extraneous bytes"
/// rules of W3C "WOFF File Format 2.0" §3. Layout validation is a separate concern
/// from cryptographic-style well-formed-ness checks on individual fields (those live
/// on <see cref="WoffTwoHeader"/> and <see cref="WoffTwoTableEntry"/>); this class
/// looks at the file <i>as a whole</i>.
/// </summary>
internal static class WoffTwoLayoutValidator
{
    /// <summary>
    /// Validate the file-wide layout. Throws <see cref="InvalidDataException"/> on any
    /// violation. <paramref name="compressedDataStart"/> is the offset of the compressed-
    /// data block (i.e., the position immediately after the table directory). The full
    /// <paramref name="file"/> bytes are needed so padding regions between blocks can be
    /// verified to be all-zero per §3.
    /// </summary>
    public static void Validate(WoffTwoHeader header, ReadOnlySpan<byte> file, int compressedDataStart)
    {
        ArgumentNullException.ThrowIfNull(header);
        var fileLength = file.Length;

        // Compressed-data block must fit between the directory and the start of the
        // metadata block (or end of file if no optional blocks follow).
        var compressedEnd = compressedDataStart + (long)header.TotalCompressedSize;
        if (compressedEnd > fileLength)
        {
            throw new InvalidDataException(
                $"WOFF2: compressed data block (offset={compressedDataStart}, length={header.TotalCompressedSize}) extends past file end ({fileLength}).");
        }

        var metaPresent = header.MetaOffset != 0 || header.MetaLength != 0;
        var privPresent = header.PrivOffset != 0 || header.PrivLength != 0;

        // Compressed block must not overlap the header or directory.
        if (compressedDataStart < WoffTwoConstants.HeaderSize)
        {
            throw new InvalidDataException(
                "WOFF2: compressed data block starts inside the file header — directory size computation is wrong.");
        }

        // Metadata block (if present): begins at MetaOffset, runs MetaLength bytes; must
        // be 4-byte aligned per §3. Padding from compressedEnd to MetaOffset is at most
        // 3 bytes and must be zero.
        long expectedNextBlockStart = compressedEnd;
        if (metaPresent)
        {
            var metaEnd = (long)header.MetaOffset + header.MetaLength;
            if (header.MetaOffset < expectedNextBlockStart)
            {
                throw new InvalidDataException(
                    $"WOFF2: metadata block (offset={header.MetaOffset}) overlaps the compressed-data block (which ends at {compressedEnd}).");
            }
            if (header.MetaOffset < WoffTwoConstants.HeaderSize)
            {
                throw new InvalidDataException(
                    "WOFF2: metadata block starts inside the file header.");
            }
            // Padding gap must be ≤ 3 bytes and all-zero.
            ValidatePaddingGap(file, expectedNextBlockStart, header.MetaOffset, "compressed-data → metadata");
            if ((header.MetaOffset & 3) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: metadata block offset {header.MetaOffset} is not 4-byte aligned.");
            }
            if (metaEnd > fileLength)
            {
                throw new InvalidDataException(
                    $"WOFF2: metadata block (offset={header.MetaOffset}, length={header.MetaLength}) extends past file end ({fileLength}).");
            }
            expectedNextBlockStart = metaEnd;
        }

        // Private-data block (if present): must come AFTER metadata (and compressed-data
        // if no metadata), 4-byte aligned, must not overlap, MUST end exactly at file end.
        // Per §3 the private block, when present, terminates the file — no trailing bytes.
        if (privPresent)
        {
            var privEnd = (long)header.PrivOffset + header.PrivLength;
            if (header.PrivOffset < expectedNextBlockStart)
            {
                throw new InvalidDataException(
                    $"WOFF2: private-data block (offset={header.PrivOffset}) overlaps the previous block (which ends at {expectedNextBlockStart}).");
            }
            ValidatePaddingGap(file, expectedNextBlockStart, header.PrivOffset, "previous-block → private-data");
            if ((header.PrivOffset & 3) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: private-data block offset {header.PrivOffset} is not 4-byte aligned.");
            }
            if (privEnd != fileLength)
            {
                throw new InvalidDataException(
                    $"WOFF2: private-data block (offset={header.PrivOffset}, length={header.PrivLength}) must end exactly at file end ({fileLength}); got {privEnd}.");
            }
            expectedNextBlockStart = privEnd;
        }

        // If neither metadata nor private exists, the file may end with up to 3 trailing
        // alignment bytes after compressed-data (those bytes must also be zero).
        if (!metaPresent && !privPresent)
        {
            ValidatePaddingGap(file, compressedEnd, fileLength, "compressed-data → EOF");
        }
        // If metadata exists but no private: §3 doesn't require metadata to align with
        // EOF, so up to 3 trailing alignment bytes are allowed and must be zero.
        else if (metaPresent && !privPresent)
        {
            ValidatePaddingGap(file, expectedNextBlockStart, fileLength, "metadata → EOF");
        }
        // If priv exists, the priv-end-equals-fileLength check above already eliminated
        // any trailing slop.
    }

    private static void ValidatePaddingGap(ReadOnlySpan<byte> file, long fromOffset, long toOffset, string label)
    {
        var gap = toOffset - fromOffset;
        if (gap < 0)
        {
            throw new InvalidDataException(
                $"WOFF2: {label} gap is negative ({gap}) — block ordering is wrong.");
        }
        if (gap > 3)
        {
            throw new InvalidDataException(
                $"WOFF2: {label} has {gap} byte(s) of padding; at most 3 are permitted for 4-byte alignment.");
        }
        for (var k = 0; k < gap; k++)
        {
            if (file[(int)(fromOffset + k)] != 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: {label} padding byte at offset {fromOffset + k} is non-zero (0x{file[(int)(fromOffset + k)]:X2}); §3 requires zero-fill.");
            }
        }
    }
}
