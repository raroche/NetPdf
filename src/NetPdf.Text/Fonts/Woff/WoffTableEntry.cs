// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// One 20-byte WOFF table directory entry per W3C WOFF File Format 1.0 §3 "TableDirectoryEntry".
/// </summary>
/// <remarks>
/// <para>
/// Each entry describes one table inside the wrapped font. <see cref="CompLength"/> is the
/// length of the on-disk (possibly zlib-compressed) bytes; <see cref="OrigLength"/> is the
/// length after decompression. The two are equal when the table is stored uncompressed
/// (zlib failed to shrink it, or the encoder preferred not to compress); decompression
/// is required when <see cref="CompLength"/> is strictly less than <see cref="OrigLength"/>.
/// </para>
/// <para>
/// <see cref="OrigChecksum"/> is the SFNT table checksum of the original uncompressed
/// bytes (sum of big-endian uint32 values, with wraparound, over the table padded to a
/// 4-byte boundary). The decoder uses it as the directory checksum in the reconstructed
/// SFNT — saves recomputing per-table checksums and lets a corrupted decompression be
/// detected by the decoder if it ever wants to verify.
/// </para>
/// </remarks>
internal readonly record struct WoffTableEntry(
    uint Tag,
    uint Offset,
    uint CompLength,
    uint OrigLength,
    uint OrigChecksum)
{
    /// <summary>Fixed size of one directory entry on disk: 20 bytes.</summary>
    public const int RecordSize = 20;

    /// <summary>True when the table data is stored uncompressed (compLength == origLength).</summary>
    public bool IsStored => CompLength == OrigLength;

    /// <summary>
    /// Parse <c>header.NumTables</c> entries from the source <paramref name="woffBytes"/>
    /// starting at the byte immediately after the 44-byte header.
    /// </summary>
    /// <remarks>
    /// Validates each entry against file length and rejects:
    /// <list type="bullet">
    ///   <item>compLength &gt; origLength (impossible — zlib never expands beyond the original).</item>
    ///   <item>offset + compLength &gt; file length (out-of-bounds).</item>
    ///   <item>offset &lt; first valid table-data offset (would alias the directory).</item>
    ///   <item>duplicate tags within the directory.</item>
    /// </list>
    /// The directory order is preserved as-is (caller sorts by tag for SFNT output —
    /// WOFF spec recommends but does not require tag-sorted directory ordering).
    /// </remarks>
    public static WoffTableEntry[] ParseDirectory(ReadOnlySpan<byte> woffBytes, WoffHeader header)
    {
        var directorySize = header.NumTables * RecordSize;
        var firstValidDataOffset = WoffHeader.HeaderSize + directorySize;
        if (woffBytes.Length < firstValidDataOffset)
        {
            throw new InvalidDataException(
                $"WOFF: buffer too small for header + directory ({firstValidDataOffset} bytes required, " +
                $"{woffBytes.Length} present).");
        }

        var entries = new WoffTableEntry[header.NumTables];
        var reader = new BigEndianReader(woffBytes.Slice(WoffHeader.HeaderSize, directorySize));
        var seenTags = new HashSet<uint>(header.NumTables);

        for (var i = 0; i < header.NumTables; i++)
        {
            var tag = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var compLength = reader.ReadUInt32();
            var origLength = reader.ReadUInt32();
            var origChecksum = reader.ReadUInt32();

            if (!seenTags.Add(tag))
            {
                throw new InvalidDataException(
                    $"WOFF: duplicate table tag '{OpenTypeTags.ToAsciiString(tag)}' in directory.");
            }

            if (compLength > origLength)
            {
                throw new InvalidDataException(
                    $"WOFF: table '{OpenTypeTags.ToAsciiString(tag)}' has compLength {compLength} > origLength {origLength}; " +
                    "compressed payload cannot exceed the original.");
            }

            if (offset < firstValidDataOffset)
            {
                throw new InvalidDataException(
                    $"WOFF: table '{OpenTypeTags.ToAsciiString(tag)}' offset {offset} aliases header/directory " +
                    $"region (first valid table-data offset is {firstValidDataOffset}).");
            }

            if ((long)offset + compLength > header.Length)
            {
                throw new InvalidDataException(
                    $"WOFF: table '{OpenTypeTags.ToAsciiString(tag)}' [{offset}..{offset + compLength}) " +
                    $"extends past file length {header.Length}.");
            }

            entries[i] = new WoffTableEntry(tag, offset, compLength, origLength, origChecksum);
        }

        return entries;
    }
}
