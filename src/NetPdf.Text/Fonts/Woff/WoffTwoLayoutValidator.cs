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
    /// data block (i.e., the position immediately after the table directory).
    /// </summary>
    public static void Validate(WoffTwoHeader header, long fileLength, int compressedDataStart)
    {
        ArgumentNullException.ThrowIfNull(header);

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
        // be 4-byte aligned per §3 (compressed-data block is followed by 0..3 zero
        // padding bytes for 4-byte alignment when metadata follows). Must come AFTER
        // compressed data and BEFORE private data.
        long expectedNextBlockStart = compressedEnd;
        if (metaPresent)
        {
            // §3: metaOffset must be ≥ compressedEnd, with 4-byte alignment after
            // compressed-data padding.
            var metaEnd = (long)header.MetaOffset + header.MetaLength;
            if (header.MetaOffset < expectedNextBlockStart)
            {
                throw new InvalidDataException(
                    $"WOFF2: metadata block (offset={header.MetaOffset}) overlaps the compressed-data block (which ends at {compressedEnd}).");
            }
            if ((header.MetaOffset & 3) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: metadata block offset {header.MetaOffset} is not 4-byte aligned.");
            }
            if (header.MetaOffset < WoffTwoConstants.HeaderSize)
            {
                throw new InvalidDataException(
                    "WOFF2: metadata block starts inside the file header.");
            }
            if (metaEnd > fileLength)
            {
                throw new InvalidDataException(
                    $"WOFF2: metadata block (offset={header.MetaOffset}, length={header.MetaLength}) extends past file end ({fileLength}).");
            }
            expectedNextBlockStart = metaEnd;
        }

        // Private-data block (if present): must come AFTER metadata (and compressed-data
        // if no metadata), 4-byte aligned, must not overlap, must end at file end (no
        // trailing bytes after the last declared block).
        if (privPresent)
        {
            var privEnd = (long)header.PrivOffset + header.PrivLength;
            if (header.PrivOffset < expectedNextBlockStart)
            {
                throw new InvalidDataException(
                    $"WOFF2: private-data block (offset={header.PrivOffset}) overlaps the previous block (which ends at {expectedNextBlockStart}).");
            }
            if ((header.PrivOffset & 3) != 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: private-data block offset {header.PrivOffset} is not 4-byte aligned.");
            }
            if (privEnd > fileLength)
            {
                throw new InvalidDataException(
                    $"WOFF2: private-data block (offset={header.PrivOffset}, length={header.PrivLength}) extends past file end ({fileLength}).");
            }
            expectedNextBlockStart = privEnd;
        }

        // No extraneous trailing bytes after the last declared block. The expected end
        // should be at the file length (modulo a final 0–3 byte 4-byte alignment pad,
        // which §3 permits but counts toward header.length).
        var slop = fileLength - expectedNextBlockStart;
        if (slop < 0 || slop > 3)
        {
            throw new InvalidDataException(
                $"WOFF2: file has {slop} byte(s) after the last declared block (expected end at {expectedNextBlockStart}, file length {fileLength}). At most 3 trailing alignment bytes are permitted.");
        }
    }
}
