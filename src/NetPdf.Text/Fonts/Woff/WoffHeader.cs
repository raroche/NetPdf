// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Parsed 44-byte WOFF File Format 1.0 header (W3C Recommendation, 13 December 2012,
/// <c>https://www.w3.org/TR/WOFF/</c>, §3 "WOFF File Format" — "WOFFHeader").
/// </summary>
/// <remarks>
/// <para>
/// WOFF wraps a complete TTF or OTF/CFF font (the "flavor") inside a small envelope. The
/// per-table data inside that envelope is zlib-compressed (or stored uncompressed when
/// compression would not shrink it). The header carries every piece of information
/// needed to reconstitute the original SFNT byte stream.
/// </para>
/// <para>
/// <b>Trust boundary.</b> <see cref="Parse"/> is a parser entry point — every field is
/// range-checked against the file length. Malformed inputs reject with
/// <see cref="InvalidDataException"/> instead of producing a half-built header that
/// downstream code would have to defend against.
/// </para>
/// </remarks>
internal sealed class WoffHeader
{
    /// <summary>4-byte ASCII magic "wOFF" (<c>0x774F4646</c>) — §3 "signature".</summary>
    public const uint Signature = 0x774F4646u;

    /// <summary>Fixed size of the WOFF header on disk: 44 bytes.</summary>
    public const int HeaderSize = 44;

    /// <summary>The "flavor" of the wrapped font — <c>0x00010000</c> for TTF, ASCII <c>"OTTO"</c> for OTF/CFF, ASCII <c>"true"</c> for legacy Apple TrueType.</summary>
    public required uint Flavor { get; init; }

    /// <summary>Total length of the WOFF file, in bytes. Must equal the source buffer length.</summary>
    public required uint Length { get; init; }

    /// <summary>Number of font tables inside the wrapped font. Must be ≥ 1.</summary>
    public required ushort NumTables { get; init; }

    /// <summary>Total size of the uncompressed SFNT byte stream that <see cref="WoffDecoder"/> will reconstruct (header + sorted directory + 4-byte-padded tables).</summary>
    public required uint TotalSfntSize { get; init; }

    /// <summary>Major version of the wrapped font (typically copied from the font's <c>head.fontRevision</c> high word). Cosmetic — not used during decoding.</summary>
    public required ushort MajorVersion { get; init; }

    /// <summary>Minor version of the wrapped font.</summary>
    public required ushort MinorVersion { get; init; }

    /// <summary>Offset (from the start of the WOFF file) to the optional XML metadata block. Zero when absent.</summary>
    public required uint MetaOffset { get; init; }

    /// <summary>Compressed length of the metadata block. Zero when absent.</summary>
    public required uint MetaLength { get; init; }

    /// <summary>Uncompressed length of the metadata block. Zero when absent.</summary>
    public required uint MetaOrigLength { get; init; }

    /// <summary>Offset (from the start of the WOFF file) to the optional private-data block. Zero when absent.</summary>
    public required uint PrivOffset { get; init; }

    /// <summary>Length of the private-data block. Zero when absent.</summary>
    public required uint PrivLength { get; init; }

    /// <summary>
    /// Parse the 44-byte WOFF header from the start of <paramref name="woffBytes"/>. The
    /// flavor is checked against the SFNT magics declared in <see cref="OpenTypeTags"/>;
    /// an unrecognized flavor rejects rather than letting an unknown wrapper format slip
    /// through to <c>OpenTypeFont.Parse</c>.
    /// </summary>
    public static WoffHeader Parse(ReadOnlySpan<byte> woffBytes)
    {
        if (woffBytes.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"WOFF: header requires {HeaderSize} bytes but buffer has {woffBytes.Length}.");
        }

        var reader = new BigEndianReader(woffBytes[..HeaderSize]);

        var signature = reader.ReadUInt32();
        if (signature != Signature)
        {
            throw new InvalidDataException(
                $"WOFF: bad signature 0x{signature:X8} — expected 0x{Signature:X8} (\"wOFF\").");
        }

        var flavor = reader.ReadUInt32();
        if (flavor != OpenTypeTags.SfntVersionTtf
            && flavor != OpenTypeTags.SfntVersionOtf
            && flavor != OpenTypeTags.SfntVersionAppleTrue)
        {
            throw new InvalidDataException(
                $"WOFF: unrecognized flavor 0x{flavor:X8} — expected TTF (0x00010000), " +
                $"OTF (\"OTTO\"), or Apple TrueType (\"true\").");
        }

        var length = reader.ReadUInt32();
        // §3 / §4: header.length is the total file size — strict equality, not "≤".
        // A buffer larger than length carries extraneous data; smaller is truncated. Both
        // are non-conformant per §4 ("user agents MUST reject as invalid any input that
        // does not conform").
        if (length != (uint)woffBytes.Length)
        {
            throw new InvalidDataException(
                $"WOFF: header.length {length} does not equal actual buffer length {woffBytes.Length}.");
        }

        var numTables = reader.ReadUInt16();
        if (numTables == 0)
        {
            throw new InvalidDataException("WOFF: numTables is zero — a font must have at least one table.");
        }

        var reserved = reader.ReadUInt16();
        if (reserved != 0)
        {
            throw new InvalidDataException(
                $"WOFF: reserved field is 0x{reserved:X4} — spec requires zero.");
        }

        var totalSfntSize = reader.ReadUInt32();
        // §3 "totalSfntSize: Total size needed for the uncompressed font data [...] including
        // padding." All component sizes (12-byte SFNT header, 16-byte directory records,
        // 4-byte-aligned table data) are multiples of 4, so the sum must be a multiple of 4.
        // Cross-validation against the directory's origLengths happens in WoffLayoutValidator.
        if ((totalSfntSize & 3u) != 0)
        {
            throw new InvalidDataException(
                $"WOFF: totalSfntSize {totalSfntSize} is not a multiple of 4.");
        }

        var majorVersion = reader.ReadUInt16();
        var minorVersion = reader.ReadUInt16();

        var metaOffset = reader.ReadUInt32();
        var metaLength = reader.ReadUInt32();
        var metaOrigLength = reader.ReadUInt32();
        var privOffset = reader.ReadUInt32();
        var privLength = reader.ReadUInt32();

        // Cross-field consistency: when the metadata or private blocks are absent, both
        // the offset and length must be zero. A non-zero offset with zero length (or
        // vice versa) is malformed. The metadata block also has a third field
        // (metaOrigLength) — when metadata is absent, that field MUST be zero too,
        // otherwise the encoder produced inconsistent state and we reject.
        if ((metaOffset == 0) != (metaLength == 0))
        {
            throw new InvalidDataException(
                $"WOFF: metadata offset/length inconsistency (metaOffset={metaOffset}, metaLength={metaLength}).");
        }
        if (metaOffset == 0 && metaOrigLength != 0)
        {
            throw new InvalidDataException(
                $"WOFF: metadata block is absent (metaOffset = 0, metaLength = 0) but metaOrigLength is " +
                $"{metaOrigLength}; the spec requires all three fields to be zero when metadata is absent.");
        }
        if ((privOffset == 0) != (privLength == 0))
        {
            throw new InvalidDataException(
                $"WOFF: private-data offset/length inconsistency (privOffset={privOffset}, privLength={privLength}).");
        }

        // Optional blocks must fit inside the file.
        if (metaOffset != 0 && (long)metaOffset + metaLength > length)
        {
            throw new InvalidDataException(
                $"WOFF: metadata block [{metaOffset}..{metaOffset + metaLength}) extends past file length {length}.");
        }
        if (privOffset != 0 && (long)privOffset + privLength > length)
        {
            throw new InvalidDataException(
                $"WOFF: private block [{privOffset}..{privOffset + privLength}) extends past file length {length}.");
        }

        return new WoffHeader
        {
            Flavor = flavor,
            Length = length,
            NumTables = numTables,
            TotalSfntSize = totalSfntSize,
            MajorVersion = majorVersion,
            MinorVersion = minorVersion,
            MetaOffset = metaOffset,
            MetaLength = metaLength,
            MetaOrigLength = metaOrigLength,
            PrivOffset = privOffset,
            PrivLength = privLength,
        };
    }
}
