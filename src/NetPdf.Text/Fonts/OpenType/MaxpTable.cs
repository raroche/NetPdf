// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>maxp</c> table (OpenType §"maxp"). Two versions exist: v0.5 (6 bytes, used by
/// CFF fonts) carries only <see cref="NumGlyphs"/>; v1.0 (32 bytes, used by TTF fonts)
/// adds TrueType-specific maxima. Phase 1 only consumes <see cref="NumGlyphs"/> downstream;
/// the v1.0 fields are parsed for completeness so consumers can validate font shape.
/// </summary>
internal sealed class MaxpTable
{
    public const uint Version05 = 0x00005000u; // 0.5
    public const uint Version10 = 0x00010000u; // 1.0

    public required uint Version { get; init; }
    public required ushort NumGlyphs { get; init; }

    // v1.0 only — null for v0.5 fonts.
    public ushort? MaxPoints { get; init; }
    public ushort? MaxContours { get; init; }
    public ushort? MaxCompositePoints { get; init; }
    public ushort? MaxCompositeContours { get; init; }
    public ushort? MaxZones { get; init; }
    public ushort? MaxTwilightPoints { get; init; }
    public ushort? MaxStorage { get; init; }
    public ushort? MaxFunctionDefs { get; init; }
    public ushort? MaxInstructionDefs { get; init; }
    public ushort? MaxStackElements { get; init; }
    public ushort? MaxSizeOfInstructions { get; init; }
    public ushort? MaxComponentElements { get; init; }
    public ushort? MaxComponentDepth { get; init; }

    public bool IsTrueTypeProfile => Version == Version10;

    public static MaxpTable Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 6)
        {
            throw new InvalidDataException(
                $"maxp: expected at least 6 bytes; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        var version = reader.ReadUInt32();
        if (version is not (Version05 or Version10))
        {
            throw new InvalidDataException(
                $"maxp: unrecognized version 0x{version:X8}. Expected 0.5 (0x00005000) or 1.0 (0x00010000).");
        }
        var numGlyphs = reader.ReadUInt16();
        if (numGlyphs == 0)
        {
            throw new InvalidDataException("maxp: numGlyphs is 0; font has no glyphs.");
        }

        if (version == Version05)
        {
            return new MaxpTable { Version = version, NumGlyphs = numGlyphs };
        }

        if (tableBytes.Length < 32)
        {
            throw new InvalidDataException(
                $"maxp v1.0: expected 32 bytes; got {tableBytes.Length}.");
        }
        return new MaxpTable
        {
            Version = version,
            NumGlyphs = numGlyphs,
            MaxPoints = reader.ReadUInt16(),
            MaxContours = reader.ReadUInt16(),
            MaxCompositePoints = reader.ReadUInt16(),
            MaxCompositeContours = reader.ReadUInt16(),
            MaxZones = reader.ReadUInt16(),
            MaxTwilightPoints = reader.ReadUInt16(),
            MaxStorage = reader.ReadUInt16(),
            MaxFunctionDefs = reader.ReadUInt16(),
            MaxInstructionDefs = reader.ReadUInt16(),
            MaxStackElements = reader.ReadUInt16(),
            MaxSizeOfInstructions = reader.ReadUInt16(),
            MaxComponentElements = reader.ReadUInt16(),
            MaxComponentDepth = reader.ReadUInt16(),
        };
    }
}
