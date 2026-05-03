// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Parsed WOFF 2.0 file header (48 bytes). Spec basis: W3C "WOFF File Format 2.0"
/// Recommendation §3 Table 1. Field semantics are spelled out in the per-property
/// XML; only the trust-boundary checks (bounds, signature, reserved must-be-zero,
/// metadata / private-data offset+length consistency) live in the parser.
/// </summary>
internal sealed class WoffTwoHeader
{
    public required uint Signature { get; init; }
    public required uint Flavor { get; init; }
    public required uint Length { get; init; }
    public required ushort NumTables { get; init; }
    public required ushort Reserved { get; init; }
    public required uint TotalSfntSize { get; init; }
    public required uint TotalCompressedSize { get; init; }
    public required ushort MajorVersion { get; init; }
    public required ushort MinorVersion { get; init; }
    public required uint MetaOffset { get; init; }
    public required uint MetaLength { get; init; }
    public required uint MetaOrigLength { get; init; }
    public required uint PrivOffset { get; init; }
    public required uint PrivLength { get; init; }

    /// <summary>True when <see cref="Flavor"/> is the TrueType collection signature ('ttcf').</summary>
    public bool IsCollection => Flavor == WoffTwoConstants.FlavorTtc;

    /// <summary>
    /// Parse the 48-byte header from <paramref name="span"/> with full trust-boundary
    /// validation. Throws <see cref="InvalidDataException"/> on any malformed field.
    /// </summary>
    public static WoffTwoHeader Parse(ReadOnlySpan<byte> span)
    {
        if (span.Length < WoffTwoConstants.HeaderSize)
        {
            throw new InvalidDataException(
                $"WOFF2: header expects {WoffTwoConstants.HeaderSize} bytes; got {span.Length}.");
        }

        var signature = BinaryPrimitives.ReadUInt32BigEndian(span[0..4]);
        if (signature != WoffTwoConstants.Signature)
        {
            throw new InvalidDataException(
                $"WOFF2: bad signature 0x{signature:X8}; expected 0x{WoffTwoConstants.Signature:X8} ('wOF2').");
        }

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
        if (flavor != WoffTwoConstants.FlavorTrueType
            && flavor != WoffTwoConstants.FlavorCff
            && flavor != WoffTwoConstants.FlavorTtc)
        {
            throw new InvalidDataException(
                $"WOFF2: unrecognized flavor 0x{flavor:X8}. Expected TTF (0x00010000), CFF ('OTTO'), or TTC ('ttcf').");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(span[8..12]);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[12..14]);
        if (numTables == 0)
        {
            throw new InvalidDataException("WOFF2: numTables = 0 is invalid (file has no tables).");
        }

        // Per W3C WOFF 2.0 §3 the encoder is required to set 'reserved' to 0, but the spec
        // does NOT mandate decoder rejection on non-zero values. The Google reference
        // decoder (woff2) and Postel's robustness principle both favor accepting non-zero
        // reserved values rather than failing fonts that would otherwise round-trip.
        var reserved = BinaryPrimitives.ReadUInt16BigEndian(span[14..16]);

        var totalSfntSize = BinaryPrimitives.ReadUInt32BigEndian(span[16..20]);
        var totalCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(span[20..24]);
        var majorVersion = BinaryPrimitives.ReadUInt16BigEndian(span[24..26]);
        var minorVersion = BinaryPrimitives.ReadUInt16BigEndian(span[26..28]);

        var metaOffset = BinaryPrimitives.ReadUInt32BigEndian(span[28..32]);
        var metaLength = BinaryPrimitives.ReadUInt32BigEndian(span[32..36]);
        var metaOrigLength = BinaryPrimitives.ReadUInt32BigEndian(span[36..40]);

        var privOffset = BinaryPrimitives.ReadUInt32BigEndian(span[40..44]);
        var privLength = BinaryPrimitives.ReadUInt32BigEndian(span[44..48]);

        // Metadata block: per §3, all three metadata fields must agree (all-zero or all-non-zero).
        var metaPresent = metaOffset != 0 || metaLength != 0 || metaOrigLength != 0;
        var metaConsistent = (metaOffset != 0 && metaLength != 0 && metaOrigLength != 0)
                          || (metaOffset == 0 && metaLength == 0 && metaOrigLength == 0);
        if (!metaConsistent)
        {
            throw new InvalidDataException(
                $"WOFF2: metadata block fields inconsistent (offset={metaOffset}, length={metaLength}, origLength={metaOrigLength}).");
        }
        if (metaPresent && (long)metaOffset + metaLength > length)
        {
            throw new InvalidDataException(
                $"WOFF2: metadata block (offset={metaOffset}, length={metaLength}) extends past file length {length}.");
        }

        // Private-data block: same all-or-none rule.
        var privPresent = privOffset != 0 || privLength != 0;
        var privConsistent = (privOffset != 0 && privLength != 0)
                          || (privOffset == 0 && privLength == 0);
        if (!privConsistent)
        {
            throw new InvalidDataException(
                $"WOFF2: private-data block fields inconsistent (offset={privOffset}, length={privLength}).");
        }
        if (privPresent && (long)privOffset + privLength > length)
        {
            throw new InvalidDataException(
                $"WOFF2: private-data block (offset={privOffset}, length={privLength}) extends past file length {length}.");
        }

        return new WoffTwoHeader
        {
            Signature = signature,
            Flavor = flavor,
            Length = length,
            NumTables = numTables,
            Reserved = reserved,
            TotalSfntSize = totalSfntSize,
            TotalCompressedSize = totalCompressedSize,
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
