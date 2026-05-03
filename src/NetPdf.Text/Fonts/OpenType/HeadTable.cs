// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>head</c> table. 54 bytes, fixed layout (OpenType §"head").
/// Carries the master coordinate system (<see cref="UnitsPerEm"/>), font bounding box,
/// and the <see cref="IndexToLocFormat"/> flag that controls how <c>loca</c> is parsed.
/// </summary>
internal sealed class HeadTable
{
    public const uint MagicNumber = 0x5F0F3CF5u;

    public required ushort MajorVersion { get; init; }
    public required ushort MinorVersion { get; init; }

    /// <summary>Font revision, set by font manufacturer (Fixed-point 16.16, raw uint32).</summary>
    public required uint FontRevision { get; init; }

    /// <summary>Sum of all SFNT byte values to ensure file integrity. Not validated by Phase 1.</summary>
    public required uint CheckSumAdjustment { get; init; }

    public required ushort Flags { get; init; }

    /// <summary>Em-square size in font design units. Typical: 1000 (CFF), 2048 (TTF). Range 16..16384.</summary>
    public required ushort UnitsPerEm { get; init; }

    /// <summary>Created (LongDateTime — seconds since 1904-01-01 00:00:00 UTC). Phase 1 carries the raw int64.</summary>
    public required long Created { get; init; }
    public required long Modified { get; init; }

    public required short XMin { get; init; }
    public required short YMin { get; init; }
    public required short XMax { get; init; }
    public required short YMax { get; init; }

    public required ushort MacStyle { get; init; }
    public required ushort LowestRecPPEM { get; init; }
    public required short FontDirectionHint { get; init; }

    /// <summary>0 = short offsets (uint16, divide by 2); 1 = long offsets (uint32). Drives <c>loca</c> parsing.</summary>
    public required short IndexToLocFormat { get; init; }

    public required short GlyphDataFormat { get; init; }

    public static HeadTable Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 54)
        {
            throw new InvalidDataException(
                $"head: expected at least 54 bytes; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        var majorVersion = reader.ReadUInt16();
        var minorVersion = reader.ReadUInt16();
        var fontRevision = reader.ReadUInt32();
        var checkSumAdjustment = reader.ReadUInt32();
        var magic = reader.ReadUInt32();
        if (magic != MagicNumber)
        {
            throw new InvalidDataException(
                $"head: magicNumber mismatch. Expected 0x{MagicNumber:X8}, got 0x{magic:X8}.");
        }
        var flags = reader.ReadUInt16();
        var unitsPerEm = reader.ReadUInt16();
        if (unitsPerEm is < 16 or > 16384)
        {
            throw new InvalidDataException(
                $"head: unitsPerEm {unitsPerEm} is outside the spec-permitted range [16, 16384].");
        }
        var created = reader.ReadInt64();
        var modified = reader.ReadInt64();
        var xMin = reader.ReadInt16();
        var yMin = reader.ReadInt16();
        var xMax = reader.ReadInt16();
        var yMax = reader.ReadInt16();
        var macStyle = reader.ReadUInt16();
        var lowestRecPPEM = reader.ReadUInt16();
        var fontDirectionHint = reader.ReadInt16();
        var indexToLocFormat = reader.ReadInt16();
        if (indexToLocFormat is not (0 or 1))
        {
            throw new InvalidDataException(
                $"head: indexToLocFormat must be 0 (short) or 1 (long); got {indexToLocFormat}.");
        }
        var glyphDataFormat = reader.ReadInt16();

        return new HeadTable
        {
            MajorVersion = majorVersion,
            MinorVersion = minorVersion,
            FontRevision = fontRevision,
            CheckSumAdjustment = checkSumAdjustment,
            Flags = flags,
            UnitsPerEm = unitsPerEm,
            Created = created,
            Modified = modified,
            XMin = xMin,
            YMin = yMin,
            XMax = xMax,
            YMax = yMax,
            MacStyle = macStyle,
            LowestRecPPEM = lowestRecPPEM,
            FontDirectionHint = fontDirectionHint,
            IndexToLocFormat = indexToLocFormat,
            GlyphDataFormat = glyphDataFormat,
        };
    }
}
