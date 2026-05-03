// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>OS/2</c> table (OpenType §"OS/2"). Length depends on version: v0 is 78 bytes,
/// v1 is 86, v2/v3/v4 are 96, v5 is 100. Phase 1 needs typo metrics (ascent / descent),
/// x-height, cap height, weight, and width — all the data the PDF font descriptor builds
/// against.
/// </summary>
internal sealed class Os2Table
{
    public required ushort Version { get; init; }

    public required short XAvgCharWidth { get; init; }
    public required ushort UsWeightClass { get; init; }
    public required ushort UsWidthClass { get; init; }
    public required ushort FsType { get; init; }

    public required short YSubscriptXSize { get; init; }
    public required short YSubscriptYSize { get; init; }
    public required short YSubscriptXOffset { get; init; }
    public required short YSubscriptYOffset { get; init; }
    public required short YSuperscriptXSize { get; init; }
    public required short YSuperscriptYSize { get; init; }
    public required short YSuperscriptXOffset { get; init; }
    public required short YSuperscriptYOffset { get; init; }
    public required short YStrikeoutSize { get; init; }
    public required short YStrikeoutPosition { get; init; }
    public required short SFamilyClass { get; init; }

    /// <summary>10-byte PANOSE classification.</summary>
    public required byte[] Panose { get; init; }

    public required uint UlUnicodeRange1 { get; init; }
    public required uint UlUnicodeRange2 { get; init; }
    public required uint UlUnicodeRange3 { get; init; }
    public required uint UlUnicodeRange4 { get; init; }

    /// <summary>4-byte vendor ID (ASCII).</summary>
    public required byte[] AchVendId { get; init; }

    public required ushort FsSelection { get; init; }
    public required ushort UsFirstCharIndex { get; init; }
    public required ushort UsLastCharIndex { get; init; }

    public required short STypoAscender { get; init; }
    public required short STypoDescender { get; init; }
    public required short STypoLineGap { get; init; }
    public required ushort UsWinAscent { get; init; }
    public required ushort UsWinDescent { get; init; }

    // v1+
    public uint? UlCodePageRange1 { get; init; }
    public uint? UlCodePageRange2 { get; init; }

    // v2+ (also v3, v4)
    public short? SxHeight { get; init; }
    public short? SCapHeight { get; init; }
    public ushort? UsDefaultChar { get; init; }
    public ushort? UsBreakChar { get; init; }
    public ushort? UsMaxContext { get; init; }

    // v5
    public ushort? UsLowerOpticalPointSize { get; init; }
    public ushort? UsUpperOpticalPointSize { get; init; }

    public static Os2Table Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 78)
        {
            throw new InvalidDataException(
                $"OS/2: expected at least 78 bytes; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        var version = reader.ReadUInt16();
        if (version > 5)
        {
            throw new InvalidDataException(
                $"OS/2: unsupported version {version}. Spec defines 0..5.");
        }

        var xAvg = reader.ReadInt16();
        var weight = reader.ReadUInt16();
        var width = reader.ReadUInt16();
        var fsType = reader.ReadUInt16();
        var ySubXSize = reader.ReadInt16();
        var ySubYSize = reader.ReadInt16();
        var ySubXOff = reader.ReadInt16();
        var ySubYOff = reader.ReadInt16();
        var ySupXSize = reader.ReadInt16();
        var ySupYSize = reader.ReadInt16();
        var ySupXOff = reader.ReadInt16();
        var ySupYOff = reader.ReadInt16();
        var yStrikeSize = reader.ReadInt16();
        var yStrikePos = reader.ReadInt16();
        var sFamilyClass = reader.ReadInt16();
        var panose = reader.ReadBytes(10).ToArray();
        var ulRange1 = reader.ReadUInt32();
        var ulRange2 = reader.ReadUInt32();
        var ulRange3 = reader.ReadUInt32();
        var ulRange4 = reader.ReadUInt32();
        var vendId = reader.ReadBytes(4).ToArray();
        var fsSelection = reader.ReadUInt16();
        var firstChar = reader.ReadUInt16();
        var lastChar = reader.ReadUInt16();
        var typoAscender = reader.ReadInt16();
        var typoDescender = reader.ReadInt16();
        var typoLineGap = reader.ReadInt16();
        var winAscent = reader.ReadUInt16();
        var winDescent = reader.ReadUInt16();

        uint? cpRange1 = null;
        uint? cpRange2 = null;
        if (version >= 1)
        {
            if (tableBytes.Length < 86)
            {
                throw new InvalidDataException(
                    $"OS/2 v{version}: expected at least 86 bytes; got {tableBytes.Length}.");
            }
            cpRange1 = reader.ReadUInt32();
            cpRange2 = reader.ReadUInt32();
        }

        short? xHeight = null;
        short? capHeight = null;
        ushort? defaultChar = null;
        ushort? breakChar = null;
        ushort? maxContext = null;
        if (version >= 2)
        {
            if (tableBytes.Length < 96)
            {
                throw new InvalidDataException(
                    $"OS/2 v{version}: expected at least 96 bytes; got {tableBytes.Length}.");
            }
            xHeight = reader.ReadInt16();
            capHeight = reader.ReadInt16();
            defaultChar = reader.ReadUInt16();
            breakChar = reader.ReadUInt16();
            maxContext = reader.ReadUInt16();
        }

        ushort? lowerOptical = null;
        ushort? upperOptical = null;
        if (version >= 5)
        {
            if (tableBytes.Length < 100)
            {
                throw new InvalidDataException(
                    $"OS/2 v{version}: expected at least 100 bytes; got {tableBytes.Length}.");
            }
            lowerOptical = reader.ReadUInt16();
            upperOptical = reader.ReadUInt16();
        }

        return new Os2Table
        {
            Version = version,
            XAvgCharWidth = xAvg,
            UsWeightClass = weight,
            UsWidthClass = width,
            FsType = fsType,
            YSubscriptXSize = ySubXSize,
            YSubscriptYSize = ySubYSize,
            YSubscriptXOffset = ySubXOff,
            YSubscriptYOffset = ySubYOff,
            YSuperscriptXSize = ySupXSize,
            YSuperscriptYSize = ySupYSize,
            YSuperscriptXOffset = ySupXOff,
            YSuperscriptYOffset = ySupYOff,
            YStrikeoutSize = yStrikeSize,
            YStrikeoutPosition = yStrikePos,
            SFamilyClass = sFamilyClass,
            Panose = panose,
            UlUnicodeRange1 = ulRange1,
            UlUnicodeRange2 = ulRange2,
            UlUnicodeRange3 = ulRange3,
            UlUnicodeRange4 = ulRange4,
            AchVendId = vendId,
            FsSelection = fsSelection,
            UsFirstCharIndex = firstChar,
            UsLastCharIndex = lastChar,
            STypoAscender = typoAscender,
            STypoDescender = typoDescender,
            STypoLineGap = typoLineGap,
            UsWinAscent = winAscent,
            UsWinDescent = winDescent,
            UlCodePageRange1 = cpRange1,
            UlCodePageRange2 = cpRange2,
            SxHeight = xHeight,
            SCapHeight = capHeight,
            UsDefaultChar = defaultChar,
            UsBreakChar = breakChar,
            UsMaxContext = maxContext,
            UsLowerOpticalPointSize = lowerOptical,
            UsUpperOpticalPointSize = upperOptical,
        };
    }
}
